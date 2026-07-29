using System.IO;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MixCut.Data;
using MixCut.Models;
using MixCut.Services.Dubbing;
using MixCut.Services.VideoProcessing;
using MixCut.Utilities;

namespace MixCut.ViewModels;

/// <summary>
/// 分镜级 AI 配音编排（v0.5.0）。对应 mac DubbingViewModel。clone-only：只克隆原声、不做预设音色。
///
/// 数据访问全走 <see cref="IDbContextFactory{TContext}"/> 短上下文（按 Id 重载 Segment/SegmentDub 读写），
/// 与分镜库的长生命周期 context 解耦——配音数据的唯一权威在这里，检视器绑定本 VM 暴露的变体集合。
///
/// 状态<b>按 videoId 分桶</b>，不同视频互不联动（PRD 坑12）。
/// </summary>
public sealed partial class DubbingViewModel : ObservableObject
{
    private readonly IDbContextFactory<MixCutDbContext> _dbFactory;
    private readonly VocalSeparationService _vocalSep;
    private readonly VoiceCloneService _cloneService;
    private readonly CloneTtsClient _tts;
    private readonly DubAudioFinalizer _finalizer;
    private readonly ScriptRewriteService _rewriteService;
    private readonly SegmentReRecognizer _reRecognizer;
    private readonly FFmpegRunner _ffmpeg;
    private readonly AppSettings _settings;
    private readonly Services.ASR.ASRService _asr;   // 逐句字幕对齐用（对成品配音跑 whisper 测每句时间）
    private readonly ILogger<DubbingViewModel> _logger;

    public DubbingViewModel(
        IDbContextFactory<MixCutDbContext> dbFactory,
        VocalSeparationService vocalSep,
        VoiceCloneService cloneService,
        CloneTtsClient tts,
        DubAudioFinalizer finalizer,
        ScriptRewriteService rewriteService,
        SegmentReRecognizer reRecognizer,
        FFmpegRunner ffmpeg,
        AppSettings settings,
        Services.ASR.ASRService asr,
        ILogger<DubbingViewModel> logger)
    {
        _dbFactory = dbFactory;
        _vocalSep = vocalSep;
        _cloneService = cloneService;
        _tts = tts;
        _finalizer = finalizer;
        _rewriteService = rewriteService;
        _reRecognizer = reRecognizer;
        _ffmpeg = ffmpeg;
        _settings = settings;
        _asr = asr;
        _logger = logger;
    }

    // ---- 全局设置 ----

    /// <summary>台词变体数（每非锁定分镜产出几套改写版），1~5，持久化。</summary>
    public int VariantCount
    {
        get => _settings.DubVariantCount;
        set { _settings.DubVariantCount = value; OnPropertyChanged(); }
    }

    // ---- 按 videoId 分桶的状态 ----

    private readonly HashSet<Guid> _busyVideoIds = new();
    private readonly Dictionary<Guid, DubProgressInfo> _videoProgress = new();
    /// <summary>P1-5：每个正在配音的 videoId 一个取消令牌源，供「取消」按钮中断整片配音流水线。</summary>
    private readonly Dictionary<Guid, CancellationTokenSource> _videoCts = new();
    /// <summary>P2-7：不同 videoId 的配音可真并发（且 P1-6 后同一视频的 TTS 也并发），保护上面几个集合免于并发损坏。</summary>
    private readonly object _stateLock = new();

    /// <summary>
    /// 配音进度信息：阶段文案 + 完成比例。
    /// Fraction &lt; 0 表示该阶段无法估算百分比（如分离人声 / 克隆音色，时长不定），UI 走「无限滚动进度条」；
    /// Fraction ∈ [0,1] 表示可估算（改写 k/n、合成 x/total），UI 走「百分比进度条」。
    /// </summary>
    public readonly record struct DubProgressInfo(string Text, double Fraction);

    /// <summary>某视频的配音忙碌/进度变化（UI：该视频的配音设置条据此刷新）。</summary>
    public event Action<Guid>? VideoStateChanged;

    /// <summary>配音变体增删/生成后触发（用于失效 SegmentLibrary/Schemes/Export/Overview 缓存）。</summary>
    public event Action? DubsChanged;

    /// <summary>
    /// 给用户看的错误（人话，已翻译）。
    ///
    /// 本类有近 20 处 <c>ErrorMessage = "…"; return;</c> 作为唯一的失败反馈，
    /// 而这个属性**从来没有被任何 XAML 绑定过** —— 结果就是：用户点「克隆并改写配音」，
    /// Key 无效/欠费时转几秒、按钮弹回来、**屏幕上什么都不出现**，他只会以为软件坏了、再点一次。
    /// 试听、导出变体等路径同样静默。
    ///
    /// 与其给每处失败补 UI，不如让"赋值即可见"：在 setter 里直接弹 Toast，
    /// 一处改动封住全部失败路径，且以后新增的失败点自动获得反馈。
    /// </summary>
    [ObservableProperty]
    private string? _errorMessage;

    partial void OnErrorMessageChanged(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;   // 清空错误（开始新任务时）不该弹提示
        }
        try
        {
            // 可能从后台线程赋值 —— Toast 必须回 UI 线程。
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher is null || dispatcher.CheckAccess())
            {
                Views.Components.ToastService.Show(value, Views.Components.ToastStyle.Warning);
            }
            else
            {
                dispatcher.BeginInvoke(new Action(() =>
                    Views.Components.ToastService.Show(value, Views.Components.ToastStyle.Warning)));
            }
        }
        catch (Exception ex)
        {
            // Toast 宿主未就绪（启动早期 / 自测路径）时不能让提示本身把流程带崩。
            _logger.LogWarning(ex, "[DubDiag] 错误提示 Toast 弹出失败，仅记录: {Message}", value);
        }
    }

    public bool IsBusy(Guid? videoId)
    {
        if (videoId is not { } id) return false;
        lock (_stateLock) { return _busyVideoIds.Contains(id); }
    }

    public DubProgressInfo ProgressInfo(Guid? videoId)
    {
        if (videoId is not { } id) return new DubProgressInfo(string.Empty, -1);
        lock (_stateLock)
        {
            return _videoProgress.TryGetValue(id, out var p) ? p : new DubProgressInfo(string.Empty, -1);
        }
    }

    private bool BeginBusy(Guid videoId)
    {
        lock (_stateLock)
        {
            if (!_busyVideoIds.Add(videoId)) return false; // 已忙：防重复点击
            _videoCts[videoId] = new CancellationTokenSource();
        }
        VideoStateChanged?.Invoke(videoId);   // 事件在锁外触发，防处理器回调重入死锁
        return true;
    }

    private void EndBusy(Guid videoId)
    {
        lock (_stateLock)
        {
            _busyVideoIds.Remove(videoId);
            _videoProgress.Remove(videoId);
            if (_videoCts.Remove(videoId, out var cts)) cts.Dispose();
        }
        VideoStateChanged?.Invoke(videoId);
    }

    /// <summary>P1-5：取消某视频正在进行的配音流水线（分离/克隆/改写/合成）。</summary>
    public void CancelDub(Guid videoId)
    {
        CancellationTokenSource? cts;
        bool stillBusy;
        lock (_stateLock)
        {
            _videoCts.TryGetValue(videoId, out cts);
            stillBusy = _busyVideoIds.Contains(videoId);
        }
        try { cts?.Cancel(); } catch { /* 已释放则忽略 */ }
        // 仅在仍忙时上报，避免在 EndBusy 之后往 _videoProgress 重新塞入已空闲 videoId 的脏进度（泄漏）。
        if (stillBusy) SetProgress(videoId, "正在取消…");
    }

    /// <summary>取本视频配音的取消令牌（无则 None）。配音入口的 external ct 均为 default，故直接用视频 CTS。</summary>
    private CancellationToken BusyToken(Guid videoId)
    {
        lock (_stateLock) { return _videoCts.TryGetValue(videoId, out var c) ? c.Token : CancellationToken.None; }
    }

    private void SetProgress(Guid videoId, string text, double fraction = -1)
    {
        lock (_stateLock)
        {
            if (string.IsNullOrEmpty(text)) _videoProgress.Remove(videoId);
            else _videoProgress[videoId] = new DubProgressInfo(text, fraction);
        }
        VideoStateChanged?.Invoke(videoId);
    }

    /// <summary>只更新当前阶段的完成比例（保留已有文案）。供人声分离这种「文案不变、百分比在涨」的长任务用。</summary>
    private void SetProgressFraction(Guid videoId, double fraction)
    {
        lock (_stateLock)
        {
            var text = _videoProgress.TryGetValue(videoId, out var p) && !string.IsNullOrEmpty(p.Text)
                ? p.Text : "① 分离人声与背景音乐…";
            _videoProgress[videoId] = new DubProgressInfo(text, fraction);
        }
        VideoStateChanged?.Invoke(videoId);
    }

    // ---- 查询：检视器/设置条用 ----

    /// <summary>读某视频已生成的有效变体总数（用于设置条「✓ N 个变体」）。</summary>
    public async Task<int> EffectiveVariantCountAsync(Guid videoId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var dubs = await db.SegmentDubs
            .Where(d => d.Segment!.VideoId == videoId && d.AudioFilePath != null)
            .Select(d => new { d.SegmentId, d.TextVariantIndex })
            .ToListAsync();
        // 按 (分镜, 改写版) 去重计数（clone-only 下每版唯一音色）。
        return dubs.Select(d => (d.SegmentId, d.TextVariantIndex)).Distinct().Count();
    }

    /// <summary>某视频是否已克隆原声。</summary>
    public async Task<bool> IsClonedAsync(Guid videoId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var v = await db.Videos.FirstOrDefaultAsync(v => v.Id == videoId);
        return !string.IsNullOrEmpty(v?.ClonedVoiceId);
    }

    /// <summary>
    /// 批量版：一次查出多个视频的「是否已克隆 + 有效变体数」。
    ///
    /// 为什么需要它：分镜库每次重建分组（切项目 / 搜索 / 筛选 / 排序 / 删除后刷新）都会重建全部
    /// 视频组，若每组各自调 <see cref="IsClonedAsync"/> + <see cref="EffectiveVariantCountAsync"/>，
    /// 就是「视频数 × 2 次 SQL × 2 个 DbContext」。这里改成固定 2 次查询、1 个 DbContext。
    /// 返回的字典对没有配音数据的视频也有条目（IsCloned=false, VariantCount=0），调用方不用判空。
    /// </summary>
    public async Task<Dictionary<Guid, Cards.VideoGroupViewModel.DubStatusSnapshot>> LoadDubStatusAsync(
        IReadOnlyCollection<Guid> videoIds)
    {
        var result = new Dictionary<Guid, Cards.VideoGroupViewModel.DubStatusSnapshot>();
        if (videoIds.Count == 0) return result;

        await using var db = await _dbFactory.CreateDbContextAsync();

        var cloned = await db.Videos.AsNoTracking()
            .Where(v => videoIds.Contains(v.Id))
            .Select(v => new { v.Id, v.ClonedVoiceId })
            .ToListAsync();

        var dubs = await db.SegmentDubs.AsNoTracking()
            .Where(d => d.Segment != null && d.Segment.VideoId != null
                        && videoIds.Contains(d.Segment.VideoId.Value) && d.AudioFilePath != null)
            .Select(d => new { VideoId = d.Segment!.VideoId!.Value, d.SegmentId, d.TextVariantIndex })
            .ToListAsync();

        // 与单视频版口径保持一致：按 (分镜, 改写版) 去重后计数。
        var counts = dubs
            .GroupBy(d => d.VideoId)
            .ToDictionary(
                g => g.Key,
                g => g.Select(d => (d.SegmentId, d.TextVariantIndex)).Distinct().Count());

        var clonedSet = cloned
            .Where(v => !string.IsNullOrEmpty(v.ClonedVoiceId))
            .Select(v => v.Id)
            .ToHashSet();

        foreach (var id in videoIds)
        {
            result[id] = new Cards.VideoGroupViewModel.DubStatusSnapshot(
                clonedSet.Contains(id),
                counts.TryGetValue(id, out var n) ? n : 0);
        }
        return result;
    }

    /// <summary>读某分镜的全部配音变体（按改写版升序，供检视器展示）。脱离跟踪只读。</summary>
    public async Task<IReadOnlyList<SegmentDub>> LoadVariantsAsync(Guid segmentId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        return await db.SegmentDubs.AsNoTracking()
            .Where(d => d.SegmentId == segmentId)
            .OrderBy(d => d.TextVariantIndex)
            .ToListAsync();
    }

    // ---- 原声克隆 ----

    /// <summary>确保该视频已克隆出原声音色（无则分离人声并注册）。返回是否就绪。</summary>
    public async Task<bool> EnsureClonedVoiceAsync(Guid videoId, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var video = await db.Videos.FirstOrDefaultAsync(v => v.Id == videoId, ct);
        if (video is null) { ErrorMessage = "找不到视频"; return false; }

        var hash = string.IsNullOrEmpty(video.ContentHash) ? video.Id.ToString("N") : video.ContentHash;

        // 已克隆且是「高保真参考」配方注册的 → 直接复用。
        // 旧版用 24k 单声道参考注册的克隆像机器朗读，这里视为过期、强制重克隆（人声分离已缓存，仅重取
        // 6s 参考 + 重注册，几秒完成，不重跑 demucs）。对齐 mac「换 Key 自动重克隆」的失效重注册思路。
        if (!string.IsNullOrEmpty(video.ClonedVoiceId) && _settings.IsCloneHiFi(hash)) return true;
        var oldVoiceId = video.ClonedVoiceId; // 记录旧音色，重克隆成功后清理其残留（机器音）配音变体
        if (!string.IsNullOrEmpty(oldVoiceId))
            _logger.LogInformation("[DubDiag] 存量克隆音色为旧(低保真)配方，自动重克隆 video={Path}", video.LocalPath);

        if (string.IsNullOrEmpty(video.LocalPath) || !File.Exists(video.LocalPath))
        {
            ErrorMessage = "找不到原视频文件，无法克隆原声";
            return false;
        }
        var progress = new Progress<string>(msg => SetProgress(videoId, "① " + msg));
        // 人声分离的实时百分比（demucs 很慢，必须让用户看到在涨，否则像卡死）。
        var pct = new Progress<double>(f => SetProgressFraction(videoId, f));
        string? refClip = null;   // finally 兜底删除：Enroll 抛异常/取消也不泄漏参考 mp3
        try
        {
            _logger.LogInformation("[DubDiag] 开始克隆原声 video={Path}", video.LocalPath);
            SetProgress(videoId, "① 分离人声与背景音乐…");
            var stems = await _vocalSep.SeparateAsync(video.LocalPath, hash, progress, pct, ct);

            SetProgress(videoId, "① 提取克隆参考…");
            refClip = await _vocalSep.ReferenceClipAsync(stems.VocalsPath, 6, ct);

            SetProgress(videoId, "② 注册克隆音色…");
            var voiceId = await _cloneService.EnrollAsync(refClip, $"mixcut{hash[..Math.Min(8, hash.Length)]}", ct);

            video.ClonedVoiceId = voiceId;
            await db.SaveChangesAsync(ct);
            _settings.MarkCloneHiFi(hash); // 标记本次用高保真参考注册，后续复用不再重克隆

            // 重克隆（低保真→高保真）：清掉旧音色残留的配音变体，避免检视器/组合导出里
            // 新旧（机器音）变体并存重复。用户重新点「改写配音」即用新音色重新生成。
            if (!string.IsNullOrEmpty(oldVoiceId) && oldVoiceId != voiceId)
            {
                var stale = await db.SegmentDubs
                    .Where(d => d.Segment!.VideoId == videoId && d.VoiceId == oldVoiceId)
                    .ToListAsync(ct);
                if (stale.Count > 0)
                {
                    db.SegmentDubs.RemoveRange(stale);
                    await db.SaveChangesAsync(ct);
                    _logger.LogInformation("[DubDiag] 清理旧配方配音变体 {N} 条 video={Vid}", stale.Count, videoId);
                    DubsChanged?.Invoke();
                }
            }

            _logger.LogInformation("[DubDiag] 克隆成功 voiceId={VoiceId}", voiceId);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[DubDiag] 克隆失败");
            ErrorMessage = "原声克隆失败：" + ExceptionTranslator.ToUserMessage(ex);
            return false;
        }
        finally { TryDeleteTempFile(refClip); }   // P1-4：注册用完即删；失败/取消也不泄漏
    }

    /// <summary>
    /// 取该视频分离出的人声轨路径（分离已在 <see cref="EnsureClonedVoiceAsync"/> 里做过，这里命中缓存秒回）。
    /// 逐分镜克隆时用它按分镜时间区间截参考音。
    /// </summary>
    /// <summary>视频级克隆音色（逐段克隆的回退：段太短/无声/注册失败时用它）。</summary>
    private async Task<string?> GetVideoVoiceAsync(Guid videoId, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        return (await db.Videos.FirstOrDefaultAsync(v => v.Id == videoId, ct))?.ClonedVoiceId;
    }

    private async Task<string?> GetVocalsPathAsync(Guid videoId, CancellationToken ct)
    {
        string localPath, hash;
        await using (var db = await _dbFactory.CreateDbContextAsync())
        {
            var video = await db.Videos.FirstOrDefaultAsync(v => v.Id == videoId, ct);
            if (video is null || string.IsNullOrEmpty(video.LocalPath) || !File.Exists(video.LocalPath)) return null;
            localPath = video.LocalPath;
            hash = string.IsNullOrEmpty(video.ContentHash) ? video.Id.ToString("N") : video.ContentHash;
        }
        var stems = await _vocalSep.SeparateAsync(localPath, hash, null, null, ct); // 缓存命中
        return stems.VocalsPath;
    }

    /// <summary>
    /// 确保「本分镜」有自己的克隆音色 —— 用本段自己的音频当克隆参考，配音就跟本段原声一致
    /// （广告常见开头带货钩子换人，整片只取前 6s 克隆会把后段主播串成别人/别的性别）。
    /// 已克隆则复用 <see cref="Segment.ClonedVoiceId"/>；段太短/无声/注册失败则回退传入的视频级音色。
    /// </summary>
    private async Task<string> EnsureSegmentVoiceAsync(Guid segmentId, string vocalsPath, string fallbackVoiceId, CancellationToken ct)
    {
        double start, dur; string namePrefix;
        await using (var db = await _dbFactory.CreateDbContextAsync())
        {
            var seg = await db.Segments.FirstOrDefaultAsync(s => s.Id == segmentId, ct);
            if (seg is null) return fallbackVoiceId;
            if (!string.IsNullOrEmpty(seg.ClonedVoiceId)) return seg.ClonedVoiceId!; // 已逐段克隆，复用
            start = seg.StartTime;
            dur = seg.Duration;
            namePrefix = "seg" + segmentId.ToString("N")[..Math.Min(6, 32)];
        }
        // 参考取本段音频，≤6s；太短(<1.2s)难克隆 → 回退视频级音色，避免糊出怪音。
        var refDur = Math.Min(6.0, dur);
        if (refDur < 1.2) return fallbackVoiceId; // 段太短，克隆不稳，回退
        string? refClip = null;   // finally 兜底删除：Enroll 抛异常/取消也不泄漏参考 mp3
        try
        {
            refClip = await _vocalSep.SegmentReferenceClipAsync(vocalsPath, start, refDur, ct);
            var voiceId = await _cloneService.EnrollAsync(refClip, namePrefix, ct);
            await using var db = await _dbFactory.CreateDbContextAsync();
            var seg = await db.Segments.FirstOrDefaultAsync(s => s.Id == segmentId, ct);
            if (seg is not null) { seg.ClonedVoiceId = voiceId; await db.SaveChangesAsync(ct); }
            _logger.LogInformation("[DubDiag] 分镜克隆音色成功 seg={Seg} voiceId={V}", segmentId, voiceId);
            return voiceId;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("[DubDiag] 分镜克隆失败 seg={Seg}，回退视频级音色：{Msg}", segmentId, ex.Message);
            return fallbackVoiceId;
        }
        finally { TryDeleteTempFile(refClip); }
    }

    // ---- 一键改写（自动克隆 → 改写 N 套 → 合成配音）----

    public async Task RewriteAllAsync(Guid videoId, CancellationToken ct = default)
    {
        if (!BeginBusy(videoId)) return;
        var token = BusyToken(videoId);   // P1-5：受「取消」按钮控制
        try
        {
            if (!await EnsureClonedVoiceAsync(videoId, token)) return;

            string voiceId;
            List<Guid> segIds;
            await using (var db = await _dbFactory.CreateDbContextAsync())
            {
                var video = await db.Videos.FirstOrDefaultAsync(v => v.Id == videoId, token);
                voiceId = video?.ClonedVoiceId ?? string.Empty;
                segIds = await db.Segments
                    .Where(s => s.VideoId == videoId && !s.IsVoiceLocked && s.Text != "")
                    .OrderBy(s => s.StartFrame)
                    .Select(s => s.Id)
                    .ToListAsync(token);
            }
            if (string.IsNullOrEmpty(voiceId)) { ErrorMessage = "克隆原声未就绪"; return; }
            if (segIds.Count == 0) { ErrorMessage = "没有可重配的分镜"; return; }

            var n = VariantCount;
            var vocalsPath = await GetVocalsPathAsync(videoId, token); // 逐分镜克隆用（分离已缓存，秒回）
            await RewriteSegmentsAsync(videoId, segIds, voiceId, vocalsPath, n, token);

            var (ok, fail, errors) = await GenerateAllPendingAsync(videoId, segIds, token);
            DubsChanged?.Invoke();
            var produced = segIds.Count * n;
            ShowDubResult($"已生成 {produced} 个配音变体并完成合成，点开任意分镜可在右侧试听", ok, fail, errors);
        }
        catch (OperationCanceledException)
        {
            // 用户主动取消：已生成的变体保留（下次可续），静默收尾。
            DubsChanged?.Invoke();
            ShowSummary("已取消配音（已生成的部分已保留）", true);
        }
        finally
        {
            EndBusy(videoId);
        }
    }

    /// <summary>单分镜重新改写（改完原台词后只重出本分镜的 N 套改写 + 合成）。</summary>
    public async Task RewriteSegmentAsync(Guid segmentId, CancellationToken ct = default)
    {
        Guid videoId;
        string voiceId;
        await using (var db = await _dbFactory.CreateDbContextAsync())
        {
            var seg = await db.Segments.Include(s => s.Video).FirstOrDefaultAsync(s => s.Id == segmentId, ct);
            if (seg?.Video is null) { ErrorMessage = "找不到分镜"; return; }
            if (seg.IsVoiceLocked) { ErrorMessage = "该分镜保留原声，不参与配音"; return; }
            if (string.IsNullOrEmpty(seg.Text)) { ErrorMessage = "该分镜没有原台词"; return; }
            videoId = seg.Video.Id;
        }

        if (!BeginBusy(videoId)) return;
        try
        {
            if (!await EnsureClonedVoiceAsync(videoId, ct)) return;
            await using (var db = await _dbFactory.CreateDbContextAsync())
            {
                voiceId = (await db.Videos.FirstOrDefaultAsync(v => v.Id == videoId, ct))?.ClonedVoiceId ?? "";
            }
            if (string.IsNullOrEmpty(voiceId)) { ErrorMessage = "克隆原声未就绪"; return; }

            var vocalsPath = await GetVocalsPathAsync(videoId, ct);
            await RewriteSegmentsAsync(videoId, new[] { segmentId }, voiceId, vocalsPath, VariantCount, ct);
            var (ok, fail, errors) = await GenerateAllPendingAsync(videoId, new[] { segmentId }, ct);
            DubsChanged?.Invoke();
            ShowDubResult("本分镜已重新改写并生成配音", ok, fail, errors);
        }
        finally { EndBusy(videoId); }
    }

    /// <summary>手动新增一个空白改写版（用户自己写台词）。返回新版下标。</summary>
    public async Task<int?> AddManualVariantAsync(Guid segmentId, CancellationToken ct = default)
    {
        Guid videoId;
        await using (var db = await _dbFactory.CreateDbContextAsync())
        {
            var seg = await db.Segments.Include(s => s.Video).FirstOrDefaultAsync(s => s.Id == segmentId, ct);
            if (seg?.Video is null) { ErrorMessage = "找不到分镜"; return null; }
            if (seg.IsVoiceLocked) { ErrorMessage = "该分镜保留原声，不参与配音"; return null; }
            videoId = seg.Video.Id;
        }

        // 对齐 mac addManualVariant：整段标记该视频忙碌。否则首次手动添加要先克隆原声（demucs 分离很慢），
        // 但视频进度条（绑 IsDubBusy）不显示 → 用户「点了没反应」干等。BeginBusy 后克隆/分离进度可见。
        if (!BeginBusy(videoId)) { _logger.LogInformation("[DubDiag] 手动添加忽略（该视频正忙）seg={Seg}", segmentId); return null; }
        try
        {
            _logger.LogInformation("[DubDiag] 手动添加开始 seg={Seg}", segmentId);
            if (!await EnsureClonedVoiceAsync(videoId, ct))
            {
                _logger.LogWarning("[DubDiag] 手动添加失败：克隆未就绪 seg={Seg} err={Err}", segmentId, ErrorMessage);
                return null;
            }

            // 本分镜自己的克隆音色（逐段克隆）；拿不到人声轨或段太短则回退视频级音色。
            var fallback = (await GetVideoVoiceAsync(videoId, ct)) ?? "";
            var vocals = await GetVocalsPathAsync(videoId, ct);
            var voiceId = string.IsNullOrEmpty(vocals)
                ? fallback
                : await EnsureSegmentVoiceAsync(segmentId, vocals!, fallback, ct);
            if (string.IsNullOrEmpty(voiceId)) { ErrorMessage = "克隆原声未就绪"; return null; }

            await using var db = await _dbFactory.CreateDbContextAsync();
            var nextIndex = await db.SegmentDubs.Where(d => d.SegmentId == segmentId)
                .Select(d => (int?)d.TextVariantIndex).MaxAsync(ct) ?? -1;
            nextIndex += 1;
            db.SegmentDubs.Add(new SegmentDub
            {
                SegmentId = segmentId, VoiceId = voiceId, TextVariantIndex = nextIndex, RewrittenText = "",
            });
            await db.SaveChangesAsync(ct);
            DubsChanged?.Invoke();
            _logger.LogInformation("[DubDiag] 手动添加完成 seg={Seg} 新版下标={Idx}", segmentId, nextIndex);
            return nextIndex;
        }
        finally { EndBusy(videoId); }
    }

    /// <summary>编辑某改写版台词并重生成该版配音。</summary>
    public async Task UpdateVariantTextAsync(Guid segmentId, int textVariantIndex, string newText, CancellationToken ct = default)
    {
        var trimmed = newText.Trim();
        if (trimmed.Length == 0) { ErrorMessage = "台词不能为空"; return; }

        Guid videoId;
        var changed = false;
        await using (var db = await _dbFactory.CreateDbContextAsync())
        {
            var seg = await db.Segments.FirstOrDefaultAsync(s => s.Id == segmentId, ct);
            if (seg?.VideoId is null) return;
            videoId = seg.VideoId.Value;
            var dubs = await db.SegmentDubs
                .Where(d => d.SegmentId == segmentId && d.TextVariantIndex == textVariantIndex).ToListAsync(ct);
            foreach (var d in dubs.Where(d => d.RewrittenText != trimmed))
            {
                d.RewrittenText = trimmed;
                d.AudioFilePath = null;
                d.StatusRaw = nameof(SegmentDubStatus.Pending);
                changed = true;
            }
            if (changed) await db.SaveChangesAsync(ct);
        }
        if (!changed) return;

        if (!BeginBusy(videoId)) return;
        try
        {
            SetProgress(videoId, "重新合成本版配音…");
            var (ok, fail, errors) = await GenerateAllPendingAsync(videoId, new[] { segmentId }, ct);
            DubsChanged?.Invoke();
            ShowDubResult("台词已更新并重新生成配音", ok, fail, errors);
        }
        finally { EndBusy(videoId); }
    }

    /// <summary>删除某改写版（其所有音色配音 + 磁盘音频）。返回删除快照用于撤销。</summary>
    public async Task<IReadOnlyList<SegmentDub>> DeleteVariantAsync(Guid segmentId, int textVariantIndex, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var dubs = await db.SegmentDubs
            .Where(d => d.SegmentId == segmentId && d.TextVariantIndex == textVariantIndex).ToListAsync(ct);
        if (dubs.Count == 0) return Array.Empty<SegmentDub>();

        var snapshot = dubs.Select(CloneDub).ToList();
        foreach (var d in dubs)
        {
            if (playingDubId == d.Id) StopDubPlayback();
            if (!string.IsNullOrEmpty(d.AudioFilePath)) { try { File.Delete(d.AudioFilePath); } catch { } }
            db.SegmentDubs.Remove(d);
        }
        await db.SaveChangesAsync(ct);
        DubsChanged?.Invoke();
        return snapshot;
    }

    /// <summary>撤销删除：把变体快照重新写回。</summary>
    public async Task RestoreVariantsAsync(IReadOnlyList<SegmentDub> snapshot, CancellationToken ct = default)
    {
        if (snapshot.Count == 0) return;
        await using var db = await _dbFactory.CreateDbContextAsync();
        foreach (var s in snapshot)
        {
            if (!await db.SegmentDubs.AnyAsync(d => d.Id == s.Id, ct)) db.SegmentDubs.Add(CloneDub(s));
        }
        await db.SaveChangesAsync(ct);
        DubsChanged?.Invoke();
    }

    /// <summary>
    /// 设置「保留原声」（明星出镜锁定）并落库。锁定后不参与改写/配音/换字幕。
    /// 对应 mac SegmentDubControls 的 isVoiceLocked binding。
    /// </summary>
    public async Task SetVoiceLockedAsync(Guid segmentId, bool locked, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var seg = await db.Segments.FirstOrDefaultAsync(s => s.Id == segmentId, ct);
        if (seg is null || seg.IsVoiceLocked == locked) return;
        seg.IsVoiceLocked = locked;
        await db.SaveChangesAsync(ct);
        _logger.LogInformation("[DubDiag] 保留原声 seg={Seg} locked={Locked}", segmentId, locked);
        DubsChanged?.Invoke();
    }

    /// <summary>#13：设置「原版是否参与导出排列组合」并落库。对应 mac Segment.originalParticipatesInCombination。</summary>
    public async Task SetOriginalParticipatesAsync(Guid segmentId, bool value, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var seg = await db.Segments.FirstOrDefaultAsync(s => s.Id == segmentId, ct);
        if (seg is null || seg.OriginalParticipatesInCombination == value) return;
        seg.OriginalParticipatesInCombination = value;
        await db.SaveChangesAsync(ct);
        _logger.LogInformation("[DubDiag] 原版参与组合 seg={Seg} value={Value}", segmentId, value);
        DubsChanged?.Invoke();
    }

    /// <summary>
    /// #13：设置某改写版「是否参与排列组合」并落库。对同一分镜同一 TextVariantIndex 的<b>所有音色行</b>一并更新，
    /// 保证 EffectiveDubVariants 去重挑到哪条都带对参与标志。对应 mac SegmentDub.participatesInCombination。
    /// </summary>
    public async Task SetVariantParticipatesAsync(Guid segmentId, int textVariantIndex, bool value, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var rows = await db.SegmentDubs
            .Where(d => d.SegmentId == segmentId && d.TextVariantIndex == textVariantIndex)
            .ToListAsync(ct);
        var changed = false;
        foreach (var d in rows)
        {
            if (d.ParticipatesInCombination != value) { d.ParticipatesInCombination = value; changed = true; }
        }
        if (!changed) return;
        await db.SaveChangesAsync(ct);
        _logger.LogInformation("[DubDiag] 改写版参与组合 seg={Seg} idx={Idx} value={Value}", segmentId, textVariantIndex, value);
        DubsChanged?.Invoke();
    }

    /// <summary>
    /// 手动编辑分镜台词并落库（对齐 mac saveText）。只改 Text，不动边界/不动已生成的配音变体。
    /// 返回清洗后的文本（trim）。改完台词后如需按新词重配音，用户再点该分镜的「重新改写」。
    /// </summary>
    public async Task<string> UpdateSegmentTextAsync(Guid segmentId, string newText, CancellationToken ct = default)
    {
        var trimmed = (newText ?? string.Empty).Trim();
        await using var db = await _dbFactory.CreateDbContextAsync();
        var seg = await db.Segments.FirstOrDefaultAsync(s => s.Id == segmentId, ct);
        if (seg is null) return trimmed;
        if (seg.Text == trimmed) return trimmed;
        seg.Text = trimmed;
        await db.SaveChangesAsync(ct);
        _logger.LogInformation("[DubDiag] 编辑台词 seg={Seg} 字数={N}", segmentId, trimmed.Length);
        DubsChanged?.Invoke();
        return trimmed;
    }

    /// <summary>
    /// 设置字幕处理方式（直接烧录 / 模糊虚化 / 纯色遮挡）并落库。
    /// 映射到底层 HasHardSubtitle + MaskStyleRaw（见 Segment.SubtitleTreatment 计算属性）。
    /// </summary>
    public async Task SetSubtitleTreatmentAsync(Guid segmentId, SubtitleTreatment treatment, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var seg = await db.Segments.FirstOrDefaultAsync(s => s.Id == segmentId, ct);
        if (seg is null || seg.SubtitleTreatment == treatment) return;
        seg.SubtitleTreatment = treatment;
        await db.SaveChangesAsync(ct);
        _logger.LogInformation("[DubDiag] 字幕处理 seg={Seg} treatment={T}", segmentId, treatment);
        DubsChanged?.Invoke();
    }

    /// <summary>
    /// 保存遮挡框归一化坐标（拖拽松手时调一次，拖拽期间只写内存不落库——对齐 mac onCommit）。
    /// </summary>
    public async Task SetMaskRectAsync(Guid segmentId, SubtitleMaskRect rect, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var seg = await db.Segments.FirstOrDefaultAsync(s => s.Id == segmentId, ct);
        if (seg is null) return;
        seg.MaskRect = rect;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// 逐分镜字幕字号落库（issue #23 后续：卡片滑块停手时调用），并记为「新分镜默认值」偏好
    /// （对齐 mac SubtitleFontSize.rememberPreferred——之后导入的新素材出生即用户调惯的字号）。
    /// </summary>
    public async Task SetFontRatioAsync(Guid segmentId, double ratio, CancellationToken ct = default)
    {
        var clamped = SubtitleFontSize.Clamp(ratio);
        await using (var db = await _dbFactory.CreateDbContextAsync())
        {
            var seg = await db.Segments.FirstOrDefaultAsync(s => s.Id == segmentId, ct);
            if (seg is null) return;
            seg.SubtitleFontRatio = clamped;
            await db.SaveChangesAsync(ct);
        }
        _settings.SubtitleFontRatio = clamped;
        Serilog.Log.Information("[FontDiag] 分镜字号落库 seg={Seg} ratio={Ratio:F4}", segmentId, clamped);
    }

    /// <summary>
    /// 把某分镜的字幕处理（HasHardSubtitle + 样式 + 遮挡框）应用到同视频<b>其余</b>所有分镜。
    /// 返回被影响的分镜数。对应 mac applyMaskToAllSegments。
    /// </summary>
    public async Task<int> ApplyMaskToAllAsync(Guid segmentId, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var src = await db.Segments.FirstOrDefaultAsync(s => s.Id == segmentId, ct);
        if (src?.VideoId is null) return 0;
        var hasHard = src.HasHardSubtitle;
        var styleRaw = src.MaskStyleRaw;
        var rectJson = src.MaskRectJson;
        var sibs = await db.Segments
            .Where(s => s.VideoId == src.VideoId && s.Id != src.Id)
            .ToListAsync(ct);
        foreach (var s in sibs)
        {
            s.HasHardSubtitle = hasHard;
            s.MaskStyleRaw = styleRaw;
            s.MaskRectJson = rectJson;
        }
        await db.SaveChangesAsync(ct);
        _logger.LogInformation("[DubDiag] 遮挡应用到所有 src={Seg} affected={N}", segmentId, sibs.Count);
        DubsChanged?.Invoke();
        return sibs.Count;
    }

    /// <summary>
    /// 单分镜重识别（↻ 按钮）：抽该分镜区间音频 → paraformer 流式 ASR → 只替换台词文本，不动边界。
    /// 返回新识别的文本。失败抛异常（由 UI 层翻译为人话 toast）。
    /// 对应 mac Sources/MixCut/SegmentReRecognition.swift。
    /// </summary>
    public async Task<string> ReRecognizeSegmentAsync(Guid segmentId, CancellationToken ct = default)
    {
        Segment seg;
        await using (var db = await _dbFactory.CreateDbContextAsync())
        {
            seg = await db.Segments.Include(s => s.Video)
                .FirstOrDefaultAsync(s => s.Id == segmentId, ct)
                ?? throw new DubException("分镜不存在");
        }

        var videoPath = seg.Video?.LocalPath;
        if (string.IsNullOrEmpty(videoPath) || !File.Exists(videoPath))
            throw new DubException("源视频文件不存在，无法提取音频");

        // 抽区间音频 + Paraformer 精识别（与导入自动逐分镜精识别共用 SegmentReRecognizer，逻辑单一来源）。
        var transcript = await _reRecognizer.RecognizeAsync(videoPath, seg.StartTime, seg.EndTime, ct);

        // 落库：只换 Text，不动边界。
        await using (var db = await _dbFactory.CreateDbContextAsync())
        {
            var target = await db.Segments.FirstOrDefaultAsync(s => s.Id == segmentId, ct);
            if (target is not null)
            {
                target.Text = transcript;
                await db.SaveChangesAsync(ct);
            }
        }

        _logger.LogInformation("[DubDiag] 重识别 seg={Seg} 新文本={Text}",
            segmentId, transcript.Length > 60 ? transcript[..60] + "…" : transcript);
        DubsChanged?.Invoke();
        return transcript;
    }

    /// <summary>重新生成单个变体的配音（检视器 ↻ 按钮）。</summary>
    public async Task<bool> RegenerateAudioAsync(Guid dubId, CancellationToken ct = default)
    {
        Guid videoId;
        await using (var db = await _dbFactory.CreateDbContextAsync())
        {
            var dub = await db.SegmentDubs.Include(d => d.Segment).ThenInclude(s => s!.Video)
                .FirstOrDefaultAsync(d => d.Id == dubId, ct);
            videoId = dub?.Segment?.Video?.Id ?? Guid.Empty;
        }
        if (videoId == Guid.Empty) return false;
        // BeginBusy 本身在锁内原子（Add 成功才返回 true），用它的返回值作唯一权威——
        // 消除「先 IsBusy 判空再 BeginBusy」的 check-then-act 竞态（两个并发重生成都读到 not busy
        // → 都 solo=true → 其一 EndBusy 误 Dispose 掉另一条正在用的 CTS，致 ObjectDisposedException）。
        var solo = BeginBusy(videoId);
        try
        {
            var err = await GenerateAudioAsync(dubId, ct);
            if (err is not null) ErrorMessage = err;   // 单条生成失败也把真实原因透出
            DubsChanged?.Invoke();
            return err is null;
        }
        finally { if (solo) EndBusy(videoId); }
    }

    // ---- 内部：改写 + 合成 ----

    private async Task RewriteSegmentsAsync(Guid videoId, IReadOnlyList<Guid> segIds, string fallbackVoiceId, string? vocalsPath, int n, CancellationToken ct)
    {
        // ① 每个分镜先各自克隆自己的音色（配音跟本段原声一致）。无人声轨时全体回退视频级音色。
        var segVoice = new Dictionary<Guid, string>();
        for (var i = 0; i < segIds.Count; i++)
        {
            SetProgress(videoId, $"② 逐分镜克隆音色 {i + 1}/{segIds.Count}…", segIds.Count > 0 ? (double)i / segIds.Count : -1);
            segVoice[segIds[i]] = string.IsNullOrEmpty(vocalsPath)
                ? fallbackVoiceId
                : await EnsureSegmentVoiceAsync(segIds[i], vocalsPath!, fallbackVoiceId, ct);
        }

        // 清理"本段旧音色"残留的配音变体：换成逐段克隆后，用别的音色（如旧的视频级前 6s 音色）生成的变体应作废，
        // 避免检视器/组合导出里新旧并存重复。
        await using (var cdb = await _dbFactory.CreateDbContextAsync())
        {
            var dubs = await cdb.SegmentDubs.Where(d => segIds.Contains(d.SegmentId!.Value)).ToListAsync(ct);
            var toRemove = dubs.Where(d => segVoice.TryGetValue(d.SegmentId!.Value, out var v) && d.VoiceId != v).ToList();
            if (toRemove.Count > 0)
            {
                cdb.SegmentDubs.RemoveRange(toRemove);
                await cdb.SaveChangesAsync(ct);
                _logger.LogInformation("[DubDiag] 逐段克隆：清理旧音色变体 {N} 条 video={Vid}", toRemove.Count, videoId);
            }
        }

        // 取原台词/时长/关键词
        List<RewriteSegmentInput> AllInputs;
        await using (var db = await _dbFactory.CreateDbContextAsync())
        {
            var segs = await db.Segments.Where(s => segIds.Contains(s.Id)).ToListAsync(ct);
            AllInputs = segs.Select(s => new RewriteSegmentInput(
                s.Id.ToString(), s.Text, s.Duration, s.Keywords)).ToList();
        }

        for (var k = 0; k < n; k++)
        {
            SetProgress(videoId, $"③ 改写台词 第 {k + 1}/{n} 套…", n > 0 ? (double)k / n : -1);
            IReadOnlyList<RewrittenSegment> results;
            try
            {
                results = await _rewriteService.RewriteAsync(AllInputs, ScriptRewriteService.StyleForVariant(k), ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[DubDiag] 改写失败 k={K}", k);
                ErrorMessage = "改写失败：" + ExceptionTranslator.ToUserMessage(ex);
                return;
            }

            await using var db = await _dbFactory.CreateDbContextAsync();
            foreach (var r in results)
            {
                if (!Guid.TryParse(r.SegmentId, out var segId)) continue;
                var vId = segVoice.TryGetValue(segId, out var vv) ? vv : fallbackVoiceId; // 本段自己的克隆音色
                var existing = await db.SegmentDubs.FirstOrDefaultAsync(
                    d => d.SegmentId == segId && d.VoiceId == vId && d.TextVariantIndex == k, ct);
                if (existing is null)
                {
                    db.SegmentDubs.Add(new SegmentDub
                    {
                        SegmentId = segId, VoiceId = vId, TextVariantIndex = k, RewrittenText = r.RewrittenText,
                    });
                }
                else if (existing.RewrittenText != r.RewrittenText)
                {
                    existing.RewrittenText = r.RewrittenText;
                    existing.AudioFilePath = null;
                    existing.StatusRaw = nameof(SegmentDubStatus.Pending);
                }
            }
            await db.SaveChangesAsync(ct);
        }
        SetProgress(videoId, "");
    }

    private async Task<(int ok, int fail, List<string> errors)> GenerateAllPendingAsync(Guid videoId, IReadOnlyList<Guid> segIds, CancellationToken ct)
    {
        List<Guid> pending;
        await using (var db = await _dbFactory.CreateDbContextAsync())
        {
            pending = await db.SegmentDubs
                .Where(d => segIds.Contains(d.SegmentId!.Value) && d.AudioFilePath == null && d.RewrittenText != "")
                .Select(d => d.Id).ToListAsync(ct);
        }
        if (pending.Count == 0) return (0, 0, new List<string>());

        var ok = 0;
        var fail = 0;
        var done = 0;
        var errors = new List<string>();   // 去重收集真实失败原因（对齐 mac generateAllAudio errors[]）
        var errLock = new object();
        // P1-6：逐段 TTS 从串行改并发（gate=3，避免撞 DashScope 限流）。每条各自新建短 DbContext，天然线程安全；
        // 计数用 Interlocked、错误列表加锁、进度字典已线程安全（P2-7）。
        SetProgress(videoId, $"④ 合成配音 0/{pending.Count}…", 0);
        using var gate = new SemaphoreSlim(3);
        await Task.WhenAll(pending.Select(async dubId =>
        {
            await gate.WaitAsync(ct);
            try
            {
                var err = await GenerateAudioAsync(dubId, ct);
                if (err is null) { Interlocked.Increment(ref ok); }
                else { Interlocked.Increment(ref fail); lock (errLock) { if (!errors.Contains(err)) errors.Add(err); } }
            }
            finally
            {
                gate.Release();
                var d = Interlocked.Increment(ref done);
                SetProgress(videoId, $"④ 合成配音 {d}/{pending.Count}…", (double)d / pending.Count);
            }
        }));
        SetProgress(videoId, "");
        return (ok, fail, errors);
    }

    /// <summary>生成一条配音音频。成功返回 null；失败返回<b>人话</b>失败原因（供批量汇总透出真实原因）。</summary>
    private async Task<string?> GenerateAudioAsync(Guid dubId, CancellationToken ct)
    {
        // 读取生成所需上下文
        string text, voiceId, videoHash;
        Guid segmentId;
        double targetDuration, fps;
        int startFrame, endFrame, textVariantIndex;
        await using (var db = await _dbFactory.CreateDbContextAsync())
        {
            var dub = await db.SegmentDubs.Include(d => d.Segment).ThenInclude(s => s!.Video)
                .FirstOrDefaultAsync(d => d.Id == dubId, ct);
            if (dub?.Segment?.Video is null || string.IsNullOrEmpty(dub.RewrittenText))
                return "配音数据缺失或改写文本为空（请重新改写）";
            text = dub.RewrittenText;
            voiceId = dub.VoiceId;
            segmentId = dub.Segment.Id;
            targetDuration = dub.Segment.Duration;
            fps = dub.Segment.EffectiveFps > 0 ? dub.Segment.EffectiveFps : 30;
            startFrame = dub.Segment.StartFrame;
            endFrame = dub.Segment.EndFrame;
            textVariantIndex = dub.TextVariantIndex;
            videoHash = string.IsNullOrEmpty(dub.Segment.Video.ContentHash)
                ? dub.Segment.Video.Id.ToString("N") : dub.Segment.Video.ContentHash;
        }

        TtsResult? tts = null;
        try
        {
            tts = await _tts.SynthesizeRobustAsync(text, voiceId, ct);
            var finalized = await _finalizer.FinalizeAsync(tts, targetDuration, fps, videoHash, segmentId, voiceId, textVariantIndex, ct);

            await using var db = await _dbFactory.CreateDbContextAsync();
            var dub = await db.SegmentDubs.FirstOrDefaultAsync(d => d.Id == dubId, ct);
            if (dub is null) return "配音已生成但写回失败（该变体可能已被删除）";
            dub.AudioFilePath = finalized.M4aPath;
            dub.AtempoFactor = finalized.Plan.AtempoFactor;
            dub.FreezePadFrames = finalized.Plan.FreezePadFrames;
            dub.TrailingSilence = finalized.Plan.TrailingSilence;
            dub.AudioDuration = tts.RawDuration / Math.Max(0.0001, finalized.Plan.AtempoFactor);
            dub.GeneratedForStartFrame = startFrame;
            dub.GeneratedForEndFrame = endFrame;
            dub.GeneratedForTextHash = TextHash(text);
            dub.StatusRaw = nameof(SegmentDubStatus.Generated);
            await db.SaveChangesAsync(ct);
            // 配音落库后自动逐句对齐（whisper 对成品配音测每句时间；失败静默走比例兜底，不阻断配音成功）
            await AlignCaptionsAsync(dubId, ct);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[DubDiag] 配音生成失败 dub={Dub}", dubId);
            await using var db = await _dbFactory.CreateDbContextAsync();
            var dub = await db.SegmentDubs.FirstOrDefaultAsync(d => d.Id == dubId, ct);
            if (dub is not null) { dub.StatusRaw = nameof(SegmentDubStatus.Failed); await db.SaveChangesAsync(ct); }
            // 把真实原因翻成人话回传（欠费/免费额度耗尽/无效 Key…），供批量汇总展示，不再吞掉。
            return ExceptionTranslator.ToUserMessage(ex);
        }
        finally
        {
            // P1-4：TTS 原始 wav 只是 finalize 的输入，用完即删，避免每条配音在 %TEMP% 堆一个 wav。
            TryDeleteTempFile(tts?.WavPath);
        }
    }

    /// <summary>
    /// 对已生成配音的 dub 做逐句对齐，写回 CaptionLines。失败静默走比例兜底，绝不阻断配音成功。
    /// ASR 跑成品 m4a（已 atempo，时间=分镜内 0 起）。对齐 macOS DubbingViewModel.alignCaptions。
    /// 编辑器「重新自动对齐」也调它。
    /// </summary>
    public async Task AlignCaptionsAsync(Guid dubId, CancellationToken ct = default)
    {
        string audio, text;
        double segDur, audioDur;
        await using (var db = await _dbFactory.CreateDbContextAsync())
        {
            var dub = await db.SegmentDubs.Include(d => d.Segment).FirstOrDefaultAsync(d => d.Id == dubId, ct);
            if (dub?.Segment is null) return;
            if (string.IsNullOrEmpty(dub.AudioFilePath) || !System.IO.File.Exists(dub.AudioFilePath)) return;
            if (dub.Segment.Duration <= 0 || string.IsNullOrEmpty(dub.RewrittenText)) return;
            audio = dub.AudioFilePath;
            text = dub.RewrittenText;
            segDur = dub.Segment.Duration;
            audioDur = dub.AudioDuration;
        }

        var words = new List<Services.Captions.AlignWord>();
        try
        {
            var r = await _asr.TranscribeAsync(audio, cancellationToken: ct);
            words = r.Words.Select(w => new Services.Captions.AlignWord(w.Word, w.Start, w.End)).ToList();
            if (r.Duration > 0) audioDur = r.Duration;
        }
        catch (Exception ex)
        {
            _logger.LogInformation("[Caption] 逐句对齐 ASR 失败，走比例兜底: {Msg}", ex.Message);
        }

        var lines = Services.Captions.SentenceTimingAligner.Align(text, words, audioDur, segDur);
        await using (var db2 = await _dbFactory.CreateDbContextAsync())
        {
            var dub = await db2.SegmentDubs.FirstOrDefaultAsync(d => d.Id == dubId, ct);
            if (dub is not null) { dub.CaptionLines = lines; await db2.SaveChangesAsync(ct); }
        }
    }

    /// <summary>逐句字幕编辑器：读某变体当前的逐句字幕行（短上下文）。</summary>
    public async Task<List<CaptionLine>> LoadCaptionLinesAsync(Guid dubId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var dub = await db.SegmentDubs.FirstOrDefaultAsync(d => d.Id == dubId);
        return dub?.CaptionLines ?? new List<CaptionLine>();
    }

    /// <summary>逐句字幕编辑器：即时写库（每次调时间/联动分界后调用）。</summary>
    public async Task SaveCaptionLinesAsync(Guid dubId, List<CaptionLine> lines)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var dub = await db.SegmentDubs.FirstOrDefaultAsync(d => d.Id == dubId);
        if (dub is null) return;
        dub.CaptionLines = lines;
        await db.SaveChangesAsync();
    }

    /// <summary>删除临时文件（best-effort，失败忽略）。</summary>
    private static void TryDeleteTempFile(string? path)
    {
        if (string.IsNullOrEmpty(path)) return;
        try { if (System.IO.File.Exists(path)) System.IO.File.Delete(path); } catch { /* 忽略 */ }
    }

    // ---- 试听（应用内播放）----
    // 用 NAudio（项目已依赖，FfmpegFramePlayer 同款）WaveOutEvent + AudioFileReader 播 PCM wav。
    // 成片是 AAC/m4a，先用自带 ffmpeg 解成 PCM wav（不碰系统 codec，缓存在 m4a 同目录 .preview.wav）再播。
    // 注：出不出声取决于系统默认播放设备是否为真实扬声器——2026-07-02 曾因默认设备被 ThirdParty
    // 虚拟音频（ToDesk Virtual Audio）占用导致整机无声，与本代码无关。
    private NAudio.Wave.IWavePlayer? _waveOut;
    private NAudio.Wave.AudioFileReader? _audioReader;
    private Guid? playingDubId;
    private CancellationTokenSource? _playCts;

    public Guid? PlayingDubId => playingDubId;

    public async void PlayDub(SegmentDub dub)
    {
        if (playingDubId == dub.Id) { StopDubPlayback(); return; }
        StopDubPlayback();

        var m4a = dub.AudioFilePath;
        if (string.IsNullOrEmpty(m4a) || !File.Exists(m4a))
        {
            ErrorMessage = "该变体还没有生成音频，先点「生成」";
            _logger.LogWarning("[DubPlayDiag] 音频文件不存在 dub={Id} path={Path}", dub.Id, m4a);
            return;
        }

        playingDubId = dub.Id;
        try
        {
            var wav = Path.ChangeExtension(m4a, ".preview.wav");
            if (!File.Exists(wav) || File.GetLastWriteTimeUtc(wav) < File.GetLastWriteTimeUtc(m4a))
            {
                _playCts = new CancellationTokenSource();
                await _ffmpeg.RunAsync(
                    new[] { "-y", "-i", m4a, "-vn", "-ac", "2", "-ar", "44100", "-c:a", "pcm_s16le", wav },
                    timeout: TimeSpan.FromSeconds(30), cancellationToken: _playCts.Token);
            }
            if (playingDubId != dub.Id) return; // 解码期间被抢占/停止

            _audioReader = new NAudio.Wave.AudioFileReader(wav);
            _waveOut = new NAudio.Wave.WaveOutEvent();
            _waveOut.Init(_audioReader);
            var thisId = dub.Id;
            _waveOut.PlaybackStopped += (_, _) =>
                System.Windows.Application.Current?.Dispatcher.Invoke(() =>
                {
                    if (playingDubId == thisId) StopDubPlayback();
                });
            _waveOut.Play();
            _logger.LogInformation("[DubPlayDiag] 试听开始 dub={Id} dur={Dur:F1}s wav={Wav}",
                dub.Id, dub.AudioDuration, Path.GetFileName(wav));
        }
        catch (Exception ex)
        {
            playingDubId = null;
            ErrorMessage = "试听失败，请重试（音频文件可能损坏或被其它程序占用）";
            _logger.LogWarning(ex, "[DubPlayDiag] 试听失败 dub={Id}", dub.Id);
        }
    }

    public void StopDubPlayback()
    {
        try { _playCts?.Cancel(); } catch { }
        try { _waveOut?.Stop(); } catch { }
        try { _waveOut?.Dispose(); } catch { }
        try { _audioReader?.Dispose(); } catch { }
        _waveOut = null;
        _audioReader = null;
        _playCts = null;
        playingDubId = null;
    }

    /// <summary>
    /// 把生成好的配音音频导出到用户指定文件（供本地播放器试听 / 归档）。
    /// 返回实际写出的路径；未生成音频或复制失败返回 null（调用方给人话提示）。
    /// </summary>
    public string? ExportDubAudio(SegmentDub dub, string targetPath)
    {
        if (string.IsNullOrEmpty(dub.AudioFilePath) || !File.Exists(dub.AudioFilePath))
        {
            ErrorMessage = "该变体还没有生成音频，先点「生成」再导出";
            return null;
        }
        try
        {
            File.Copy(dub.AudioFilePath, targetPath, overwrite: true);
            _logger.LogInformation("[DubPlayDiag] 配音已导出 dub={Id} -> {Path}", dub.Id, targetPath);
            return targetPath;
        }
        catch (Exception ex)
        {
            ErrorMessage = "导出失败，请重试（目标位置可能无写入权限或磁盘空间不足）";
            _logger.LogWarning(ex, "[DubPlayDiag] 配音导出失败 dub={Id}", dub.Id);
            return null;
        }
    }

    // ---- helpers ----

    private static void ShowSummary(string message, bool isWarning) =>
        Views.Components.ToastService.Show(message,
            isWarning ? Views.Components.ToastStyle.Warning : Views.Components.ToastStyle.Success);

    /// <summary>
    /// 配音批量结果统一提示（对齐 mac dubFailureMessage）：全成功走成功 toast；
    /// 有失败时把<b>前 2 类真实原因</b>拼进横幅 <see cref="ErrorMessage"/>（可换行看全），
    /// toast 只给一句「N 成功/M 失败，点看原因」，不再只报干巴巴的计数让用户一头雾水。
    /// </summary>
    private void ShowDubResult(string successMessage, int ok, int fail, IReadOnlyList<string> errors)
    {
        if (fail <= 0)
        {
            ErrorMessage = null;
            ShowSummary(successMessage, false);
            return;
        }
        var reasons = errors.Take(2).ToList();
        var reasonText = reasons.Count > 0 ? string.Join("；", reasons) : "未知原因";
        var more = errors.Count > 2 ? $"（另有 {errors.Count - 2} 类原因）" : "";
        // ErrorMessage 目前无 UI 绑定 → 真实原因（欠费/额度/无效 Key…）必须直接进 toast，否则用户永远看不到。
        ErrorMessage = $"配音 {ok} 成功 / {fail} 失败：{reasonText}{more}";
        ShowSummary($"配音 {ok} 成功 / {fail} 失败：{reasonText}{more}", true);
    }

    /// <summary>文本哈希（失效追踪用）。</summary>
    public static string TextHash(string text)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(bytes, 0, 8);
    }

    private static SegmentDub CloneDub(SegmentDub d) => new()
    {
        Id = d.Id, SegmentId = d.SegmentId, VoiceId = d.VoiceId, VoiceProvider = d.VoiceProvider,
        TextVariantIndex = d.TextVariantIndex, RewrittenText = d.RewrittenText, AudioFilePath = d.AudioFilePath,
        AudioDuration = d.AudioDuration, AtempoFactor = d.AtempoFactor, FreezePadFrames = d.FreezePadFrames,
        TrailingSilence = d.TrailingSilence, GeneratedForStartFrame = d.GeneratedForStartFrame,
        GeneratedForEndFrame = d.GeneratedForEndFrame, GeneratedForTextHash = d.GeneratedForTextHash,
        StatusRaw = d.StatusRaw,
    };
}
