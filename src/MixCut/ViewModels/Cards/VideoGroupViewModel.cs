using System.Collections.ObjectModel;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MixCut.Models;

namespace MixCut.ViewModels.Cards;

/// <summary>
/// 按视频分组的容器 VM（SegmentLibrary V2 用）。
/// 一个 VideoGroupViewModel = 一个视频的标题栏 + 配音设置条 + 它的全部分镜卡片。
/// </summary>
public sealed partial class VideoGroupViewModel : ObservableObject
{
    private readonly DubbingViewModel? _dubbing;

    public Video Video { get; }

    public Guid VideoId => Video.Id;

    public string VideoName => Video.Name ?? "未命名视频";

    /// <summary>"3 个分镜 · 12s" 这种统计文本。</summary>
    public string MetaText { get; }

    public ImageSource? ThumbnailImage { get; }

    /// <summary>本组的分镜卡片（视图绑定到这里）。</summary>
    public ObservableCollection<SegmentCardViewModel> Segments { get; }

    public VideoGroupViewModel(Video video, IEnumerable<SegmentCardViewModel> cards, DubbingViewModel? dubbing = null)
    {
        Video = video;
        _dubbing = dubbing;
        Segments = new ObservableCollection<SegmentCardViewModel>(cards);
        var dur = video.Duration > 0 ? $" · {video.Duration:F0}s" : string.Empty;
        MetaText = $"{Segments.Count} 个分镜{dur}";
        ThumbnailImage = LoadThumbnail(video.ThumbnailPath);

        if (_dubbing is not null)
        {
            _variantCount = _dubbing.VariantCount;
            _dubbing.VideoStateChanged += OnDubVideoStateChanged;
            _ = RefreshDubStatusAsync();
        }
    }

    // ---- 配音设置条（v0.5.0）----

    /// <summary>变体数（1~5，全局设置）。设置条 stepper 绑定。</summary>
    [ObservableProperty] private int _variantCount = 2;

    partial void OnVariantCountChanged(int value)
    {
        if (_dubbing is not null) _dubbing.VariantCount = value;
    }

    /// <summary>本视频是否正在配音处理中（克隆/改写/合成）。</summary>
    [ObservableProperty] private bool _isDubBusy;

    /// <summary>非忙碌（按钮/stepper 的 IsEnabled 绑定）。</summary>
    public bool IsNotDubBusy => !IsDubBusy;

    partial void OnIsDubBusyChanged(bool value) => OnPropertyChanged(nameof(IsNotDubBusy));

    /// <summary>忙碌时的进度文案。</summary>
    [ObservableProperty] private string _dubProgress = string.Empty;

    /// <summary>空闲时的状态文案，如「✓ 6 个变体」。</summary>
    [ObservableProperty] private string _dubStatusText = string.Empty;

    /// <summary>主按钮文案：未克隆「克隆并改写配音」/ 已克隆「改写配音」。</summary>
    [ObservableProperty] private string _dubMainButtonText = "克隆并改写配音";

    private void OnDubVideoStateChanged(Guid videoId)
    {
        if (videoId != VideoId || _dubbing is null) return;
        System.Windows.Application.Current?.Dispatcher.Invoke(() =>
        {
            IsDubBusy = _dubbing.IsBusy(VideoId);
            DubProgress = _dubbing.ProgressText(VideoId);
            if (!IsDubBusy) _ = RefreshDubStatusAsync();
        });
    }

    /// <summary>刷新「✓ N 个变体」+ 主按钮文案（克隆与否）。</summary>
    public async Task RefreshDubStatusAsync()
    {
        if (_dubbing is null) return;
        var cloned = await _dubbing.IsClonedAsync(VideoId);
        var n = await _dubbing.EffectiveVariantCountAsync(VideoId);
        System.Windows.Application.Current?.Dispatcher.Invoke(() =>
        {
            DubMainButtonText = cloned ? "改写配音" : "克隆并改写配音";
            DubStatusText = n > 0 ? $"✓ {n} 个变体" : string.Empty;
        });
    }

    [RelayCommand]
    private async Task RewriteAllAsync()
    {
        if (_dubbing is null) return;
        await _dubbing.RewriteAllAsync(VideoId);
    }

    [RelayCommand]
    private void IncVariant() { if (VariantCount < 5) VariantCount++; }

    [RelayCommand]
    private void DecVariant() { if (VariantCount > 1) VariantCount--; }

    /// <summary>用新的 cards 集合替换并重新计算 meta（保持 ObservableCollection 实例不变，触发 diff 而非整体替换）。</summary>
    public void ReplaceSegments(IEnumerable<SegmentCardViewModel> cards)
    {
        Segments.Clear();
        foreach (var c in cards)
        {
            Segments.Add(c);
        }
        OnPropertyChanged(nameof(MetaText));
    }

    private static ImageSource? LoadThumbnail(string? path) =>
        Infrastructure.ThumbnailCache.Shared.GetImage(path);
}
