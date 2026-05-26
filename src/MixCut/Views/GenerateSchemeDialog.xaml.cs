using System.Globalization;
using System.Windows;
using System.Windows.Controls.Primitives;
using MixCut.Models;

namespace MixCut.Views;

/// <summary>批量生成混剪方案对话框。对应 macOS 版 SchemeListView.generateSheet。</summary>
public partial class GenerateSchemeDialog : Window
{
    public int TargetVideoCount { get; private set; } = 50;
    public string? CustomPrompt { get; private set; }

    public GenerateSchemeDialog(Project project)
    {
        InitializeComponent();
        var totalDuration = project.Videos.Sum(v => v.Duration);
        var totalSegments = project.Videos.Sum(v => v.Segments.Count);

        VideoCountText.Text = project.VideoCount.ToString(CultureInfo.InvariantCulture);
        SegmentCountText.Text = totalSegments.ToString(CultureInfo.InvariantCulture);
        DurationText.Text = totalDuration.ToString("F0", CultureInfo.InvariantCulture) + "s";

        UpdateTargetText(50);
    }

    private void OnSliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        UpdateTargetText((int)Math.Round(e.NewValue / 10.0) * 10);
    }

    private void UpdateTargetText(int value)
    {
        // Slider 在 InitializeComponent 期间设 Minimum/Maximum/Value 会触发 ValueChanged，
        // 此时 EstimateText 等晚于 Slider 出现的 XAML 元素还没实例化 → 防御性 null check。
        if (TargetCountText is null || EstimateText is null) return;

        TargetCountText.Text = value.ToString(CultureInfo.InvariantCulture);
        var strategies = value <= 30 ? 3 : (value <= 80 ? 4 : 5);
        var perStrategy = (int)Math.Ceiling(value / (double)strategies);
        var estimatedCalls = strategies * (int)Math.Ceiling(perStrategy / 8.0) + 1;
        var estSeconds = strategies * 40;
        var etaStr = estSeconds < 60
            ? $"{estSeconds} 秒"
            : $"{estSeconds / 60} 分钟" + (estSeconds % 60 > 0 ? $" {estSeconds % 60} 秒" : string.Empty);
        EstimateText.Text =
            $"⏱ 预估耗时约 {etaStr}  ·  {strategies} 个策略 × ~{perStrategy} 个变体 ({estimatedCalls} 次 AI 调用)";
    }

    private void OnCustomPromptChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (CustomPromptPlaceholder is null || CustomPromptBox is null) return;
        CustomPromptPlaceholder.Visibility = string.IsNullOrEmpty(CustomPromptBox.Text)
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnStart(object sender, RoutedEventArgs e)
    {
        TargetVideoCount = (int)Math.Round(CountSlider.Value / 10.0) * 10;
        if (TargetVideoCount < 10)
        {
            TargetVideoCount = 10;
        }
        var prompt = CustomPromptBox.Text.Trim();
        CustomPrompt = string.IsNullOrEmpty(prompt) ? null : prompt;
        DialogResult = true;
    }
}
