using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using LibVLCSharp.Shared;
using Serilog;

namespace MixCut.Infrastructure;

/// <summary>
/// LibVLC 内核启动器（v0.6.0+）。
///
/// 设计要点：
/// 1. <b>启动期只做轻量检查</b>：libvlc.dll / libvlccore.dll / plugins 文件完备性 +
///    PE Import 静态分析（vcomp140/vcruntime 等依赖）。**不**在启动期调用
///    <c>Core.Initialize</c> / <c>new LibVLC</c> —— 这俩会扫 365 个 plugin dll 并初始化
///    VLC 内核，耗时 200ms~2s，会阻塞主窗口显示。
/// 2. <b>LibVLC 实例 Lazy 化</b>：第一次 hover 触发播放时才同步 init。Lazy 失败
///    （DllNotFoundException / 内核崩）会被 InlineVideoPlayer 捕获 + 弹错误码 VLC-03。
/// 3. <b>explicit 路径</b>：启动期 <c>NativeLibrary.SetDllImportResolver</c> 不可用时，
///    用 <c>Core.Initialize(libvlcDir)</c> 显式告诉 LibVLCSharp 去哪找 libvlc.dll。
/// 4. <b>启动 fail-fast</b>：文件缺失立即弹窗（VLC-01/VLC-02）+ 退出，不让用户进 app
///    后才发现 hover 播放崩溃。
/// </summary>
public static class VlcBootstrap
{
    private static string? _libvlcDir;
    private static readonly Lazy<LibVLC> SharedInstance = new(CreateLibVlc);

    /// <summary>
    /// 全局共享 LibVLC 实例。第一次访问时才同步初始化（耗时 200ms~2s）。
    /// 初始化失败会抛 LibVLCSharp 异常，调用方应包 try/catch。
    /// </summary>
    public static LibVLC Shared => SharedInstance.Value;

    /// <summary>
    /// 启动期初始化：只做文件完备性检查 + 静态分析依赖。
    /// 失败返回 false（已弹窗），调用方应 Shutdown。
    /// </summary>
    public static bool Initialize()
    {
        try
        {
            var libvlcDir = Path.Combine(AppContext.BaseDirectory, "libvlc", "win-x64");
            var libvlcDll = Path.Combine(libvlcDir, "libvlc.dll");
            var libvlccoreDll = Path.Combine(libvlcDir, "libvlccore.dll");
            var pluginsDir = Path.Combine(libvlcDir, "plugins");

            // 错误码 VLC-01：libvlc.dll / libvlccore.dll 缺失
            if (!File.Exists(libvlcDll) || !File.Exists(libvlccoreDll))
            {
                Log.Error("[VlcDiag] missing libvlc.dll={Has1} libvlccore.dll={Has2} dir={Dir}",
                    File.Exists(libvlcDll), File.Exists(libvlccoreDll), libvlcDir);
                MessageBox.Show(
                    "MixCut 视频内核（VLC）文件缺失，无法启动。\n\n" +
                    $"路径：{libvlcDir}\n\n" +
                    "请卸载后重新安装 MixCut。如果反复出现，请联系开发者。\n\n" +
                    "错误码：VLC-01",
                    "MixCut 启动失败",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }

            // 错误码 VLC-02：plugins 目录不全
            var pluginCount = 0;
            if (Directory.Exists(pluginsDir))
            {
                pluginCount = Directory.GetFiles(pluginsDir, "*.dll", SearchOption.AllDirectories).Length;
            }
            if (pluginCount < 100)
            {
                Log.Error("[VlcDiag] plugins dir invalid count={Count} dir={Dir}", pluginCount, pluginsDir);
                MessageBox.Show(
                    $"MixCut 视频解码插件不完整（仅找到 {pluginCount} 个，正常应有 300+）。\n\n" +
                    $"路径：{pluginsDir}\n\n" +
                    "请卸载后重新安装 MixCut。\n\n" +
                    "错误码：VLC-02",
                    "MixCut 启动失败",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }

            // 文件完备性 OK，记下 libvlcDir 给 Lazy 用
            _libvlcDir = libvlcDir;

            // 静态分析 libvlc.dll 的 native dll 依赖（对齐 v0.4.0 vcomp140 自检方法）。
            // 该检查不阻塞，仅 WRN 日志告知缺漏 —— Lazy 初始化时真撞 DLL 缺失才会弹窗。
            var depReport = AnalyzeLibvlcDependencies(libvlcDll);

            var libvlcSize = new FileInfo(libvlcDll).Length / 1024;
            var libvlccoreSize = new FileInfo(libvlccoreDll).Length / 1024;
            Log.Information(
                "[VlcDiag] libvlc={V1}KB libvlccore={V2}KB plugins={P} (lazy-init on first hover)",
                libvlcSize, libvlccoreSize, pluginCount);
            Log.Information("[VlcRuntimeDiag] {Report}", depReport);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[VlcDiag] Initialize 异常");
            MessageBox.Show(
                "MixCut 视频内核（VLC）初始化检查失败。\n\n" +
                $"错误信息：{ex.Message}\n\n" +
                "请尝试关闭杀软后重新安装。\n\n" +
                "错误码：VLC-04",
                "MixCut 启动失败",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
    }

    /// <summary>Lazy 工厂：第一次访问 Shared 时同步初始化 LibVLC 内核。</summary>
    private static LibVLC CreateLibVlc()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            // explicit 路径告诉 LibVLCSharp 去哪加载 libvlc.dll，绕过默认搜索逻辑
            if (_libvlcDir is not null)
            {
                Core.Initialize(_libvlcDir);
            }
            else
            {
                Core.Initialize();
            }

            var libVlc = new LibVLC(
                "--quiet",                          // 关掉 VLC stdout 噪音
                "--no-video-title-show",            // 禁用「正在播放 xxx」标题叠加
                "--no-snapshot-preview",
                "--no-stats");
            sw.Stop();
            Log.Information("[VlcDiag] LibVLC instance created in {Ms}ms", sw.ElapsedMilliseconds);
            return libVlc;
        }
        catch (Exception ex)
        {
            sw.Stop();
            Log.Error(ex, "[VlcDiag] LibVLC 初始化失败 elapsed_ms={Ms}", sw.ElapsedMilliseconds);
            // 异步弹窗（避免阻塞 Lazy 链）
            Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
            {
                MessageBox.Show(
                    "MixCut 视频播放内核启动失败。\n\n" +
                    $"错误信息：{ex.Message}\n\n" +
                    "可能原因：\n" +
                    "1. 缺少 Visual C++ 运行库（应用已自带，理论上不会发生）\n" +
                    "2. 杀毒软件拦截了 libvlc.dll\n" +
                    "3. Windows 版本过低（最低需 Windows 10 1809）\n\n" +
                    "请关闭杀软后重启 MixCut。\n\n" +
                    "错误码：VLC-03",
                    "MixCut 视频播放不可用",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }));
            throw;
        }
    }

    /// <summary>
    /// 静态分析 libvlc.dll 的 PE Import 表，列出所有 native dll 依赖。
    /// 对齐 v0.4.0 [VcRuntimeDiag] 用同样方法发现 vcomp140 漏网。
    /// </summary>
    private static string AnalyzeLibvlcDependencies(string libvlcDll)
    {
        try
        {
            var bytes = File.ReadAllBytes(libvlcDll);
            var content = System.Text.Encoding.ASCII.GetString(bytes);
            var matches = Regex.Matches(content, @"([A-Za-z][A-Za-z0-9_\-]{0,40}\.dll)",
                RegexOptions.IgnoreCase);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match m in matches)
            {
                var name = m.Groups[1].Value;
                if (name.Length < 5 || name.Length > 40) continue;
                seen.Add(name.ToLowerInvariant());
            }

            // 关键 VC Runtime / Windows native dll 自检（必须在打包目录或系统）
            var critical = new[]
            {
                "vcruntime140.dll", "vcruntime140_1.dll",
                "msvcp140.dll",
            };
            var binDir = Path.Combine(AppContext.BaseDirectory, "bin");
            var missing = new List<string>();
            foreach (var dll in critical)
            {
                if (!seen.Contains(dll)) continue;
                var inBin = File.Exists(Path.Combine(binDir, dll));
                var inSystem = File.Exists(Path.Combine(Environment.SystemDirectory, dll));
                if (!inBin && !inSystem) missing.Add(dll);
            }

            return missing.Count == 0
                ? $"libvlc deps OK ({seen.Count} dll imports scanned)"
                : $"libvlc deps WARN missing={string.Join(",", missing)} (scanned {seen.Count})";
        }
        catch (Exception ex)
        {
            return $"deps analysis failed: {ex.Message}";
        }
    }
}
