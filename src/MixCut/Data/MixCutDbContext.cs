using Microsoft.EntityFrameworkCore;
using MixCut.Models;

namespace MixCut.Data;

/// <summary>
/// EF Core 数据库上下文。对应 macOS 版 SwiftData 的 ModelContainer。
/// 通过 <see cref="IDbContextFactory{TContext}"/> 按操作创建短生命周期实例，避免并发竞态。
/// </summary>
public class MixCutDbContext : DbContext
{
    public MixCutDbContext(DbContextOptions<MixCutDbContext> options) : base(options)
    {
    }

    public DbSet<Project> Projects => Set<Project>();
    public DbSet<Video> Videos => Set<Video>();
    public DbSet<Segment> Segments => Set<Segment>();
    public DbSet<MixStrategy> Strategies => Set<MixStrategy>();
    public DbSet<MixScheme> Schemes => Set<MixScheme>();
    public DbSet<SchemeSegment> SchemeSegments => Set<SchemeSegment>();
    public DbSet<ProjectVideo> ProjectVideos => Set<ProjectVideo>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        // 枚举以字符串形式存储（数据库可读）。
        b.Entity<Project>().Property(p => p.Status).HasConversion<string>();
        b.Entity<Video>().Property(v => v.Status).HasConversion<string>();
        b.Entity<Segment>().Property(s => s.PositionType).HasConversion<string>();

        // 视频内容哈希索引，用于全局去重查询。
        b.Entity<Video>().HasIndex(v => v.ContentHash);

        // 项目 *──* 视频（经 ProjectVideo 中间表，级联删除）。
        b.Entity<ProjectVideo>()
            .HasOne(pv => pv.Project).WithMany(p => p.ProjectVideos)
            .HasForeignKey(pv => pv.ProjectId)
            .OnDelete(DeleteBehavior.Cascade);
        b.Entity<ProjectVideo>()
            .HasOne(pv => pv.Video).WithMany(v => v.ProjectVideos)
            .HasForeignKey(pv => pv.VideoId)
            .OnDelete(DeleteBehavior.Cascade);

        // 视频 1──* 分镜（级联删除）。
        b.Entity<Segment>()
            .HasOne(s => s.Video).WithMany(v => v.Segments)
            .HasForeignKey(s => s.VideoId)
            .OnDelete(DeleteBehavior.Cascade);

        // 项目 1──* 策略（级联删除）。
        b.Entity<MixStrategy>()
            .HasOne(s => s.Project).WithMany(p => p.Strategies)
            .HasForeignKey(s => s.ProjectId)
            .OnDelete(DeleteBehavior.Cascade);

        // 项目 1──* 方案（级联删除）。
        b.Entity<MixScheme>()
            .HasOne(s => s.Project).WithMany(p => p.Schemes)
            .HasForeignKey(s => s.ProjectId)
            .OnDelete(DeleteBehavior.Cascade);

        // 策略 1──* 方案（级联删除）。
        b.Entity<MixScheme>()
            .HasOne(s => s.Strategy).WithMany(st => st.Schemes)
            .HasForeignKey(s => s.StrategyId)
            .OnDelete(DeleteBehavior.Cascade);

        // 方案 1──* 方案分镜（级联删除）。
        b.Entity<SchemeSegment>()
            .HasOne(ss => ss.Scheme).WithMany(s => s.SchemeSegments)
            .HasForeignKey(ss => ss.SchemeId)
            .OnDelete(DeleteBehavior.Cascade);

        // 分镜 1──* 方案分镜（级联删除）。
        b.Entity<SchemeSegment>()
            .HasOne(ss => ss.Segment).WithMany(s => s.SchemeSegments)
            .HasForeignKey(ss => ss.SegmentId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
