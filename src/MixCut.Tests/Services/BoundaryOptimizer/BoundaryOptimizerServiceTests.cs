using MixCut.Models;
using MixCut.Services.ASR;
using MixCut.Services.BoundaryOptimizer;
using MixCut.Services.SceneDetection;
using Xunit;

namespace MixCut.Tests.Services.BoundaryOptimizer;

/// <summary>
/// BoundaryOptimizerService 四阶段边界优化算法单元测试。
/// 该服务是纯算法（构造仅吃 config，Optimize 输入数据返回边界），无外部依赖，可完整单测。
/// </summary>
public class BoundaryOptimizerServiceTests
{
    private static VideoLocalAnalysis Analysis(
        double duration = 10.0,
        IReadOnlyList<SceneBoundary>? scenes = null,
        IReadOnlyList<SilencePeriod>? silences = null,
        IReadOnlyList<double>? iframes = null)
        => new(
            scenes ?? Array.Empty<SceneBoundary>(),
            silences ?? Array.Empty<SilencePeriod>(),
            iframes ?? Array.Empty<double>(),
            duration,
            30.0);

    private static TranscriptionSentence Sentence(double end)
        => new(string.Empty, Math.Max(0, end - 1), end, Array.Empty<AsrWord>());

    [Fact]
    public void Optimize_EmptyBoundaries_ReturnsEmpty()
    {
        var svc = new BoundaryOptimizerService();
        var (boundaries, report) = svc.Optimize(
            Array.Empty<double>(), Array.Empty<TranscriptionSentence>(), Analysis());

        Assert.Empty(boundaries);
        Assert.Equal(0, report.SegmentsAffected);
        Assert.Equal(0, report.MaxShift);
    }

    [Fact]
    public void Optimize_NoOptimizationData_OutputStrictlyIncreasingAndInBounds()
    {
        var svc = new BoundaryOptimizerService();
        var input = new double[] { 2.0, 4.0, 6.0, 8.0 };
        var (boundaries, _) = svc.Optimize(input, Array.Empty<TranscriptionSentence>(), Analysis(duration: 10));

        Assert.Equal(input.Length, boundaries.Count);
        for (var i = 1; i < boundaries.Count; i++)
        {
            Assert.True(boundaries[i] > boundaries[i - 1], "边界必须严格递增");
        }
        Assert.All(boundaries, b => Assert.InRange(b, 0.01, 10.0));
    }

    [Fact]
    public void Optimize_SnapsBoundaryToNearbySentenceEnd()
    {
        var svc = new BoundaryOptimizerService();
        // 切点 5.0 距句子结束 5.3 只差 0.3 < AsrSnapMaxOffset(0.8) → 应吸附到 5.3
        var (boundaries, _) = svc.Optimize(
            new[] { 5.0 }, new[] { Sentence(5.3) }, Analysis(duration: 10));

        Assert.Single(boundaries);
        Assert.Equal(5.3, boundaries[0], precision: 3);
    }

    [Fact]
    public void Optimize_DoesNotSnapWhenSentenceEndTooFar()
    {
        var svc = new BoundaryOptimizerService();
        // 句子结束 7.0 距切点 5.0 有 2.0 > 0.8 → 不吸附
        var (boundaries, _) = svc.Optimize(
            new[] { 5.0 }, new[] { Sentence(7.0) }, Analysis(duration: 10));

        Assert.Equal(5.0, boundaries[0], precision: 3);
    }

    [Fact]
    public void Optimize_CollidingSnaps_EnforceStrictlyIncreasing()
    {
        var svc = new BoundaryOptimizerService();
        // 两个切点都被吸附到同一句子结束 3.5 → 约束2 应把第二个推到 3.5 + MinSegmentDuration(1.0)=4.5
        var (boundaries, _) = svc.Optimize(
            new[] { 3.0, 3.2 }, new[] { Sentence(3.5) }, Analysis(duration: 10));

        Assert.Equal(2, boundaries.Count);
        Assert.True(boundaries[1] > boundaries[0], "碰撞后必须仍严格递增");
    }

    [Fact]
    public void Optimize_AlignsToNearbySceneChange()
    {
        var svc = new BoundaryOptimizerService();
        // 切点 4.0 附近有场景切换 4.2（距 0.2 ≤ SceneSearchRange 0.3）→ 对齐到 4.2
        var scenes = new[] { new SceneBoundary(4.2, 0.9) };
        var (boundaries, _) = svc.Optimize(
            new[] { 4.0 }, Array.Empty<TranscriptionSentence>(), Analysis(duration: 10, scenes: scenes));

        Assert.Equal(4.2, boundaries[0], precision: 3);
    }

    [Fact]
    public void Optimize_StrongSceneWinsOverCloserSentenceAndSilence()
    {
        var svc = new BoundaryOptimizerService();
        var scenes = new[] { new SceneBoundary(5.4, 0.8) };
        var silences = new[] { new SilencePeriod(5.05, 5.15) };

        var (boundaries, _) = svc.Optimize(
            new[] { 5.0 },
            new[] { Sentence(5.1) },
            Analysis(duration: 10, scenes: scenes, silences: silences));

        Assert.Equal(5.4, boundaries[0], precision: 3);
    }

    [Fact]
    public void Optimize_WeakSceneDoesNotBeatSentence()
    {
        var svc = new BoundaryOptimizerService();
        var scenes = new[] { new SceneBoundary(5.2, 0.1) };

        var (boundaries, _) = svc.Optimize(
            new[] { 5.0 },
            new[] { Sentence(5.1) },
            Analysis(duration: 10, scenes: scenes));

        Assert.Equal(5.1, boundaries[0], precision: 3);
    }

    [Fact]
    public void Optimize_QuantizesEveryBoundaryToFrameGrid()
    {
        var svc = new BoundaryOptimizerService();
        var (boundaries, _) = svc.Optimize(
            new[] { 3.512 },
            Array.Empty<TranscriptionSentence>(),
            Analysis(duration: 10));

        Assert.Equal(3.5, boundaries[0], precision: 9);
    }

    [Fact]
    public void Optimize_ClampsWithinVideoDuration()
    {
        var svc = new BoundaryOptimizerService();
        // 越界输入（含 > duration 与 < 0）→ 最后一个结束边界允许等于视频总帧。
        var (boundaries, _) = svc.Optimize(
            new[] { -5.0, 3.0, 999.0 }, Array.Empty<TranscriptionSentence>(), Analysis(duration: 10));

        Assert.All(boundaries, b => Assert.InRange(b, 0.01, 10.0));
    }

    [Fact]
    public void Optimize_ReportShiftsMatchBoundaryMovement()
    {
        var svc = new BoundaryOptimizerService();
        var (boundaries, report) = svc.Optimize(
            new[] { 5.0 }, new[] { Sentence(5.3) }, Analysis(duration: 10));

        // 切点从 5.0 吸附到 5.3，位移约 0.3
        Assert.Single(report.Shifts);
        Assert.Equal(0.3, report.Shifts[0], precision: 2);
        Assert.Equal(1, report.SegmentsAffected);
        Assert.Equal(boundaries[0], report.OptimizedBoundaries[0], precision: 3);
    }
}
