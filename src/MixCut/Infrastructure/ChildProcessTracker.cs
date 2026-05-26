using System.Diagnostics;
using System.Runtime.InteropServices;

namespace MixCut.Infrastructure;

/// <summary>
/// 用 Win32 Job Object 把所有子进程跟 MixCut 主进程绑死：
/// 主进程退出（正常退出 / 崩溃 / 被任务管理器结束 / 系统注销）时，所有挂在 Job 上的子进程自动被 OS 终止。
///
/// 解决的问题：whisper-cli / ffmpeg 这类长跑外部程序在 MixCut 异常退出时变成孤儿进程，继续吃 CPU，
/// 下次启动 MixCut 时新启动的 whisper-cli 跟孤儿抢资源全跑不动。
///
/// 用法：每次 <see cref="Process.Start()"/> 之后调 <see cref="AddProcess(Process)"/>。
/// 多次添加同一进程是安全的（OS 内幂等）。
/// </summary>
public static class ChildProcessTracker
{
    // ----- Win32 Job Object API -----

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll")]
    private static extern bool SetInformationJobObject(
        IntPtr hJob, JobObjectInfoType infoType,
        IntPtr lpJobObjectInfo, uint cbJobObjectInfoLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    private enum JobObjectInfoType
    {
        ExtendedLimitInformation = 9,
    }

    [Flags]
    private enum JobObjectLimitFlags : uint
    {
        KillOnJobClose = 0x2000,
        DieOnUnhandledException = 0x0400,
        BreakawayOk = 0x0800,
        SilentBreakawayOk = 0x1000,
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public JobObjectLimitFlags LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public IntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    // ----- 单例 Job Object -----

    private static readonly Lazy<IntPtr> _jobHandle = new(CreateAndConfigureJob);
    private static readonly object _gate = new();

    private static IntPtr CreateAndConfigureJob()
    {
        if (!OperatingSystem.IsWindows())
        {
            return IntPtr.Zero;
        }

        var handle = CreateJobObject(IntPtr.Zero, null);
        if (handle == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        var info = new JOBOBJECT_BASIC_LIMIT_INFORMATION
        {
            // 主进程死时杀掉 Job 里所有子进程。
            LimitFlags = JobObjectLimitFlags.KillOnJobClose,
        };
        var extended = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            BasicLimitInformation = info,
        };

        var size = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
        var ptr = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(extended, ptr, fDeleteOld: false);
            if (!SetInformationJobObject(handle, JobObjectInfoType.ExtendedLimitInformation, ptr, (uint)size))
            {
                CloseHandle(handle);
                return IntPtr.Zero;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
        return handle;
    }

    /// <summary>把进程挂到全局 Job Object 下。失败时静默忽略，不影响业务流程。</summary>
    public static void AddProcess(Process process)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        try
        {
            var job = _jobHandle.Value;
            if (job == IntPtr.Zero)
            {
                return;
            }
            lock (_gate)
            {
                // process.Handle 在进程已退出时会抛 InvalidOperationException。
                if (process.HasExited)
                {
                    return;
                }
                _ = AssignProcessToJobObject(job, process.Handle);
            }
        }
        catch (Exception)
        {
            // 加入 Job 失败不致命：进程仍能跑，只是失去孤儿保护。
        }
    }
}
