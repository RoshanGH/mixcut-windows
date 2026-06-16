using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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
/// 后续任何代码都可调 <see cref="Show"/> 弹出自动消失的浮层提示。
/// 对应 macOS 版 ToastCenter。
///
/// 支持可选的【可点击操作按钮】（如删除后的「撤销」）：传 <paramref name="actionText"/> + <paramref name="onAction"/>
/// 即在提示右侧渲染一个可点击胶囊；此时提示停留更久（给用户时间点击）且开启命中测试。
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

    public static void Show(
        string message, ToastStyle style = ToastStyle.Info,
        string? actionText = null, Action? onAction = null)
    {
        if (_host is null || string.IsNullOrEmpty(message)) return;
        var dispatcher = _host.Dispatcher;
        if (!dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(() => Show(message, style, actionText, onAction));
            return;
        }

        Dismiss();

        var (bg, _) = ResolveColors(style);
        var icon = ResolveIcon(style);
        var hasAction = !string.IsNullOrEmpty(actionText) && onAction is not null;

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

        if (hasAction)
        {
            // 可点击「撤销」胶囊：半透明白底，hover 提亮，点了执行回调并立刻收起 toast。
            var idle = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF));
            var hover = new SolidColorBrush(Color.FromArgb(0x73, 0xFF, 0xFF, 0xFF));
            idle.Freeze();
            hover.Freeze();
            var actionBorder = new Border
            {
                Background = idle,
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(11, 3, 11, 3),
                Margin = new Thickness(14, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Cursor = Cursors.Hand,
                Child = new TextBlock
                {
                    Text = actionText,
                    FontSize = 12,
                    FontWeight = FontWeights.Bold,
                    Foreground = Brushes.White,
                },
            };
            actionBorder.MouseEnter += (_, _) => actionBorder.Background = hover;
            actionBorder.MouseLeave += (_, _) => actionBorder.Background = idle;
            actionBorder.MouseLeftButtonUp += (_, _) =>
            {
                Dismiss();
                try { onAction!(); }
                catch (Exception ex) { Serilog.Log.Warning(ex, "[Toast] 操作按钮回调执行失败"); }
            };
            sp.Children.Add(actionBorder);
        }

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
            // 无操作按钮时不挡鼠标（纯提示）；有按钮时必须开启命中测试，否则点不到（旧版恒 false 是撤销点不动的根因）。
            IsHitTestVisible = hasAction,
        };
        _host.Children.Add(border);
        _current = border;

        // 有可点操作时停留更久（6s），给用户时间点「撤销」；纯提示 2.5s。
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(hasAction ? 6.0 : 2.5) };
        _timer.Tick += (_, _) => Dismiss();
        _timer.Start();
    }

    /// <summary>立即收起当前 toast 并停表（点击撤销、超时、被新 toast 顶替时共用）。</summary>
    private static void Dismiss()
    {
        _timer?.Stop();
        _timer = null;
        if (_host is not null && _current is not null)
        {
            _host.Children.Remove(_current);
            _current = null;
        }
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
