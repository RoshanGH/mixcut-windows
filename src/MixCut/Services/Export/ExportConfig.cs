using System.Globalization;
using MixCut.Infrastructure;

namespace MixCut.Services.Export;

/// <summary>导出分辨率。对应 macOS 版 ExportConfig.ExportResolution。</summary>
public enum ExportResolution
{
    Original,
    P1080,
    P720,
    P480,
}

/// <summary>
/// 导出编码器。对应 macOS 版 ExportConfig.ExportCodec。
/// 硬件加速默认（5-10× 软件编码速度）；macOS 用 VideoToolbox，Windows 用 NVENC/QSV/AMF/MF 自动选。
/// </summary>
public enum ExportCodec
{
    H264Hardware,   // 硬件 H.264 编码（默认）
    H265Hardware,   // 硬件 H.265 编码
    H264,           // CPU 软件 libx264（最佳画质）
    H265,           // CPU 软件 libx265
}

/// <summary>导出质量。对应 macOS 版 ExportConfig.ExportQuality。</summary>
public enum ExportQuality
{
    Low,
    Medium,
    High,
    Lossless,
}

public static class ExportEnumExtensions
{
    public static string Label(this ExportResolution r) => r switch
    {
        ExportResolution.Original => "原始分辨率",
        ExportResolution.P1080 => "1080p",
        ExportResolution.P720 => "720p",
        ExportResolution.P480 => "480p",
        _ => r.ToString(),
    };

    /// <summary>导出分辨率的 FFmpeg scale 滤镜（null = 不缩放）。</summary>
    public static string? ScaleFilter(this ExportResolution r) => r switch
    {
        ExportResolution.Original => null,
        ExportResolution.P1080 => "scale=-2:1080",
        ExportResolution.P720 => "scale=-2:720",
        ExportResolution.P480 => "scale=-2:480",
        _ => null,
    };

    public static string Label(this ExportCodec c) => c switch
    {
        ExportCodec.H264Hardware => "H.264（硬件加速 · 推荐）",
        ExportCodec.H265Hardware => "H.265 / HEVC（硬件加速）",
        ExportCodec.H264 => "H.264（CPU 软件编码 · 最佳画质）",
        ExportCodec.H265 => "H.265 / HEVC（CPU 软件编码）",
        _ => c.ToString(),
    };

    /// <summary>
    /// 返回 ffmpeg 编码器名。硬件编码器从 <see cref="HardwareEncoderProbe"/> 拿；
    /// 若硬件未探测到，自动回退到对应软件编码器（libx264 / libx265）。
    /// </summary>
    public static string FfmpegCodec(this ExportCodec c) => c switch
    {
        ExportCodec.H264 => "libx264",
        ExportCodec.H265 => "libx265",
        ExportCodec.H264Hardware => HardwareEncoderProbe.H264Hardware ?? "libx264",
        ExportCodec.H265Hardware => HardwareEncoderProbe.H265Hardware ?? "libx265",
        _ => "libx264",
    };

    /// <summary>是否走硬件编码（影响 FFmpeg 参数：硬件用 -b:v，软件用 -crf）。</summary>
    public static bool IsHardware(this ExportCodec c)
    {
        return c switch
        {
            ExportCodec.H264Hardware => HardwareEncoderProbe.H264Hardware is not null,
            ExportCodec.H265Hardware => HardwareEncoderProbe.H265Hardware is not null,
            _ => false,
        };
    }

    public static string Label(this ExportQuality q) => q switch
    {
        ExportQuality.Low => "低质量（文件小）",
        ExportQuality.Medium => "中等质量",
        ExportQuality.High => "高质量",
        ExportQuality.Lossless => "无损",
        _ => q.ToString(),
    };

    /// <summary>根据编码器返回合适的 CRF 值（H.265 同等视觉质量 CRF 更高）。仅用于软件编码。</summary>
    public static int Crf(this ExportQuality q, ExportCodec codec) => (q, codec) switch
    {
        (ExportQuality.Low, ExportCodec.H264) => 28,
        (ExportQuality.Low, ExportCodec.H265) => 30,
        (ExportQuality.Low, ExportCodec.H264Hardware) => 28,
        (ExportQuality.Low, ExportCodec.H265Hardware) => 30,
        (ExportQuality.Medium, ExportCodec.H264) => 23,
        (ExportQuality.Medium, ExportCodec.H265) => 26,
        (ExportQuality.Medium, ExportCodec.H264Hardware) => 23,
        (ExportQuality.Medium, ExportCodec.H265Hardware) => 26,
        (ExportQuality.High, ExportCodec.H264) => 18,
        (ExportQuality.High, ExportCodec.H265) => 22,
        (ExportQuality.High, ExportCodec.H264Hardware) => 18,
        (ExportQuality.High, ExportCodec.H265Hardware) => 22,
        (ExportQuality.Lossless, _) => 0,
        _ => 23,
    };

    /// <summary>
    /// 硬件编码用的目标比特率 (kbps，按 1080p 估算)。
    /// 硬件编码器不支持 CRF，必须用 -b:v + -maxrate 控制画质。
    /// </summary>
    public static int VideoBitrateKbps(this ExportQuality q, ExportCodec codec) => (q, codec) switch
    {
        (ExportQuality.Low, ExportCodec.H264Hardware) => 3_000,
        (ExportQuality.Medium, ExportCodec.H264Hardware) => 6_000,
        (ExportQuality.High, ExportCodec.H264Hardware) => 10_000,
        (ExportQuality.Low, ExportCodec.H265Hardware) => 1_800,
        (ExportQuality.Medium, ExportCodec.H265Hardware) => 3_500,
        (ExportQuality.High, ExportCodec.H265Hardware) => 6_500,
        (ExportQuality.Lossless, _) => 50_000,  // 硬件不支持真无损，给极高码率近似
        _ => 8_000,
    };
}

/// <summary>导出配置。对应 macOS 版 ExportConfig。</summary>
public sealed class ExportConfig
{
    public ExportResolution Resolution { get; set; } = ExportResolution.Original;

    /// <summary>默认硬件加速 H.264，若机器无可用硬件编码器，<see cref="ExportEnumExtensions.IsHardware"/> 自动回退软件。</summary>
    public ExportCodec Codec { get; set; } = ExportCodec.H264Hardware;
    public ExportQuality Quality { get; set; } = ExportQuality.High;

    /// <summary>
    /// 用户可读的质量说明（含码率/CRF + 30 秒文件大小估算），在 UI 下方显示。
    /// 对齐 macOS 版 ExportConfig.qualityHint。
    /// </summary>
    public string QualityHint
    {
        get
        {
            if (Codec.IsHardware())
            {
                var kbps = Quality.VideoBitrateKbps(Codec);
                var mbps = kbps / 1000.0;
                var mbpsStr = mbps >= 10
                    ? mbps.ToString("F0", CultureInfo.InvariantCulture)
                    : mbps.ToString("F1", CultureInfo.InvariantCulture);
                var sizeMB = mbps * 30 / 8.0;
                var sizeStr = sizeMB >= 10
                    ? sizeMB.ToString("F0", CultureInfo.InvariantCulture)
                    : sizeMB.ToString("F1", CultureInfo.InvariantCulture);
                return $"目标码率 {mbpsStr} Mbps（1080p 基准）· 30 秒视频约 {sizeStr} MB";
            }
            else
            {
                var crf = Quality.Crf(Codec);
                var approxMbps = (Codec, Quality) switch
                {
                    (ExportCodec.H264, ExportQuality.Low) => "约 2-4 Mbps",
                    (ExportCodec.H264, ExportQuality.Medium) => "约 5-8 Mbps",
                    (ExportCodec.H264, ExportQuality.High) => "约 12-20 Mbps",
                    (ExportCodec.H264, ExportQuality.Lossless) => "无损（极大文件）",
                    (ExportCodec.H265, ExportQuality.Low) => "约 1-2 Mbps",
                    (ExportCodec.H265, ExportQuality.Medium) => "约 2-4 Mbps",
                    (ExportCodec.H265, ExportQuality.High) => "约 6-12 Mbps",
                    (ExportCodec.H265, ExportQuality.Lossless) => "无损（极大文件）",
                    _ => string.Empty,
                };
                return $"CRF {crf}（数值越小画质越好）· 实际码率 {approxMbps}";
            }
        }
    }

    /// <summary>
    /// 按当前编码/质量配置估算给定总时长的导出文件大小（MB）。
    /// 硬件编码用 VideoBitrateKbps 精确算；软件编码按各档位 CRF 对应的近似中位码率算。
    /// 对齐 macOS ExportConfig.estimatedTotalSizeMB。
    /// </summary>
    public double EstimatedTotalSizeMB(double totalDurationSec)
    {
        if (totalDurationSec <= 0) return 0;
        if (Codec.IsHardware())
        {
            var kbps = Quality.VideoBitrateKbps(Codec);
            return kbps / 1000.0 * totalDurationSec / 8.0;
        }
        // 软件编码：CRF 没有固定码率，用各档位经验中位 Mbps
        var midMbps = (Codec, Quality) switch
        {
            (ExportCodec.H264, ExportQuality.Low) => 3.0,
            (ExportCodec.H264, ExportQuality.Medium) => 6.5,
            (ExportCodec.H264, ExportQuality.High) => 16.0,
            (ExportCodec.H264, ExportQuality.Lossless) => 80.0,
            (ExportCodec.H265, ExportQuality.Low) => 1.5,
            (ExportCodec.H265, ExportQuality.Medium) => 3.0,
            (ExportCodec.H265, ExportQuality.High) => 9.0,
            (ExportCodec.H265, ExportQuality.Lossless) => 80.0,
            _ => 8.0,
        };
        return midMbps * totalDurationSec / 8.0;
    }
}
