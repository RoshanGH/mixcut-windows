using System.IO;
using System.Runtime.Intrinsics.X86;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.Logging;
using MixCut.Utilities;

namespace MixCut.Infrastructure;

/// <summary>
/// 启动期环境一站式体检。
///
/// 目标：在用户撞到运行时崩溃之前，先在日志里把"这台机器到底哪里不对"说清楚。
/// 关键决策：检查项失败 = 写日志 + 可选弹窗，但不阻塞启动。让应用尽量起来，
/// 用户即使在边界环境也能用一部分功能，而不是看一个红叉就完事。
///
/// 诊断 grep 关键字：<c>[EnvDiag]</c>（汇总）/ <c>[CpuDiag]</c> / <c>[VcRuntimeDiag]</c>
/// / <c>[BinariesDiag]</c> / <c>[AppDataDiag]</c> / <c>[InstallDirDiag]</c> / <c>[PathDiag]</c>
/// </summary>
public static class EnvironmentDiagnostics
{
    /// <summary>体检结果汇总。所有字段是给日志 / 后续 UI 使用的 snapshot。</summary>
    public sealed record Report(
        string OsCaption,
        int OsBuild,
        string Arch,
        bool CpuAvx2,
        bool VcRuntimeOk,
        string[] VcRuntimeMissing,
        bool BinariesOk,
        string[] BinariesMissing,
        bool AppDataWritable,
        string AppDataRoot,
        string InstallDir,
        bool InstallDirOk,
        string InstallDirNote,
        bool ExePathAscii)
    {
        public bool AllPassed =>
            OsBuild >= 17763 && Arch == "x64" && CpuAvx2
            && VcRuntimeOk && BinariesOk && AppDataWritable && InstallDirOk;
    }

    /// <summary>跑一遍体检并写日志。返回结果供 UI 决策使用。</summary>
    public static Report RunAndLog()
    {
        var os = GetOsInfo();
        var arch = RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();
        var cpuAvx2 = Avx2.IsSupported;
        var (vcOk, vcMissing) = BundledBinaries.ProbeVcRuntime();
        var (binOk, binMissing) = ProbeBundledBinaries();
        var (appDataOk, appDataRoot) = ProbeAppDataWritable();
        var (installOk, installNote) = ProbeInstallDir();
        var exePathAscii = IsAscii(AppContext.BaseDirectory);

        var report = new Report(
            os.Caption, os.Build, arch, cpuAvx2,
            vcOk, vcMissing, binOk, binMissing,
            appDataOk, appDataRoot,
            AppContext.BaseDirectory, installOk, installNote,
            exePathAscii);

        WriteDetailLogs(report);
        WriteSummary(report);
        return report;
    }

    // ---- 各检查项 ----

    private static (string Caption, int Build) GetOsInfo()
    {
        try
        {
            var build = Environment.OSVersion.Version.Build;
            var os11 = build >= 22000;
            var caption = os11 ? $"Win11 ({build})" : $"Win10 ({build})";
            return (caption, build);
        }
        catch
        {
            return ("Windows (unknown)", 0);
        }
    }

    private static (bool Ok, string[] Missing) ProbeBundledBinaries()
    {
        // whisper-cli + ggml 一族 + ffmpeg/ffprobe 必须全在
        var required = new[]
        {
            "ffmpeg.exe", "ffprobe.exe", "whisper-cli.exe",
            "whisper.dll", "ggml.dll", "ggml-base.dll", "ggml-cpu.dll",
        };
        var missing = required
            .Where(name =>
            {
                var p = Path.Combine(BundledBinaries.BinDirectory, name);
                return !File.Exists(p) || new FileInfo(p).Length == 0;
            })
            .ToArray();
        return (missing.Length == 0, missing);
    }

    private static (bool Ok, string Root) ProbeAppDataWritable()
    {
        var root = AppPaths.Root;
        try
        {
            var probe = Path.Combine(root, $".write-probe-{Guid.NewGuid():N}");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            return (true, root);
        }
        catch
        {
            return (false, root);
        }
    }

    private static (bool Ok, string Note) ProbeInstallDir()
    {
        var dir = AppContext.BaseDirectory;
        var notes = new List<string>();
        var ok = true;

        // OneDrive 同步目录运行（已知大坑：跑一半 OneDrive 抢锁）
        if (dir.Contains("OneDrive", StringComparison.OrdinalIgnoreCase))
        {
            notes.Add("OneDrive");
            ok = false;
        }

        // 网络驱动器 / UNC 路径
        if (dir.StartsWith(@"\\", StringComparison.Ordinal))
        {
            notes.Add("UNC-network-path");
            ok = false;
        }

        // Program Files / Program Files (x86) —— 写权限受限
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var pfx86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        if (!string.IsNullOrEmpty(pf) && dir.StartsWith(pf, StringComparison.OrdinalIgnoreCase))
        {
            notes.Add("Program-Files");
            // 不算 fail —— 我们的 AppData 在用户目录下，install 在 Program Files 没问题
        }

        return (ok, notes.Count == 0 ? "clean" : string.Join(",", notes));
    }

    private static bool IsAscii(string s)
    {
        foreach (var c in s)
        {
            if (c > 0x7E) return false;
        }
        return true;
    }

    // ---- 日志输出 ----

    private static void WriteDetailLogs(Report r)
    {
        var log = Serilog.Log.ForContext("SourceContext", "EnvironmentDiagnostics");

        if (r.OsBuild < 17763)
        {
            log.Warning("[OsDiag] WRN os too old: {Cap} (要求 Win10 1809 即 Build 17763 以上)", r.OsCaption);
        }

        if (r.Arch != "x64")
        {
            log.Warning("[OsDiag] WRN arch={Arch} 非 x64（应用为 x64 build，可能跑不起来）", r.Arch);
        }

        if (!r.CpuAvx2)
        {
            log.Warning("[CpuDiag] WRN CPU 不支持 AVX2 —— whisper-cli 跑起来会立即崩 (ExitCode=-1073741795 / 0xC000001D)");
        }

        if (!r.VcRuntimeOk)
        {
            log.Warning("[VcRuntimeDiag] WRN missing in {Dir}: {Missing}",
                BundledBinaries.BinDirectory, string.Join(", ", r.VcRuntimeMissing));
        }
        else
        {
            log.Information("[VcRuntimeDiag] all 6 VC Runtime DLLs present in {Dir}",
                BundledBinaries.BinDirectory);
        }

        if (!r.BinariesOk)
        {
            log.Error("[BinariesDiag] ERR 内置二进制缺失: {Missing}", string.Join(", ", r.BinariesMissing));
        }

        if (!r.AppDataWritable)
        {
            log.Error("[AppDataDiag] ERR 数据目录无写权限: {Root}", r.AppDataRoot);
        }

        if (!r.InstallDirOk)
        {
            log.Warning("[InstallDirDiag] WRN install dir 不推荐: {Note} ({Dir})", r.InstallDirNote, r.InstallDir);
        }

        if (!r.ExePathAscii)
        {
            log.Warning("[PathDiag] WRN EXE 路径含非 ASCII 字符: {Dir}", r.InstallDir);
        }
    }

    private static void WriteSummary(Report r)
    {
        var sb = new StringBuilder(160);
        sb.Append("[EnvDiag] ");
        sb.Append($"os={r.OsCaption} ");
        sb.Append($"arch={r.Arch} ");
        sb.Append($"cpu={(r.CpuAvx2 ? "AVX2" : "no-AVX2")} ");
        sb.Append($"vcrt={(r.VcRuntimeOk ? "ok" : "missing-" + r.VcRuntimeMissing.Length)} ");
        sb.Append($"bin={(r.BinariesOk ? "ok" : "missing-" + r.BinariesMissing.Length)} ");
        sb.Append($"appdata={(r.AppDataWritable ? "ok" : "READONLY")} ");
        sb.Append($"installdir={r.InstallDirNote} ");
        sb.Append($"path={(r.ExePathAscii ? "ascii" : "non-ascii")} ");
        sb.Append($"pass={r.AllPassed}");
        Serilog.Log.Information(sb.ToString());
    }
}
