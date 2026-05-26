namespace MixCut.Models;

/// <summary>
/// 项目与视频的多对多关联中间表。对应 macOS 版 SwiftData 的 ProjectVideo @Model。
/// 视频全局共享，不再从属单一项目。
/// </summary>
public class ProjectVideo
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>视频加入该项目的时间。</summary>
    public DateTime AddedAt { get; set; } = DateTime.Now;

    public Guid? ProjectId { get; set; }
    public Project? Project { get; set; }

    public Guid? VideoId { get; set; }
    public Video? Video { get; set; }
}
