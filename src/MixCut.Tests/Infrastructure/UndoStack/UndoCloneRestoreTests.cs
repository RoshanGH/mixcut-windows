using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using MixCut.Data;
using MixCut.Infrastructure.UndoStack;
using MixCut.Models;
using Xunit;

namespace MixCut.Tests.Infrastructure.UndoStack;

/// <summary>
/// P0-10 撤销「删除→恢复」往返集成测试，跑在真实 MixCutDbContext + SQLite 内存库上。
/// 这是 SSH GUI 验不了的关键环节：验证 UndoClone 克隆图能被 EF 原样插回（含子集合 + 主键保持）。
/// </summary>
public class UndoCloneRestoreTests : IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly DbContextOptions<MixCutDbContext> _opts;

    public UndoCloneRestoreTests()
    {
        // :memory: 库随连接存活；所有 context 复用同一打开的连接 = 同一个库。
        _conn = new SqliteConnection("DataSource=:memory:");
        _conn.Open();
        _opts = new DbContextOptionsBuilder<MixCutDbContext>().UseSqlite(_conn).Options;
        using var db = new MixCutDbContext(_opts);
        db.Database.EnsureCreated();
    }

    public void Dispose() => _conn.Dispose();

    [Fact]
    public void DeleteThenRestoreScheme_RoundTripsWithSchemeSegments()
    {
        var schemeId = Guid.NewGuid();

        using (var db = new MixCutDbContext(_opts))
        {
            db.Schemes.Add(new MixScheme
            {
                Id = schemeId,
                Name = "测试方案",
                Style = "快节奏",
                VariationIndex = 3,
                SchemeSegments = new List<SchemeSegment>
                {
                    new() { Id = Guid.NewGuid(), Position = 0, Reasoning = "开场" },
                    new() { Id = Guid.NewGuid(), Position = 1, Reasoning = "卖点" },
                },
            });
            db.SaveChanges();
        }

        // 快照 + 删除
        MixScheme snapshot;
        using (var db = new MixCutDbContext(_opts))
        {
            var tracked = db.Schemes.Include(s => s.SchemeSegments).First(s => s.Id == schemeId);
            snapshot = UndoClone.CloneScheme(tracked);
            db.Schemes.Remove(tracked);
            db.SaveChanges();
        }

        // 确认删干净（含级联的 SchemeSegment）
        using (var db = new MixCutDbContext(_opts))
        {
            Assert.False(db.Schemes.Any(s => s.Id == schemeId));
            Assert.Equal(0, db.SchemeSegments.Count());
        }

        // 撤销恢复
        using (var db = new MixCutDbContext(_opts))
        {
            db.Schemes.Add(UndoClone.CloneScheme(snapshot));
            db.SaveChanges();
        }

        // 确认原样回来（主键 + 字段 + 子集合）
        using (var db = new MixCutDbContext(_opts))
        {
            var restored = db.Schemes.Include(s => s.SchemeSegments).First(s => s.Id == schemeId);
            Assert.Equal("测试方案", restored.Name);
            Assert.Equal("快节奏", restored.Style);
            Assert.Equal(3, restored.VariationIndex);
            Assert.Equal(2, restored.SchemeSegments.Count);
            Assert.Contains(restored.SchemeSegments, x => x.Reasoning == "开场");
            Assert.Contains(restored.SchemeSegments, x => x.Reasoning == "卖点");
        }
    }

    [Fact]
    public void DeleteThenRestoreStrategy_RoundTripsWholeGraph()
    {
        var strategyId = Guid.NewGuid();

        using (var db = new MixCutDbContext(_opts))
        {
            db.Strategies.Add(new MixStrategy
            {
                Id = strategyId,
                Name = "情感共鸣",
                Schemes = new List<MixScheme>
                {
                    new()
                    {
                        Id = Guid.NewGuid(), Name = "变体A", VariationIndex = 1,
                        SchemeSegments = new List<SchemeSegment> { new() { Id = Guid.NewGuid(), Position = 0 } },
                    },
                    new() { Id = Guid.NewGuid(), Name = "变体B", VariationIndex = 2 },
                },
            });
            db.SaveChanges();
        }

        MixStrategy snapshot;
        using (var db = new MixCutDbContext(_opts))
        {
            var tracked = db.Strategies
                .Include(s => s.Schemes).ThenInclude(sc => sc.SchemeSegments)
                .First(s => s.Id == strategyId);
            snapshot = UndoClone.CloneStrategy(tracked);
            db.Strategies.Remove(tracked);
            db.SaveChanges();
        }

        using (var db = new MixCutDbContext(_opts))
        {
            Assert.False(db.Strategies.Any(s => s.Id == strategyId));
            Assert.Equal(0, db.Schemes.Count());
            Assert.Equal(0, db.SchemeSegments.Count());
        }

        using (var db = new MixCutDbContext(_opts))
        {
            db.Strategies.Add(UndoClone.CloneStrategy(snapshot));
            db.SaveChanges();
        }

        using (var db = new MixCutDbContext(_opts))
        {
            var restored = db.Strategies
                .Include(s => s.Schemes).ThenInclude(sc => sc.SchemeSegments)
                .First(s => s.Id == strategyId);
            Assert.Equal("情感共鸣", restored.Name);
            Assert.Equal(2, restored.Schemes.Count);
            var variantA = restored.Schemes.First(x => x.Name == "变体A");
            Assert.Single(variantA.SchemeSegments);
        }
    }

    [Fact]
    public void DeleteThenRestoreSegment_RoundTrips()
    {
        var segId = Guid.NewGuid();
        using (var db = new MixCutDbContext(_opts))
        {
            db.Segments.Add(new Segment
            {
                Id = segId, SegmentIndex = "seg_001", StartTime = 1.5, EndTime = 4.2, Text = "台词内容",
            });
            db.SaveChanges();
        }

        Segment snapshot;
        using (var db = new MixCutDbContext(_opts))
        {
            var tracked = db.Segments.First(s => s.Id == segId);
            snapshot = UndoClone.CloneSegment(tracked);
            db.Segments.Remove(tracked);
            db.SaveChanges();
        }

        using (var db = new MixCutDbContext(_opts))
        {
            db.Segments.Add(UndoClone.CloneSegment(snapshot));
            db.SaveChanges();
        }

        using (var db = new MixCutDbContext(_opts))
        {
            var restored = db.Segments.First(s => s.Id == segId);
            Assert.Equal("seg_001", restored.SegmentIndex);
            Assert.Equal(1.5, restored.StartTime);
            Assert.Equal(4.2, restored.EndTime);
            Assert.Equal("台词内容", restored.Text);
        }
    }
}
