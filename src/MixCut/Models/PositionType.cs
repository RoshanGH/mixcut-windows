namespace MixCut.Models;

/// <summary>位置类型。对应 macOS 版 PositionType（3 种）。</summary>
public enum PositionType
{
    /// <summary>开头 —— 承担「吸引注意力」功能。</summary>
    Opening,
    /// <summary>中间 —— 核心内容，可灵活排列。</summary>
    Middle,
    /// <summary>结尾 —— 承担「驱动转化」功能。</summary>
    Ending,
}

public static class PositionTypeExtensions
{
    private static readonly Dictionary<PositionType, string> Labels = new()
    {
        [PositionType.Opening] = "开头",
        [PositionType.Middle] = "中间",
        [PositionType.Ending] = "结尾",
    };

    private static readonly Dictionary<string, PositionType> ByLabel =
        Labels.ToDictionary(kv => kv.Value, kv => kv.Key);

    /// <summary>所有位置类型。</summary>
    public static IReadOnlyList<PositionType> All { get; } = Enum.GetValues<PositionType>();

    /// <summary>中文标签。</summary>
    public static string ToLabel(this PositionType type) => Labels[type];

    /// <summary>由中文标签解析；无法识别时回退到「中间」。</summary>
    public static PositionType FromLabel(string label) =>
        ByLabel.TryGetValue(label.Trim(), out var type) ? type : PositionType.Middle;
}
