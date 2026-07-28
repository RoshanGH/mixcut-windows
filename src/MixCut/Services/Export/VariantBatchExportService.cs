using System.IO;
using Microsoft.Extensions.Logging;
using MixCut.Models;
using MixCut.Services.Dubbing;
using MixCut.Services.VideoProcessing;
using MixCut.Utilities;

namespace MixCut.Services.Export;

/// <summary>单个导出任务：原版（流/帧精确切片）或变体（dub 渲染，含烧字幕 + BGM）。对应 mac VariantExportJob。</summary>
public sealed record VariantExportJob(
    bool IsVariant, string FileName, double DurationSeconds,
    // 原版字段
    string? SourcePath, int StartFrame, int EndFrame, double Fps,
    // 变体字段
    DubSegmentSpec? Spec, int VideoWidth, int VideoHeight);

/// <summary>变体批量导出进度（纯值）。对应 mac VariantExportProgress。</summary>
public sealed record VariantExportProgress(
    int Total, int Completed, string? CurrentName, double CurrentDurationSeconds,
    IReadOnlyList<(string Name, string Error)> Failed)
{
    public double Fraction => Total > 0 ? (double)Completed / Total : 0;
}

/// <summary>从选中分镜解析出「原版 + 各已生成变体」的导出任务。对应 mac VariantExportInput。</summary>
public static class VariantExportInput
{
    /// <summary>
    /// <paramref name="segments"/> 必须已加载 <c>Video</c> 与 <c>SegmentDubs</c>（变体读 CombinationDubVariants）。
    /// #13：保留原声的分镜只出原版；否则出「(原版参与?原版:∅) + 各勾选参与组合的改写版」，都没勾则兜底出原版。
    /// </summary>
    public static IReadOnlyList<VariantExportJob> From(
        IReadOnlyList<Segment> segments, Func<Segment, int> numberProvider)
    {
        var segById = new Dictionary<Guid, Segment>();
        var dubById = new Dictionary<Guid, SegmentDub>();
        var sources = new List<SegmentExportSource>();

        foreach (var seg in segments)
        {
            var video = seg.Video;
            if (video is null || string.IsNullOrEmpty(video.LocalPath)) continue;
            segById[seg.Id] = seg;
            var stem = Path.GetFileNameWithoutExtension(video.Name);
            var variants = new List<VariantRef>();
            // #13：只展开「勾选参与组合」的改写版（CombinationDubVariants），不再全部变体无条件参与。
            foreach (var dub in seg.CombinationDubVariants)
            {
                dubById[dub.Id] = dub;
                variants.Add(new VariantRef(dub.Id, dub.TextVariantIndex));
            }
            sources.Add(new SegmentExportSource(
                seg.Id, numberProvider(seg), stem, seg.IsVoiceLocked, seg.OriginalParticipatesInCombination, variants));
        }

        var items = SegmentExportExpander.Expand(sources);

        var jobs = new List<VariantExportJob>();
        foreach (var item in items)
        {
            if (!segById.TryGetValue(item.SegmentId, out var seg) || seg.Video is null) continue;
            var video = seg.Video;
            // #12：画面源统一走 EffectivePicture（有替换用替换、否则原源）；未替换时返回原值，行为不变。
            var ep = seg.EffectivePicture;
            var fps = ep.Fps > 0 ? ep.Fps : 30;
            var dur = seg.Duration;

            if (item.DubId is { } dubId && dubById.TryGetValue(dubId, out var dub)
                && !string.IsNullOrEmpty(dub.AudioFilePath) && File.Exists(dub.AudioFilePath))
            {
                // #15 逐句字幕：优先对齐好的 CaptionLines；旧数据无对齐 → 整段一条兜底（防丢字幕）
                var capLines = dub.CaptionLines;
                if (capLines.Count == 0)
                {
                    var whole = string.IsNullOrEmpty(dub.RewrittenText) ? seg.Text : dub.RewrittenText;
                    capLines = string.IsNullOrEmpty(whole)
                        ? new List<Models.CaptionLine>()
                        : new List<Models.CaptionLine> { new(whole, 0, seg.Duration) };
                }
                var spec = new DubSegmentSpec(
                    ep.VideoPath, ep.StartFrame, ep.EndFrame, fps,
                    capLines, seg.HasHardSubtitle, seg.MaskStyleRaw, seg.MaskRect,
                    IsVoiceLocked: false, DubAudioPath: dub.AudioFilePath,
                    dub.FreezePadFrames, dub.TrailingSilence, BgmPath(video),
                    FontRatio: seg.SubtitleFontRatio > 0 ? seg.SubtitleFontRatio : null);
                jobs.Add(new VariantExportJob(true, item.FileName, dur, null, 0, 0, 0, spec, video.Width, video.Height));
            }
            else
            {
                jobs.Add(new VariantExportJob(false, item.FileName, dur,
                    ep.VideoPath, ep.StartFrame, ep.EndFrame, fps, null, 0, 0));
            }
        }
        return jobs;
    }

    private static string? BgmPath(Video video)
    {
        if (string.IsNullOrEmpty(video.ContentHash)) return null;
        var p = Path.Combine(AppPaths.StemsDirectory(video.ContentHash), "bgm.wav");
        return File.Exists(p) ? p : null;
    }
}

/// <summary>
/// 变体感知批量导出：原版走帧精确切片（<see cref="FFmpegRunner.CutSegmentFramesAsync"/>，保首帧不黑），
/// 变体走 <see cref="DubExportService.ExportSingleSegmentAsync"/>（烧字幕 + 克隆配音 + BGM）。
/// 对应 mac VariantBatchExportService。注册为单例服务。
/// </summary>
public sealed class VariantBatchExportService
{
    private readonly FFmpegRunner _ffmpeg;
    private readonly DubExportService _dubExport;
    private readonly ILogger<VariantBatchExportService> _logger;

    public VariantBatchExportService(FFmpegRunner ffmpeg, DubExportService dubExport,
        ILogger<VariantBatchExportService> logger)
    {
        _ffmpeg = ffmpeg;
        _dubExport = dubExport;
        _logger = logger;
    }

    public async Task<(int Succeeded, IReadOnlyList<(string Name, string Error)> Failed)> ExportAllAsync(
        IReadOnlyList<VariantExportJob> jobs, string outputDirectory, ExportConfig? config = null,
        Action<VariantExportProgress>? onProgress = null, CancellationToken ct = default)
    {
        config ??= new ExportConfig();
        Directory.CreateDirectory(outputDirectory);

        var succeeded = 0;
        var failed = new List<(string, string)>();
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var total = jobs.Count;

        for (var idx = 0; idx < total; idx++)
        {
            ct.ThrowIfCancellationRequested();
            var job = jobs[idx];
            onProgress?.Invoke(new VariantExportProgress(total, idx, job.FileName, job.DurationSeconds, failed));

            var outPath = UniqueDestination(outputDirectory, job.FileName, usedNames);
            try
            {
                if (job.IsVariant && job.Spec is { } spec)
                {
                    await _dubExport.ExportSingleSegmentAsync(spec, job.VideoWidth, job.VideoHeight, outPath, config, ct);
                }
                else if (job.SourcePath is { } src)
                {
                    await _ffmpeg.CutSegmentFramesAsync(
                        new FrameClip(src, job.StartFrame, job.EndFrame, job.Fps), outPath, ct);
                }
                succeeded++;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // 原始异常进日志；用户看的失败原因走翻译（配音相关异常已是人话）。
                var reason = MixCut.ViewModels.ExceptionTranslator.ToUserMessage(ex);
                failed.Add((job.FileName, reason));
                _logger.LogError(ex, "[VariantExportDiag] 变体导出失败 {File}", job.FileName);
            }

            onProgress?.Invoke(new VariantExportProgress(total, idx + 1,
                idx + 1 < total ? jobs[idx + 1].FileName : null,
                idx + 1 < total ? jobs[idx + 1].DurationSeconds : 0, failed));
            await Task.Delay(10, ct);
        }

        return (succeeded, failed);
    }

    /// <summary>同名冲突 → 加 (1)(2)（与 BatchSegmentExportService 同策略）。</summary>
    private static string UniqueDestination(string directory, string preferredName, HashSet<string> usedInBatch)
    {
        bool Exists(string name) =>
            usedInBatch.Contains(name) || File.Exists(Path.Combine(directory, name));

        if (!Exists(preferredName))
        {
            usedInBatch.Add(preferredName);
            return Path.Combine(directory, preferredName);
        }

        var stem = Path.GetFileNameWithoutExtension(preferredName);
        var ext = Path.GetExtension(preferredName);
        var counter = 1;
        var candidate = preferredName;
        while (Exists(candidate))
        {
            candidate = string.IsNullOrEmpty(ext) ? $"{stem} ({counter})" : $"{stem} ({counter}){ext}";
            counter++;
            if (counter > 9999) break;
        }
        usedInBatch.Add(candidate);
        return Path.Combine(directory, candidate);
    }
}
