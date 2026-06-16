using MixCut.Models;
using Xunit;

namespace MixCut.Tests.Models;

public class SegmentFrameBoundsTests
{
    [Fact]
    public void SetBoundsFrames_MakesSecondsDerivedAndEndExclusive()
    {
        var segment = new Segment();

        segment.SetBoundsFrames(150, 181, 30);

        Assert.Equal(150, segment.StartFrame);
        Assert.Equal(181, segment.EndFrame);
        Assert.Equal(180, segment.LastFrame);
        Assert.Equal(31, segment.DurationFrames);
        Assert.Equal(5.0, segment.StartTime, 9);
        Assert.Equal(181.0 / 30.0, segment.EndTime, 9);
        Assert.Equal("00:05:00", segment.StartTimecode);
        Assert.Equal("00:06:01", segment.EndTimecode);
        Assert.Equal("00:06:00", segment.LastFrameTimecode);
    }

    [Fact]
    public void SetBoundsFrames_AlwaysKeepsAtLeastOneFrame()
    {
        var segment = new Segment();

        segment.SetBoundsFrames(42, 42, 30);

        Assert.Equal(42, segment.StartFrame);
        Assert.Equal(43, segment.EndFrame);
        Assert.Equal(1, segment.DurationFrames);
    }
}
