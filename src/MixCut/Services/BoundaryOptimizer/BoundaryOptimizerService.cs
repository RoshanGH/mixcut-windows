using System.Globalization;
using MixCut.Services.ASR;
using MixCut.Services.SceneDetection;

namespace MixCut.Services.BoundaryOptimizer;

/// <summary>边界优化配置。对应 macOS 版 BoundaryOptimizationConfig。</summary>
public sealed class BoundaryOptimizationConfig
{
    /// <summary>阶段1：ASR 句子边界吸附最大偏移量（秒）。</summary>
    public double AsrSnapMaxOffset { get; init; } = 0.8;

    /// <summary>阶段2：场景切换对齐搜索范围（秒）。</summary>
    public double SceneSearchRange { get; init; } = 0.3;

    /// <summary>阶段3：静音段吸附搜索范围（秒）。</summary>
    public double SilenceSearchRange { get; init; } = 0.5;

    /// <summary>阶段4：I-frame 对齐最大距离（秒）。</summary>
    public double IframeAlignMaxDistance { get; init; } = 0.1;

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
/// 四阶段边界优化服务。对应 macOS 版 BoundaryOptimizerService。
/// 阶段1 句子结束吸附 → 阶段2 场景切换对齐 → 阶段3 静音段吸附 → 阶段4 I-frame 对齐 → 安全约束。
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

        // 阶段1：ASR 句子结束吸附（最高优先级）。
        var optimized = SnapToSentenceEnds(original, asrSentences);
        // 阶段2：场景切换对齐。
        optimized = AlignToSceneChanges(optimized, localAnalysis.SceneBoundaries);
        // 阶段3：静音段吸附。
        optimized = SnapToSilence(optimized, localAnalysis.SilencePeriods);
        // 阶段4：I-frame 对齐。
        optimized = AlignToIFrames(optimized, localAnalysis.IframePositions);
        // 安全约束。
        optimized = EnforceConstraints(optimized, original, localAnalysis.VideoDuration);

        var shifts = original.Zip(optimized, (o, n) => Math.Abs(n - o)).ToList();
        var report = new BoundaryOptimizationReport(
            original, optimized, shifts,
            shifts.Count == 0 ? 0 : shifts.Sum() / shifts.Count,
            shifts.Count == 0 ? 0 : shifts.Max(),
            shifts.Count(s => s > 0.01));

        return (optimized, report);
    }

    // 阶段1：将切点吸附到最近的句子结束时间。
    private List<double> SnapToSentenceEnds(
        List<double> boundaries, IReadOnlyList<TranscriptionSentence> sentences)
    {
        if (sentences.Count == 0)
        {
            return boundaries;
        }

        var sentenceEnds = sentences.Select(s => s.EndTime).OrderBy(e => e).ToList();
        return boundaries.Select(boundary =>
        {
            double? bestEnd = null;
            var bestDistance = double.PositiveInfinity;
            foreach (var end in sentenceEnds)
            {
                var distance = Math.Abs(end - boundary);
                if (distance <= _config.AsrSnapMaxOffset && distance < bestDistance)
                {
                    bestDistance = distance;
                    bestEnd = end;
                }
            }
            return bestEnd ?? boundary;
        }).ToList();
    }

    // 阶段2：对齐到最近的画面切换点。
    private List<double> AlignToSceneChanges(
        List<double> boundaries, IReadOnlyList<SceneBoundary> sceneChanges)
    {
        if (sceneChanges.Count == 0)
        {
            return boundaries;
        }

        var sceneTimes = sceneChanges.Select(s => s.Time).OrderBy(t => t).ToList();
        return boundaries.Select(boundary =>
        {
            var candidates = sceneTimes
                .Where(t => Math.Abs(t - boundary) <= _config.SceneSearchRange)
                .ToList();
            if (candidates.Count == 0)
            {
                return boundary;
            }
            return candidates.MinBy(t => Math.Abs(t - boundary));
        }).ToList();
    }

    // 阶段3：吸附到附近静音段中点。
    private List<double> SnapToSilence(
        List<double> boundaries, IReadOnlyList<SilencePeriod> silencePeriods)
    {
        if (silencePeriods.Count == 0)
        {
            return boundaries;
        }

        return boundaries.Select(boundary =>
        {
            // 切点已在某静音段内 → 移到中点。
            foreach (var period in silencePeriods)
            {
                if (period.Start <= boundary && boundary <= period.End)
                {
                    return period.Midpoint;
                }
            }

            // 查找附近的静音段。
            var nearby = silencePeriods
                .Where(p => Math.Abs(p.Midpoint - boundary) <= _config.SilenceSearchRange)
                .ToList();
            if (nearby.Count == 0)
            {
                return boundary;
            }
            return nearby.MinBy(p => Math.Abs(p.Midpoint - boundary)).Midpoint;
        }).ToList();
    }

    // 阶段4：微调到最近的 I-frame。
    private List<double> AlignToIFrames(List<double> boundaries, IReadOnlyList<double> iframePositions)
    {
        if (iframePositions.Count == 0)
        {
            return boundaries;
        }

        return boundaries.Select(boundary =>
        {
            var closest = iframePositions.MinBy(t => Math.Abs(t - boundary));
            return Math.Abs(closest - boundary) <= _config.IframeAlignMaxDistance ? closest : boundary;
        }).ToList();
    }

    // 安全约束。
    private List<double> EnforceConstraints(
        List<double> boundaries, List<double> originalBoundaries, double videoDuration)
    {
        var result = new List<double>(boundaries);

        // 约束1：限制最大移动距离。
        for (var i = 0; i < result.Count && i < originalBoundaries.Count; i++)
        {
            if (Math.Abs(result[i] - originalBoundaries[i]) > _config.MaxBoundaryShift)
            {
                result[i] = originalBoundaries[i];
            }
        }

        // 约束2：保证边界严格递增。
        for (var i = 1; i < result.Count; i++)
        {
            if (result[i] <= result[i - 1])
            {
                result[i] = result[i - 1] + _config.MinSegmentDuration;
            }
        }

        // 约束3：保证最小片段时长。
        if (result.Count > 0 && result[0] < _config.MinSegmentDuration)
        {
            result[0] = _config.MinSegmentDuration;
        }
        if (result.Count > 0 && videoDuration - result[^1] < _config.MinSegmentDuration)
        {
            result[^1] = videoDuration - _config.MinSegmentDuration;
        }

        // 约束4：clamp 在视频时长范围内（保持数组长度不变）。
        for (var i = 0; i < result.Count; i++)
        {
            result[i] = Math.Max(0.01, Math.Min(result[i], videoDuration - 0.01));
        }

        return result;
    }
}
