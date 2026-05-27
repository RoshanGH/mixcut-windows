using System.IO;

namespace MixCut.Utilities;

/// <summary>
/// 应用数据目录集中管理。对应 macOS 版的 <c>~/Library/Application Support/MixCut</c>。
///
/// 三级写权限兜底（v0.4.0）：
/// 1. <c>%APPDATA%\MixCut\</c>            ← 默认（漫游配置目录）
/// 2. <c>%LOCALAPPDATA%\MixCut\</c>      ← 漫游不可写时（公司 GPO / 漫游目录坏）
/// 3. <c>&lt;EXE 目录&gt;\data\</c>       ← 都不行时（U 盘绿色运行 / 沙盒）
///
/// 启动期跑一遍写探针决定用哪一层，决策日志 <c>[AppDataDiag]</c>。
/// </summary>
public static class AppPaths
{
    /// <summary>应用根数据目录（三级兜底中第一个可写的）。</summary>
    public static string Root { get; } = ResolveWritableRoot();

    /// <summary>日志目录：<c>&lt;Root&gt;\logs</c>。</summary>
    public static string LogDirectory { get; } = CreateDir(Path.Combine(Root, "logs"));

    /// <summary>视频全局存储目录：<c>&lt;Root&gt;\Videos</c>。</summary>
    public static string VideosDirectory { get; } = CreateDir(Path.Combine(Root, "Videos"));

    /// <summary>
    /// Whisper 模型目录：优先 <c>%LOCALAPPDATA%\MixCut\whisper-models</c>，回退到 Root\whisper-models。
    /// 模型体积大，放本地（非漫游）。
    /// </summary>
    public static string WhisperModelsDirectory { get; } = ResolveWhisperModelsDir();

    /// <summary>SQLite 数据库文件路径：<c>&lt;Root&gt;\mixcut.db</c>。</summary>
    public static string DatabaseFile { get; } = Path.Combine(Root, "mixcut.db");

    // ---- 内部实现 ----

    private static string ResolveWritableRoot()
    {
        var tier1 = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MixCut");
        var tier2 = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MixCut");
        var tier3 = Path.Combine(AppContext.BaseDirectory, "data");

        var tries = new[]
        {
            ("tier1-Roaming", tier1),
            ("tier2-Local", tier2),
            ("tier3-AppDir", tier3),
        };

        foreach (var (tier, path) in tries)
        {
            if (TryUse(path))
            {
                // Serilog 启动期还没初始化，先 Console 输出，后续 EnvironmentDiagnostics 也会读 Root 字符串
                Console.WriteLine($"[AppDataDiag] selected={tier} root={path}");
                return path;
            }
        }

        // 三级全部失败 —— 极少见。返回 tier1（让后续操作显式失败，EnvironmentDiagnostics 会抓到）
        Console.WriteLine($"[AppDataDiag] ALL TIERS FAILED, fallback to {tier1}");
        return tier1;
    }

    private static bool TryUse(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            var probe = Path.Combine(path, $".write-probe-{Guid.NewGuid():N}");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string ResolveWhisperModelsDir()
    {
        // 优先 LocalAppData\MixCut\whisper-models
        var localDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MixCut", "whisper-models");
        if (TryUse(localDir)) return CreateDir(localDir);

        // 回退到 Root\whisper-models（跟主数据目录走相同 tier）
        return CreateDir(Path.Combine(Root, "whisper-models"));
    }

    private static string CreateDir(string path)
    {
        Directory.CreateDirectory(path);
        return path;
    }
}
