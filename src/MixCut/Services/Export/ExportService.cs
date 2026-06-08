using System.IO;
using Microsoft.Extensions.Logging;
using MixCut.Models;
using MixCut.Services.VideoProcessing;

namespace MixCut.Services.Export;

/// <summary>导出阶段。对应 macOS 版 ExportProgress.Phase。</summary>
public enum ExportPhase
{
    Cutting,
    Concatenating,
    Encoding,
    Completed,
    Failed,
}

/// <summary>导出进度。对应 macOS 版 ExportProgress。</summary>
public readonly record struct ExportProgress(ExportPhase Phase, double Progress, string Description);

/// <summary>导出任务的输入数据。对应 macOS 版 ExportInput。</summary>
public sealed record ExportInput(
    IReadOnlyList<(string Path, double Start, double End)> Segments,
    int MaxWidth,
    int MaxHeight,
    int SkippedCount = 0)
{
    /// <summary>从 MixScheme 提取导出数据；无有效分镜时返回 null。</summary>
    public static ExportInput? FromScheme(MixScheme scheme)
    {
        var ordered = scheme.OrderedSegments;
        if (ordered.Count == 0)
        {
            return null;
        }

        var segments = new List<(string Path, double Start, double End)>();
        var skipped = 0;
        var maxWidth = 0;
        var maxHeight = 0;
        foreach (var schemeSeg in ordered)
        {
            var segment = schemeSeg.Segment;
            var video = segment?.Video;
            if (segment is null || video is null || !File.Exists(video.LocalPath))
            {
                skipped++; // 源文件丢失的分镜：统计后由调用方告知用户，不静默少导出
                continue;
            }
            segments.Add((video.LocalPath, segment.StartTime, segment.EndTime));
            maxWidth = Math.Max(maxWidth, video.Width);
            maxHeight = Math.Max(maxHeight, video.Height);
        }

        return segments.Count == 0 ? null : new ExportInput(segments, maxWidth, maxHeight, skipped);
    }
}

/// <summary>导出异常。对应 macOS 版 ExportError。</summary>
public sealed class ExportException : Exception
{
    public ExportException(string message) : base(message)
    {
    }

    public static ExportException NoSegments() => new("方案中没有任何分镜");
    public static ExportException NoValidSegments() => new("方案中没有有效的分镜（可能视频文件不存在）");
    public static ExportException EncodingFailed(string detail) => new($"编码失败: {detail}");
}

/// <summary>视频导出服务。对应 macOS 版 ExportService。注册为单例服务。</summary>
public sealed class ExportService
{
    private readonly FFmpegRunner _ffmpeg;
    private readonly ILogger<ExportService> _logger;

    public ExportService(FFmpegRunner ffmpeg, ILogger<ExportService> logger)
    {
        _ffmpeg = ffmpeg;
        _logger = logger;
    }

    /// <summary>导出混剪方案为 MP4。</summary>
    public async Task ExportAsync(
        ExportInput input,
        string outputPath,
        ExportConfig? config = null,
        Action<ExportProgress>? onProgress = null,
        CancellationToken cancellationToken = default)
    {
        config ??= new ExportConfig();

        var resolution = ResolveResolution(config.Resolution, input.MaxWidth, input.MaxHeight);

        onProgress?.Invoke(new ExportProgress(
            ExportPhase.Encoding, 0.05, $"正在编码拼接 {input.Segments.Count} 个片段..."));

        Action<FFmpegProgress> reportEncode = ffmpegProgress => onProgress?.Invoke(new ExportProgress(
            ExportPhase.Encoding,
            0.05 + ffmpegProgress.Percentage * 0.9,
            $"正在编码... {(int)(ffmpegProgress.Percentage * 100)}%"));

        var isHardware = config.Codec.IsHardware();
        try
        {
            await _ffmpeg.ConcatAsync(
                input.Segments, outputPath, resolution,
                config.Quality.Crf(config.Codec), config.Codec.FfmpegCodec(),
                isHardware: isHardware,
                videoBitrateKbps: config.Quality.VideoBitrateKbps(config.Codec),
                onProgress: reportEncode, cancellationToken: cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw; // 用户主动取消，不兜底
        }
        catch (Exception ex) when (isHardware)
        {
            // 硬件编码器（NVENC/QSV/AMF）在部分机器上会崩溃 (0xC0000005) 或产出 0 帧
            // (exit -542398533 "received no packets") —— 成因可能是显卡驱动不兼容、消费级 NVENC
            // 会话数超限、或特殊输入像素格式。构建机无对应 GPU，这类坑测不出（见 CLAUDE.md）。
            // 自动降级到 CPU libx264 重试一次：慢但一定能导出，守住「装上即跑」底线。
            _logger.LogWarning(ex,
                "[ExportFallback] 硬件编码失败，降级 CPU libx264 重试: {Output}", outputPath);
            onProgress?.Invoke(new ExportProgress(
                ExportPhase.Encoding, 0.05, "硬件编码失败，正在用 CPU 重新编码（较慢）..."));
            await _ffmpeg.ConcatAsync(
                input.Segments, outputPath, resolution,
                config.Quality.Crf(ExportCodec.H264), ExportCodec.H264.FfmpegCodec(),
                isHardware: false,
                videoBitrateKbps: config.Quality.VideoBitrateKbps(ExportCodec.H264),
                onProgress: reportEncode, cancellationToken: cancellationToken);
            _logger.LogInformation("[ExportFallback] CPU 降级编码成功: {Output}", outputPath);
        }

        onProgress?.Invoke(new ExportProgress(ExportPhase.Completed, 1.0, "导出完成"));
        _logger.LogInformation("导出完成: {Output}", outputPath);
    }

    private static string ResolveResolution(ExportResolution res, int maxWidth, int maxHeight)
    {
        var isLandscape = maxWidth > maxHeight;
        return res switch
        {
            ExportResolution.Original =>
                $"{(maxWidth > 0 ? (maxWidth + 1) / 2 * 2 : 1080)}:" +
                $"{(maxHeight > 0 ? (maxHeight + 1) / 2 * 2 : 1920)}",
            ExportResolution.P1080 => isLandscape ? "1920:1080" : "1080:1920",
            ExportResolution.P720 => isLandscape ? "1280:720" : "720:1280",
            ExportResolution.P480 => isLandscape ? "854:480" : "480:854",
            _ => "1080:1920",
        };
    }
}
