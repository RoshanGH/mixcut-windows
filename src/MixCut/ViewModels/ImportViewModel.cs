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
                .Where(pv => pv.Video != null)
                .Include(pv => pv.Video!).ThenInclude(v => v.Segments)
                .Select(pv => pv.Video!)
                .OrderBy(v => v.CreatedAt)
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[ImportVM] GetProjectVideos 失败");
            return Array.Empty<Video>();
        }
    }

    /// <summary>查询某个视频当前的处理进度状态。</summary>
    public VideoProgressState GetVideoProgress(Guid videoId) =>
        _videoProgress.TryGetValue(videoId, out var s) ? s : VideoProgressState.Empty;

    /// <summary>各阶段在整体进度中的区间。</summary>
    private static readonly (double Start, double End, string Label)[] StageRanges =
    {
        (0.00, 0.00, "等待中"),              // Queued
        (0.00, 0.20, "1/4 视频分析中"),      // SceneDetect
        (0.20, 0.80, "2/4 语音识别中"),      // Asr
        (0.80, 0.95, "3/4 AI 语义分析中"),   // AiAnalyze
        (0.95, 1.00, "4/4 边界优化中"),      // Finalize
        (1.00, 1.00, "处理完成"),            // Completed
        (1.00, 1.00, "处理失败"),            // Failed
    };

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
        var pctText = queued
            ? $"{label}·排队中…"
            : stage is VideoStage.Completed or VideoStage.Failed
                ? label
                : $"{label} {clamped * 100:F0}%";

        var state = new VideoProgressState(stage, clamped, overall, pctText);
        _videoProgress[videoId] = state;
        VideoProgressChanged?.Invoke(videoId, state);
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
        AppSettings settings,
        ILogger<ImportViewModel> logger)
    {
        _dbFactory = dbFactory;
        _ffmpeg = ffmpeg;
        _sceneDetection = sceneDetection;
        _asrService = asrService;
        _aiAnalysis = aiAnalysis;
        _boundaryOptimizer = boundaryOptimizer;
        _settings = settings;
        _logger = logger;
    }

    /// <summary>导入视频文件列表（全局去重 + 共享引用）。</summary>
    public async Task ImportVideosAsync(IReadOnlyList<string> paths, Guid projectId)
    {
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
                var hash = ComputeFileHash(path);
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

        if (skipped.Count > 0)
        {
            var list = string.Join("、", skipped);
            if (deduped.Count == 0)
            {
                ErrorMessage = $"所有视频均已导入过：{list}";
                IsProcessing = false;
                Phase = ImportPhase.Completed;
                Progress = 1.0;
                return;
            }
            ErrorMessage = $"已跳过重复视频：{list}";
        }

        if (!CheckDiskSpace(deduped))
        {
            IsProcessing = false;
            return;
        }

        SetProjectStatus(projectId, ProjectStatus.Importing);

        // 阶段 1：快速创建/关联视频实体。
        var videosToAnalyze = new List<Guid>();
        for (var index = 0; index < deduped.Count; index++)
        {
            ProgressDescription = $"导入第 {index + 1}/{deduped.Count} 个视频...";
            Progress = (double)index / deduped.Count * 0.2;
            try
            {
                var (videoId, needsAnalysis) = await ImportOrLinkVideoAsync(deduped[index], projectId);
                if (needsAnalysis)
                {
                    videosToAnalyze.Add(videoId);
                }
                // 每导入一个视频立即通知 UI 刷新列表（对齐 Mac 体验：视频先出现，分析进度后跟上）
                VideoListChanged?.Invoke();
            }
            catch (Exception ex)
            {
                AppendError($"导入 {Path.GetFileName(deduped[index])} 失败：{ExceptionTranslator.ToUserMessage(ex)}");
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
        }

        SetProjectStatus(projectId, ProjectStatus.Ready);
        Phase = ImportPhase.Completed;
        Progress = 1.0;
        IsProcessing = false;
    }

    // ---- 核心导入逻辑 ----

    /// <summary>导入或关联视频。返回 (videoId, needsAnalysis)。</summary>
    private async Task<(Guid VideoId, bool NeedsAnalysis)> ImportOrLinkVideoAsync(string path, Guid projectId)
    {
        Phase = ImportPhase.Copying;
        var hash = ComputeFileHash(path);

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
                return (existing.Id, needsAnalysis);
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
        return (video.Id, true);
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
            // 把视频时长传给 ASR：动态超时基于此计算。
            // 进度回调把 whisper 内部 0-1 进度映射到 ASR 阶段。
            var asrProgress = new Progress<double>(p =>
                ReportVideoStage(videoId, VideoStage.Asr, p));
            asr = await _asrService.TranscribeAsync(
                video.LocalPath, language: "zh",
                videoDurationSec: video.Duration,
                progress: asrProgress);
        }
        catch (Exception ex)
        {
            _logger.LogError("ASR 异常: {Message}", ex.Message);
            video.ErrorMessage = (video.ErrorMessage ?? string.Empty) + $"\n语音识别失败：{ExceptionTranslator.ToUserMessage(ex)}";
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
            var msg = ex.Message;
            aiErrorMessage = msg.Contains("API Key") || msg.Contains("未配置")
                ? "AI 分析跳过：请先在「设置」中配置 API Key"
                : "AI 分析失败：" + msg;
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
                StartTime = adjustedStart,
                EndTime = adjustedEnd,
                Text = finalText,
                SemanticTypes = aiSeg.EffectiveTypes.Select(NormalizeSemanticType).ToList(),
                PositionType = NormalizePositionType(aiSeg.Position),
                QualityScore = aiSeg.DataQuality.Score,
                QualityReasoning = aiSeg.DataQuality.Reasoning,
                Keywords = aiSeg.Keywords,
                VideoId = video.Id,
            };
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
