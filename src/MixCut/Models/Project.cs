using System.ComponentModel.DataAnnotations.Schema;

namespace MixCut.Models;

/// <summary>项目。对应 macOS 版 SwiftData 的 Project @Model。</summary>
public class Project
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public ProjectStatus Status { get; set; } = ProjectStatus.Created;

    /// <summary>用户自定义 AI Prompt（可选）。</summary>
    public string? CustomPrompt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime UpdatedAt { get; set; } = DateTime.Now;

    // ---- 导航属性 ----

    /// <summary>项目与视频的多对多关联（视频全局共享）。</summary>
    public List<ProjectVideo> ProjectVideos { get; set; } = new();

    /// <summary>混剪策略。</summary>
    public List<MixStrategy> Strategies { get; set; } = new();

    /// <summary>混剪方案。</summary>
    public List<MixScheme> Schemes { get; set; } = new();

    // ---- 计算属性（不映射到数据库）----

    /// <summary>关联的视频列表。</summary>
    [NotMapped]
    public IEnumerable<Video> Videos =>
        ProjectVideos.Where(pv => pv.Video != null).Select(pv => pv.Video!);

    /// <summary>视频总数。</summary>
    [NotMapped]
    public int VideoCount => ProjectVideos.Count;

    /// <summary>分镜总数。</summary>
    [NotMapped]
    public int SegmentCount => Videos.Sum(v => v.Segments.Count);

    /// <summary>方案总数。</summary>
    [NotMapped]
    public int SchemeCount => Schemes.Count;
}
