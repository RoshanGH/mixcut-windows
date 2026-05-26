namespace MixCut.Models;

/// <summary>
/// 语义类型（来自 segment_types_definition.md），共 11 种。
/// 对应 macOS 版 SemanticType。AI 分析结果与 Prompt 模板均以中文标签为准。
/// </summary>
public enum SemanticType
{
    /// <summary>噱头引入。</summary>
    Hook,
    /// <summary>痛点。</summary>
    PainPoint,
    /// <summary>产品方案。</summary>
    Solution,
    /// <summary>效果展示。</summary>
    Results,
    /// <summary>信任背书。</summary>
    SocialProof,
    /// <summary>价格对比。</summary>
    PriceAnchor,
    /// <summary>活动福利。</summary>
    Promotion,
    /// <summary>行动号召。</summary>
    CallToAction,
    /// <summary>产品定位。</summary>
    ProductPositioning,
    /// <summary>产品使用教育。</summary>
    UsageEducation,
    /// <summary>过渡。</summary>
    Transition,
}

public static class SemanticTypeExtensions
{
    private static readonly Dictionary<SemanticType, string> Labels = new()
    {
        [SemanticType.Hook] = "噱头引入",
        [SemanticType.PainPoint] = "痛点",
        [SemanticType.Solution] = "产品方案",
        [SemanticType.Results] = "效果展示",
        [SemanticType.SocialProof] = "信任背书",
        [SemanticType.PriceAnchor] = "价格对比",
        [SemanticType.Promotion] = "活动福利",
        [SemanticType.CallToAction] = "行动号召",
        [SemanticType.ProductPositioning] = "产品定位",
        [SemanticType.UsageEducation] = "产品使用教育",
        [SemanticType.Transition] = "过渡",
    };

    // 显示颜色（十六进制，沿用 macOS 版 SwiftUI 系统色）。
    private static readonly Dictionary<SemanticType, string> Colors = new()
    {
        [SemanticType.Hook] = "#FF3B30",
        [SemanticType.PainPoint] = "#FF9500",
        [SemanticType.Solution] = "#007AFF",
        [SemanticType.Results] = "#34C759",
        [SemanticType.SocialProof] = "#AF52DE",
        [SemanticType.PriceAnchor] = "#FFCC00",
        [SemanticType.Promotion] = "#FF2D55",
        [SemanticType.CallToAction] = "#00C7BE",
        [SemanticType.ProductPositioning] = "#30B0C7",
        [SemanticType.UsageEducation] = "#5856D6",
        [SemanticType.Transition] = "#8E8E93",
    };

    private static readonly Dictionary<string, SemanticType> ByLabel =
        Labels.ToDictionary(kv => kv.Value, kv => kv.Key);

    /// <summary>所有语义类型（按声明顺序）。</summary>
    public static IReadOnlyList<SemanticType> All { get; } =
        Enum.GetValues<SemanticType>();

    /// <summary>中文标签。</summary>
    public static string ToLabel(this SemanticType type) => Labels[type];

    /// <summary>显示颜色（十六进制字符串）。</summary>
    public static string ToColorHex(this SemanticType type) => Colors[type];

    /// <summary>由中文标签解析；无法识别时回退到「过渡」。</summary>
    public static SemanticType FromLabel(string label) =>
        ByLabel.TryGetValue(label.Trim(), out var type) ? type : SemanticType.Transition;
}
