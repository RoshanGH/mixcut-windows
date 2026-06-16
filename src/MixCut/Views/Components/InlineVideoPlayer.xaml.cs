using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MixCut.Utilities;

namespace MixCut.Views.Components;

/// <summary>
/// 原地视频播放器：缩略图 ↔ 播放态切换。对应 macOS 版 InlineVideoPlayer。
/// 解码走自带 <see cref="FfmpegFramePlayer"/>（进程外 ffmpeg 裸帧管道，与导出同源、不碰系统编解码器，
/// 见 CLAUDE.md §兼容性总纲）。支持完整视频播放和分镜片段播放（startFrame → endFrame）。
/// 丝滑铁律见 CLAUDE.md §H：首帧落地才起算墙钟、首帧前不显示播放层、整片传真实时长。
/// </summary>
public partial class InlineVideoPlayer : UserControl
{
    private string? _videoPath;
    private double? _segmentStart;
    private double? _segmentEnd;
    private int? _segmentStartFrame;
    private int? _segmentEndFrame;
    private double _segmentFps;
    /// <summary>整片模式（非分镜）下的已知总时长（秒）。0=未知，进度条上限随播放兜底增长。</summary>
    private double _fullDuration;
    private bool _isEnded;
    private bool _isSeeking;
    /// <summary>已请求播放（OnPlayClick 起 / Stop 止）。配合 _isEnded 推导 <see cref="IsPlaying"/>。</summary>
    private bool _active;
    /// <summary>由宿主预置的解码目标物理像素（见 <see cref="PrimeDecodeSize"/>），首帧免等同步布局。</summary>
    private int _primedW, _primedH;
    private DispatcherTimer? _timer;
    private DispatcherTimer? _hoverTimer;

    /// <summary>
    /// 全局协调：任意 InlineVideoPlayer 开始播放时通知其他实例停止。
    /// 对齐 Mac viewModel.requestPlay 的「全局唯一播放」行为。
    /// </summary>
    private static event Action<InlineVideoPlayer>? PlaybackStarted;

    /// <summary>true 时鼠标停 350ms 自动播放，离开立即停。全局已改为「点击播放」(false)，详见 §会话踩坑沉淀。</summary>
    public bool AutoPlayOnHover { get; set; }

    /// <summary>是否正处于实际播放中（已请求播放且未播完/未停止）。卡片据此决定鼠标移开时是否保留播放器。</summary>
    public bool IsPlaying => _active && !_isEnded;

    /// <summary>播放器回到「非播放」静止态（停止 / 播完冻结 / 失败回退）时触发，宿主卡片据此在鼠标已离开时清理还原缩略图。</summary>
    public event EventHandler? Idle;

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

    /// <summary>设置完整视频（用于项目概览视频卡片）。<paramref name="totalDurationSeconds"/> 已知时进度条上限/总时长直接正确显示（0=未知）。</summary>
    public void SetVideo(string videoPath, string? thumbnailPath, double totalDurationSeconds = 0)
    {
        _videoPath = videoPath;
        _segmentStart = null;
        _segmentEnd = null;
        _segmentStartFrame = null;
        _segmentEndFrame = null;
        _segmentFps = 0;
        _fullDuration = totalDurationSeconds > 0 ? totalDurationSeconds : 0;
        ThumbImage.Source = LoadThumb(thumbnailPath);
    }

    /// <summary>设置分镜片段（用于分镜库卡片）—— 播放时只播指定时间范围。</summary>
    public void SetSegment(string videoPath, string? thumbnailPath, double startTime, double endTime)
    {
        SetSegmentCore(videoPath, thumbnailPath, startTime, endTime, null, null, 0);
    }

    /// <summary>
    /// 设置帧精确分镜。StartFrame 包含、EndFrame 不包含，播放结束时保留 EndFrame-1。
    /// </summary>
    public void SetSegment(
        string videoPath, string? thumbnailPath,
        int startFrame, int endFrame, double fps)
    {
        var startTime = FrameTime.FrameToSeconds(startFrame, fps);
        var endTime = FrameTime.FrameToSeconds(endFrame, fps);
        SetSegmentCore(videoPath, thumbnailPath, startTime, endTime, startFrame, endFrame, fps);
    }

    private void SetSegmentCore(
        string videoPath, string? thumbnailPath, double startTime, double endTime,
        int? startFrame, int? endFrame, double fps)
    {
        var videoPathChanged = _videoPath != videoPath;
        var rangeChanged = _segmentStart != startTime || _segmentEnd != endTime
                           || _segmentStartFrame != startFrame || _segmentEndFrame != endFrame;
        _videoPath = videoPath;
        _segmentStart = startTime;
        _segmentEnd = endTime;
        _segmentStartFrame = startFrame;
        _segmentEndFrame = endFrame;
        _segmentFps = fps;
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
        if (PlayingState.Visibility == Visibility.Visible && rangeChanged && (Player.IsPlaying || _isEnded))
        {
            Serilog.Log.Information("[PlayerDiag] SetSegmentCore re-Open (rangeChanged) start={S}", startTime);
            var (tw, th) = FrameTargetPixels();
            Player.Open(videoPath, startTime, endTime - startTime, tw, th,
                startFrame ?? -1, endFrame ?? -1, fps);
            _isEnded = false;
            PauseButton.Content = "⏸";
            StartTimer();
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
            // 按显示尺寸解码，避免把全分辨率（竖屏 1080×1920）整图解进内存（消卡顿，详见 ThumbnailCache）。
            bitmap.DecodePixelWidth = 540;
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

    /// <summary>
    /// 本宿主控件的物理像素尺寸，作为帧泵的解码目标框。宿主一直可见、尺寸确定，
    /// 不像内部 Player 那样在 Collapsed→Visible 同步栈里读到 0。
    /// 自身尚未布局（ActualWidth=0，刚点击播放、未走布局）时回退到 <see cref="PrimeDecodeSize"/> 预置的尺寸，
    /// 避免退化成 240×134 小横帧黑屏（替代昂贵的同步 UpdateLayout）。
    /// </summary>
    private (int w, int h) FrameTargetPixels()
    {
        if (ActualWidth < 1 || ActualHeight < 1)
        {
            return (_primedW, _primedH); // 0,0 时帧泵自身回退
        }
        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this);
        var w = (int)Math.Round(ActualWidth * dpi.DpiScaleX);
        var h = (int)Math.Round(ActualHeight * dpi.DpiScaleY);
        return (w, h);
    }

    /// <summary>
    /// 预置解码目标物理像素（宿主在点击时已布局、尺寸确定，但刚塞进去的本控件 ActualWidth 还是 0）。
    /// 让首帧不必等一次昂贵的同步 UpdateLayout（数十张卡时 ~150-200ms）即可拿到正确竖屏尺寸。
    /// </summary>
    public void PrimeDecodeSize(int wPx, int hPx)
    {
        _primedW = wPx > 0 ? wPx : 0;
        _primedH = hPx > 0 ? hPx : 0;
    }

    /// <summary>外部触发播放（卡片上的静态 ▶ 调用）。等价于点内部播放按钮。</summary>
    public void Play() => OnPlayClick(this, new RoutedEventArgs());

    private void OnPlayClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_videoPath) || !File.Exists(_videoPath))
        {
            return;
        }
        // 通知其他 InlineVideoPlayer 实例停止（全局唯一播放）
        PlaybackStarted?.Invoke(this);
        _active = true;

        // 「点击即播」且「不黑屏」二者兼得：
        // 1) 播放层【立刻可见】——裸帧 Image 必须从点击起就在渲染管线里，否则把显示延迟到
        //    OnMediaOpened（后台帧泵线程触发）会导致可见性变更被推迟到下一次输入事件才刷新，
        //    表现为「点了不播、移动鼠标才开始播」（用户实测）。
        // 2) 同时把缩略图临时提到最上层（ZIndex=1）盖住首帧前的纯黑位图；首帧就绪后（OnMediaOpened）
        //    再隐藏缩略图揭开视频。视频其实一直在底层渲染，揭开即是活动画面，不依赖鼠标事件。
        // 3) 立刻把进度条量程设成真实区间并把 Value 置到起点，避免 StartTimer 用默认量程(0~1)钳位闪动。
        PlayingState.Visibility = Visibility.Visible;
        ThumbnailState.Visibility = Visibility.Visible; // 重播时此前可能已折叠，重新显示以盖住重启的黑位图
        System.Windows.Controls.Panel.SetZIndex(ThumbnailState, 1);
        var start = _segmentStart ?? 0;
        var dur = _segmentEnd is { } end ? end - start : 0; // 0 = 完整视频播到 EOF
        ProgressSlider.Minimum = start;
        ProgressSlider.Maximum = _segmentEnd ?? (_fullDuration > 0 ? start + _fullDuration : start + 1);
        ProgressSlider.Value = start;
        CurrentTimeText.Text = FormatTime(0);
        // 整片模式且总时长已知：右侧总时长立即正确显示（不必等 OnMediaOpened，修「整片进度条/总时长错误」）。
        if (_segmentEnd is null)
        {
            DurationText.Text = _fullDuration > 0 ? FormatTime(_fullDuration) : "0:00";
        }
        // 根因修复（修「hover 播放小图/黑屏」）：解码目标尺寸用本宿主 UserControl 的尺寸传给帧泵。
        // 宿主一直显示着缩略图、尺寸确定（ActualWidth 非 0）；而内部 Player 刚 Collapsed→Visible，
        // 自身 ActualWidth=0 不可靠（UpdateLayout 也救不回，已实证）。传宿主尺寸 → 帧按卡片比例解码。
        var (tw, th) = FrameTargetPixels();
        Serilog.Log.Information("[PlayerDiag] OnPlayClick→Open start={S} tw={TW} th={TH}", start, tw, th);
        Player.Open(_videoPath, start, dur, tw, th,
            _segmentStartFrame ?? -1, _segmentEndFrame ?? -1, _segmentFps);
        _isEnded = false;
        StartTimer();
    }

    private void OnPauseClick(object sender, RoutedEventArgs e)
    {
        if (_isEnded)
        {
            OnPlayClick(sender, e);
            return;
        }
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
        System.Windows.Controls.Panel.SetZIndex(ThumbnailState, 0); // 复位层级（OnPlayClick 曾提到最上层盖黑）
        _isEnded = false;
        PauseButton.Content = "⏸";
        _active = false;
        // 回到静止态：通知宿主卡片（鼠标已离开时把播放器拆掉、还原静态缩略图）。
        Idle?.Invoke(this, EventArgs.Empty);
    }

    private void OnMediaOpened(object? sender, EventArgs e)
    {
        // 首帧已就绪：隐藏盖在最上层的缩略图，揭开底层正在渲染的视频画面（播放层在 OnPlayClick 已可见）。
        ThumbnailState.Visibility = Visibility.Collapsed;
        // 帧泵已用 ffmpeg -ss 从起点开播，无需再 seek。
        var startSec = _segmentStart ?? 0;
        ProgressSlider.Minimum = startSec;
        if (_segmentEnd is { } endSec)
        {
            // 分镜模式：起止已知，滑块满量程
            DurationText.Text = FormatTime(endSec - startSec);
            ProgressSlider.Maximum = endSec;
        }
        else if (_fullDuration > 0)
        {
            // 整片模式 + 已知总时长：固定满量程，进度条/总时长正确（修「整片播放进度条错误」）。
            DurationText.Text = FormatTime(_fullDuration);
            ProgressSlider.Maximum = startSec + _fullDuration;
        }
        else
        {
            // 整片模式且时长未知：滑块上限随播放位置增长兜底（见 StartTimer）
            ProgressSlider.Maximum = Math.Max(ProgressSlider.Maximum, startSec + 1);
        }
    }

    private void OnMediaEnded(object? sender, EventArgs e)
    {
        if (_segmentEndFrame is null)
        {
            Stop();
            return;
        }

        // 裸帧管道已经按 end_frame(exclusive) 在 EndFrame-1 自然 EOF。
        // 这里只释放进程与音频，保留 WriteableBitmap，不切回缩略图。
        StopTimer();
        Player.Stop();
        _isEnded = true;
        PauseButton.Content = "↻";
        if (_segmentStartFrame is { } startFrame
            && _segmentEndFrame is { } endFrame
            && _segmentFps > 0)
        {
            var lastFrame = FrameTime.LastIncludedFrame(startFrame, endFrame);
            var lastSec = FrameTime.FrameToSeconds(lastFrame, _segmentFps);
            ProgressSlider.Value = lastSec;
            CurrentTimeText.Text = FormatTime(Math.Max(0, lastSec - (_segmentStart ?? 0)));
            Serilog.Log.Information(
                "[FrameFreezeDiag] startFrame={StartFrame} endFrame={EndFrame} frozenFrame={LastFrame} fps={Fps:F3}",
                startFrame, endFrame, lastFrame, _segmentFps);
        }
        // 分镜播完冻结末帧（hover 中可见结束态 + ↻ 重播）；若鼠标已离开，宿主据 Idle 还原缩略图。
        Idle?.Invoke(this, EventArgs.Empty);
    }

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
            if (_segmentEndFrame is null && _segmentEnd is { } end && posSec >= end)
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
