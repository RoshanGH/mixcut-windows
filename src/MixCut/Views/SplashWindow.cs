using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace MixCut.Views;

/// <summary>
/// 启动闪屏（QW-10）。冷启动期要跑 host 启动 + DB 迁移 + 硬件探测，主窗口出现前有数秒空白，
/// 用户容易产生「点了没反应」的错觉。这里给一个即时的「正在启动…」反馈，主窗口就绪后立即关闭。
/// <para>刻意用纯代码构建（不依赖 XAML/BAML）+ emoji Logo（不依赖图标资源加载），把崩溃面降到最低 ——
/// 闪屏是启动路径上最早执行的 UI，任何异常都可能让用户连主界面都看不到，所以越简单越安全。</para>
/// </summary>
public sealed class SplashWindow : Window
{
    public SplashWindow()
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        Background = Brushes.White;
        Width = 420;
        Height = 260;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ShowInTaskbar = false;
        Topmost = true;

        var panel = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };

        // Logo —— 与全项目视觉一致的 🎬，零资源加载风险。
        panel.Children.Add(new TextBlock
        {
            Text = "🎬",
            FontSize = 56,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 12),
        });
        panel.Children.Add(new TextBlock
        {
            Text = "MixCut",
            FontSize = 24,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(0x1D, 0x6B, 0xE5)), // 主色
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 16),
        });
        panel.Children.Add(new TextBlock
        {
            Text = "正在启动…",
            FontSize = 13,
            Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
            HorizontalAlignment = HorizontalAlignment.Center,
        });
        // 无限进度条（启动耗时算不出百分比，用 indeterminate）。
        panel.Children.Add(new ProgressBar
        {
            IsIndeterminate = true,
            Width = 200,
            Height = 4,
            Margin = new Thickness(0, 16, 0, 0),
            BorderThickness = new Thickness(0),
        });

        Content = new Border
        {
            Child = panel,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xE0, 0xE0, 0xE0)),
            BorderThickness = new Thickness(1),
        };
    }
}
