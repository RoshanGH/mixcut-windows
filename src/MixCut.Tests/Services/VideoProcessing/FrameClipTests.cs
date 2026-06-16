using MixCut.Services.VideoProcessing;
using Xunit;

namespace MixCut.Tests.Services.VideoProcessing;

public class FrameClipTests
{
    [Fact]
    public void FrameClip_DurationComesOnlyFromFrameCount()
    {
        var clip = new FrameClip("video.mp4", 150, 181, 30);

        Assert.Equal(31, clip.FrameCount);
        Assert.Equal(5.0, clip.StartSeconds, 9);
        Assert.Equal(181.0 / 30.0, clip.EndSeconds, 9);
        Assert.Equal(31.0 / 30.0, clip.Duration, 9);
    }
}
