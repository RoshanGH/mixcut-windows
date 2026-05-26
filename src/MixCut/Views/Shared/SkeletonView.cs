using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace MixCut.Views.Shared;

/// <summary>
/// 骨架屏占位组件。对应 macOS 版 SkeletonView。
/// 用 LinearGradientBrush + 平移动画模拟「shimmer」效果。
/// </summary>
public static class SkeletonView
{
    /// <summary>创建一个 shimmer 矩形（用于卡片占位）。</summary>
    public static UIElement Box(double width = double.NaN, double height = 80, double cornerRadius = 10)
    {
        var border = new Border
        {
            CornerRadius = new CornerRadius(cornerRadius),
            ClipToBounds = true,
            Width = double.IsNaN(width) ? 200 : width,
            Height = height,
        };

        var grid = new Grid();
        // 底层灰色基底
        grid.Children.Add(new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0xEE, 0xEE, 0xEF)),
        });

        // shimmer 高光层（半透明白色斜线，水平平移制造 shimmer）
        var shimmer = new Rectangle
        {
            Width = 80, Height = double.IsNaN(height) ? 80 : height,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        var gradient = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0.5),
            EndPoint = new Point(1, 0.5),
        };
        gradient.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), 0));
        gradient.GradientStops.Add(new GradientStop(Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF), 0.5));
        gradient.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), 1));
        shimmer.Fill = gradient;

        var translate = new TranslateTransform(-80, 0);
        shimmer.RenderTransform = translate;
        grid.Children.Add(shimmer);

        border.Child = grid;
        border.Loaded += (_, _) =>
        {
            var w = border.ActualWidth > 0 ? border.ActualWidth : 200;
            var anim = new DoubleAnimation
            {
                From = -80, To = w,
                Duration = TimeSpan.FromMilliseconds(1500),
                RepeatBehavior = RepeatBehavior.Forever,
            };
            translate.BeginAnimation(TranslateTransform.XProperty, anim);
        };

        return border;
    }

    /// <summary>3 行卡片骨架（项目列表 / 视频列表通用）。</summary>
    public static UIElement CardList(int rows = 3)
    {
        var sp = new StackPanel();
        for (var i = 0; i < rows; i++)
        {
            sp.Children.Add(new ContentPresenter
            {
                Content = Box(double.NaN, 60, 10),
                Margin = new Thickness(0, 0, 0, 8),
            });
        }
        return sp;
    }
}

