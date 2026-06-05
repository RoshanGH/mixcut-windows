using System.Globalization;

namespace MixCut.Services.VideoProcessing;

/// <summary>
/// hover 预览裸帧管道的 ffmpeg 参数拼接（纯函数，便于单测）。
///
/// 设计出发点：CLAUDE.md §兼容性总纲 —— 预览用我们自带的 ffmpeg.exe 进程外解码，
/// 与导出同源、彻底不碰系统编解码器（消灭 0xC00D109B）。这里只负责把命令行拼对，
/// 真正的进程/渲染在 <see cref="MixCut.Views.Components.FfmpegFramePlayer"/>。
/// </summary>
public static class FramePipeArgs
{
    private static string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>
    /// 视频裸帧管道参数：输出 bgra 像素流（WPF <c>WriteableBitmap</c> 直接可用），
    /// 已裁剪缩放到卡片尺寸 + 固定 fps，便于按定长帧读取。
    /// <c>-ss</c> 放在 <c>-i</c> 前用输入级快速 seek。
    /// </summary>
    public static string[] Video(string path, double start, double dur, int w, int h, int fps) => new[]
    {
        "-ss", F(start), "-i", path, "-t", F(dur), "-an",
        "-vf", $"scale={w}:{h}:force_original_aspect_ratio=decrease,fps={fps}",
        "-f", "rawvideo", "-pix_fmt", "bgra", "pipe:1",
    };

    /// <summary>
    /// 音频管道参数：s16le 立体声 44.1kHz PCM，喂给音频输出设备作主时钟（音画同步用）。
    /// </summary>
    public static string[] Audio(string path, double start, double dur) => new[]
    {
        "-ss", F(start), "-i", path, "-t", F(dur), "-vn",
        "-f", "s16le", "-ar", "44100", "-ac", "2", "pipe:1",
    };

    /// <summary>一帧 bgra 的字节数（宽 × 高 × 4 通道）。</summary>
    public static int FrameBytes(int w, int h) => w * h * 4;
}
