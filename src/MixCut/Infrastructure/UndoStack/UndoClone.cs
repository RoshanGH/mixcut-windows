using MixCut.Models;

namespace MixCut.Infrastructure.UndoStack;

/// <summary>
/// 把待删除实体克隆成「脱离 EF 跟踪、保留主键/外键、不含上行导航属性」的快照（P0-10）。
/// 撤销时把克隆图 <c>Add</c> 回 DbContext 一次 SaveChanges 即可整图恢复（EF 按设置好的 PK 插入）。
/// 纯函数，无副作用，可单测。
/// </summary>
public static class UndoClone
{
    public static Segment CloneSegment(Segment s) => new()
    {
        Id = s.Id,
        SegmentIndex = s.SegmentIndex,
        StartTime = s.StartTime,
        EndTime = s.EndTime,
        StartFrame = s.StartFrame,
        EndFrame = s.EndFrame,
        Fps = s.Fps,
        Text = s.Text,
        SemanticTypesJson = s.SemanticTypesJson,
        PositionType = s.PositionType,
        Confidence = s.Confidence,
        QualityScore = s.QualityScore,
        VisualDescription = s.VisualDescription,
        ThumbnailPath = s.ThumbnailPath,
        KeywordsJson = s.KeywordsJson,
        QualityReasoning = s.QualityReasoning,
        CreatedAt = s.CreatedAt,
        VideoId = s.VideoId,
    };

    public static SchemeSegment CloneSchemeSegment(SchemeSegment x) => new()
    {
        Id = x.Id,
        Position = x.Position,
        Reasoning = x.Reasoning,
        PositionReasoning = x.PositionReasoning,
        SchemeId = x.SchemeId,
        SegmentId = x.SegmentId,
    };

    public static MixScheme CloneScheme(MixScheme s) => new()
    {
        Id = s.Id,
        VariationIndex = s.VariationIndex,
        SchemeIndex = s.SchemeIndex,
        Name = s.Name,
        Style = s.Style,
        SchemeDescription = s.SchemeDescription,
        TargetAudience = s.TargetAudience,
        NarrativeStructure = s.NarrativeStructure,
        EstimatedDuration = s.EstimatedDuration,
        StrategyReasoning = s.StrategyReasoning,
        Differentiation = s.Differentiation,
        CreatedAt = s.CreatedAt,
        IsManuallyEdited = s.IsManuallyEdited,
        StrategyId = s.StrategyId,
        ProjectId = s.ProjectId,
        SchemeSegments = s.SchemeSegments.Select(CloneSchemeSegment).ToList(),
    };

    public static MixStrategy CloneStrategy(MixStrategy s) => new()
    {
        Id = s.Id,
        Name = s.Name,
        Style = s.Style,
        StrategyDescription = s.StrategyDescription,
        TargetAudience = s.TargetAudience,
        NarrativeStructure = s.NarrativeStructure,
        TargetDuration = s.TargetDuration,
        StrategyReasoning = s.StrategyReasoning,
        Differentiation = s.Differentiation,
        CreatedAt = s.CreatedAt,
        IsCustomGroup = s.IsCustomGroup,
        ProjectId = s.ProjectId,
        Schemes = s.Schemes.Select(CloneScheme).ToList(),
    };
}
