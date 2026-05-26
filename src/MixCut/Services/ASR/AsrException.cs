namespace MixCut.Services.ASR;

/// <summary>下载进度（百分比 + 已接收 / 总字节）。</summary>
public readonly record struct DownloadProgress(double Percent, long Received, long Total);

/// <summary>ASR 错误种类。对应 macOS 版 ASRError。</summary>
public enum AsrErrorKind
{
    ModelNotFound,
    WhisperNotFound,
}

/// <summary>ASR 语音识别异常，携带面向用户的中文提示。</summary>
public sealed class AsrException : Exception
{
    public AsrErrorKind Kind { get; }

    public AsrException(AsrErrorKind kind, string message) : base(message)
    {
        Kind = kind;
    }

    public static AsrException ModelNotFound() =>
        new(AsrErrorKind.ModelNotFound, "Whisper 模型文件未找到，请在设置中下载模型");

    public static AsrException WhisperNotFound() =>
        new(AsrErrorKind.WhisperNotFound, "语音识别组件未找到，请重新安装应用");
}
