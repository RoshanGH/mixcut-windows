using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LibVLCSharp.Shared;

namespace MixCut.Views.Components;

/// <summary>
/// 原地视频播放器：缩略图 ↔ 播放态切换。对应 macOS 版 InlineVideoPlayer。
/// v0.6.0 起底层换为 VideoLAN LibVLCSharp（前代 WPF MediaElement 无法精确感知 seek 完成时机，
/// 会闪现视频第 0 秒画面，根治不了）。
///
/// 防闪烁核心：
/// - VideoView（VLC HwndHost 渲染）默认 Collapsed → 在 visual tree 不存在，画面绝不暴露
/// - ThumbImage 默认 Visible 显示分镜首帧
/// - hover/click 触发：MediaPlayer 后台 Play + Seek 到 startTime，VideoView 仍 Collapsed
/// - 监听 MediaPlayer.TimeChanged，当 Time ≥ startMs 时才把 VideoView 设 Visible
/// - 用户从 ThumbImage 直接看到正确画面，零中间帧
/// </summary>
public partial class InlineVideoPlayer : UserControl
{
    private string? _videoPath;
    private double? _segmentStart;
    private double? _segmentEnd;
    private bool _isSeeking;
    private DispatcherTimer? _uiTimer;
    private DispatcherTimer? _hoverTimer;
    private bool _isPlaying;
    private bool _videoViewShown;

    // VLC 全局单例由 Infrastructure.VlcBootstrap 提供：启动期做文件完备性检查（VLC-01/02），
    // 真正 LibVLC 实例 Lazy 化在第一次 hover 同步 init（VlcBootstrap.Shared）。
    // Lazy 失败已在 VlcBootstrap 内弹窗 VLC-03。
    private static LibVLC SharedLibVlc => Infrastructure.VlcBootstrap.Shared;

    private MediaPlayer? _mediaPlayer;

    /// <summary>
    /// 全局协调：任意 InlineVideoPlayer 开始播放时通知其他实例停止。
    /// 对齐 Mac viewModel.requestPlay 的「全局唯一播放」行为。
    /// </summary>
    private static event Action<InlineVideoPlayer>? PlaybackStarted;

    /// <summary>true 时鼠标停 350ms 自动播放，离开立即停。分镜库默认 true，ImportView 默认 false。</summary>
    public bool AutoPlayOnHover { get; set; }

    public InlineVideoPlayer()
    {
        InitializeComponent();
        // 不在构造时实例化 MediaPlayer —— LibVLC 首次实例化 200ms~2s，会卡 hover 创建瞬间。
        // 延迟到 EnsureMediaPlayer（OnPlayClick 第一次调用）按需创建。

        Unloaded += (_, _) =>
        {
            PlaybackStarted -= OnAnotherPlayerStarted;
            Stop();
            DisposeMediaPlayer();
        };
        Loaded += (_, _) =>
        {
            PlaybackStarted -= OnAnotherPlayerStarted;
            PlaybackStarted += OnAnotherPlayerStarted;
        };
        MouseEnter += OnRootMouseEnter;
        MouseLeave += OnRootMouseLeave;
    }

    /// <summary>按需创建 MediaPlayer（首次 hover 播放时调用）。失败返回 false，已弹窗。</summary>
    private bool EnsureMediaPlayer()
    {
        if (_mediaPlayer is not null) return true;
        try
        {
            _mediaPlayer = new MediaPlayer(SharedLibVlc);
            VideoView.MediaPlayer = _mediaPlayer;
            _mediaPlayer.TimeChanged += OnVlcTimeChanged;
            _mediaPlayer.EncounteredError += OnVlcError;
            _mediaPlayer.EndReached += OnVlcEndReached;
            return true;
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "[InlinePlayer] EnsureMediaPlayer 失败");
            return false;
        }
    }

    private void DisposeMediaPlayer()
    {
        if (_mediaPlayer is null) return;
        try
        {
            _mediaPlayer.TimeChanged -= OnVlcTimeChanged;
            _mediaPlayer.EncounteredError -= OnVlcError;
            _mediaPlayer.EndReached -= OnVlcEndReached;
            _mediaPlayer.Stop();
            _mediaPlayer.Dispose();
        }
        catch (Exception) { /* ignore */ }
        _mediaPlayer = null;
    }

    private void OnAnotherPlayerStarted(InlineVideoPlayer who)
    {
        if (!ReferenceEquals(who, this) && _isPlaying)
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
            if (IsMouseOver && !_isPlaying)
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
        if (_isPlaying)
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

        // 用户调 IN/OUT 时如果正在播：重新 seek 到新 startTime
        if (_isPlaying && _mediaPlayer is not null && _mediaPlayer.Length > 0)
        {
            var posMs = _mediaPlayer.Time;
            var startMs = (long)(startTime * 1000);
            var endMs = (long)(endTime * 1000);
            if (posMs < startMs || posMs > endMs)
            {
                // 重新隐藏 VideoView，等 seek 完成后再显示
                VideoView.Visibility = Visibility.Collapsed;
                _videoViewShown = false;
                _mediaPlayer.Time = startMs;
            }
            ProgressSlider.Minimum = startMs / 1000.0;
            ProgressSlider.Maximum = endMs / 1000.0;
        }
    }

    private static BitmapImage? LoadThumb(string? path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
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
        catch (Exception) { return null; }
    }

    private void OnPlayClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_videoPath) || !File.Exists(_videoPath)) return;

        // 首次 hover 播放时才同步初始化 LibVLC + MediaPlayer（避免启动期阻塞）。
        if (!EnsureMediaPlayer() || _mediaPlayer is null) return;

        PlaybackStarted?.Invoke(this);

        // 控制栏切换 + 标记播放中。VideoView 仍 Collapsed → ThumbImage 继续显示分镜首帧。
        ThumbControls.Visibility = Visibility.Collapsed;
        PlayerControls.Visibility = Visibility.Visible;
        _isPlaying = true;
        _videoViewShown = false;

        // 后台启动 VLC：Play + Seek（VideoView 仍 Collapsed，画面不暴露）
        try
        {
            using var media = new Media(SharedLibVlc, _videoPath, FromType.FromPath);
            _mediaPlayer.Play(media);

            // 直接 seek 到分镜起点。VLC TimeChanged 事件会报告真实播放位置，
            // 监听到 Time ≥ startMs 才把 VideoView Visibility=Visible，零闪烁
            if (_segmentStart is { } start && start > 0)
            {
                _mediaPlayer.Time = (long)(start * 1000);
            }
            else
            {
                // 整段视频模式：从 0 开始播，立即可显示 VideoView
                Dispatcher.BeginInvoke(new Action(ShowVideoView));
            }
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "[InlinePlayer] Play 失败");
            Stop();
            return;
        }

        StartUiTimer();
    }

    /// <summary>把 VideoView 揭开（覆盖到 ThumbImage 之上）。只触发一次。</summary>
    private void ShowVideoView()
    {
        if (_videoViewShown || !_isPlaying) return;
        _videoViewShown = true;
        VideoView.Visibility = Visibility.Visible;
    }

    private void OnVlcTimeChanged(object? sender, MediaPlayerTimeChangedEventArgs e)
    {
        // VLC 事件在 worker thread，必须 marshal 回 UI 线程
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!_isPlaying || _mediaPlayer is null) return;

            var posMs = e.Time;

            // 首次到达分镜起点：揭开 VideoView
            if (!_videoViewShown && _segmentStart is { } start)
            {
                var startMs = (long)(start * 1000);
                if (posMs >= startMs - 50)
                {
                    ShowVideoView();
                }
            }

            // 到达分镜终点：停止
            if (_segmentEnd is { } end && posMs >= (long)(end * 1000))
            {
                Stop();
                return;
            }
        }));
    }

    private void OnVlcError(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            Serilog.Log.Warning("[InlinePlayer] VLC encountered error: {Path}", _videoPath);
            Stop();
        }));
    }

    private void OnVlcEndReached(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(new Action(Stop));
    }

    private void OnPauseClick(object sender, RoutedEventArgs e)
    {
        if (_mediaPlayer is null) return;
        if (_mediaPlayer.IsPlaying)
        {
            _mediaPlayer.Pause();
            PauseButton.Content = "▶";
        }
        else
        {
            _mediaPlayer.Play();
            PauseButton.Content = "⏸";
        }
    }

    private void OnStopClick(object sender, RoutedEventArgs e) => Stop();

    private void Stop()
    {
        StopUiTimer();
        try
        {
            if (_mediaPlayer is not null && _mediaPlayer.IsPlaying)
            {
                _mediaPlayer.Stop();
            }
        }
        catch (Exception) { /* ignore */ }

        _isPlaying = false;
        _videoViewShown = false;
        VideoView.Visibility = Visibility.Collapsed;
        ThumbControls.Visibility = Visibility.Visible;
        PlayerControls.Visibility = Visibility.Collapsed;
        PauseButton.Content = "⏸";
    }

    private void OnSliderDragStart(object sender, DragStartedEventArgs e) => _isSeeking = true;

    private void OnSliderDragEnd(object sender, DragCompletedEventArgs e)
    {
        if (_mediaPlayer is not null)
        {
            _mediaPlayer.Time = (long)(ProgressSlider.Value * 1000);
        }
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

    /// <summary>UI 层定时器：刷新进度条 / 时间显示（VLC TimeChanged 太频繁，UI 用 200ms 一刷就够）。</summary>
    private void StartUiTimer()
    {
        StopUiTimer();
        _uiTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _uiTimer.Tick += (_, _) =>
        {
            if (_mediaPlayer is null || !_isPlaying || _mediaPlayer.Length <= 0) return;
            var posSec = _mediaPlayer.Time / 1000.0;
            if (!_isSeeking)
            {
                ProgressSlider.Value = posSec;
                var offset = posSec - (_segmentStart ?? 0);
                CurrentTimeText.Text = FormatTime(Math.Max(0, offset));
            }
            if (_mediaPlayer.Length > 0 && DurationText.Text == "0:00")
            {
                var startSec = _segmentStart ?? 0;
                var endSec = _segmentEnd ?? _mediaPlayer.Length / 1000.0;
                DurationText.Text = FormatTime(endSec - startSec);
                ProgressSlider.Minimum = startSec;
                ProgressSlider.Maximum = endSec;
            }
        };
        _uiTimer.Start();
    }

    private void StopUiTimer()
    {
        _uiTimer?.Stop();
        _uiTimer = null;
    }

    private static string FormatTime(double seconds)
    {
        var ts = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return ts.TotalHours >= 1
            ? ts.ToString(@"h\:mm\:ss")
            : ts.ToString(@"m\:ss");
    }
}
