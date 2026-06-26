using System.Globalization;
using System.IO;
using Microsoft.Extensions.Logging;
using MixCut.Infrastructure;
using MixCut.Services.Export;
using MixCut.Services.VideoProcessing;
using MixCut.Utilities;

namespace MixCut.Services.Dubbing;

/// <summary>
/// 配音组合导出（两阶段）。对应 mac DubExportService。现有普通导出 <see cref="ExportService"/> 不受影响。
/// 逐分镜渲染中间片（统一编码参数 + <b>-ac 2</b> 统一立体声）→ concat 无损拼接 → 首帧时间归零。
/// </summary>
public sealed class DubExportService
{
    private readonly FFmpegRunner _ffmpeg;
    private readonly ILogger<DubExportService> _logger;

    public DubExportService(FFmpegRunner ffmpeg, ILogger<DubExportService> logger)
    {
        _ffmpeg = ffmpeg;
        _logger = logger;
    }

    /// <summary>导出一条配音成片到 <paramref name="outputPath"/>。</summary>
    public async Task ExportAsync(
        DubExportInput input, string outputPath, ExportConfig? config = null,
        Action<ExportProgress>? onProgress = null, CancellationToken ct = default)
    {
        config ??= new ExportConfig();
        if (input.Segments.Count == 0) throw new DubException("没有可导出的分镜");

        var (outW, outH) = Resolution(config, input.MaxWidth, input.MaxHeight);
        var workDir = Path.Combine(Path.GetTempPath(), $"mixcut-dubexport-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workDir);
        try
        {
            var intermediates = new List<string>();
            var total = input.Segments.Count;
            for (var i = 0; i < total; i++)
            {
                onProgress?.Invoke(new ExportProgress(ExportPhase.Cutting, (double)i / total * 0.85, $"处理分镜 {i + 1}/{total}…"));
                var inter = Path.Combine(workDir, $"seg_{i:D3}.mp4");
                await RenderSegmentAsync(input.Segments[i], i, outW, outH, workDir, config, inter, ct);
                intermediates.Add(inter);
            }

            onProgress?.Invoke(new ExportProgress(ExportPhase.Concatenating, 0.9, "拼接成片…"));
            await ConcatCopyAsync(intermediates, workDir, outputPath, ct);

            onProgress?.Invoke(new ExportProgress(ExportPhase.Completed, 1.0, "配音导出完成"));
        }
        finally
        {
            try { Directory.Delete(workDir, true); } catch { /* 忽略 */ }
        }
    }

    // ---- 单分镜中间片 ----

    private async Task RenderSegmentAsync(DubSegmentSpec spec, int index, int outW, int outH,
        string workDir, ExportConfig config, string outputPath, CancellationToken ct)
    {
        var mode = spec.IsVoiceLocked
            ? SubtitleMaskMode.None
            : SubtitleMaskModeExtensions.From(spec.HasHardSubtitle, spec.MaskStyleRaw);
        var maskPixel = PixelRect.From(spec.MaskRect, outW, outH);

        var keepOriginalAudio = spec.IsVoiceLocked || spec.DubAudioPath is null;

        // 输入按追加顺序编号：input 0 = 源视频，extraInputs 依次 input 1..N。
        var extraInputs = new List<string>();
        (int X, int Y)? captionOrigin = null;
        var captionInputIndex = 1;
        var dubAudioInputIndex = 1;
        int? bgmInputIndex = null;

        if (!spec.IsVoiceLocked && !string.IsNullOrEmpty(spec.CaptionText))
        {
            var canvasW = Math.Max(2, (int)(outW * 0.9));
            var withBackdrop = mode != SubtitleMaskMode.Solid;
            var pngPath = Path.Combine(workDir, $"cap_{index:D3}.png");
            var img = CaptionRenderer.RenderToFile(spec.CaptionText, canvasW, withBackdrop, pngPath);
            captionOrigin = CaptionLayout.OverlayOrigin(outW, outH, spec.MaskRect, img.PixelWidth, img.PixelHeight);
            extraInputs.Add(pngPath);
            captionInputIndex = extraInputs.Count; // 1-based
        }

        if (!keepOriginalAudio && spec.DubAudioPath is { } dubPath)
        {
            extraInputs.Add(dubPath);
            dubAudioInputIndex = extraInputs.Count;
            if (spec.BgmAudioPath is { } bgmPath && File.Exists(bgmPath))
            {
                extraInputs.Add(bgmPath);
                bgmInputIndex = extraInputs.Count;
            }
        }

        var graph = DubSegmentGraphBuilder.Build(
            mode, spec.StartFrame, spec.EndFrame, spec.Fps, outW, outH, maskPixel,
            captionOrigin, captionInputIndex, keepOriginalAudio, dubAudioInputIndex,
            spec.FreezePadFrames, spec.TrailingSilence, bgmInputIndex);

        var encoder = HardwareEncoderProbe.H264Hardware ?? "libx264";
        var bitrate = config.Quality.VideoBitrateKbps(ExportCodec.H264Hardware);

        var args = new List<string> { "-y", "-i", spec.VideoPath };
        foreach (var input in extraInputs) { args.Add("-i"); args.Add(input); }
        args.Add("-filter_complex"); args.Add(graph.FilterComplex);
        args.Add("-map"); args.Add(graph.VideoMapLabel);
        args.Add("-map"); args.Add(graph.AudioMapLabel);
        // 中间片编码参数必须完全一致，阶段二才能 -c copy。
        args.AddRange(new[] { "-c:v", encoder, "-b:v", $"{bitrate}k", "-pix_fmt", "yuv420p", "-tag:v", "avc1" });
        // -ac 2：所有中间片统一立体声（坑1：否则原声段立体声/配音段单声道，concat 后声道不符的段静音）。
        args.AddRange(new[] { "-c:a", "aac", "-b:a", "192k", "-ar", "44100", "-ac", "2", "-movflags", "+faststart", outputPath });

        // 超时按分镜时长动态算（编码 + loudnorm），下限 3 分钟。
        var durSec = Math.Max(0.1, (spec.EndFrame - spec.StartFrame) / Math.Max(1.0, spec.Fps));
        var timeout = TimeSpan.FromSeconds(Math.Max(180, durSec * 60));
        await _ffmpeg.RunAsync(args, timeout: timeout, cancellationToken: ct);
    }

    // ---- 阶段二：concat 拼接 + 首帧归零 ----

    private async Task ConcatCopyAsync(IReadOnlyList<string> paths, string workDir, string outputPath, CancellationToken ct)
    {
        var listPath = Path.Combine(workDir, "list.txt");
        await File.WriteAllTextAsync(listPath,
            string.Join("\n", paths.Select(p => $"file '{p.Replace("'", "'\\''")}'")) + "\n", ct);

        var joined = Path.Combine(workDir, "joined.mp4");
        await _ffmpeg.RunAsync(
            new[] { "-y", "-f", "concat", "-safe", "0", "-i", listPath, "-c", "copy", "-movflags", "+faststart", joined },
            timeout: TimeSpan.FromMinutes(10), cancellationToken: ct);

        // concat 把首段 AAC priming 延迟转成视频起始偏移(~0.023s) → t=0 无帧 → 平台截首帧做封面得黑图。
        // 平移视频起始时间回 0。（坑2：必须用播放器/系统取帧在 t=0 验证，不能只用 ffmpeg 抽帧。）
        var startTime = await ProbeStartTimeAsync(joined, ct);
        if (startTime > 0.001)
        {
            var offset = (-startTime).ToString("F6", CultureInfo.InvariantCulture);
            await _ffmpeg.RunAsync(
                new[] { "-y", "-i", joined, "-c", "copy", "-output_ts_offset", offset, "-movflags", "+faststart", outputPath },
                timeout: TimeSpan.FromMinutes(5), cancellationToken: ct);
            _logger.LogInformation("[DubDiag] 导出首帧归零 start_time={Start:F4}s → 0", startTime);
        }
        else
        {
            if (File.Exists(outputPath)) File.Delete(outputPath);
            File.Move(joined, outputPath);
        }
    }

    private async Task<double> ProbeStartTimeAsync(string path, CancellationToken ct)
    {
        try
        {
            var outp = await _ffmpeg.RunProbeAsync(
                new[] { "-v", "error", "-select_streams", "v:0", "-show_entries", "stream=start_time", "-of", "csv=p=0", path },
                cancellationToken: ct);
            return double.TryParse(outp.Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : 0;
        }
        catch { return 0; }
    }

    // ---- 分辨率（9:16，偶数）----

    private static (int, int) Resolution(ExportConfig config, int maxWidth, int maxHeight) => config.Resolution switch
    {
        ExportResolution.Original => (maxWidth > 0 ? (maxWidth + 1) / 2 * 2 : 1080, maxHeight > 0 ? (maxHeight + 1) / 2 * 2 : 1920),
        ExportResolution.P1080 => (1080, 1920),
        ExportResolution.P720 => (720, 1280),
        ExportResolution.P480 => (480, 854),
        _ => (1080, 1920),
    };
}
