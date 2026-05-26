using System.ComponentModel.DataAnnotations.Schema;
using MixCut.Utilities;

namespace MixCut.Models;

/// <summary>分镜。对应 macOS 版 SwiftData 的 Segment @Model。分镜随视频全局共享。</summary>
public class Segment
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>分镜序号，seg_001 格式。</summary>
    public string SegmentIndex { get; set; } = string.Empty;

    public double StartTime { get; set; }
    public double EndTime { get; set; }

    /// <summary>台词文本。</summary>
    public string Text { get; set; } = string.Empty;

    /// <summary>语义类型列表（中文标签 JSON 数组，经 <see cref="SemanticTypes"/> 读写，可多个）。</summary>
    public string? SemanticTypesJson { get; set; }

    public PositionType PositionType { get; set; } = PositionType.Middle;

    public double Confidence { get; set; } = 0.8;
    public double QualityScore { get; set; } = 8.0;

    /// <summary>画面描述。</summary>
    public string? VisualDescription { get; set; }

    /// <summary>缩略图路径。</summary>
    public string? ThumbnailPath { get; set; }

    /// <summary>关键词列表（JSON 数组，经 <see cref="Keywords"/> 读写）。</summary>
    public string? KeywordsJson { get; set; }

    /// <summary>数据质量详情说明。</summary>
    public string? QualityReasoning { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.Now;

    // ---- 导航属性 ----

    public Guid? VideoId { get; set; }
    public Video? Video { get; set; }

    public List<SchemeSegment> SchemeSegments { get; set; } = new();

    // ---- 计算属性 ----

    /// <summary>语义类型（支持多个）。存储为中文标签 JSON 数组。</summary>
    [NotMapped]
    public IReadOnlyList<SemanticType> SemanticTypes
    {
        get => JsonColumn.Read<string>(SemanticTypesJson)
            .Select(SemanticTypeExtensions.FromLabel)
            .ToList();
        set => SemanticTypesJson = JsonColumn.Write(value.Select(t => t.ToLabel()));
    }

    /// <summary>主语义类型（第一个，兼容旧代码）。</summary>
    [NotMapped]
    public SemanticType PrimarySemanticType =>
        SemanticTypes.Count > 0 ? SemanticTypes[0] : SemanticType.Transition;

    /// <summary>关键词。</summary>
    [NotMapped]
    public IReadOnlyList<string> Keywords
    {
        get => JsonColumn.Read<string>(KeywordsJson);
        set => KeywordsJson = JsonColumn.Write(value);
    }

    /// <summary>时长（保证非负）。</summary>
    [NotMapped]
    public double Duration => Math.Max(0, EndTime - StartTime);
}
