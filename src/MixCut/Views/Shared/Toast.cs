using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace MixCut.Views.Shared;

/// <summary>Toast 风格枚举。对齐 macOS 版 InlineBanner.Style。</summary>
public enum ToastStyle
{
    Success,
    Warning,
    Error,
    Info,
}

/// <summary>
/// 全局 Toast 中心。对应 macOS 版 ToastCenter（单例 + @Observable）。
///
/// 使用方法：在 MainWindow 启动时 <c>ToastCenter.Shared.AttachTo(window)</c>。
/// 任何代码调 <c>ToastCenter.Shared.Show("成功", ToastStyle.Success)</c> 即可。
/// </summary>
public sealed class ToastCenter
{
    public static ToastCenter Shared { get; } = new();
    private ToastCenter() { }

    private Window? _host;
    private Border? _toastBorder;
    private DispatcherTimer? _hideTimer;

    /// <summary>把 ToastOverlay 附加到指定 Window（应在主窗口构造完成后调）。</summary>
    public void AttachTo(Window window)
    {
        _host = window;
        // ToastBorder 直接放在 Window 的 Content 上层（用 AdornerDecorator 或者 Grid）
        // 我们用一种简单做法：在 window.Content 外包一层 Grid，把 Toast Border 加进去。
        if (window.Content is FrameworkElement original)
        {
            var grid = new Grid();
            window.Content = grid;
            grid.Children.Add(original);

            _toastBorder = new Border
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 50, 0, 0),
                Background = new SolidColorBrush(Color.FromArgb(0xF5, 0xFF, 0xFF, 0xFF)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(999),
                Padding = new Thickness(14, 8, 14, 8),
                Visibility = Visibility.Collapsed,
                IsHitTestVisible = false,
                Effect = new System.Windows.Media.Effects.DropShadowEffect
                {
                    BlurRadius = 12,
                    ShadowDepth = 3,
                    Opacity = 0.18,
                    Color = Colors.Black,
                },
            };
            Panel.SetZIndex(_toastBorder, 9999);
            grid.Children.Add(_toastBorder);
        }
    }

    /// <summary>显示 Toast。在 UI 线程上调，自动 N 秒后淡出。</summary>
    public void Show(string text, ToastStyle style = ToastStyle.Success, double durationSeconds = 2.2)
    {
        if (_host is null || _toastBorder is null)
        {
            return;
        }
        if (!_host.Dispatcher.CheckAccess())
        {
            _host.Dispatcher.BeginInvoke(() => Show(text, style, durationSeconds));
            return;
        }

        var (icon, color) = StyleVisuals(style);

        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        sp.Children.Add(new TextBlock
        {
            Text = icon, FontSize = 13, FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(color),
            VerticalAlignment = VerticalAlignment.Center,
        });
        sp.Children.Add(new TextBlock
        {
            Text = text, FontSize = 12, FontWeight = FontWeights.Medium,
            Foreground = new SolidColorBrush(Color.FromArgb(0xD8, 0x00, 0x00, 0x00)),
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        });
        _toastBorder.Child = sp;
        _toastBorder.BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, color.R, color.G, color.B));
        _toastBorder.Visibility = Visibility.Visible;

        // 简单淡入：opacity 0 → 1
        _toastBorder.Opacity = 0;
        var fadeIn = new DoubleAnimation
        {
            From = 0, To = 1, Duration = TimeSpan.FromMilliseconds(180),
        };
        _toastBorder.BeginAnimation(UIElement.OpacityProperty, fadeIn);

        _hideTimer?.Stop();
        _hideTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(durationSeconds),
        };
        _hideTimer.Tick += (_, _) =>
        {
            _hideTimer!.Stop();
            var fadeOut = new DoubleAnimation
            {
                From = 1, To = 0, Duration = TimeSpan.FromMilliseconds(220),
            };
            fadeOut.Completed += (_, _) => _toastBorder.Visibility = Visibility.Collapsed;
            _toastBorder.BeginAnimation(UIElement.OpacityProperty, fadeOut);
        };
        _hideTimer.Start();
    }

    private static (string Icon, Color Color) StyleVisuals(ToastStyle style) => style switch
    {
        ToastStyle.Success => ("✓", Color.FromRgb(0x2E, 0x8B, 0x57)),
        ToastStyle.Warning => ("⚠", Color.FromRgb(0xC0, 0x6F, 0x00)),
        ToastStyle.Error => ("✕", Color.FromRgb(0xD3, 0x3A, 0x3A)),
        ToastStyle.Info => ("ℹ", Color.FromRgb(0x1D, 0x6B, 0xE5)),
        _ => ("ℹ", Color.FromRgb(0x66, 0x66, 0x66)),
    };
}
