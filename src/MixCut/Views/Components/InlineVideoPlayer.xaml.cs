using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace MixCut.Views.Components;

/// <summary>
/// 原地视频播放器：缩略图 ↔ 播放态切换。对应 macOS 版 InlineVideoPlayer。
/// 使用 WPF 内置 <see cref="MediaElement"/>（依赖系统媒体编解码）。
/// 支持完整视频播放和分镜片段播放（startTime → endTime）。
/// </summary>
public partial class InlineVideoPlayer : UserControl
{
    private string? _videoPath;
    private double? _segmentStart;
    private double? _segmentEnd;
    private bool _isSeeking;
    private DispatcherTimer? _timer;
    private DispatcherTimer? _hoverTimer;

    /// <summary>
    /// 全局协调：任意 InlineVideoPlayer 开始播放时通知其他实例停止。
    /// 对齐 Mac viewModel.requestPlay 的「全局唯一播放」行为。
    /// </summary>
    private static event Action<InlineVideoPlayer>? PlaybackStarted;

    /// <summary>true 时鼠标停 350ms 自动播放，离开立即停。分镜库默认 true，ImportView 默认 false。</summary>
    public bool AutoPlayOnHover { get; set; }

    /// <summary>
    /// 视频帧 + 缩略图的填充方式。默认 <see cref="System.Windows.Media.Stretch.Uniform"/>（等比适应、留黑边，ImportView 沿用）。
    /// 分镜库卡片是固定 9:16 框且 ClipToBounds，且卡片静态缩略图用的是 UniformToFill，
    /// 故注入到卡片里的 player 设 UniformToFill，让 hover 播放与静态缩略图一致铺满（修「播放不铺满」）。
    /// 同时设给 Player 与 ThumbImage，保证播放态/缩略态填充行为一致，不出现切换瞬间跳变。
    /// </summary>
    public System.Windows.Media.Stretch VideoStretch
    {
        get => Player.Stretch;
        set
        {
            Player.Stretch = value;
            ThumbImage.Stretch = value;
        }
    }

    public InlineVideoPlayer()
    {
        InitializeComponent();
        Unloaded += (_, _) =>
        {
            PlaybackStarted -= OnAnotherPlayerStarted;
            Stop();
        };
        Loaded += (_, _) =>
        {
            PlaybackStarted -= OnAnotherPlayerStarted;
            PlaybackStarted += OnAnotherPlayerStarted;
        };
        MouseEnter += OnRootMouseEnter;
        MouseLeave += OnRootMouseLeave;
    }

    private void OnAnotherPlayerStarted(InlineVideoPlayer who)
    {
        if (!ReferenceEquals(who, this) && PlayingState.Visibility == Visibility.Visible)
        {
            Stop();
        }
    }

    private void OnRootMouseEnter(object sender, MouseEventArgs e)
    {
        if (!AutoPlayOnHover) return;
        _hoverTimer?.Stop();
        _hoverTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        _hoverTimer.Tick += (_, _) =>
        {
            _hoverTimer?.Stop();
            _hoverTimer = null;
            // 计时器触发时鼠标仍在控件内 && 还没播 → 自动开播
            if (IsMouseOver && PlayingState.Visibility != Visibility.Visible)
            {
                OnPlayClick(this, new RoutedEventArgs());
            }
        };
        _hoverTimer.Start();
    }

    private void OnRootMouseLeave(object sender, MouseEventArgs e)
    {
        if (!AutoPlayOnHover) return;
        _hoverTimer?.Stop();
        _hoverTimer = null;
        // hover 模式：离开就停（对齐 Mac 行为）
        if (PlayingState.Visibility == Visibility.Visible)
        {
            Stop();
        }
    }

    /// <summary>设置完整视频（用于项目概览视频卡片）。</summary>
    public void SetVideo(string videoPath, string? thumbnailPath)
    {
        _videoPath = videoPath;
        _segmentStart = null;
        _segmentEnd = null;
        ThumbImage.Source = LoadThumb(thumbnailPath);
    }

    /// <summary>设置分镜片段（用于分镜库卡片）—— 播放时只播指定时间范围。</summary>
    public void SetSegment(string videoPath, string? thumbnailPath, double startTime, double endTime)
    {
        var videoPathChanged = _videoPath != videoPath;
        _videoPath = videoPath;
        _segmentStart = startTime;
        _segmentEnd = endTime;
        if (videoPathChanged)
        {
            ThumbImage.Source = LoadThumb(thumbnailPath);
        }
        var duration = endTime - startTime;
        DurationBadgeText.Text = duration.ToString("F1", System.Globalization.CultureInfo.InvariantCulture) + "s";
        DurationBadge.Visibility = Visibility.Visible;

        // 如果正在播放：根据新区间调整当前位置
        // - 当前位置 < 新 startTime → seek 到 startTime
        // - 当前位置 > 新 endTime → seek 到 startTime 重头开始（也可选择 Stop()，但用户调 ±0.1s 时连续看到内容更友好）
        if (PlayingState.Visibility == Visibility.Visible && Player.IsPlaying)
        {
            var posSec = Player.Position.TotalSeconds;
            if (posSec < startTime || posSec > endTime)
            {
                // 区间变了且当前位置越界：用新区间重启帧泵
                Player.Open(videoPath, startTime, endTime - startTime);
            }
            ProgressSlider.Minimum = startTime;
            ProgressSlider.Maximum = endTime;
        }
    }

    private static BitmapImage? LoadThumb(string? path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            return null;
        }
        try
        {
            using var stream = File.OpenRead(path);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = stream;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private void OnPlayClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_videoPath) || !File.Exists(_videoPath))
        {
            return;
        }
        // 通知其他 InlineVideoPlayer 实例停止（全局唯一播放）
        PlaybackStarted?.Invoke(this);

        ThumbnailState.Visibility = Visibility.Collapsed;
        PlayingState.Visibility = Visibility.Visible;
        var start = _segmentStart ?? 0;
        var dur = _segmentEnd is { } end ? end - start : 0; // 0 = 完整视频播到 EOF
        Player.Open(_videoPath, start, dur);
        StartTimer();
    }

    private void OnPauseClick(object sender, RoutedEventArgs e)
    {
        if (!Player.IsPaused)
        {
            Player.Pause();
            PauseButton.Content = "▶";
        }
        else
        {
            Player.Resume();
            PauseButton.Content = "⏸";
        }
    }

    private void OnStopClick(object sender, RoutedEventArgs e) => Stop();

    private void Stop()
    {
        StopTimer();
        try
        {
            Player.Stop();
        }
        catch (Exception)
        {
            // ignore
        }
        PlayingState.Visibility = Visibility.Collapsed;
        ThumbnailState.Visibility = Visibility.Visible;
        PauseButton.Content = "⏸";
    }

    private void OnMediaOpened(object? sender, EventArgs e)
    {
        // 帧泵已用 ffmpeg -ss 从起点开播，无需再 seek。
        var startSec = _segmentStart ?? 0;
        ProgressSlider.Minimum = startSec;
        if (_segmentEnd is { } endSec)
        {
            // 分镜模式：起止已知，滑块满量程
            DurationText.Text = FormatTime(endSec - startSec);
            ProgressSlider.Maximum = endSec;
        }
        else
        {
            // 完整视频：无系统级 NaturalDuration，滑块上限随播放位置增长兜底（见 StartTimer）
            ProgressSlider.Maximum = Math.Max(ProgressSlider.Maximum, startSec + 1);
        }
    }

    private void OnMediaEnded(object? sender, EventArgs e) => Stop();

    private void OnMediaFailed(object? sender, string reason)
    {
        // 总纲推论4：不暴露系统错误码/格式提示；静默退回缩略图（Stop 内已切回），仅日志留痕。
        // HEVC 等格式已由自带 ffmpeg 解码，走到这里基本只剩真正损坏的文件。
        Serilog.Log.Warning("[InlinePlayDiag] 预览失败: {Reason} path={Path}", reason, _videoPath);
        Stop();
    }

    private void OnSliderDragStart(object sender, DragStartedEventArgs e) => _isSeeking = true;

    private void OnSliderDragEnd(object sender, DragCompletedEventArgs e)
    {
        Player.Seek(ProgressSlider.Value); // 帧泵 seek = 用新起点重启 ffmpeg
        _isSeeking = false;
    }

    private void OnSliderValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_isSeeking)
        {
            var offset = e.NewValue - (_segmentStart ?? 0);
            CurrentTimeText.Text = FormatTime(Math.Max(0, offset));
        }
    }

    private void StartTimer()
    {
        StopTimer();
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _timer.Tick += (_, _) =>
        {
            var posSec = Player.Position.TotalSeconds;

            // 分镜模式：到达 endTime 自动停止
            if (_segmentEnd is { } end && posSec >= end)
            {
                Stop();
                return;
            }

            // 完整视频：时长未知，滑块上限随位置增长兜底
            if (_segmentEnd is null && posSec > ProgressSlider.Maximum)
            {
                ProgressSlider.Maximum = posSec;
            }

            if (!_isSeeking)
            {
                ProgressSlider.Value = posSec;
                var offset = posSec - (_segmentStart ?? 0);
                CurrentTimeText.Text = FormatTime(Math.Max(0, offset));
            }
        };
        _timer.Start();
    }

    private void StopTimer()
    {
        _timer?.Stop();
        _timer = null;
    }

    private static string FormatTime(double seconds)
    {
        var ts = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return ts.TotalHours >= 1
            ? ts.ToString(@"h\:mm\:ss")
            : ts.ToString(@"m\:ss");
    }
}
