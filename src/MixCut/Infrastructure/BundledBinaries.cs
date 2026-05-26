using System.IO;

namespace MixCut.Infrastructure;

/// <summary>
/// 内置二进制工具定位。对应 macOS 版「App Bundle 内置 FFmpeg/Whisper」。
/// Windows 版二进制随应用一起分发在可执行文件同级的 <c>bin\</c> 目录。
/// </summary>
public static class BundledBinaries
{
    /// <summary>内置二进制目录：<c>&lt;AppDir&gt;\bin</c>。</summary>
    public static string BinDirectory => Path.Combine(AppContext.BaseDirectory, "bin");

    public static string Ffmpeg => Path.Combine(BinDirectory, "ffmpeg.exe");
    public static string Ffprobe => Path.Combine(BinDirectory, "ffprobe.exe");
    public static string WhisperCli => Path.Combine(BinDirectory, "whisper-cli.exe");

    public static bool FfmpegAvailable => File.Exists(Ffmpeg);
    public static bool FfprobeAvailable => File.Exists(Ffprobe);
    public static bool WhisperAvailable => File.Exists(WhisperCli);
}
