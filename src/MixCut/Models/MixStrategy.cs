using System.ComponentModel.DataAnnotations.Schema;

namespace MixCut.Models;

/// <summary>
/// 混剪策略。对应 macOS 版 SwiftData 的 MixStrategy @Model。
/// 一个策略下包含多个排列组合变体（MixScheme）。
/// </summary>
public class MixStrategy
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string Style { get; set; } = string.Empty;

    /// <summary>策略说明。</summary>
    public string StrategyDescription { get; set; } = string.Empty;

    public string TargetAudience { get; set; } = string.Empty;
    public string NarrativeStructure { get; set; } = string.Empty;
    public double TargetDuration { get; set; } = 60;

    public string? StrategyReasoning { get; set; }
    public string? Differentiation { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.Now;

    /// <summary>
    /// 系统级「自定义组合」容器策略，true 时不会被 AI 生成流程触碰。
    /// 每个项目恰好有 1 条 IsCustomGroup=true 的策略，用于挂载用户手动组合的方案。
    /// </summary>
    public bool IsCustomGroup { get; set; } = false;

    /// <summary>
    /// 「自定义叙事结构」模板策略（issue #6）。true 时这条策略代表用户用系统标签
    /// 逐段定义的一条叙事结构；段位序列存 <see cref="NarrativeSlotsJson"/>，归「自定义结构」分组。
    /// 一个项目可有多条（区别于单条的 IsCustomGroup）。AI 生成流程不触碰。
    /// </summary>
    public bool IsNarrativeTemplate { get; set; } = false;

    /// <summary>叙事结构的段位序列（JSON）。仅 <see cref="IsNarrativeTemplate"/>=true 时有意义。</summary>
    public string? NarrativeSlotsJson { get; set; }

    // ---- 导航属性 ----

    public Guid? ProjectId { get; set; }
    public Project? Project { get; set; }

    public List<MixScheme> Schemes { get; set; } = new();

    // ---- 计算属性 ----

    /// <summary>变体数量。</summary>
    [NotMapped]
    public int SchemeCount => Schemes.Count;

    /// <summary>按变体序号排序的方案。</summary>
    [NotMapped]
    public IReadOnlyList<MixScheme> OrderedSchemes =>
        Schemes.OrderBy(s => s.VariationIndex).ToList();

    /// <summary>叙事结构的段位序列（读写 <see cref="NarrativeSlotsJson"/>）。</summary>
    [NotMapped]
    public IReadOnlyList<NarrativeSlot> NarrativeSlots
    {
        get => NarrativeSlot.Deserialize(NarrativeSlotsJson);
        set => NarrativeSlotsJson = NarrativeSlot.Serialize(value);
    }

    /// <summary>
    /// 叙事结构的显示名 = 各段标签按顺序拼接：段间用 <c> · </c>，同段多标签用 <c>/</c>。
    /// 例：<c>痛点 · 产品方案 · 效果展示/信任背书 · 行动号召</c>（issue #6 §二命名规则）。
    /// 结构没有单独名字，名字就是它的段位序列本身。
    /// </summary>
    [NotMapped]
    public string NarrativeDisplayName =>
        string.Join(" · ", NarrativeSlots
            .OrderBy(s => s.Order)
            .Select(s => string.Join("/", s.Tags.Select(t => t.ToLabel()))));
}
