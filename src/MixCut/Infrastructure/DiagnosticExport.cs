using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text;
using MixCut.Utilities;

namespace MixCut.Infrastructure;

/// <summary>
/// 一键导出诊断包（最近日志 + 系统/硬件信息）到桌面 zip，供终端用户通过微信发给开发者排查。
/// 背景：导出失败这类问题常是 GPU 路径特有（NVENC 驱动/会话/像素格式），开发者碰不到用户机器、
/// 用户也不会敲命令行 —— 必须让用户一键生成诊断包发回来才能定位。
/// </summary>
public static class DiagnosticExport
{
    /// <summary>生成诊断 zip，返回完整路径（失败抛异常由调用方提示）。</summary>
    public static async Task<string> ExportAsync()
    {
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        if (string.IsNullOrEmpty(desktop) || !Directory.Exists(desktop))
        {
            desktop = AppPaths.Root; // 桌面拿不到时兜底到数据目录
        }
        var zipPath = Path.Combine(desktop, $"MixCut-诊断-{stamp}.zip");

        var info = await BuildSystemInfoAsync();

        var staging = Path.Combine(Path.GetTempPath(), $"mixcut-diag-{stamp}");
        Directory.CreateDirectory(staging);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(staging, "系统信息.txt"), info, Encoding.UTF8);

            // 最近 3 个日志文件（含启动期 EnvDiag/HwProbe + 任何 ffmpeg 失败的完整 stderr）
            if (Directory.Exists(AppPaths.LogDirectory))
            {
                var logs = new DirectoryInfo(AppPaths.LogDirectory)
                    .GetFiles("*.log").OrderByDescending(f => f.LastWriteTime).Take(3);
                foreach (var f in logs)
                {
                    try { File.Copy(f.FullName, Path.Combine(staging, f.Name), overwrite: true); }
                    catch { /* 单个日志拷贝失败不阻断整体导出 */ }
                }
            }

            if (File.Exists(zipPath)) { File.Delete(zipPath); }
            ZipFile.CreateFromDirectory(staging, zipPath);
        }
        finally
        {
            try { Directory.Delete(staging, recursive: true); } catch { /* 清理失败忽略 */ }
        }
        return zipPath;
    }

    private static async Task<string> BuildSystemInfoAsync()
    {
        var sb = new StringBuilder();
        sb.AppendLine("MixCut 诊断信息");
        sb.AppendLine($"时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"应用版本: {typeof(DiagnosticExport).Assembly.GetName().Version}");
        sb.AppendLine($"操作系统: {Environment.OSVersion}  64位OS={Environment.Is64BitOperatingSystem}");
        sb.AppendLine($"CPU 逻辑核: {Environment.ProcessorCount}");
        sb.AppendLine();
        sb.AppendLine("== 硬件加速探测 ==");
        sb.AppendLine($"编码: {HardwareEncoderProbe.HardwareDescription}");
        sb.AppendLine($"解码: {HardwareEncoderProbe.DecodeHwaccelDescription}");
        sb.AppendLine($"Whisper 后端: {HardwareEncoderProbe.WhisperBackendDescription}");
        sb.AppendLine($"导出并发: {ConcurrencyPolicy.ExplainExportFormula()}");
        sb.AppendLine();
        sb.AppendLine("== nvidia-smi（显卡型号 + 驱动版本，N 卡导出问题首看这里）==");
        sb.AppendLine(await RunCaptureAsync("nvidia-smi", string.Empty, 6000));
        sb.AppendLine();
        sb.AppendLine("== ffmpeg 版本 ==");
        sb.AppendLine(await RunCaptureAsync(BundledBinaries.Ffmpeg, "-hide_banner -version", 6000));
        sb.AppendLine();
        sb.AppendLine("== h264_nvenc 编码器能力（看本机 nvenc 是否可用 + 支持的选项）==");
        sb.AppendLine(await RunCaptureAsync(BundledBinaries.Ffmpeg, "-hide_banner -h encoder=h264_nvenc", 6000));
        return sb.ToString();
    }

    private static async Task<string> RunCaptureAsync(string exe, string args, int timeoutMs)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = args,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (p is null) { return "(无法启动)"; }
            var outTask = p.StandardOutput.ReadToEndAsync();
            var errTask = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(timeoutMs))
            {
                try { p.Kill(entireProcessTree: true); } catch { /* ignore */ }
                return "(执行超时)";
            }
            var stdout = await outTask;
            var stderr = await errTask;
            return string.IsNullOrWhiteSpace(stdout) ? stderr : stdout;
        }
        catch (Exception ex)
        {
            // nvidia-smi 不在 PATH（非 N 卡 / 驱动未装）会走这里 —— 本身也是有用信息。
            return $"(执行失败: {ex.Message})";
        }
    }
}
