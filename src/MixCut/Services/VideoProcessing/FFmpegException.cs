namespace MixCut.Services.VideoProcessing;

/// <summary>FFmpeg 错误种类。对应 macOS 版 FFmpegError。</summary>
public enum FFmpegErrorKind
{
    BinaryNotFound,
    ExecutionFailed,
    OutputParsingFailed,
}

/// <summary>FFmpeg 执行异常，携带面向用户的中文提示。</summary>
public sealed class FFmpegException : Exception
{
    public FFmpegErrorKind Kind { get; }

    public FFmpegException(FFmpegErrorKind kind, string message) : base(message)
    {
        Kind = kind;
    }

    public static FFmpegException BinaryNotFound() =>
        new(FFmpegErrorKind.BinaryNotFound, "视频处理组件未找到，请重新安装应用");

    public static FFmpegException ExecutionFailed(int exitCode, string stderr)
    {
        var hint = stderr.Length > 200 ? stderr[^200..] : stderr;
        return new FFmpegException(FFmpegErrorKind.ExecutionFailed,
            $"视频处理失败 (exit {exitCode}): {hint}");
    }

    public static FFmpegException OutputParsingFailed(string detail) =>
        new(FFmpegErrorKind.OutputParsingFailed, $"视频分析结果异常: {detail}");
}
