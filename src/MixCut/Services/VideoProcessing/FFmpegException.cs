namespace MixCut.Services.VideoProcessing;

/// <summary>FFmpeg 错误种类。对应 macOS 版 FFmpegError。</summary>
public enum FFmpegErrorKind
{
    BinaryNotFound,
    ExecutionFailed,
    OutputParsingFailed,
}

/// <summary>
/// FFmpeg 执行失败的归类。导出降级/重试要按「失败原因」决策，不能一刀切：
///   - Oom：内存不足（发生在滤镜阶段，换编码器无用，必须降规格/降并发重试）；
///   - EncoderCrash：硬件编码器崩溃/产 0 帧（滤镜没问题，降级 CPU libx264 有用）；
///   - Timeout：进程超时（往往是机器繁忙/规格过高，盲目重试无意义）；
///   - Other：其余未知。
/// 分类依据是客户真实诊断日志里的指纹，详见 <see cref="FFmpegException.Classify"/>。
/// </summary>
public enum FFmpegFailureClass
{
    Oom,
    EncoderCrash,
    Timeout,
    Other,
}

/// <summary>FFmpeg 执行异常，携带面向用户的中文提示。</summary>
public sealed class FFmpegException : Exception
{
    public FFmpegErrorKind Kind { get; }

    /// <summary>原始进程退出码（未知时为 0）。用于精确分类（如 -12=ENOMEM、-1073741819=0xC0000005）。</summary>
    public int ExitCode { get; }

    /// <summary>
    /// 完整 stderr 原文（不截断）。message 只带最后 200 字给兜底展示，
    /// 但真因关键行（「Cannot allocate memory」「received no packets」等）常在上面几行，
    /// 分类必须扫全文 —— 故这里完整保留。绝不直接展示给用户（见 ExportErrorMessage）。
    /// </summary>
    public string RawStderr { get; }

    public FFmpegException(FFmpegErrorKind kind, string message, int exitCode = 0, string? rawStderr = null)
        : base(message)
    {
        Kind = kind;
        ExitCode = exitCode;
        RawStderr = rawStderr ?? string.Empty;
    }

    public static FFmpegException BinaryNotFound() =>
        new(FFmpegErrorKind.BinaryNotFound, "视频处理组件未找到，请重新安装应用");

    public static FFmpegException ExecutionFailed(int exitCode, string stderr)
    {
        var hint = stderr.Length > 200 ? stderr[^200..] : stderr;
        return new FFmpegException(FFmpegErrorKind.ExecutionFailed,
            $"视频处理失败 (exit {exitCode}): {hint}", exitCode, stderr);
    }

    public static FFmpegException OutputParsingFailed(string detail) =>
        new(FFmpegErrorKind.OutputParsingFailed, $"视频分析结果异常: {detail}");

    /// <summary>
    /// 按 <see cref="ExitCode"/> + <see cref="RawStderr"/> 全文（含 message 兜底）归类失败原因。
    /// 关键字大小写不敏感，指纹取自客户真实诊断日志（2026-06-18 RTX 3060 Ti 导出 OOM 事故）。
    /// </summary>
    public static FFmpegFailureClass Classify(FFmpegException ex)
    {
        // 扫全文：RawStderr（完整）+ Message（兜底，超时分支 stderr 为「进程超时」也在 message 里）。
        var text = (ex.RawStderr + "\n" + (ex.Message ?? string.Empty));

        bool Has(string s) => text.IndexOf(s, StringComparison.OrdinalIgnoreCase) >= 0;

        // 内存不足：ENOMEM(-12) 或滤镜阶段分配失败的两条指纹。
        if (ex.ExitCode == -12 || Has("Cannot allocate memory") || Has("Error sending frames to consumers"))
        {
            return FFmpegFailureClass.Oom;
        }

        // 进程超时：ExecuteAsync 超时分支抛 ExecutionFailed(-1, "进程超时")。
        // 注意：-1 也可能是其它失败的兜底码，故必须配「进程超时」文案，不能只看 -1。
        if (Has("进程超时"))
        {
            return FFmpegFailureClass.Timeout;
        }

        // 硬件编码器崩溃 / 产 0 帧：0xC0000005 访问违例、NVENC 收不到包、解析器拒识等。
        if (ex.ExitCode == -1073741819      // 0xC0000005 访问违例（喂错像素格式等）
            || ex.ExitCode == -542398533    // "at least one of its streams received no packets"
            || Has("received no packets"))
        {
            return FFmpegFailureClass.EncoderCrash;
        }

        return FFmpegFailureClass.Other;
    }
}
