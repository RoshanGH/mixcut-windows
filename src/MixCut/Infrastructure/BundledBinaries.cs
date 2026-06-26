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

    /// <summary>人声/BGM 分离引擎（demucs.cpp，v0.5.0 配音）。</summary>
    public static string Demucs => Path.Combine(BinDirectory, "demucs.exe");

    public static bool FfmpegAvailable => File.Exists(Ffmpeg);
    public static bool FfprobeAvailable => File.Exists(Ffprobe);
    public static bool WhisperAvailable => File.Exists(WhisperCli);
    public static bool DemucsAvailable => File.Exists(Demucs);

    /// <summary>
    /// whisper-cli.exe 和 ffmpeg.exe 用 MSVC 编译，依赖 VC++ Runtime DLL。
    /// 若用户机器没装 VC++ Redist，进程启动时会报 STATUS_DLL_NOT_FOUND (-1073741515 / 0xC0000135)。
    /// v0.3.1 起把 6 个 VC Runtime DLL 随应用打包到 bin\，开箱即用。
    /// </summary>
    public static readonly string[] RequiredVcRuntimeDlls = new[]
    {
        "vcruntime140.dll",
        "vcruntime140_1.dll",
        "msvcp140.dll",
        "msvcp140_1.dll",
        "msvcp140_2.dll",
        "concrt140.dll",
    };

    /// <summary>返回 VC Runtime DLL 在 bin\ 里的存在性，缺失的会列出。</summary>
    public static (bool AllPresent, string[] Missing) ProbeVcRuntime()
    {
        var missing = RequiredVcRuntimeDlls
            .Where(d => !File.Exists(Path.Combine(BinDirectory, d)))
            .ToArray();
        return (missing.Length == 0, missing);
    }
}
