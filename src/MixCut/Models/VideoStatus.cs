namespace MixCut.Models;

/// <summary>视频处理状态。对应 macOS 版 VideoStatus（6 种）。</summary>
public enum VideoStatus
{
    /// <summary>已导入。</summary>
    Imported,
    /// <summary>镜头检测中。</summary>
    DetectingScenes,
    /// <summary>ASR 识别中。</summary>
    Transcribing,
    /// <summary>AI 语义分析中。</summary>
    Analyzing,
    /// <summary>处理完成。</summary>
    Completed,
    /// <summary>处理失败。</summary>
    Failed,
}

public static class VideoStatusExtensions
{
    private static readonly Dictionary<VideoStatus, string> Labels = new()
    {
        [VideoStatus.Imported] = "已导入",
        [VideoStatus.DetectingScenes] = "镜头检测中",
        [VideoStatus.Transcribing] = "语音识别中",
        [VideoStatus.Analyzing] = "AI 分析中",
        [VideoStatus.Completed] = "处理完成",
        [VideoStatus.Failed] = "处理失败",
    };

    /// <summary>中文显示名。</summary>
    public static string ToLabel(this VideoStatus status) => Labels[status];

    /// <summary>是否处理中（用于 UI 显示加载动画）。</summary>
    public static bool IsProcessing(this VideoStatus status) =>
        status is VideoStatus.DetectingScenes or VideoStatus.Transcribing or VideoStatus.Analyzing;
}
