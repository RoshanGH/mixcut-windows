using System.IO;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MixCut.Data;
using MixCut.Models;
using MixCut.Services.Dubbing;
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
    private readonly AppSettings _settings;
    private readonly ILogger<DubbingViewModel> _logger;

    public DubbingViewModel(
        IDbContextFactory<MixCutDbContext> dbFactory,
        VocalSeparationService vocalSep,
        VoiceCloneService cloneService,
        CloneTtsClient tts,
        DubAudioFinalizer finalizer,
        ScriptRewriteService rewriteService,
        AppSettings settings,
        ILogger<DubbingViewModel> logger)
    {
        _dbFactory = dbFactory;
        _vocalSep = vocalSep;
        _cloneService = cloneService;
        _tts = tts;
        _finalizer = finalizer;
        _rewriteService = rewriteService;
        _settings = settings;
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
    private readonly Dictionary<Guid, string> _videoProgress = new();

    /// <summary>某视频的配音忙碌/进度变化（UI：该视频的配音设置条据此刷新）。</summary>
    public event Action<Guid>? VideoStateChanged;

    /// <summary>配音变体增删/生成后触发（用于失效 SegmentLibrary/Schemes/Export/Overview 缓存）。</summary>
    public event Action? DubsChanged;

    /// <summary>给用户看的错误（人话，已翻译）。</summary>
    [ObservableProperty]
    private string? _errorMessage;

    public bool IsBusy(Guid? videoId) => videoId is { } id && _busyVideoIds.Contains(id);

    public string ProgressText(Guid? videoId) =>
        videoId is { } id && _videoProgress.TryGetValue(id, out var t) ? t : string.Empty;

    private bool BeginBusy(Guid videoId)
    {
        if (!_busyVideoIds.Add(videoId)) return false; // 已忙：防重复点击
        VideoStateChanged?.Invoke(videoId);
        return true;
    }

    private void EndBusy(Guid videoId)
    {
        _busyVideoIds.Remove(videoId);
        _videoProgress.Remove(videoId);
        VideoStateChanged?.Invoke(videoId);
    }

    private void SetProgress(Guid videoId, string text)
    {
        _videoProgress[videoId] = text;
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
        if (!string.IsNullOrEmpty(video.ClonedVoiceId)) return true;

        if (string.IsNullOrEmpty(video.LocalPath) || !File.Exists(video.LocalPath))
        {
            ErrorMessage = "找不到原视频文件，无法克隆原声";
            return false;
        }

        var hash = string.IsNullOrEmpty(video.ContentHash) ? video.Id.ToString("N") : video.ContentHash;
        var progress = new Progress<string>(msg => SetProgress(videoId, msg));
        try
        {
            _logger.LogInformation("[DubDiag] 开始克隆原声 video={Path}", video.LocalPath);
            SetProgress(videoId, "分离人声…");
            var stems = await _vocalSep.SeparateAsync(video.LocalPath, hash, progress, ct);

            SetProgress(videoId, "提取克隆参考…");
            var refClip = await _vocalSep.ReferenceClipAsync(stems.VocalsPath, 6, ct);

            SetProgress(videoId, "注册克隆音色…");
            var voiceId = await _cloneService.EnrollAsync(refClip, $"mixcut{hash[..Math.Min(8, hash.Length)]}", ct);

            video.ClonedVoiceId = voiceId;
            await db.SaveChangesAsync(ct);
            _logger.LogInformation("[DubDiag] 克隆成功 voiceId={VoiceId}", voiceId);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[DubDiag] 克隆失败");
            ErrorMessage = $"原声克隆失败：{ex.Message}";
            return false;
        }
    }

    // ---- 一键改写（自动克隆 → 改写 N 套 → 合成配音）----

    public async Task RewriteAllAsync(Guid videoId, CancellationToken ct = default)
    {
        if (!BeginBusy(videoId)) return;
        try
        {
            if (!await EnsureClonedVoiceAsync(videoId, ct)) return;

            string voiceId;
            List<Guid> segIds;
            await using (var db = await _dbFactory.CreateDbContextAsync())
            {
                var video = await db.Videos.FirstOrDefaultAsync(v => v.Id == videoId, ct);
                voiceId = video?.ClonedVoiceId ?? string.Empty;
                segIds = await db.Segments
                    .Where(s => s.VideoId == videoId && !s.IsVoiceLocked && s.Text != "")
                    .OrderBy(s => s.StartFrame)
                    .Select(s => s.Id)
                    .ToListAsync(ct);
            }
            if (string.IsNullOrEmpty(voiceId)) { ErrorMessage = "克隆原声未就绪"; return; }
            if (segIds.Count == 0) { ErrorMessage = "没有可重配的分镜"; return; }

            var n = VariantCount;
            await RewriteSegmentsAsync(videoId, segIds, voiceId, n, ct);

            var (ok, fail) = await GenerateAllPendingAsync(videoId, segIds, ct);
            DubsChanged?.Invoke();
            var produced = segIds.Count * n;
            ShowSummary(fail > 0
                ? $"已生成 {produced} 个变体，配音合成 {ok} 成功 / {fail} 失败，失败项可在右侧点 ↻ 重试"
                : $"已生成 {produced} 个配音变体并完成合成，点开任意分镜可在右侧试听", fail > 0);
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

            await RewriteSegmentsAsync(videoId, new[] { segmentId }, voiceId, VariantCount, ct);
            var (ok, fail) = await GenerateAllPendingAsync(videoId, new[] { segmentId }, ct);
            DubsChanged?.Invoke();
            ShowSummary(fail > 0 ? $"本分镜已重写，配音 {ok} 成功 / {fail} 失败" : "本分镜已重新改写并生成配音", fail > 0);
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
        if (!await EnsureClonedVoiceAsync(videoId, ct)) return null;

        await using (var db = await _dbFactory.CreateDbContextAsync())
        {
            var voiceId = (await db.Videos.FirstOrDefaultAsync(v => v.Id == videoId, ct))?.ClonedVoiceId ?? "";
            if (string.IsNullOrEmpty(voiceId)) { ErrorMessage = "克隆原声未就绪"; return null; }
            var nextIndex = await db.SegmentDubs.Where(d => d.SegmentId == segmentId)
                .Select(d => (int?)d.TextVariantIndex).MaxAsync(ct) ?? -1;
            nextIndex += 1;
            db.SegmentDubs.Add(new SegmentDub
            {
                SegmentId = segmentId, VoiceId = voiceId, TextVariantIndex = nextIndex, RewrittenText = "",
            });
            await db.SaveChangesAsync(ct);
            DubsChanged?.Invoke();
            return nextIndex;
        }
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
            var (ok, fail) = await GenerateAllPendingAsync(videoId, new[] { segmentId }, ct);
            DubsChanged?.Invoke();
            ShowSummary(fail > 0 ? $"台词已更新，配音 {ok} 成功 / {fail} 失败" : "台词已更新并重新生成配音", fail > 0);
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
        var solo = !IsBusy(videoId);
        if (solo) BeginBusy(videoId);
        try
        {
            var ok = await GenerateAudioAsync(dubId, ct);
            DubsChanged?.Invoke();
            return ok;
        }
        finally { if (solo) EndBusy(videoId); }
    }

    // ---- 内部：改写 + 合成 ----

    private async Task RewriteSegmentsAsync(Guid videoId, IReadOnlyList<Guid> segIds, string voiceId, int n, CancellationToken ct)
    {
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
            SetProgress(videoId, $"改写第 {k + 1}/{n} 套…");
            IReadOnlyList<RewrittenSegment> results;
            try
            {
                results = await _rewriteService.RewriteAsync(AllInputs, ScriptRewriteService.StyleForVariant(k), ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[DubDiag] 改写失败 k={K}", k);
                ErrorMessage = $"改写失败：{ex.Message}";
                return;
            }

            await using var db = await _dbFactory.CreateDbContextAsync();
            foreach (var r in results)
            {
                if (!Guid.TryParse(r.SegmentId, out var segId)) continue;
                var existing = await db.SegmentDubs.FirstOrDefaultAsync(
                    d => d.SegmentId == segId && d.VoiceId == voiceId && d.TextVariantIndex == k, ct);
                if (existing is null)
                {
                    db.SegmentDubs.Add(new SegmentDub
                    {
                        SegmentId = segId, VoiceId = voiceId, TextVariantIndex = k, RewrittenText = r.RewrittenText,
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

    private async Task<(int ok, int fail)> GenerateAllPendingAsync(Guid videoId, IReadOnlyList<Guid> segIds, CancellationToken ct)
    {
        List<Guid> pending;
        await using (var db = await _dbFactory.CreateDbContextAsync())
        {
            pending = await db.SegmentDubs
                .Where(d => segIds.Contains(d.SegmentId!.Value) && d.AudioFilePath == null && d.RewrittenText != "")
                .Select(d => d.Id).ToListAsync(ct);
        }
        if (pending.Count == 0) return (0, 0);

        int ok = 0, fail = 0;
        foreach (var dubId in pending)
        {
            SetProgress(videoId, $"合成配音 {ok + fail + 1}/{pending.Count}…");
            if (await GenerateAudioAsync(dubId, ct)) ok++; else fail++;
        }
        SetProgress(videoId, "");
        return (ok, fail);
    }

    private async Task<bool> GenerateAudioAsync(Guid dubId, CancellationToken ct)
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
            if (dub?.Segment?.Video is null || string.IsNullOrEmpty(dub.RewrittenText)) return false;
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

        try
        {
            var tts = await _tts.SynthesizeRobustAsync(text, voiceId, ct);
            var finalized = await _finalizer.FinalizeAsync(tts, targetDuration, fps, videoHash, segmentId, voiceId, textVariantIndex, ct);

            await using var db = await _dbFactory.CreateDbContextAsync();
            var dub = await db.SegmentDubs.FirstOrDefaultAsync(d => d.Id == dubId, ct);
            if (dub is null) return false;
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
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[DubDiag] 配音生成失败 dub={Dub}", dubId);
            await using var db = await _dbFactory.CreateDbContextAsync();
            var dub = await db.SegmentDubs.FirstOrDefaultAsync(d => d.Id == dubId, ct);
            if (dub is not null) { dub.StatusRaw = nameof(SegmentDubStatus.Failed); await db.SaveChangesAsync(ct); }
            return false;
        }
    }

    // ---- 试听（对齐后的成片音频，用 MediaPlayer 播 m4a）----

    private MediaPlayer? _audioPlayer;
    private Guid? playingDubId;

    public Guid? PlayingDubId => playingDubId;

    public void PlayDub(SegmentDub dub)
    {
        if (playingDubId == dub.Id) { StopDubPlayback(); return; }
        if (string.IsNullOrEmpty(dub.AudioFilePath) || !File.Exists(dub.AudioFilePath))
        {
            ErrorMessage = "音频文件不存在，请重新生成该变体";
            return;
        }
        StopDubPlayback();
        try
        {
            _audioPlayer = new MediaPlayer();
            _audioPlayer.Open(new Uri(dub.AudioFilePath));
            _audioPlayer.MediaEnded += (_, _) => StopDubPlayback();
            _audioPlayer.Play();
            playingDubId = dub.Id;
        }
        catch (Exception ex)
        {
            ErrorMessage = $"播放失败：{ex.Message}";
        }
    }

    public void StopDubPlayback()
    {
        try { _audioPlayer?.Stop(); _audioPlayer?.Close(); } catch { }
        _audioPlayer = null;
        playingDubId = null;
    }

    // ---- helpers ----

    private static void ShowSummary(string message, bool isWarning) =>
        Views.Components.ToastService.Show(message,
            isWarning ? Views.Components.ToastStyle.Warning : Views.Components.ToastStyle.Success);

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
