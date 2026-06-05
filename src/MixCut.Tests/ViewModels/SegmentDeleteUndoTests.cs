using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using MixCut.Data;
using MixCut.Infrastructure.UndoStack;
using MixCut.Models;
using MixCut.ViewModels;
using Xunit;

namespace MixCut.Tests.ViewModels;

/// <summary>
/// 分镜「删除 → 撤销恢复 → 再删另一个」整链回归测试，跑在真实 SegmentLibraryViewModel + SQLite 上。
/// 这是 SSH GUI 验不了、且第一版修复漏掉的关键环节：撤销恢复若用 LoadSegments 换 EF context，
/// 页面上已存在的卡片会抱着旧 context 的死实例，撤销后再删第二个即撞「同主键已被跟踪」失败
/// （用户实测：删一个→Ctrl+Z 恢复 OK，但删第二个提示「删除失败」）。
/// </summary>
public class SegmentDeleteUndoTests : IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly DbContextOptions<MixCutDbContext> _opts;
    private readonly Guid _projectId = Guid.NewGuid();
    private readonly Guid _videoId = Guid.NewGuid();
    private readonly Guid _segAId = Guid.NewGuid();
    private readonly Guid _segBId = Guid.NewGuid();

    public SegmentDeleteUndoTests()
    {
        _conn = new SqliteConnection("DataSource=:memory:");
        _conn.Open();
        _opts = new DbContextOptionsBuilder<MixCutDbContext>().UseSqlite(_conn).Options;

        using var db = new MixCutDbContext(_opts);
        db.Database.EnsureCreated();
        db.Projects.Add(new Project { Id = _projectId, Name = "测试项目" });
        db.Videos.Add(new Video { Id = _videoId, Name = "v.mp4", LocalPath = "C:/v.mp4" });
        db.ProjectVideos.Add(new ProjectVideo { Id = Guid.NewGuid(), ProjectId = _projectId, VideoId = _videoId });
        db.Segments.Add(new Segment { Id = _segAId, SegmentIndex = "seg_A", StartTime = 0, EndTime = 1, VideoId = _videoId });
        db.Segments.Add(new Segment { Id = _segBId, SegmentIndex = "seg_B", StartTime = 1, EndTime = 2, VideoId = _videoId });
        db.SaveChanges();
    }

    public void Dispose() => _conn.Dispose();

    private sealed class TestDbContextFactory : IDbContextFactory<MixCutDbContext>
    {
        private readonly DbContextOptions<MixCutDbContext> _opts;
        public TestDbContextFactory(DbContextOptions<MixCutDbContext> opts) => _opts = opts;
        public MixCutDbContext CreateDbContext() => new(_opts);
    }

    [Fact]
    public void DeleteRestoreThenDeleteAnother_SecondDeleteSucceeds()
    {
        var vm = new SegmentLibraryViewModel(
            new TestDbContextFactory(_opts), NullLogger<SegmentLibraryViewModel>.Instance);
        var project = new Project { Id = _projectId, Name = "测试项目" };
        vm.LoadSegments(project);

        Assert.Equal(2, vm.FilteredSegments.Count);
        var segA = vm.FilteredSegments.First(s => s.Id == _segAId);
        var segBBefore = vm.FilteredSegments.First(s => s.Id == _segBId);
        var snapshotA = UndoClone.CloneSegment(segA);

        // 1) 删 A
        Assert.True(vm.DeleteSegment(segA));
        Assert.DoesNotContain(vm.FilteredSegments, s => s.Id == _segAId);

        // 2) 撤销恢复 A —— 不得换 context，B 的实例引用必须不变
        Assert.Equal(1, vm.RestoreSegments(new[] { snapshotA }));
        var segBAfter = vm.FilteredSegments.First(s => s.Id == _segBId);
        Assert.Same(segBBefore, segBAfter); // 根因守门：恢复未换 context → 其余分镜同一实例
        Assert.Contains(vm.FilteredSegments, s => s.Id == _segAId);

        // 3) 再删 B（用户报的失败点）—— 故意用「恢复前」捕获的旧引用 segBBefore，
        //    模拟卡片复用时抱着的旧实例；DeleteSegment 必须按 Id 查当前实例删成功，不撞 EF 跟踪冲突。
        Assert.True(vm.DeleteSegment(segBBefore));
        Assert.DoesNotContain(vm.FilteredSegments, s => s.Id == _segBId);
        Assert.Contains(vm.FilteredSegments, s => s.Id == _segAId);

        vm.Dispose();

        // DB 终态：A 在、B 不在
        using var verify = new MixCutDbContext(_opts);
        Assert.True(verify.Segments.Any(s => s.Id == _segAId));
        Assert.False(verify.Segments.Any(s => s.Id == _segBId));
    }
}
