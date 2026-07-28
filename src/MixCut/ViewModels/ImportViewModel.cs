using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MixCut.Data;
using MixCut.Models;
using MixCut.Services.AI;
using MixCut.Services.ASR;
using MixCut.Services.BoundaryOptimizer;
using MixCut.Services.SceneDetection;
using MixCut.Services.VideoProcessing;
using MixCut.Utilities;

namespace MixCut.ViewModels;

/// <summary>导入阶段。对应 macOS 版 ImportPhase。</summary>
public enum ImportPhase
{
    Idle,
    Copying,
    ExtractingMetadata,
    GeneratingThumbnail,
    DetectingScenes,
    Transcribing,
    Analyzing,
    Optimizing,
    Completed,
    Failed,
}

/// <summary>单个视频分析阶段——卡片进度条用。</summary>
public enum VideoStage
{
    Queued,         // 排队中
    SceneDetect,    // 视频分析（场景/静音/I-frame）
    Asr,            // 语音识别
    AiAnalyze,      // AI 语义分析
    Finalize,       // 边界优化 + 缩略图
    Refine,         // 阿里逐分镜精识别台词（对齐 mac 混合架构）
    Completed,
    Failed,
}

/// <summary>单视频处理进度快照（卡片用）。</summary>
public sealed record VideoProgressState(
    VideoStage Stage,
    double StagePercent,     // 0.0 - 1.0，当前阶段内部进度
    double OverallPercent,   // 0.0 - 1.0，整个视频处理总进度
    string StageLabel)       // "2/4 语音识别中..."
{
    public static VideoProgressState Empty { get; } =
        new(VideoStage.Queued, 0, 0, "等待中");
}

/// <summary>
/// 一次导入的结构化报告（issue #23 Agent 用，对应 macOS ImportReport）。
/// UI 调用方可忽略返回值，行为不变；Agent 的 import_videos job 把它整包返回给客户端。
/// </summary>
public sealed class ImportReport
{
    public List<string> ImportedNames { get; } = new();
    public List<string> LinkedExistingNames { get; } = new();
    public List<string> SkippedDuplicateNames { get; } = new();
    public List<(string Name, string Reason)> Failed { get; } = new();
    /// <summary>整体中止原因（全部重复 / 磁盘不足），null 表示正常走完。</summary>
    public string? AbortMessage { get; set; }
}

/// <summary>视频导入及分析 ViewModel。对应 macOS 版 ImportViewModel。</summary>
public partial class ImportViewModel : ObservableObject
{
    /// <summary>WPF 文件对话框过滤器。</summary>
    public const string FileDialogFilter = "视频文件|*.mp4;*.mov;*.avi;*.m4v;*.mkv|所有文件|*.*";

    private readonly IDbContextFactory<MixCutDbContext> _dbFactory;
    private readonly FFmpegRunner _ffmpeg;
    private readonly SceneDetectionService _sceneDetection;
    private readonly ASRService _asrService;
    private readonly AIAnalysisService _aiAnalysis;
    private readonly BoundaryOptimizerService _boundaryOptimizer;
    private readonly Services.Dubbing.SegmentReRecognizer _reRecognizer;
    private readonly AppSettings _settings;
    private readonly ILogger<ImportViewModel> _logger;

    /// <summary>已取消的视频 ID（删除视频时加入，用于跳过后续处理）。</summary>
    private readonly ConcurrentDictionary<Guid, byte> _cancelledVideoIds = new();

    /// <summary>每个视频的实时处理进度（用于卡片显示）。</summary>
    private readonly ConcurrentDictionary<Guid, VideoProgressState> _videoProgress = new();

    /// <summary>视频进度变更通知（视图层订阅以刷新单卡片，无需重建整个列表）。</summary>
    public event Action<Guid, VideoProgressState>? VideoProgressChanged;

    /// <summary>有视频被导入到项目时触发（阶段 1 内每完成一个就 fire 一次），UI 重新拉取列表。</summary>
    public event Action? VideoListChanged;

    /// <summary>
    /// 视频分析完成、segments 写入 DB 之后触发。MainWindow 用来失效 SegmentLibrary / Schemes 的视图缓存，
    /// 避免「分析完切到分镜库看到旧数据，重启才有」的 bug（v0.5.0 修复）。
    /// </summary>
    public event Action? SegmentsChanged;

    /// <summary>#17：外部（自建分镜上传）新增/变更分镜后主动广播，失效 SegmentLibrary/Schemes/Overview 缓存（§F）。</summary>
    public void NotifySegmentsChanged() => SegmentsChanged?.Invoke();

    /// <summary>
    /// 一批视频全部分析完成（视频数, 该项目分镜总数）。View 据此提示结果并给出「去分镜素材库」入口。
    /// 分析是长任务，跑完不出声等于让用户自己去猜有没有成功。
    /// </summary>
    public event Action<int, int>? AnalysisCompleted;

    /// <summary>
    /// 从 DB 拉取当前项目的视频列表（含 Segments）。
    /// UI 通过此方法获取最新数据，避免依赖 stale entity navigation。
    /// </summary>
    public IReadOnlyList<Video> GetProjectVideos(Guid projectId)
    {
        try
        {
            using var db = _dbFactory.CreateDbContext();
            return db.Projects
                .Where(p => p.Id == projectId)
                .SelectMany(p => p.ProjectVideos)
                // #17：导入页只列成片视频，排除自建分镜载体（它们只在分镜库以分镜形态出现）。
                .Where(pv => pv.Video != null && !pv.Video.IsUserUploaded)
                .Include(pv => pv.Video!).ThenInclude(v => v.Segments)
                // issue #23：按项目内导入时间（AddedAt）排——与视频编号徽章 / Agent video_no 同一顺序
                //（共享视频的 Video.CreatedAt 是它最早那次导入的时间，会错序）。
                .OrderBy(pv => pv.AddedAt)
                .Select(pv => pv.Video!)
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[ImportVM] GetProjectVideos 失败");
            return Array.Empty<Video>();
        }
    }

    /// <summary>
    /// 项目内视频编号（issue #23）：按导入时间（ProjectVideo.AddedAt）升序 1 起，动态计算、删除自动前补。
    /// 编号覆盖<b>全部</b>视频（含自建分镜载体，即使导入页不显示它们），与 Agent 的 video_no 同一套规则。
    /// </summary>
    public IReadOnlyDictionary<Guid, int> GetVideoNumbers(Guid projectId)
    {
        try
        {
            using var db = _dbFactory.CreateDbContext();
            var ids = db.ProjectVideos
                .Where(pv => pv.ProjectId == projectId && pv.VideoId != null)
                .OrderBy(pv => pv.AddedAt)
                .Select(pv => pv.VideoId!.Value)
                .ToList();
            return ids.Select((id, idx) => (id, idx)).ToDictionary(x => x.id, x => x.idx + 1);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[ImportVM] GetVideoNumbers 失败");
            return new Dictionary<Guid, int>();
        }
    }

    /// <summary>查询某个视频当前的处理进度状态。</summary>
    public VideoProgressState GetVideoProgress(Guid videoId) =>
        _videoProgress.TryGetValue(videoId, out var s) ? s : VideoProgressState.Empty;

    /// <summary>各阶段在整体进度中的区间。</summary>
    private static readonly (double Start, double End, string Label)[] StageRanges =
    {
        (0.00, 0.00, "等待中"),              // Queued
        (0.00, 0.15, "1/5 视频分析中"),      // SceneDetect
        (0.15, 0.55, "2/5 语音识别中"),      // Asr
        (0.55, 0.68, "3/5 AI 语义分析中"),   // AiAnalyze
        (0.68, 0.74, "4/5 边界优化中"),      // Finalize
        (0.74, 1.00, "5/5 台词精识别中"),    // Refine（阿里逐分镜）
        (1.00, 1.00, "处理完成"),            // Completed
        (1.00, 1.00, "处理失败"),            // Failed
    };

    /// <summary>
    /// 阶段计时快照：开始时间 + 排队标记 + 最近两个进度样本（Prev→Last）。
    /// ETA 用「最近一对样本的实测速率」计算 —— 不能用「阶段总耗时 ÷ 当前进度」：
    /// 阶段开头的里程碑式进度（如 ASR 的提音频 10%/找模型 12%）几秒内完成，会把平均速率
    /// 拉得虚高，估出离谱短的总时长，然后一路超期。
    /// </summary>
    private sealed record StageTiming(
        VideoStage Stage, DateTime StartedUtc, bool Queued,
        double PrevPct, DateTime PrevPctUtc, double LastPct, DateTime LastPctUtc);

    /// <summary>每视频当前阶段的计时快照（心跳与 ETA 用）。key=videoId。</summary>
    private readonly ConcurrentDictionary<Guid, StageTiming> _stageStartedAt = new();

    /// <summary>心跳定时器（v0.15.0，见 <see cref="EnsureHeartbeat"/>）。仅 UI 线程访问。</summary>
    private System.Windows.Threading.DispatcherTimer? _heartbeatTimer;

    /// <summary>报告某个视频在某个阶段的内部进度（0.0-1.0），换算成整体百分比并通知 UI。</summary>
    private void ReportVideoStage(Guid videoId, VideoStage stage, double stagePercent, bool queued = false)
    {
        var idx = (int)stage;
        if (idx < 0 || idx >= StageRanges.Length)
        {
            return;
        }
        var (start, end, label) = StageRanges[idx];
        var clamped = Math.Clamp(stagePercent, 0.0, 1.0);
        var overall = start + (end - start) * clamped;

        // 心跳计时：阶段切换（或排队↔运行切换）时重置起点；进度前进时滚动 Prev←Last 样本对；结束态清除。
        if (stage is VideoStage.Completed or VideoStage.Failed)
        {
            _stageStartedAt.TryRemove(videoId, out _);
        }
        else if (!_stageStartedAt.TryGetValue(videoId, out var t) || t.Stage != stage || t.Queued != queued)
        {
            var now = DateTime.UtcNow;
            _stageStartedAt[videoId] = new StageTiming(stage, now, queued, clamped, now, clamped, now);
        }
        else if (clamped > t.LastPct)
        {
            _stageStartedAt[videoId] = t with
            {
                PrevPct = t.LastPct, PrevPctUtc = t.LastPctUtc,
                LastPct = clamped, LastPctUtc = DateTime.UtcNow,
            };
        }

        var state = new VideoProgressState(
            stage, clamped, overall, BuildStageText(videoId, stage, clamped, label, queued));
        _videoProgress[videoId] = state;
        VideoProgressChanged?.Invoke(videoId, state);
        EnsureHeartbeat();
    }

    /// <summary>
    /// 阶段状态文案：「2/5 语音识别中 12% · 已用 6:24 · 预计还需 47:10」。
    /// 已用时间由心跳每秒刷新 —— 慢机上 whisper 每 5% 才打一条进度（可能隔几分钟），
    /// 百分比之外必须有肉眼可见的持续走动，用户才不会误判「卡死了」（CLAUDE.md §1）。
    /// </summary>
    private string BuildStageText(Guid videoId, VideoStage stage, double pct, string label, bool queued)
    {
        if (stage is VideoStage.Completed or VideoStage.Failed)
        {
            return label;
        }
        var text = queued ? $"{label}·排队中…" : $"{label} {pct * 100:F0}%";
        if (_stageStartedAt.TryGetValue(videoId, out var t) && t.Stage == stage)
        {
            var elapsed = DateTime.UtcNow - t.StartedUtc;
            if (elapsed.TotalSeconds >= 5)
            {
                text += $" · 已用 {FormatClock(elapsed)}";

                // ETA 三条铁律（2026-07-24 用户两次当场反馈「预计时间怎么在涨」后定稿）：
                //   ① 只在拿到**可靠实测速率**后才显示 —— 最近一对进度样本间隔 ≥15s 且有实际增量
                //     （阶段开头几秒内完成的里程碑进度不算数，whisper 首条真实进度到达前只显示「已用」）；
                //   ② 显示后只**倒数**：锚定最近样本时刻的估算剩余 − 距该时刻的流逝时间，新进度到达重校准；
                //   ③ 倒数到底仍没等来新进度 → 封底显示「<1 分钟」，绝不回涨、绝不显示 0。
                var dp = t.LastPct - t.PrevPct;
                var gap = (t.LastPctUtc - t.PrevPctUtc).TotalSeconds;
                if (!queued && dp > 0.005 && gap >= 15)
                {
                    var rate = dp / gap;   // 进度/秒（实测）
                    var remainSec = (1 - t.LastPct) / rate
                                    - (DateTime.UtcNow - t.LastPctUtc).TotalSeconds;
                    if (remainSec < 60 && remainSec < gap)
                    {
                        text += " · 预计还需 <1 分钟";
                    }
                    else if (remainSec >= 60 || remainSec >= 5)
                    {
                        var remain = TimeSpan.FromSeconds(Math.Max(remainSec, 5));
                        if (remain.TotalHours < 24)
                        {
                            text += $" · 预计还需 {FormatClock(remain)}";
                            if (stage == VideoStage.Asr && remain.TotalMinutes > 10)
                            {
                                // 弱机跑大模型的明确出路提示（i5 轻薄本 + large 模型实测 0.03x 实时）。
                                text += "（大模型在本机较慢，可到设置换小模型加速）";
                            }
                        }
                    }
                }
            }
        }
        return text;
    }

    private static string FormatClock(TimeSpan t) => t.TotalHours >= 1
        ? $"{(int)t.TotalHours}:{t.Minutes:D2}:{t.Seconds:D2}"
        : $"{(int)t.TotalMinutes}:{t.Seconds:D2}";

    /// <summary>
    /// 进度心跳（v0.15.0）：每秒把处理中视频的状态文案（含已用/预计时间）重发一遍，
    /// 保证进度区永远在走动、绝不静止。无处理中视频时自动停表，零常驻开销。
    /// ReportVideoStage 可能从后台线程进来，定时器统一在 UI 线程创建。
    /// </summary>
    private void EnsureHeartbeat()
    {
        var disp = System.Windows.Application.Current?.Dispatcher;
        if (disp is null)
        {
            return;
        }
        if (!disp.CheckAccess())
        {
            disp.BeginInvoke(EnsureHeartbeat);
            return;
        }
        if (_heartbeatTimer is not null)
        {
            return;
        }
        _heartbeatTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1),
        };
        _heartbeatTimer.Tick += (_, _) =>
        {
            if (_stageStartedAt.IsEmpty)
            {
                _heartbeatTimer?.Stop();
                _heartbeatTimer = null;
                return;
            }
            foreach (var (videoId, t) in _stageStartedAt)
            {
                if (!_videoProgress.TryGetValue(videoId, out var s) || s.Stage != t.Stage)
                {
                    continue;
                }
                var newText = BuildStageText(
                    videoId, s.Stage, s.StagePercent, StageRanges[(int)s.Stage].Label, t.Queued);
                if (newText != s.StageLabel)
                {
                    var ns = s with { StageLabel = newText };
                    _videoProgress[videoId] = ns;
                    VideoProgressChanged?.Invoke(videoId, ns);
                }
            }
        };
        _heartbeatTimer.Start();
    }

    [ObservableProperty]
    private ImportPhase _phase = ImportPhase.Idle;

    [ObservableProperty]
    private double _progress;

    [ObservableProperty]
    private string _progressDescription = string.Empty;

    [ObservableProperty]
    private bool _isProcessing;

    [ObservableProperty]
    private string? _errorMessage;

    public ImportViewModel(
        IDbContextFactory<MixCutDbContext> dbFactory,
        FFmpegRunner ffmpeg,
        SceneDetectionService sceneDetection,
        ASRService asrService,
        AIAnalysisService aiAnalysis,
        BoundaryOptimizerService boundaryOptimizer,
        Services.Dubbing.SegmentReRecognizer reRecognizer,
        AppSettings settings,
        ILogger<ImportViewModel> logger)
    {
        _dbFactory = dbFactory;
        _ffmpeg = ffmpeg;
        _sceneDetection = sceneDetection;
        _asrService = asrService;
        _aiAnalysis = aiAnalysis;
        _boundaryOptimizer = boundaryOptimizer;
        _reRecognizer = reRecognizer;
        _settings = settings;
        _logger = logger;
    }

    /// <summary>导入视频文件列表（全局去重 + 共享引用）。返回结构化报告（UI 可忽略，Agent 用）。</summary>
    public async Task<ImportReport> ImportVideosAsync(IReadOnlyList<string> paths, Guid projectId)
    {
        var report = new ImportReport();
        IsProcessing = true;
        ErrorMessage = null;

        // 去重：跳过已在当前项目中的视频。
        var deduped = new List<string>();
        var skipped = new List<string>();
        await using (var db = await _dbFactory.CreateDbContextAsync())
        {
            var existing = await db.Projects
                .Include(p => p.ProjectVideos).ThenInclude(pv => pv.Video!)
                .Where(p => p.Id == projectId)
                .SelectMany(p => p.ProjectVideos)
                .Select(pv => new { pv.Video!.Name, pv.Video.ContentHash })
                .ToListAsync();
            var existingNames = existing.Select(e => e.Name).ToHashSet();
            var existingHashes = existing.Where(e => e.ContentHash != null)
                .Select(e => e.ContentHash!).ToHashSet();

            foreach (var path in paths)
            {
                var name = Path.GetFileName(path);
                // hash 读 head+tail 各 4MB + SHA256，放线程池避免阻塞 UI（§5 UI 永不卡顿）。
                var hash = await Task.Run(() => ComputeFileHash(path));
                if (existingNames.Contains(name) || (hash != null && existingHashes.Contains(hash)))
                {
                    skipped.Add(name);
                }
                else
                {
                    deduped.Add(path);
                }
            }
        }

        report.SkippedDuplicateNames.AddRange(skipped);
        if (skipped.Count > 0)
        {
            var list = string.Join("、", skipped);
            if (deduped.Count == 0)
            {
                ErrorMessage = $"所有视频均已导入过：{list}";
                report.AbortMessage = ErrorMessage;
                IsProcessing = false;
                Phase = ImportPhase.Completed;
                Progress = 1.0;
                return report;
            }
            ErrorMessage = $"已跳过重复视频：{list}";
        }

        if (!CheckDiskSpace(deduped))
        {
            report.AbortMessage = ErrorMessage ?? "磁盘空间不足";
            IsProcessing = false;
            return report;
        }

        SetProjectStatus(projectId, ProjectStatus.Importing);

        // 阶段 1：快速创建/关联视频实体。
        var videosToAnalyze = new List<Guid>();
        var videoNames = new Dictionary<Guid, string>();   // Agent 报告用：分析失败时按名字归档
        for (var index = 0; index < deduped.Count; index++)
        {
            ProgressDescription = $"导入第 {index + 1}/{deduped.Count} 个视频...";
            Progress = (double)index / deduped.Count * 0.2;
            var fileName = Path.GetFileName(deduped[index]);
            try
            {
                var (videoId, needsAnalysis, linkedExisting) = await ImportOrLinkVideoAsync(deduped[index], projectId);
                videoNames[videoId] = fileName;
                if (linkedExisting && !needsAnalysis)
                {
                    report.LinkedExistingNames.Add(fileName);
                }
                else
                {
                    report.ImportedNames.Add(fileName);
                }
                if (needsAnalysis)
                {
                    videosToAnalyze.Add(videoId);
                }
                // 每导入一个视频立即通知 UI 刷新列表（对齐 Mac 体验：视频先出现，分析进度后跟上）
                VideoListChanged?.Invoke();
            }
            catch (Exception ex)
            {
                var reason = ExceptionTranslator.ToUserMessage(ex);
                report.Failed.Add((fileName, reason));
                AppendError($"导入 {fileName} 失败：{reason}");
            }
        }

        // 阶段 2：并行执行视频分析（每个视频独立 DbContext，避免竞态）。
        if (videosToAnalyze.Count > 0)
        {
            SetProjectStatus(projectId, ProjectStatus.Analyzing);

            // 并发数走统一策略（Infrastructure.ConcurrencyPolicy）。
            // v0.5.0 起根据 HwProbe.DecodeHwaccel 给 GPU 解码可用的机器 +1 路加成。
            var maxConcurrency = Infrastructure.ConcurrencyPolicy.MaxAnalyzeConcurrency(videosToAnalyze.Count);
            ProgressDescription = $"并行分析 {videosToAnalyze.Count} 个视频（{maxConcurrency} 路并发）...";

            using var semaphore = new SemaphoreSlim(maxConcurrency);
            var completed = 0;
            var tasks = videosToAnalyze.Select(async videoId =>
            {
                await semaphore.WaitAsync();
                try
                {
                    await AnalyzeVideoAsync(videoId);
                }
                catch (OperationCanceledException)
                {
                    // 用户删除视频，跳过。
                }
                catch (Exception ex)
                {
                    lock (report)
                    {
                        report.Failed.Add((videoNames.GetValueOrDefault(videoId, videoId.ToString()),
                            ExceptionTranslator.ToUserMessage(ex)));
                    }
                    await HandleAnalyzeFailureAsync(videoId, ex);
                }
                finally
                {
                    semaphore.Release();
                    var done = Interlocked.Increment(ref completed);
                    Progress = 0.2 + (double)done / videosToAnalyze.Count * 0.8;
                    ProgressDescription = $"已完成 {done}/{videosToAnalyze.Count} 个视频分析";
                    // 每个视频 segments 入库后立即通知 MainWindow 失效 SegmentLibrary/Schemes 缓存。
                    // 用户切过去时强制重新 LoadProject，避免「分析完看不到分镜，重启才有」的 bug。
                    SegmentsChanged?.Invoke();
                }
            });
            await Task.WhenAll(tasks);

            // 全部分析完成最后再触发一次，确保最终状态被刷到。
            SegmentsChanged?.Invoke();

            // 主动告知结果并给出去处。
            // 用户反馈：分析跑完后什么提示都没有，切到分镜素材库看到空白，以为软件坏了 ——
            // 尤其一次导入多个视频时，进度条走完到分镜出现之间有明显空窗。
            // 这里报「几个视频、共几个分镜」，并让 View 给出「去分镜素材库」的入口。
            try
            {
                await using var db = await _dbFactory.CreateDbContextAsync();
                var segCount = await db.Segments
                    .CountAsync(s => s.Video != null
                                     && s.Video.ProjectVideos.Any(pv => pv.ProjectId == projectId));
                AnalysisCompleted?.Invoke(videosToAnalyze.Count, segCount);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "统计分镜总数失败，跳过完成提示");
            }
        }

        SetProjectStatus(projectId, ProjectStatus.Ready);
        Phase = ImportPhase.Completed;
        Progress = 1.0;
        IsProcessing = false;
        return report;
    }

    // ---- 核心导入逻辑 ----

    /// <summary>导入或关联视频。返回 (videoId, needsAnalysis, linkedExisting)。</summary>
    private async Task<(Guid VideoId, bool NeedsAnalysis, bool LinkedExisting)> ImportOrLinkVideoAsync(string path, Guid projectId)
    {
        Phase = ImportPhase.Copying;
        // hash 计算放线程池，避免在 UI 线程同步读 8MB 阻塞界面（§5）。
        var hash = await Task.Run(() => ComputeFileHash(path));

        await using var db = await _dbFactory.CreateDbContextAsync();

        // 查找全局是否已有同 hash 的视频。
        if (hash != null)
        {
            var existing = await db.Videos
                .Include(v => v.Segments)
                .FirstOrDefaultAsync(v => v.ContentHash == hash);
            if (existing != null)
            {
                db.ProjectVideos.Add(new ProjectVideo { ProjectId = projectId, VideoId = existing.Id });
                await db.SaveChangesAsync();
                _logger.LogInformation("全局已有视频「{Name}」，直接关联到项目", existing.Name);
                var needsAnalysis = existing.Status != VideoStatus.Completed || existing.Segments.Count == 0;
                return (existing.Id, needsAnalysis, true);
            }
        }

        // 全局没有，创建新的。
        var destPath = FileHelper.CopyVideoToGlobal(path, hash ?? Guid.NewGuid().ToString());
        var video = new Video
        {
            Name = Path.GetFileName(path),
            LocalPath = destPath,
            Status = VideoStatus.Imported,
            ContentHash = hash,
        };
        db.Videos.Add(video);
        db.ProjectVideos.Add(new ProjectVideo { ProjectId = projectId, VideoId = video.Id });
        await db.SaveChangesAsync();

        // 提取元数据（失败不阻塞）。
        try
        {
            await ExtractMetadataAsync(video);
        }
        catch (Exception ex)
        {
            video.ErrorMessage = $"元数据提取失败：{ExceptionTranslator.ToUserMessage(ex)}";
        }

        // 生成视频缩略图（失败不阻塞）。
        try
        {
            Phase = ImportPhase.GeneratingThumbnail;
            var thumbPath = Path.Combine(FileHelper.GlobalThumbnailDirectory, $"{video.Id}.jpg");
            await _ffmpeg.GenerateThumbnailAsync(video.LocalPath, thumbPath);
            video.ThumbnailPath = thumbPath;
        }
        catch (Exception ex)
        {
            _logger.LogError("缩略图生成失败: video={Name}, error={Message}", video.Name, ex.Message);
        }

        await db.SaveChangesAsync();
        return (video.Id, true, false);
    }

    /// <summary>执行视频分析（场景检测 + ASR + AI 分析 + 边界优化）。每个视频独立 DbContext。</summary>
    private async Task AnalyzeVideoAsync(Guid videoId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var video = await db.Videos.Include(v => v.Segments).FirstOrDefaultAsync(v => v.Id == videoId);
        if (video is null)
        {
            return;
        }

        CheckCancelled(videoId);

        // Step 1: 本地视频分析。
        video.Status = VideoStatus.DetectingScenes;
        await db.SaveChangesAsync();
        Phase = ImportPhase.DetectingScenes;
        ReportVideoStage(videoId, VideoStage.SceneDetect, 0);

        var fps = video.Fps > 0 ? video.Fps : 30;
        var localAnalysis = new VideoLocalAnalysis(
            Array.Empty<SceneBoundary>(), Array.Empty<SilencePeriod>(),
            Array.Empty<double>(), video.Duration, fps);
        try
        {
            // 场景检测内部分 3 步：场景、静音、I-frame；用粗粒度进度回调即可。
            localAnalysis = await _sceneDetection.AnalyzeLocallyAsync(
                video.LocalPath, video.Duration, fps,
                progress: new Progress<double>(p =>
                    ReportVideoStage(videoId, VideoStage.SceneDetect, p)));
        }
        catch (Exception ex)
        {
            video.ErrorMessage = (video.ErrorMessage ?? string.Empty) + $"\n本地分析失败：{ExceptionTranslator.ToUserMessage(ex)}";
        }
        ReportVideoStage(videoId, VideoStage.SceneDetect, 1.0);

        CheckCancelled(videoId);

        // Step 2: ASR 语音识别。
        Phase = ImportPhase.Transcribing;
        video.Status = VideoStatus.Transcribing;
        await db.SaveChangesAsync();
        // whisper 全局串行：若槽位被前一个视频占用，本视频会在 TranscribeAsync 内 WaitAsync 阻塞。
        // 此时显示「排队中」而非停在 0%，避免用户误以为卡死（CLAUDE.md §1 进度反馈）。
        ReportVideoStage(videoId, VideoStage.Asr, 0, queued: ASRService.IsWhisperBusy);

        var asr = TranscriptionResult.Empty();
        try
        {
            // 把视频时长传给 ASR：看门狗（进度停滞才杀）基于此计算绝对上限。
            // 进度回调把 whisper 内部 0-1 进度映射到 ASR 阶段。
            var asrProgress = new Progress<double>(p =>
                ReportVideoStage(videoId, VideoStage.Asr, p));
            asr = await _asrService.TranscribeAsync(
                video.LocalPath, language: "zh",
                videoDurationSec: video.Duration,
                progress: asrProgress);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // v0.15.0：ASR 真失败（超时被看门狗中止 / 崩溃）就到此为止，直接判定该视频失败并给出
            // **完整原因**（AsrException 的 message 已是「原因 + 模型名 + 换小模型建议」的人话）。
            // 绝不再拿空文本继续跑 AI —— 那只会得到「AI 未返回有效分镜结果」，把真因彻底掩盖
            // （2026-07-24 弱机用户实测踩坑：卡片只显示「处理失败」，看不到任何可自救的信息）。
            _logger.LogError("ASR 异常: {Message}", ex.Message);
            video.Status = VideoStatus.Failed;
            video.ErrorMessage = ex is Services.ASR.AsrException
                ? ex.Message
                : "语音识别失败：" + ExceptionTranslator.ToUserMessage(ex);
            await db.SaveChangesAsync();
            ReportVideoStage(videoId, VideoStage.Failed, 1.0);
            _logger.LogInformation("分析完成: video={Name}, status={Status}（ASR 失败提前结束）",
                video.Name, video.Status);
            return;
        }

        video.Transcript = asr.Text;
        video.AsrWords = asr.Words;
        video.AsrSentences = asr.RawSentences;
        await db.SaveChangesAsync();

        CheckCancelled(videoId);

        // Step 3: AI 语义分析。
        // 重试场景：清掉之前的错误消息再来。
        video.ErrorMessage = null;

        Phase = ImportPhase.Analyzing;
        video.Status = VideoStatus.Analyzing;
        await db.SaveChangesAsync();
        ReportVideoStage(videoId, VideoStage.AiAnalyze, 0.1);

        AISegmentationResult? analysisResult = null;
        string? aiErrorMessage = null;
        try
        {
            analysisResult = await _aiAnalysis.AnalyzeVideoAsync(
                video.Name, asr, localAnalysis.SceneBoundaries, localAnalysis);
        }
        catch (Exception ex)
        {
            // 走统一翻译：未配置 Key → 跳过提示；其余（含免费额度耗尽 403 / 欠费 / 限流）翻成准确人话，
            // 不再用脆弱的 Contains("API Key") 子串判断把免费额度 403 误报成「AI 分析失败：<英文>」。
            var friendly = ExceptionTranslator.ToUserMessage(ex);
            aiErrorMessage = ex is MixCut.Services.AI.AIProviderException
                { Kind: MixCut.Services.AI.AIProviderErrorKind.ApiKeyNotConfigured }
                ? "AI 分析跳过：" + friendly
                : "AI 分析失败：" + friendly;
        }
        ReportVideoStage(videoId, VideoStage.AiAnalyze, 1.0);

        CheckCancelled(videoId);

        // Step 4+5: 边界优化 + 创建分镜。
        if (analysisResult is not null && analysisResult.Segments.Count > 0)
        {
            Phase = ImportPhase.Optimizing;
            ReportVideoStage(videoId, VideoStage.Finalize, 0.2);
            // 重新分析时清除旧分镜，保证幂等。
            if (video.Segments.Count > 0)
            {
                db.Segments.RemoveRange(video.Segments);
            }
            CreateSegments(analysisResult, asr, localAnalysis, video, db);
            await db.SaveChangesAsync();
            ReportVideoStage(videoId, VideoStage.Finalize, 0.5);
            await GenerateSegmentThumbnailsAsync(video.Id, video.LocalPath, db);
            ReportVideoStage(videoId, VideoStage.Finalize, 1.0);

            // 分镜此刻已入库 + 有缩略图 —— 立即失效 SegmentLibrary 缓存，让用户点过去就能看到分镜。
            // 关键修复：下面 Step 6 逐分镜精识别对一条 111s / 29 段的视频要跑 60~90s WebSocket，
            // 若把「通知 UI 刷新」拖到整个 AnalyzeVideoAsync 结束（finally 里那次 SegmentsChanged）才发，
            // 用户在这 90s 里点分镜库会命中 MainWindow 的 project 缓存 → 跳过 LoadProject → 看到空列表，
            // 直到重启才显示（用户实测报的正是这个）。精识别只是覆盖 Text，分镜边界/缩略图已定，先让它们显示。
            SegmentsChanged?.Invoke();
            _logger.LogInformation("[SegVisibleDiag] 分镜已入库并通知刷新 video={Name} 段数={Count}",
                video.Name, video.Segments.Count);

            // Step 6: 逐分镜阿里精识别台词（对齐 mac「阿里 ASR 混合架构」）。
            // whisper 整片转写只用于切分镜的时间戳；分镜显示台词由 Paraformer 各段独立重识别，
            // 短音频天生准、带标点。无千问 key 时优雅跳过（保留 whisper 文本，不报错）。
            CheckCancelled(videoId);
            await ReidentifySegmentTextsAsync(video, db, videoId);
            // 精识别覆盖了 Text，再刷一次让分镜卡片显示更准的台词。
            SegmentsChanged?.Invoke();

            video.Status = VideoStatus.Completed;
            video.ErrorMessage = null;
            ReportVideoStage(videoId, VideoStage.Completed, 1.0);
        }
        else
        {
            video.Status = VideoStatus.Failed;
            video.ErrorMessage = aiErrorMessage ?? "AI 未返回有效分镜结果";
            ReportVideoStage(videoId, VideoStage.Failed, 1.0);
        }

        await db.SaveChangesAsync();
        _logger.LogInformation("分析完成: video={Name}, status={Status}", video.Name, video.Status);
    }

    /// <summary>
    /// 逐分镜用阿里 Paraformer 重识别台词，覆盖 whisper 词拼接文本（对齐 mac reidentifySegmentTexts）。
    /// 每段按自己时间范围切音频独立识别 —— 短音频时间戳天生准、带标点，不受整片漂移影响。
    /// 无千问 key 时整段跳过（保留 whisper 文本，不报错）；单段失败保留该段原文，不中断整体导入。
    /// </summary>
    private async Task<(int ok, int fail)> ReidentifySegmentTextsAsync(
        Video video, MixCutDbContext db, Guid videoId, CancellationToken ct = default)
    {
        if (!_reRecognizer.IsAvailable)
        {
            _logger.LogInformation("[ReidDiag] 跳过阿里精识别（未配千问 key），保留 whisper 文本 video={Name}", video.Name);
            return (0, 0);
        }

        // 从 db 直接查（CreateSegments 走 db.Segments.Add + MergeShortSegments，video.Segments 导航未必已同步）。
        var segs = await db.Segments.Where(s => s.VideoId == video.Id)
            .OrderBy(s => s.StartFrame).ToListAsync(ct);
        if (segs.Count == 0) return (0, 0);

        var fps = video.Fps > 0 ? video.Fps : 30.0;
        int ok = 0, fail = 0;
        for (var i = 0; i < segs.Count; i++)
        {
            CheckCancelled(videoId);
            ct.ThrowIfCancellationRequested();
            var seg = segs[i];
            try
            {
                // 帧为边界真值 → 秒（对齐 mac start=startFrame/fps），比派生的 StartTime 缓存更稳。
                var text = await _reRecognizer.RecognizeAsync(
                    video.LocalPath, seg.StartFrame / fps, seg.EndFrame / fps, ct);
                if (!string.IsNullOrWhiteSpace(text)) { seg.Text = text; ok++; }
                else fail++;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                fail++;
                _logger.LogWarning("[ReidDiag] 分镜 {Idx} 精识别失败，保留原文: {Msg}", i + 1, ex.Message);
            }
            ReportVideoStage(videoId, VideoStage.Refine, (i + 1.0) / segs.Count);
        }

        await db.SaveChangesAsync(ct);
        _logger.LogInformation("[ReidDiag] 阿里逐分镜精识别完成 video={Name} 成功={Ok} 失败={Fail} 共={Total}",
            video.Name, ok, fail, segs.Count);
        return (ok, fail);
    }

    /// <summary>
    /// 对已分析完成的视频，手动重跑一遍阿里逐分镜精识别（自测入口；也可作为未来「整片重识别」按钮的后端）。
    /// 独立开 db，加载视频后走与导入相同的核心逻辑。返回 (成功段数, 失败段数)。
    /// </summary>
    public async Task<(int ok, int fail)> ReidentifyVideoAsync(Guid videoId, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var video = await db.Videos.FirstOrDefaultAsync(v => v.Id == videoId, ct);
        if (video is null) return (0, 0);
        return await ReidentifySegmentTextsAsync(video, db, videoId, ct);
    }

    /// <summary>从 AI 结果创建分镜。</summary>
    private void CreateSegments(
        AISegmentationResult result, TranscriptionResult asr,
        VideoLocalAnalysis localAnalysis, Video video, MixCutDbContext db)
    {
        var boundaries = result.Segments.Select(s => s.EndTime).ToList();
        var (optimized, _) = _boundaryOptimizer.Optimize(boundaries, asr.Sentences, localAnalysis);

        var asrWords = asr.Words;
        var videoDuration = video.Duration;
        var created = new List<Segment>();

        for (var i = 0; i < result.Segments.Count; i++)
        {
            var aiSeg = result.Segments[i];
            var adjustedEnd = i < optimized.Count ? optimized[i] : aiSeg.EndTime;
            var adjustedStart = i == 0 ? 0.0
                : i - 1 < optimized.Count ? optimized[i - 1]
                : aiSeg.StartTime;
            if (i == result.Segments.Count - 1 && videoDuration > 0)
            {
                adjustedEnd = videoDuration;
            }

            var extracted = ExtractTextFromAsr(asrWords, adjustedStart, adjustedEnd);
            var finalText = extracted.Length == 0 ? aiSeg.Text : extracted;

            var segment = new Segment
            {
                SegmentIndex = $"seg_{i + 1:D3}",
                Text = finalText,
                SemanticTypes = aiSeg.EffectiveTypes.Select(NormalizeSemanticType).ToList(),
                PositionType = NormalizePositionType(aiSeg.Position),
                QualityScore = aiSeg.DataQuality.Score,
                QualityReasoning = aiSeg.DataQuality.Reasoning,
                Keywords = aiSeg.Keywords,
                VideoId = video.Id,
            };
            // issue #7：边界量化到帧（帧为真值，秒派生）。fps 显式取视频帧率（segment.Video 尚未关联）。
            segment.SetBoundsSeconds(adjustedStart, adjustedEnd, video.Fps);
            db.Segments.Add(segment);
            created.Add(segment);
        }

        MergeShortSegments(created, 2.0, db);
    }

    /// <summary>重试某个视频的分析（清除取消标记后重跑完整流水线）。</summary>
    public async Task RetryAnalysisAsync(Guid videoId)
    {
        IsProcessing = true;
        ErrorMessage = null;
        _cancelledVideoIds.TryRemove(videoId, out _);
        try
        {
            await AnalyzeVideoAsync(videoId);
        }
        catch (OperationCanceledException)
        {
            // 已取消，跳过。
        }
        catch (Exception ex)
        {
            await HandleAnalyzeFailureAsync(videoId, ex);
        }
        finally
        {
            Phase = ImportPhase.Completed;
            IsProcessing = false;
        }
    }

    /// <summary>
    /// 重新识别 ASR：清空旧的 transcript/asrWords/asrSentences + 旧分镜，然后跑完整流水线。
    /// 用于 ASR 分句异常时用户主动重试。对齐 Mac retryASR。
    /// </summary>
    public async Task RetryASRAsync(Guid videoId)
    {
        using (var db = _dbFactory.CreateDbContext())
        {
            var video = db.Videos.Include(v => v.Segments).FirstOrDefault(v => v.Id == videoId);
            if (video is null) return;
            video.Transcript = string.Empty;
            video.AsrWords = Array.Empty<AsrWord>();
            video.AsrSentences = Array.Empty<AsrSentence>();
            // 清空旧分镜（防止 UI 卡在中间态）
            db.Segments.RemoveRange(video.Segments);
            video.Status = VideoStatus.Transcribing;
            video.ErrorMessage = null;
            db.SaveChanges();
        }
        ErrorMessage = null;
        await RetryAnalysisAsync(videoId);
    }

    // ---- 视频删除 ----

    /// <summary>从项目中移除视频；仅当无任何项目引用时才真正删除 Video + Segment + 磁盘文件。</summary>
    public void DeleteVideo(Guid videoId, Guid projectId)
    {
        _cancelledVideoIds[videoId] = 0;

        using var db = _dbFactory.CreateDbContext();
        var video = db.Videos.Include(v => v.Segments).FirstOrDefault(v => v.Id == videoId);
        if (video is null)
        {
            return;
        }

        // 处理中的视频立即标记失败，让 UI 立刻反馈。
        if (video.Status is VideoStatus.DetectingScenes or VideoStatus.Transcribing or VideoStatus.Analyzing)
        {
            video.Status = VideoStatus.Failed;
            video.ErrorMessage = "已删除";
        }

        // 删除当前项目与该视频的关联。
        var links = db.ProjectVideos.Where(pv => pv.VideoId == videoId && pv.ProjectId == projectId).ToList();
        db.ProjectVideos.RemoveRange(links);
        db.SaveChanges();

        // 检查是否还被其他项目引用。
        var remaining = db.ProjectVideos.Count(pv => pv.VideoId == videoId);
        if (remaining == 0)
        {
            var segThumbnails = video.Segments
                .Where(s => !string.IsNullOrEmpty(s.ThumbnailPath))
                .Select(s => s.ThumbnailPath!)
                .ToList();
            var localPath = video.LocalPath;
            var thumbnailPath = video.ThumbnailPath;

            db.Videos.Remove(video); // 级联删除分镜、方案分镜
            db.SaveChanges();

            FileHelper.DeleteGlobalVideoFiles(localPath, thumbnailPath);
            foreach (var thumb in segThumbnails)
            {
                TryDeleteFile(thumb);
            }
            _logger.LogInformation("视频无引用，已彻底删除: {Name}", video.Name);
        }
        else
        {
            _logger.LogInformation("视频仍被 {Count} 个项目引用，仅解除关联: {Name}", remaining, video.Name);
        }
    }

    // ---- 工具方法 ----

    /// <summary>归一化 AI 返回的语义类型字符串。</summary>
    public static SemanticType NormalizeSemanticType(string raw)
    {
        var cleaned = raw.Trim();
        foreach (var t in SemanticTypeExtensions.All)
        {
            if (t.ToLabel() == cleaned)
            {
                return t;
            }
        }

        var lower = cleaned.ToLowerInvariant();
        (string[] Keywords, SemanticType Type)[] mapping =
        {
            (new[] { "噱头", "hook", "开场", "引入", "吸引" }, SemanticType.Hook),
            (new[] { "痛点", "pain", "问题", "烦恼" }, SemanticType.PainPoint),
            (new[] { "产品方案", "solution", "产品介绍", "成分", "功能" }, SemanticType.Solution),
            (new[] { "效果展示", "effect", "result", "对比", "变化" }, SemanticType.Results),
            (new[] { "信任背书", "social proof", "用户评价", "品牌", "背书", "见证" }, SemanticType.SocialProof),
            (new[] { "价格对比", "price", "性价比", "价格" }, SemanticType.PriceAnchor),
            (new[] { "活动福利", "promotion", "优惠", "福利", "折扣", "赠品" }, SemanticType.Promotion),
            (new[] { "行动号召", "call to action", "cta", "购买", "下单", "直播间" }, SemanticType.CallToAction),
            (new[] { "产品定位", "positioning", "适用", "人群" }, SemanticType.ProductPositioning),
            (new[] { "产品使用教育", "usage", "使用方法", "教育", "使用场景" }, SemanticType.UsageEducation),
            (new[] { "过渡", "transition", "衔接", "转场" }, SemanticType.Transition),
        };
        foreach (var (keywords, type) in mapping)
        {
            if (keywords.Any(k => lower.Contains(k.ToLowerInvariant())))
            {
                return type;
            }
        }
        return SemanticType.Transition;
    }

    private static PositionType NormalizePositionType(string raw)
    {
        var cleaned = raw.Trim();
        foreach (var p in PositionTypeExtensions.All)
        {
            if (p.ToLabel() == cleaned)
            {
                return p;
            }
        }
        var lower = cleaned.ToLowerInvariant();
        if (lower.Contains("开头") || lower.Contains("opening"))
        {
            return PositionType.Opening;
        }
        if (lower.Contains("结尾") || lower.Contains("ending"))
        {
            return PositionType.Ending;
        }
        return PositionType.Middle;
    }

    /// <summary>合并过短的分镜到相邻分镜。</summary>
    private static void MergeShortSegments(List<Segment> segments, double minDuration, MixCutDbContext db)
    {
        var i = 0;
        while (i < segments.Count)
        {
            var seg = segments[i];
            if (seg.Duration >= minDuration || segments.Count <= 1)
            {
                i++;
                continue;
            }

            if (i > 0)
            {
                var prev = segments[i - 1];
                prev.EndTime = seg.EndTime;
                prev.EndFrame = seg.EndFrame; // issue #7：同步帧号（同视频同 fps），保持帧/秒一致
                prev.Text = JoinSegmentText(prev.Text, seg.Text);
                var kw = prev.Keywords.ToList();
                kw.AddRange(seg.Keywords.Where(k => !kw.Contains(k)));
                prev.Keywords = kw;
                db.Segments.Remove(seg);
                segments.RemoveAt(i);
            }
            else
            {
                var next = segments[1];
                next.StartTime = seg.StartTime;
                next.StartFrame = seg.StartFrame; // issue #7：同步帧号
                next.Text = JoinSegmentText(seg.Text, next.Text);
                db.Segments.Remove(seg);
                segments.RemoveAt(0);
            }
        }

        // 合并删掉了中间分镜，SegmentIndex 会跳号（seg_001/seg_003…），V1 角标 / 删除确认 /
        // 导出命名都会显示诡异跳号。合并完重排为连续编号。
        for (var k = 0; k < segments.Count; k++)
        {
            segments[k].SegmentIndex = $"seg_{k + 1:D3}";
        }
    }

    /// <summary>拼接两段相邻分镜的台词，中间补空格避免「上句下句」直接粘连（任一为空返回另一个）。</summary>
    private static string JoinSegmentText(string a, string b) =>
        string.IsNullOrEmpty(a) ? b : string.IsNullOrEmpty(b) ? a : a + " " + b;

    /// <summary>根据时间范围从 ASR words 中精确提取台词（中心点匹配）。</summary>
    private static string ExtractTextFromAsr(IReadOnlyList<AsrWord> words, double startTime, double endTime)
    {
        var matched = words
            .Where(w => (w.Start + w.End) / 2 >= startTime && (w.Start + w.End) / 2 < endTime)
            .Select(w => w.Word);
        var cleaned = string.Concat(matched).Replace("�", string.Empty).Trim();
        while (cleaned.Contains("  ", StringComparison.Ordinal))
        {
            cleaned = cleaned.Replace("  ", " ");
        }
        return cleaned;
    }

    /// <summary>使用 ffprobe 提取视频元数据。</summary>
    private async Task ExtractMetadataAsync(Video video)
    {
        Phase = ImportPhase.ExtractingMetadata;
        var json = await _ffmpeg.RunProbeAsync(new[]
        {
            "-v", "error", "-select_streams", "v:0",
            "-show_entries", "stream=width,height,r_frame_rate",
            "-show_entries", "format=duration",
            "-of", "json", video.LocalPath,
        });

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (root.TryGetProperty("format", out var format)
            && format.TryGetProperty("duration", out var durationEl)
            && double.TryParse(durationEl.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var duration))
        {
            video.Duration = duration;
        }

        if (root.TryGetProperty("streams", out var streams) && streams.GetArrayLength() > 0)
        {
            var stream = streams[0];
            if (stream.TryGetProperty("width", out var w))
            {
                video.Width = w.GetInt32();
            }
            if (stream.TryGetProperty("height", out var h))
            {
                video.Height = h.GetInt32();
            }
            if (stream.TryGetProperty("r_frame_rate", out var rEl) && rEl.GetString() is { } rate)
            {
                var parts = rate.Split('/');
                if (parts.Length == 2
                    && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var num)
                    && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var den)
                    && den > 0)
                {
                    video.Fps = num / den;
                }
            }
        }
    }

    /// <summary>为分镜列表生成缩略图。</summary>
    private async Task GenerateSegmentThumbnailsAsync(Guid videoId, string videoPath, MixCutDbContext db)
    {
        var segments = await db.Segments.Where(s => s.VideoId == videoId).ToListAsync();
        var thumbDir = FileHelper.GlobalThumbnailDirectory;

        await Task.WhenAll(segments.Select(async segment =>
        {
            if (!string.IsNullOrEmpty(segment.ThumbnailPath) && File.Exists(segment.ThumbnailPath))
            {
                return;
            }
            // v0.3.1 对齐：分镜缩略图改用「首帧」(startTime + 100ms)，
            // 100ms 偏移避开完全在起点的转场/黑帧。
            var firstFrameTime = Math.Max(0, segment.StartTime + 0.1);
            var thumbPath = Path.Combine(thumbDir, $"seg_{segment.Id}.jpg");
            try
            {
                await _ffmpeg.GenerateThumbnailAsync(videoPath, thumbPath, firstFrameTime);
                segment.ThumbnailPath = thumbPath;
            }
            catch (Exception)
            {
                // 单个缩略图失败不阻塞。
            }
        }));
        await db.SaveChangesAsync();
    }

    /// <summary>计算文件哈希（文件大小 + 头尾各 4MB 的 SHA-256，快速去重）。</summary>
    public static string? ComputeFileHash(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var fileSize = stream.Length;
            const int chunkSize = 4 * 1024 * 1024;

            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            sha.AppendData(Encoding.UTF8.GetBytes(fileSize.ToString(CultureInfo.InvariantCulture)));

            var buffer = new byte[chunkSize];
            var headRead = stream.Read(buffer, 0, chunkSize);
            sha.AppendData(buffer, 0, headRead);

            if (fileSize > (long)chunkSize * 2)
            {
                stream.Seek(-chunkSize, SeekOrigin.End);
                var tailRead = stream.Read(buffer, 0, chunkSize);
                sha.AppendData(buffer, 0, tailRead);
            }

            return Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
        }
        catch (Exception)
        {
            return null;
        }
    }

    private bool CheckDiskSpace(IReadOnlyList<string> paths)
    {
        try
        {
            long totalSize = 0;
            foreach (var path in paths)
            {
                totalSize += new FileInfo(path).Length;
            }
            var required = totalSize * 2;
            var root = Path.GetPathRoot(AppPaths.Root);
            if (root is not null)
            {
                var available = new DriveInfo(root).AvailableFreeSpace;
                if (available < required)
                {
                    ErrorMessage = $"磁盘空间不足：需要约 {FormatBytes(required)}，当前可用 {FormatBytes(available)}";
                    return false;
                }
            }
        }
        catch (Exception)
        {
            // 空间检查失败不阻塞导入。
        }
        return true;
    }

    private void SetProjectStatus(Guid projectId, ProjectStatus status)
    {
        using var db = _dbFactory.CreateDbContext();
        var project = db.Projects.FirstOrDefault(p => p.Id == projectId);
        if (project is not null)
        {
            project.Status = status;
            project.UpdatedAt = DateTime.Now;
            db.SaveChanges();
        }
    }

    private async Task HandleAnalyzeFailureAsync(Guid videoId, Exception error)
    {
        // 静默吃乐观并发异常和 "video 已被删除" 场景：这通常发生在用户在分析过程中删除了视频
        // 或切换项目，不算真正的失败，不需要在 UI 显示红色 banner。
        if (error is Microsoft.EntityFrameworkCore.DbUpdateConcurrencyException
            || error.Message.Contains("affect 0 row", StringComparison.OrdinalIgnoreCase)
            || error.Message.Contains("data may have been modified or deleted",
                StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogInformation("视频 {Id} 在分析过程中被外部修改/删除，跳过失败上报", videoId);
            return;
        }

        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            var video = await db.Videos.FirstOrDefaultAsync(v => v.Id == videoId);
            if (video is not null
                && video.Status is VideoStatus.DetectingScenes or VideoStatus.Transcribing or VideoStatus.Analyzing)
            {
                video.ErrorMessage = (video.ErrorMessage ?? string.Empty) + $"\n分析失败：{ExceptionTranslator.ToUserMessage(error)}";
                video.Status = VideoStatus.Failed;
                await db.SaveChangesAsync();
                _logger.LogError("兜底标记失败: videoId={Id}, error={Message}", videoId, error.Message);
            }
        }
        catch (Microsoft.EntityFrameworkCore.DbUpdateConcurrencyException)
        {
            // 兜底标记失败本身也并发了，说明视频已被删除，安静退出
            return;
        }
        AppendError($"分析视频失败: {error.Message}");
    }

    private void CheckCancelled(Guid videoId)
    {
        if (_cancelledVideoIds.TryRemove(videoId, out _))
        {
            throw new OperationCanceledException();
        }
    }

    private void AppendError(string message)
    {
        ErrorMessage = string.IsNullOrEmpty(ErrorMessage) ? message : ErrorMessage + "\n" + message;
    }

    private void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception)
        {
            // 忽略文件清理失败。
        }
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double size = bytes;
        var unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }
        return $"{size:F1} {units[unit]}";
    }
}
