using System.IO;

namespace MixCut.Utilities;

/// <summary>
/// 应用数据目录集中管理。对应 macOS 版的 <c>~/Library/Application Support/MixCut</c>。
/// 所有路径在首次访问时自动创建对应目录。
/// </summary>
public static class AppPaths
{
    /// <summary>应用根数据目录：<c>%APPDATA%\MixCut</c>。</summary>
    public static string Root { get; } = CreateDir(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MixCut"));

    /// <summary>日志目录：<c>%APPDATA%\MixCut\logs</c>。</summary>
    public static string LogDirectory { get; } = CreateDir(Path.Combine(Root, "logs"));

    /// <summary>视频全局存储目录，按内容哈希分子目录：<c>%APPDATA%\MixCut\Videos</c>。</summary>
    public static string VideosDirectory { get; } = CreateDir(Path.Combine(Root, "Videos"));

    /// <summary>
    /// Whisper 模型目录：<c>%LOCALAPPDATA%\MixCut\whisper-models</c>。
    /// 模型体积大，放本地（非漫游）目录。
    /// </summary>
    public static string WhisperModelsDirectory { get; } = CreateDir(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MixCut", "whisper-models"));

    /// <summary>SQLite 数据库文件路径：<c>%APPDATA%\MixCut\mixcut.db</c>。</summary>
    public static string DatabaseFile { get; } = Path.Combine(Root, "mixcut.db");

    private static string CreateDir(string path)
    {
        Directory.CreateDirectory(path);
        return path;
    }
}
