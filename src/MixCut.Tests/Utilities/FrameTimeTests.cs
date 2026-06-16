using MixCut.Utilities;
using Xunit;

namespace MixCut.Tests.Utilities;

/// <summary>
/// FrameTime 帧/秒/时间码互转单元测试。纯函数、零依赖，覆盖 issue #7 验收点
/// 「时间码按 fps 进位（30/60fps 正确）」+「帧↔秒互转」+「量化到帧」。
/// </summary>
public class FrameTimeTests
{
    [Theory]
    [InlineData(1.0, 30.0, 30)]
    [InlineData(0.5, 30.0, 15)]
    [InlineData(2.0, 60.0, 120)]
    [InlineData(0.0, 30.0, 0)]
    public void SecondsToFrame_RoundsToNearest(double seconds, double fps, int expected)
    {
        Assert.Equal(expected, FrameTime.SecondsToFrame(seconds, fps));
    }

    [Theory]
    [InlineData(1.016, 30.0, 30)] // 1.016*30=30.48 → 30
    [InlineData(1.017, 30.0, 31)] // 1.017*30=30.51 → 31
    public void SecondsToFrame_RoundsHalfAwayFromZero(double seconds, double fps, int expected)
    {
        Assert.Equal(expected, FrameTime.SecondsToFrame(seconds, fps));
    }

    [Theory]
    [InlineData(0, 30.0)]
    [InlineData(-5, 30.0)]
    [InlineData(30, 0)]   // fps 非法
    public void SecondsToFrame_InvalidReturnsZero(double seconds, double fps)
    {
        Assert.Equal(0, FrameTime.SecondsToFrame(seconds, fps));
    }

    [Fact]
    public void FrameToSeconds_IsExactInverse()
    {
        Assert.Equal(1.0, FrameTime.FrameToSeconds(30, 30.0), 9);
        Assert.Equal(2.0, FrameTime.FrameToSeconds(120, 60.0), 9);
        Assert.Equal(0.0, FrameTime.FrameToSeconds(-3, 30.0));
        Assert.Equal(0.0, FrameTime.FrameToSeconds(30, 0));
    }

    [Fact]
    public void QuantizeToFrame_SnapsBetweenFrames()
    {
        // 30fps 下，3.512s 落在第 105 帧(3.5s)与第 106 帧(3.5333s)之间，四舍五入到 105 → 3.5s
        Assert.Equal(3.5, FrameTime.QuantizeToFrame(3.512, 30.0), 9);
        // fps≤0：原样返回（非负）
        Assert.Equal(3.512, FrameTime.QuantizeToFrame(3.512, 0), 9);
        Assert.Equal(0.0, FrameTime.QuantizeToFrame(-1, 30.0));
    }

    [Theory]
    [InlineData(0, 30.0, "00:00:00")]
    [InlineData(30, 30.0, "00:01:00")]  // 30 帧 = 1 秒整，帧位归零
    [InlineData(45, 30.0, "00:01:15")]  // 1 秒 + 15 帧
    [InlineData(89, 30.0, "00:02:29")]  // 满 30 进位：89=2*30+29
    [InlineData(90, 30.0, "00:03:00")]
    [InlineData(119, 60.0, "00:01:59")] // 60fps 满 60 才进位
    [InlineData(120, 60.0, "00:02:00")]
    public void ToTimecode_CarriesByFps(int frame, double fps, string expected)
    {
        Assert.Equal(expected, FrameTime.ToTimecode(frame, fps));
    }

    [Fact]
    public void ToTimecode_ShowsHoursWhenPresent()
    {
        // 1 小时 = 3600 秒 * 30 帧 = 108000 帧
        Assert.Equal("01:00:00:00", FrameTime.ToTimecode(108000, 30.0));
    }

    [Fact]
    public void ToTimecode_InvalidFpsFallsBack()
    {
        Assert.Equal("00:00", FrameTime.ToTimecode(30, 0));
    }

    [Fact]
    public void ToTimecodeFromSeconds_Works()
    {
        Assert.Equal("00:01:15", FrameTime.ToTimecodeFromSeconds(1.5, 30.0)); // 1.5s*30=45 帧
        // fps≤0：退化为 分:秒
        Assert.Equal("00:03", FrameTime.ToTimecodeFromSeconds(3.7, 0));
    }

    [Fact]
    public void NonIntegerFps_UsesRealRateForSecondsAndNominalRateForTimecode()
    {
        Assert.Equal(30, FrameTime.NominalFps(29.97));
        Assert.Equal(60, FrameTime.NominalFps(59.94));
        Assert.Equal(10.01001001, FrameTime.FrameToSeconds(300, 29.97), 8);
        Assert.Equal("00:10:00", FrameTime.ToTimecode(300, 29.97));
    }

    [Theory]
    [InlineData(0, 30, 29)]
    [InlineData(120, 180, 179)]
    [InlineData(5, 5, 5)]
    public void LastIncludedFrame_UsesExclusiveEnd(int start, int end, int expected)
    {
        Assert.Equal(expected, FrameTime.LastIncludedFrame(start, end));
    }
}
