using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using MixCut.Models;

namespace MixCut.ViewModels.Cards;

/// <summary>
/// 按视频分组的容器 VM（SegmentLibrary V2 用）。
/// 一个 VideoGroupViewModel = 一个视频的标题栏 + 它的全部分镜卡片。
/// </summary>
public sealed partial class VideoGroupViewModel : ObservableObject
{
    public Video Video { get; }

    public Guid VideoId => Video.Id;

    public string VideoName => Video.Name ?? "未命名视频";

    /// <summary>"3 个分镜 · 12s" 这种统计文本。</summary>
    public string MetaText { get; }

    public ImageSource? ThumbnailImage { get; }

    /// <summary>本组的分镜卡片（视图绑定到这里）。</summary>
    public ObservableCollection<SegmentCardViewModel> Segments { get; }

    public VideoGroupViewModel(Video video, IEnumerable<SegmentCardViewModel> cards)
    {
        Video = video;
        Segments = new ObservableCollection<SegmentCardViewModel>(cards);
        var dur = video.Duration > 0 ? $" · {video.Duration:F0}s" : string.Empty;
        MetaText = $"{Segments.Count} 个分镜{dur}";
        ThumbnailImage = LoadThumbnail(video.ThumbnailPath);
    }

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
