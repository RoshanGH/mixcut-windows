using MixCut.Services.VideoProcessing;
using Xunit;

namespace MixCut.Tests;

/// <summary>
/// hover 预览裸帧管道 ffmpeg 参数拼接的纯函数单测。
/// 对应 spec §10：把可纯函数化的逻辑抽出来测，播放器内核本身靠构建机 e2e。
/// </summary>
public class FramePipeArgsTests
{
    [Fact]
    public void VideoArgs_含裸帧bgra与裁剪缩放与起止()
    {
        var args = FramePipeArgs.Video("C:\\v.mp4", start: 1.5, dur: 3.0, w: 320, h: 180, fps: 30);
        var s = string.Join(" ", args);
        Assert.Contains("-ss 1.5", s);
        Assert.Contains("-t 3", s);
        Assert.Contains("-an", s);
        Assert.Contains("-f rawvideo", s);
        Assert.Contains("-pix_fmt bgra", s);
        Assert.Contains("scale=320:180", s);
        Assert.Contains("pad=320:180", s);  // 补黑边到精确尺寸，保证定长帧
        Assert.Contains("fps=30", s);
        Assert.Equal("pipe:1", args[^1]);
    }

    [Fact]
    public void AudioArgs_含s16le立体声采样率()
    {
        var s = string.Join(" ", FramePipeArgs.Audio("C:\\v.mp4", start: 0, dur: 5));
        Assert.Contains("-vn", s);
        Assert.Contains("-f s16le", s);
        Assert.Contains("-ar 44100", s);
        Assert.Contains("-ac 2", s);
    }

    [Fact]
    public void FrameBytes_等于宽高乘4()
    {
        Assert.Equal(320 * 180 * 4, FramePipeArgs.FrameBytes(320, 180));
    }

    [Fact]
    public void VideoArgs_起点为0也能正确拼出()
    {
        var args = FramePipeArgs.Video("C:\\v.mp4", start: 0, dur: 2.5, w: 640, h: 360, fps: 25);
        var s = string.Join(" ", args);
        Assert.Contains("-ss 0", s);
        Assert.Contains("-t 2.5", s);
        Assert.Contains("scale=640:360", s);
        Assert.Contains("fps=25", s);
    }
}
