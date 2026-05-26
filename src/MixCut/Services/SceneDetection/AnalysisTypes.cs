namespace MixCut.Services.SceneDetection;

/// <summary>场景边界（画面切换点）。对应 macOS 版 SceneBoundary。</summary>
public readonly record struct SceneBoundary(double Time, double Confidence);

/// <summary>静音段。对应 macOS 版 SilencePeriod。</summary>
public readonly record struct SilencePeriod(double Start, double End)
{
    public double Duration => End - Start;
    public double Midpoint => (Start + End) / 2;
}

/// <summary>视频本地分析完整结果（传给 AI 之前的所有本地数据）。对应 macOS 版 VideoLocalAnalysis。</summary>
public sealed record VideoLocalAnalysis(
    IReadOnlyList<SceneBoundary> SceneBoundaries,
    IReadOnlyList<SilencePeriod> SilencePeriods,
    IReadOnlyList<double> IframePositions,
    double VideoDuration,
    double Fps);

/// <summary>视频本地分析异常。</summary>
public sealed class SceneDetectionException : Exception
{
    public SceneDetectionException(string message) : base(message)
    {
    }

    public static SceneDetectionException Timeout() =>
        new("视频本地分析超时（4 分钟），文件可能损坏或解码异常");
}
