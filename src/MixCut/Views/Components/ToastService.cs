using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;

namespace MixCut.Views.Components;

public enum ToastStyle
{
    Info,
    Success,
    Warning,
    Error,
}

/// <summary>
/// 简易 Toast 服务。在 MainWindow 构造里调用 <see cref="Initialize"/> 注入一个顶层 Panel 作为容器，
/// 后续任何代码都可调 <see cref="Show"/> 弹出 2.5s 自动消失的浮层提示。
/// 对应 macOS 版 ToastCenter。
/// </summary>
public static class ToastService
{
    private static Panel? _host;
    private static DispatcherTimer? _timer;
    private static Border? _current;

    /// <summary>注入承载 Toast 的浮层容器（建议是 MainWindow 顶层 Grid 的最后一个子元素，z 序最高）。</summary>
    public static void Initialize(Panel host)
    {
        _host = host;
    }

    public static void Show(string message, ToastStyle style = ToastStyle.Info)
    {
        if (_host is null || string.IsNullOrEmpty(message)) return;
        var dispatcher = _host.Dispatcher;
        if (!dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(() => Show(message, style));
            return;
        }

        // 旧 toast 先移除
        if (_current is not null)
        {
            _host.Children.Remove(_current);
            _current = null;
        }
        _timer?.Stop();

        var (bg, _) = ResolveColors(style);
        var icon = ResolveIcon(style);

        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        sp.Children.Add(new TextBlock
        {
            Text = icon,
            FontSize = 14,
            FontWeight = FontWeights.Bold,
            Foreground = Brushes.White,
            Margin = new Thickness(0, 0, 7, 0),
            VerticalAlignment = VerticalAlignment.Center,
        });
        sp.Children.Add(new TextBlock
        {
            Text = message,
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brushes.White,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.NoWrap,
            MaxWidth = 480,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });

        var border = new Border
        {
            Background = bg,
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(16, 9, 16, 9),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 0, 40),
            Effect = new DropShadowEffect
            {
                Color = Colors.Black,
                Opacity = 0.30,
                BlurRadius = 10,
                ShadowDepth = 2,
            },
            Child = sp,
            IsHitTestVisible = false,
        };
        _host.Children.Add(border);
        _current = border;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
        _timer.Tick += (_, _) =>
        {
            _timer?.Stop();
            if (_host is not null && _current is not null)
            {
                _host.Children.Remove(_current);
                _current = null;
            }
        };
        _timer.Start();
    }

    private static (Brush Bg, Brush Fg) ResolveColors(ToastStyle style) => style switch
    {
        ToastStyle.Success => (new SolidColorBrush(Color.FromRgb(0x2E, 0x8B, 0x57)), Brushes.White),
        ToastStyle.Warning => (new SolidColorBrush(Color.FromRgb(0xC0, 0x6F, 0x00)), Brushes.White),
        ToastStyle.Error => (new SolidColorBrush(Color.FromRgb(0xD3, 0x3A, 0x3A)), Brushes.White),
        _ => (new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33)), Brushes.White),
    };

    private static string ResolveIcon(ToastStyle style) => style switch
    {
        ToastStyle.Success => "✓",
        ToastStyle.Warning => "⚠",
        ToastStyle.Error => "✕",
        _ => "ℹ",
    };
}
