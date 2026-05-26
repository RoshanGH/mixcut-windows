namespace MixCut.Models;

/// <summary>方案-分镜有序关联。对应 macOS 版 SwiftData 的 SchemeSegment @Model。</summary>
public class SchemeSegment
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>在方案中的排列顺序（从 1 开始）。</summary>
    public int Position { get; set; }

    /// <summary>AI 选择此分镜的理由。</summary>
    public string? Reasoning { get; set; }

    /// <summary>为什么放在这个位置的说明。</summary>
    public string? PositionReasoning { get; set; }

    // ---- 导航属性 ----

    public Guid? SchemeId { get; set; }
    public MixScheme? Scheme { get; set; }

    public Guid? SegmentId { get; set; }
    public Segment? Segment { get; set; }
}
