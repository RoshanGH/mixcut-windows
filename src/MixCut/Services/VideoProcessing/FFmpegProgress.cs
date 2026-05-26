namespace MixCut.Services.VideoProcessing;

/// <summary>FFmpeg 执行进度。对应 macOS 版 FFmpegProgress。</summary>
public readonly record struct FFmpegProgress(
    int Frame,
    double Fps,
    double Time,
    double Speed,
    double Percentage)
{
    public static readonly FFmpegProgress Zero = new(0, 0, 0, 0, 0);
}
