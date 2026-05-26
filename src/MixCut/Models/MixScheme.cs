using System.ComponentModel.DataAnnotations.Schema;

namespace MixCut.Models;

/// <summary>混剪方案（一个具体的分镜排列组合）。对应 macOS 版 SwiftData 的 MixScheme @Model。</summary>
public class MixScheme
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>在策略内的变体序号（从 1 开始）。</summary>
    public int VariationIndex { get; set; } = 1;

    /// <summary>全局方案序号，scheme_001 格式。</summary>
    public string SchemeIndex { get; set; } = string.Empty;

    /// <summary>简短变体名。</summary>
    public string Name { get; set; } = string.Empty;

    public string Style { get; set; } = string.Empty;
    public string SchemeDescription { get; set; } = string.Empty;
    public string TargetAudience { get; set; } = string.Empty;
    public string NarrativeStructure { get; set; } = string.Empty;
    public double EstimatedDuration { get; set; }

    public string? StrategyReasoning { get; set; }
    public string? Differentiation { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.Now;

    // ---- 导航属性 ----

    public Guid? StrategyId { get; set; }
    public MixStrategy? Strategy { get; set; }

    public Guid? ProjectId { get; set; }
    public Project? Project { get; set; }

    public List<SchemeSegment> SchemeSegments { get; set; } = new();

    // ---- 计算属性 ----

    /// <summary>按 position 排序的分镜（同序时按 Id 字符串排序，保证稳定）。</summary>
    [NotMapped]
    public IReadOnlyList<SchemeSegment> OrderedSegments =>
        SchemeSegments
            .OrderBy(ss => ss.Position)
            .ThenBy(ss => ss.Id.ToString())
            .ToList();

    /// <summary>实际总时长（各分镜时长之和）。</summary>
    [NotMapped]
    public double TotalDuration =>
        SchemeSegments.Sum(ss => ss.Segment?.Duration ?? 0);

    /// <summary>分镜数量。</summary>
    [NotMapped]
    public int SegmentCount => SchemeSegments.Count;
}
