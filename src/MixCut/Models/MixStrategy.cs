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
}
