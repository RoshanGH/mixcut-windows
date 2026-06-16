using System.Globalization;
using MixCut.Services.ASR;
using MixCut.Services.SceneDetection;
using MixCut.Utilities;

namespace MixCut.Services.BoundaryOptimizer;

/// <summary>边界优化配置。对应 macOS 版 BoundaryOptimizationConfig。</summary>
public sealed class BoundaryOptimizationConfig
{
    /// <summary>句子边界吸附最大偏移量（秒）。</summary>
    public double AsrSnapMaxOffset { get; init; } = 0.8;

    /// <summary>强场景切换搜索范围（秒）。</summary>
    public double SceneSearchRange { get; init; } = 0.5;

    /// <summary>强场景切换最低分值。</summary>
    public double StrongSceneThreshold { get; init; } = 0.3;

    /// <summary>静音段吸附搜索范围（秒）。</summary>
    public double SilenceSearchRange { get; init; } = 0.5;

    /// <summary>安全约束：最大边界移动距离（秒）。</summary>
    public double MaxBoundaryShift { get; init; } = 1.5;

    /// <summary>安全约束：最小片段时长（秒）。</summary>
    public double MinSegmentDuration { get; init; } = 1.0;
}

/// <summary>边界优化报告。对应 macOS 版 BoundaryOptimizationReport。</summary>
public sealed record BoundaryOptimizationReport(
    IReadOnlyList<double> OriginalBoundaries,
    IReadOnlyList<double> OptimizedBoundaries,
    IReadOnlyList<double> Shifts,
    double AverageShift,
    double MaxShift,
    int SegmentsAffected)
{
    public string Description =>
        $"边界优化报告:\n" +
        $"- 边界数量: {OriginalBoundaries.Count}\n" +
        $"- 平均移动: {AverageShift.ToString("F3", CultureInfo.InvariantCulture)}s\n" +
        $"- 最大移动: {MaxShift.ToString("F3", CultureInfo.InvariantCulture)}s\n" +
        $"- 受影响片段: {SegmentsAffected}";
}

/// <summary>
/// 帧级边界优化服务。每个原始切点只选择一次锚点，优先级固定为：
/// 强场景切换 &gt; 句子结束 &gt; 静音段 &gt; 原始切点。最后统一量化到整数帧并执行安全约束。
/// 纯算法，无副作用。注册为单例服务。
/// </summary>
public sealed class BoundaryOptimizerService
{
    private readonly BoundaryOptimizationConfig _config;

    public BoundaryOptimizerService(BoundaryOptimizationConfig? config = null)
    {
        _config = config ?? new BoundaryOptimizationConfig();
    }

    /// <summary>优化分镜边界（完整四阶段）。</summary>
    public (IReadOnlyList<double> Boundaries, BoundaryOptimizationReport Report) Optimize(
        IReadOnlyList<double> boundaries,
        IReadOnlyList<TranscriptionSentence> asrSentences,
        VideoLocalAnalysis localAnalysis)
    {
        var original = boundaries.OrderBy(b => b).ToList();
        if (original.Count == 0)
        {
            var empty = new BoundaryOptimizationReport(
                Array.Empty<double>(), Array.Empty<double>(), Array.Empty<double>(), 0, 0, 0);
            return (Array.Empty<double>(), empty);
        }

        var fps = localAnalysis.Fps > 0 ? localAnalysis.Fps : 30.0;
        var sentenceEnds = asrSentences.Select(s => s.EndTime).OrderBy(x => x).ToList();
        var strongScenes = localAnalysis.SceneBoundaries
            .Where(s => s.Confidence >= _config.StrongSceneThreshold)
            .OrderBy(s => s.Time)
            .ToList();

        var chosen = original.Select(boundary =>
            ChooseAnchor(boundary, strongScenes, sentenceEnds, localAnalysis.SilencePeriods)).ToList();
        var optimized = EnforceFrameConstraints(chosen, original, localAnalysis.VideoDuration, fps);

        var shifts = original.Zip(optimized, (o, n) => Math.Abs(n - o)).ToList();
        var report = new BoundaryOptimizationReport(
            original, optimized, shifts,
            shifts.Count == 0 ? 0 : shifts.Sum() / shifts.Count,
            shifts.Count == 0 ? 0 : shifts.Max(),
            shifts.Count(s => s > 0.01));

        return (optimized, report);
    }

    private double ChooseAnchor(
        double boundary,
        IReadOnlyList<SceneBoundary> strongScenes,
        IReadOnlyList<double> sentenceEnds,
        IReadOnlyList<SilencePeriod> silencePeriods)
    {
        var sceneCandidates = strongScenes
            .Where(s => Math.Abs(s.Time - boundary) <= _config.SceneSearchRange)
            .ToList();
        if (sceneCandidates.Count > 0)
        {
            return sceneCandidates.MinBy(s => Math.Abs(s.Time - boundary)).Time;
        }

        var sentenceCandidates = sentenceEnds
            .Where(end => Math.Abs(end - boundary) <= _config.AsrSnapMaxOffset)
            .ToList();
        if (sentenceCandidates.Count > 0)
        {
            return sentenceCandidates.MinBy(end => Math.Abs(end - boundary));
        }

        var silenceCandidates = silencePeriods
            .Where(p => Math.Abs(p.Midpoint - boundary) <= _config.SilenceSearchRange
                        || p.Start <= boundary && boundary <= p.End)
            .ToList();
        return silenceCandidates.Count == 0
            ? boundary
            : silenceCandidates.MinBy(p => Math.Abs(p.Midpoint - boundary)).Midpoint;
    }

    private List<double> EnforceFrameConstraints(
        IReadOnlyList<double> chosen,
        IReadOnlyList<double> original,
        double videoDuration,
        double fps)
    {
        var maxFrame = Math.Max(1, FrameTime.SecondsToFrame(videoDuration, fps));
        var minFrames = Math.Max(1, FrameTime.SecondsToFrame(_config.MinSegmentDuration, fps));
        var maxShiftFrames = Math.Max(1, FrameTime.SecondsToFrame(_config.MaxBoundaryShift, fps));
        var originalFrames = original.Select(x => FrameTime.SecondsToFrame(x, fps)).ToArray();
        var frames = chosen.Select(x => FrameTime.SecondsToFrame(x, fps)).ToArray();

        for (var i = 0; i < frames.Length; i++)
        {
            if (Math.Abs(frames[i] - originalFrames[i]) > maxShiftFrames)
            {
                frames[i] = originalFrames[i];
            }
            frames[i] = Math.Clamp(frames[i], 1, maxFrame);
        }

        for (var i = 0; i < frames.Length; i++)
        {
            var lower = i == 0 ? minFrames : frames[i - 1] + minFrames;
            var remaining = frames.Length - i - 1;
            var upper = maxFrame - remaining * minFrames;
            if (upper < lower)
            {
                upper = lower;
            }
            frames[i] = Math.Clamp(frames[i], lower, Math.Max(lower, upper));
        }

        return frames.Select(frame => FrameTime.FrameToSeconds(frame, fps)).ToList();
    }
}
