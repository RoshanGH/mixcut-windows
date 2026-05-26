namespace MixCut.ViewModels;

/// <summary>侧边栏导航项。对应 macOS 版 NavigationItem。</summary>
public enum NavigationItem
{
    Overview,
    ImportMedia,
    SegmentLibrary,
    Schemes,
    Export,
}

public static class NavigationItemExtensions
{
    public static string Label(this NavigationItem item) => item switch
    {
        NavigationItem.Overview => "项目概览",
        NavigationItem.ImportMedia => "素材导入",
        NavigationItem.SegmentLibrary => "分镜素材库",
        NavigationItem.Schemes => "混剪方案",
        NavigationItem.Export => "导出",
        _ => item.ToString(),
    };

    /// <summary>导航项前缀图标（对齐 macOS 版 SF Symbol，但用 emoji 等价物）。</summary>
    public static string Icon(this NavigationItem item) => item switch
    {
        NavigationItem.Overview => "▦", // rectangle.3.group
        NavigationItem.ImportMedia => "⬇", // square.and.arrow.down
        NavigationItem.SegmentLibrary => "🎞", // film.stack
        NavigationItem.Schemes => "📋", // list.bullet.clipboard
        NavigationItem.Export => "⬆", // square.and.arrow.up
        _ => string.Empty,
    };

    /// <summary>带图标的 Label，例如 "▦  项目概览"。</summary>
    public static string LabelWithIcon(this NavigationItem item) => $"{item.Icon()}  {item.Label()}";

    public static IReadOnlyList<NavigationItem> All { get; } = Enum.GetValues<NavigationItem>();
}
