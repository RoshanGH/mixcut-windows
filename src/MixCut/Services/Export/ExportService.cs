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
    int MaxHeight)
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
        var maxWidth = 0;
        var maxHeight = 0;
        foreach (var schemeSeg in ordered)
        {
            var segment = schemeSeg.Segment;
            var video = segment?.Video;
            if (segment is null || video is null || !File.Exists(video.LocalPath))
            {
                continue;
            }
            segments.Add((video.LocalPath, segment.StartTime, segment.EndTime));
            maxWidth = Math.Max(maxWidth, video.Width);
            maxHeight = Math.Max(maxHeight, video.Height);
        }

        return segments.Count == 0 ? null : new ExportInput(segments, maxWidth, maxHeight);
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

        await _ffmpeg.ConcatAsync(
            input.Segments,
            outputPath,
            resolution,
            config.Quality.Crf(config.Codec),
            config.Codec.FfmpegCodec(),
            isHardware: config.Codec.IsHardware(),
            videoBitrateKbps: config.Quality.VideoBitrateKbps(config.Codec),
            onProgress: ffmpegProgress => onProgress?.Invoke(new ExportProgress(
                ExportPhase.Encoding,
                0.05 + ffmpegProgress.Percentage * 0.9,
                $"正在编码... {(int)(ffmpegProgress.Percentage * 100)}%")),
            cancellationToken: cancellationToken);

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
