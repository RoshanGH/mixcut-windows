using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using MixCut.Models;
using MixCut.ViewModels.Cards;

namespace MixCut.Views.SegmentLibrary;

/// <summary>
/// 调整顺序模态对话框（场景 A）。用户在分镜库勾选 ≥2 个分镜后弹出，
/// 可用左右箭头调整顺序；点「生成方案」后 DialogResult=true，调用方通过 <see cref="Result"/>
/// 拿到用户确认的最终顺序，再调 SchemeViewModel.CreateCustomSchemeAsync。
/// </summary>
public partial class ArrangeOrderSheet : Window
{
    public ObservableCollection<SegmentPickerItem> OrderedItems { get; }

    public string HintText => $"共 {OrderedItems.Count} 个分镜 · 当前顺序如下";
    public string GenerateButtonText => $"生成方案 ({OrderedItems.Count})";

    /// <summary>用户确认后的最终顺序（点取消则为 null）。</summary>
    public IReadOnlyList<Segment>? Result { get; private set; }

    public ArrangeOrderSheet(IReadOnlyList<Segment> initialOrder)
    {
        // 必须先初始化 OrderedItems 再 InitializeComponent —— XAML 里的
        // {Binding OrderedItems / HintText / GenerateButtonText} 在 InitializeComponent
        // 解析时就建立，OrderedItems 还是 null 会让 binding 静默失败，导致卡片列表空白
        // + 「生成方案」按钮没文字（v0.6.0 用户实测发现）。
        OrderedItems = new ObservableCollection<SegmentPickerItem>(
            initialOrder.Select(s => new SegmentPickerItem(s, isAlreadyInScheme: false)
            {
                ThumbnailImage = LoadThumbBitmap(s.ThumbnailPath),
            }));
        InitializeComponent();
    }

    private void OnMoveLeftClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: SegmentPickerItem item })
        {
            var idx = OrderedItems.IndexOf(item);
            if (idx > 0)
            {
                OrderedItems.Move(idx, idx - 1);
            }
        }
    }

    private void OnMoveRightClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: SegmentPickerItem item })
        {
            var idx = OrderedItems.IndexOf(item);
            if (idx >= 0 && idx < OrderedItems.Count - 1)
            {
                OrderedItems.Move(idx, idx + 1);
            }
        }
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void OnGenerateClick(object sender, RoutedEventArgs e)
    {
        Result = OrderedItems.Select(x => x.Segment).ToList();
        DialogResult = true;
        Close();
    }

    /// <summary>同步加载缩略图。与 SchemesView.LoadThumbBitmapStatic 行为一致。</summary>
    private static BitmapImage? LoadThumbBitmap(string? path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.UriSource = new Uri(path, UriKind.Absolute);
            bmp.DecodePixelWidth = 120;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch
        {
            return null;
        }
    }
}
