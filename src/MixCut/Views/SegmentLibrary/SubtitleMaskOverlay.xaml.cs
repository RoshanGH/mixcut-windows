using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MixCut.Models;

namespace MixCut.Views.SegmentLibrary;

/// <summary>
/// 9:16 预览上的可拖拽/可调高字幕遮挡框。对齐 mac SubtitleMaskOverlay：
/// 整框上下拖（仅改 Y）、底部手柄调高；拖拽期间只更新 <see cref="Rect"/>（绑定写内存），
/// 松手触发 <see cref="CommitCommand"/> 落库一次（避免拖拽期间高频写库）。
/// </summary>
public partial class SubtitleMaskOverlay : UserControl
{
    private enum DragMode { None, Move, Resize }

    private DragMode _mode = DragMode.None;
    private Point _dragStart;
    private SubtitleMaskRect _dragStartRect;

    public SubtitleMaskOverlay()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ViewModels.SubtitleFontState.Shared.RatioChanged += OnFontRatioChanged;
        UpdateVisual();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        ViewModels.SubtitleFontState.Shared.RatioChanged -= OnFontRatioChanged;
    }

    private void OnFontRatioChanged(double ratio) => UpdateVisual();

    /// <summary>遮挡框归一化坐标（0..1，相对 9:16 输出画面）。默认双向绑定。</summary>
    public static readonly DependencyProperty RectProperty = DependencyProperty.Register(
        nameof(Rect), typeof(SubtitleMaskRect), typeof(SubtitleMaskOverlay),
        new FrameworkPropertyMetadata(SubtitleMaskRect.Default,
            FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnRectChanged));

    public SubtitleMaskRect Rect
    {
        get => (SubtitleMaskRect)GetValue(RectProperty);
        set => SetValue(RectProperty, value);
    }

    /// <summary>拖拽松手时执行一次（用于落库）。</summary>
    public static readonly DependencyProperty CommitCommandProperty = DependencyProperty.Register(
        nameof(CommitCommand), typeof(System.Windows.Input.ICommand), typeof(SubtitleMaskOverlay),
        new PropertyMetadata(null));

    public System.Windows.Input.ICommand? CommitCommand
    {
        get => (System.Windows.Input.ICommand?)GetValue(CommitCommandProperty);
        set => SetValue(CommitCommandProperty, value);
    }

    /// <summary>是否显示可拖拽遮挡框 + 调高手柄（模糊/纯色处理时 true；直接烧录 false，只留字号预览）。</summary>
    public static readonly DependencyProperty ShowMaskBoxProperty = DependencyProperty.Register(
        nameof(ShowMaskBox), typeof(bool), typeof(SubtitleMaskOverlay),
        new FrameworkPropertyMetadata(true, OnRectChanged));

    public bool ShowMaskBox
    {
        get => (bool)GetValue(ShowMaskBoxProperty);
        set => SetValue(ShowMaskBoxProperty, value);
    }

    /// <summary>
    /// 逐分镜字号比例（issue #23：Agent set_subtitle_mode 可给单个分镜设字号）。
    /// ≤0 = 未单独设置，字号预览跟随全局 SubtitleFontState（存量行为不变）。
    /// </summary>
    public static readonly DependencyProperty SegmentFontRatioProperty = DependencyProperty.Register(
        nameof(SegmentFontRatio), typeof(double), typeof(SubtitleMaskOverlay),
        new FrameworkPropertyMetadata(-1.0, OnRectChanged));

    public double SegmentFontRatio
    {
        get => (double)GetValue(SegmentFontRatioProperty);
        set => SetValue(SegmentFontRatioProperty, value);
    }

    private static void OnRectChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((SubtitleMaskOverlay)d).UpdateVisual();

    private void OnSizeChanged(object sender, SizeChangedEventArgs e) => UpdateVisual();

    /// <summary>按归一化 Rect × 画布尺寸定位遮挡框、手柄与字号预览。</summary>
    private void UpdateVisual()
    {
        double w = CanvasRoot.ActualWidth, h = CanvasRoot.ActualHeight;
        if (w <= 0 || h <= 0) return;

        var r = Rect;
        double px = r.X * w, py = r.Y * h, pw = r.Width * w, ph = r.Height * h;

        // 遮挡框 + 手柄：仅模糊/纯色处理时显示；直接烧录只留字号预览。
        var boxVis = ShowMaskBox ? Visibility.Visible : Visibility.Collapsed;
        MaskBox.Visibility = boxVis;
        Handle.Visibility = boxVis;
        if (ShowMaskBox)
        {
            Canvas.SetLeft(MaskBox, px);
            Canvas.SetTop(MaskBox, py);
            MaskBox.Width = pw;
            MaskBox.Height = ph;

            // 手柄居中贴在遮挡框底边
            Canvas.SetLeft(Handle, px + pw / 2 - Handle.Width / 2);
            Canvas.SetTop(Handle, py + ph - Handle.Height / 2);
        }

        // 字号预览：示例字幕字号 = 显示宽 × 比例（与导出按成片宽换算同源），居中于遮挡带。
        // 逐分镜设置过（Agent font_ratio）用自己的比例，否则跟随全局（issue #23）。
        var ratio = MixCut.Models.SubtitleFontSize.Clamp(
            SegmentFontRatio > 0 ? SegmentFontRatio : ViewModels.SubtitleFontState.Shared.Ratio);
        PreviewText.FontSize = Math.Max(6.0, w * ratio);
        // 先量胶囊期望尺寸再居中落位（Canvas 内绝对定位）
        PreviewPill.Measure(new Size(w, h));
        var pillW = PreviewPill.DesiredSize.Width;
        var pillH = PreviewPill.DesiredSize.Height;
        var pillX = Math.Max(0, Math.Min(px + (pw - pillW) / 2, w - pillW));
        var pillY = Math.Max(0, Math.Min(py + (ph - pillH) / 2, h - pillH));
        Canvas.SetLeft(PreviewPill, pillX);
        Canvas.SetTop(PreviewPill, pillY);
    }

    private void OnBoxDown(object sender, MouseButtonEventArgs e) => BeginDrag(DragMode.Move, MaskBox, e);

    private void OnHandleDown(object sender, MouseButtonEventArgs e) => BeginDrag(DragMode.Resize, Handle, e);

    private void BeginDrag(DragMode mode, IInputElement target, MouseButtonEventArgs e)
    {
        _mode = mode;
        _dragStart = e.GetPosition(CanvasRoot);
        _dragStartRect = Rect;
        target.CaptureMouse();
        e.Handled = true;
    }

    private void OnDragMove(object sender, MouseEventArgs e)
    {
        if (_mode == DragMode.None) return;
        double h = CanvasRoot.ActualHeight;
        if (h <= 0) return;

        var cur = e.GetPosition(CanvasRoot);
        double dy = (cur.Y - _dragStart.Y) / h;   // 累计位移（始终以拖拽起点为基准，避免叠加翻倍）
        Rect = _mode == DragMode.Move
            ? _dragStartRect.MovedBy(dy)
            : _dragStartRect.ResizedBy(dy);
    }

    private void OnDragUp(object sender, MouseButtonEventArgs e)
    {
        if (_mode == DragMode.None) return;
        _mode = DragMode.None;
        (sender as UIElement)?.ReleaseMouseCapture();
        e.Handled = true;
        if (CommitCommand?.CanExecute(null) == true) CommitCommand.Execute(null);
    }
}
