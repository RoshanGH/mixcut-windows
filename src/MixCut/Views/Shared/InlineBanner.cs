using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace MixCut.Views.Shared;

/// <summary>
/// 通用内联横幅。对应 macOS 版 InlineBanner。
/// 4 种风格（成功/警告/错误/信息），可选「重试」按钮 + 关闭按钮。
/// 用法：在 XAML 里嵌入 ContentControl 然后 Content = InlineBanner.Create(...)
/// 或直接代码生成 UIElement 加到面板。
/// </summary>
public static class InlineBanner
{
    public static UIElement Create(
        ToastStyle style, string message,
        Action? onRetry = null, Action? onDismiss = null)
    {
        var (icon, color) = style switch
        {
            ToastStyle.Success => ("✓", Color.FromRgb(0x2E, 0x8B, 0x57)),
            ToastStyle.Warning => ("⚠", Color.FromRgb(0xC0, 0x6F, 0x00)),
            ToastStyle.Error => ("✕", Color.FromRgb(0xD3, 0x3A, 0x3A)),
            ToastStyle.Info => ("ℹ", Color.FromRgb(0x1D, 0x6B, 0xE5)),
            _ => ("ℹ", Color.FromRgb(0x66, 0x66, 0x66)),
        };

        var border = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0x18, color.R, color.G, color.B)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x60, color.R, color.G, color.B)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 8, 12, 8),
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var iconText = new TextBlock
        {
            Text = icon, FontSize = 14, FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(color),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
        };
        Grid.SetColumn(iconText, 0);
        grid.Children.Add(iconText);

        var msgText = new TextBlock
        {
            Text = message, FontSize = 11,
            Foreground = new SolidColorBrush(Color.FromRgb(0x44, 0x44, 0x44)),
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(msgText, 1);
        grid.Children.Add(msgText);

        var actionsPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
        };
        if (onRetry is not null)
        {
            var retryBtn = new Button
            {
                Content = "重试", Padding = new Thickness(8, 2, 8, 2), FontSize = 11,
                Background = new SolidColorBrush(color), Foreground = Brushes.White,
                BorderThickness = new Thickness(0), Cursor = System.Windows.Input.Cursors.Hand,
                Margin = new Thickness(0, 0, 4, 0),
            };
            retryBtn.Click += (_, _) => onRetry();
            actionsPanel.Children.Add(retryBtn);
        }
        if (onDismiss is not null)
        {
            var dismissBtn = new Button
            {
                Content = "✕", Padding = new Thickness(6, 2, 6, 2), FontSize = 11,
                Background = Brushes.Transparent, BorderThickness = new Thickness(0),
                Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
                Cursor = System.Windows.Input.Cursors.Hand,
            };
            dismissBtn.Click += (_, _) => onDismiss();
            actionsPanel.Children.Add(dismissBtn);
        }
        Grid.SetColumn(actionsPanel, 2);
        grid.Children.Add(actionsPanel);

        border.Child = grid;
        return border;
    }
}
