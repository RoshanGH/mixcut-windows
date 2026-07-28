using System.Globalization;
using System.IO;
using Microsoft.Extensions.Logging;
using MixCut.Infrastructure;
using MixCut.Models;
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
    private readonly AppSettings _settings;

    public DubExportService(FFmpegRunner ffmpeg, ILogger<DubExportService> logger, AppSettings settings)
    {
        _ffmpeg = ffmpeg;
        _logger = logger;
        _settings = settings;
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

            // BGM 模式（issue #22）：拼好后整片铺所选 BGM。失败必须连带删掉已拼好的成片 ——
            // 绝不留一条「BGM 没换上」的片子混进成品（§红线：兜底只兜过程不兜结果）。
            if (config.BgmPath is { } bgm)
            {
                onProgress?.Invoke(new ExportProgress(ExportPhase.Encoding, 0.96, "正在铺背景音乐…"));
                try
                {
                    await Bgm.BgmApplier.ApplyAsync(_ffmpeg, outputPath, bgm, config.BgmVolume, ct);
                }
                catch
                {
                    try { if (File.Exists(outputPath)) File.Delete(outputPath); } catch { /* 尽力 */ }
                    throw;
                }
            }

            onProgress?.Invoke(new ExportProgress(ExportPhase.Completed, 1.0, "配音导出完成"));
        }
        finally
        {
            try { Directory.Delete(workDir, true); } catch { /* 忽略 */ }
        }
    }

    /// <summary>
    /// 渲染单个分镜为独立 mp4（分镜库「变体导出」用）。分辨率按本片自身宽高与 config 计算，不与其他片拼接。
    /// 对齐 mac DubExportService.exportSingleSegment。单片重编码起点已归零（setpts=PTS-STARTPTS），
    /// 无 concat 的 AAC priming 偏移问题，直接写 outputPath。
    /// </summary>
    public async Task ExportSingleSegmentAsync(
        DubSegmentSpec spec, int videoWidth, int videoHeight, string outputPath,
        ExportConfig? config = null, CancellationToken ct = default)
    {
        config ??= new ExportConfig();
        var (outW, outH) = Resolution(config, videoWidth, videoHeight);
        var workDir = Path.Combine(Path.GetTempPath(), $"mixcut-dubseg-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workDir);
        try
        {
            await RenderSegmentAsync(spec, 0, outW, outH, workDir, config, outputPath, ct);
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
        var captions = new List<CaptionOverlay>();
        var dubAudioInputIndex = 1;
        int? bgmInputIndex = null;

        // #15 逐句字幕：每句一张去标点 PNG，全部先 append（占 input 1..K），dub/bgm 序号随之自动后移。
        // 每句一个 CaptionOverlay(含 [start,end])，图里链式 overlay=...:enable='between(t,起,止)' 逐句依次出现。锁定段不烧。
        if (!spec.IsVoiceLocked && spec.CaptionLines.Count > 0)
        {
            // 画布宽=遮挡区宽（字幕落在遮挡带内换行，下限 120px 防逐字竖排）；
            // 字号=成片宽×比例：逐分镜设置过（issue #23 Agent font_ratio）优先，否则全局值（与 UI 预览同源）。
            var canvasW = Math.Max(120, maskPixel.Width);
            var fontSize = (float)SubtitleFontSize.FontSize(outW, spec.FontRatio ?? _settings.SubtitleFontRatio);
            var withBackdrop = mode != SubtitleMaskMode.Solid;
            for (var li = 0; li < spec.CaptionLines.Count; li++)
            {
                var line = spec.CaptionLines[li];
                var burnText = CaptionRenderer.StripPunctuation(line.Text);
                if (string.IsNullOrEmpty(burnText)) continue;   // 去标点后为空的句子不出图
                var pngPath = Path.Combine(workDir, $"cap_{index:D3}_{li:D2}.png");
                var img = CaptionRenderer.RenderToFile(burnText, canvasW, withBackdrop, fontSize, pngPath);
                var origin = CaptionLayout.OverlayOrigin(outW, outH, spec.MaskRect, img.PixelWidth, img.PixelHeight);
                extraInputs.Add(pngPath);
                captions.Add(new CaptionOverlay(extraInputs.Count, origin.X, origin.Y, line.Start, line.End));
            }
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

        // BGM 替换模式（issue #22）：原声段改用整轨 vocals.wav 切片。缺产物必须让该条成片失败
        // （§红线：禁止静默回退原混音轨 —— 那等于给用户一条 BGM 没去掉的片子）。
        int? vocalsInputIndex = null;
        if (keepOriginalAudio && spec.Vocals is { } vs)
        {
            var vocalsPath = string.IsNullOrEmpty(vs.SourceVideoHash)
                ? null
                : Path.Combine(AppPaths.StemsDirectory(vs.SourceVideoHash), "vocals.wav");
            if (vocalsPath is null || !File.Exists(vocalsPath))
            {
                throw new DubException(
                    $"人声分离产物缺失（{Path.GetFileName(vs.SourceVideoPath)}），无法去除原 BGM。" +
                    "请重新导出（会自动重新分离）");
            }
            extraInputs.Add(vocalsPath);
            vocalsInputIndex = extraInputs.Count;
        }

        var graph = DubSegmentGraphBuilder.Build(
            mode, spec.StartFrame, spec.EndFrame, spec.Fps, outW, outH, maskPixel,
            captions, keepOriginalAudio, dubAudioInputIndex,
            spec.FreezePadFrames, spec.TrailingSilence, bgmInputIndex,
            vocalsInputIndex, spec.Vocals?.Start ?? 0, spec.Vocals?.End ?? 0);

        var encoder = HardwareEncoderProbe.H264Hardware ?? "libx264";
        var bitrate = config.Quality.VideoBitrateKbps(ExportCodec.H264Hardware);

        var args = new List<string> { "-y", "-i", spec.VideoPath };
        foreach (var input in extraInputs) { args.Add("-i"); args.Add(input); }
        args.Add("-filter_complex"); args.Add(graph.FilterComplex);
        args.Add("-map"); args.Add(graph.VideoMapLabel);
        args.Add("-map"); args.Add(graph.AudioMapLabel);
        // 中间片编码参数必须完全一致，阶段二才能 -c copy。
        // -maxrate 封顶突发码率（对齐 mac，避免硬件编码码率飙高），上限取 2× 目标码率。
        args.AddRange(new[] { "-c:v", encoder, "-b:v", $"{bitrate}k", "-maxrate", $"{bitrate * 2}k", "-pix_fmt", "yuv420p", "-tag:v", "avc1" });
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
