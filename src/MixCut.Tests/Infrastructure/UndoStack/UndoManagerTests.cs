using MixCut.Infrastructure.UndoStack;
using Xunit;

namespace MixCut.Tests.Infrastructure.UndoStack;

/// <summary>
/// UndoManager 撤销栈单元测试（P0-10）。纯内存逻辑，用独立实例（非 Shared 单例）保证测试隔离。
/// </summary>
public class UndoManagerTests
{
    [Fact]
    public void Undo_OnEmptyStack_ReturnsNull()
    {
        var mgr = new UndoManager();
        Assert.False(mgr.CanUndo);
        Assert.Null(mgr.Undo());
    }

    [Fact]
    public void Push_ThenUndo_ExecutesActionAndReturnsDescription()
    {
        var mgr = new UndoManager();
        var executed = false;
        mgr.Push(new DelegateUndoAction("删除 3 个分镜", () => executed = true));

        Assert.True(mgr.CanUndo);
        Assert.Equal("删除 3 个分镜", mgr.TopDescription);

        var desc = mgr.Undo();

        Assert.True(executed);
        Assert.Equal("删除 3 个分镜", desc);
        Assert.False(mgr.CanUndo);
    }

    [Fact]
    public void Undo_IsLastInFirstOut()
    {
        var mgr = new UndoManager();
        var order = new List<string>();
        mgr.Push(new DelegateUndoAction("A", () => order.Add("A")));
        mgr.Push(new DelegateUndoAction("B", () => order.Add("B")));

        mgr.Undo();
        mgr.Undo();

        Assert.Equal(new[] { "B", "A" }, order); // 后进先出
    }

    [Fact]
    public void Clear_EmptiesStack()
    {
        var mgr = new UndoManager();
        mgr.Push(new DelegateUndoAction("x", () => { }));
        mgr.Clear();
        Assert.False(mgr.CanUndo);
        Assert.Null(mgr.Undo());
    }

    [Fact]
    public void Push_BeyondMaxDepth_DropsOldest()
    {
        var mgr = new UndoManager();
        var executed = new List<int>();
        // MaxDepth=20，压 25 个；最老的 5 个（0..4）应被丢弃，只剩 5..24 可撤销
        for (var i = 0; i < 25; i++)
        {
            var captured = i;
            mgr.Push(new DelegateUndoAction($"op{i}", () => executed.Add(captured)));
        }

        var count = 0;
        while (mgr.CanUndo)
        {
            mgr.Undo();
            count++;
        }

        Assert.Equal(20, count);                       // 栈深度封顶 20
        Assert.DoesNotContain(0, executed);            // 最老的被丢弃
        Assert.Contains(24, executed);                 // 最新的保留
        Assert.Contains(5, executed);                  // 边界：第 6 个保留
    }

    [Fact]
    public void Changed_FiresOnPushAndUndo()
    {
        var mgr = new UndoManager();
        var fires = 0;
        mgr.Changed += () => fires++;

        mgr.Push(new DelegateUndoAction("x", () => { }));
        mgr.Undo();

        Assert.Equal(2, fires); // push 一次 + undo 一次
    }

    [Fact]
    public void Undo_WhenActionThrows_RemovesItFromStackAndRethrows()
    {
        var mgr = new UndoManager();
        mgr.Push(new DelegateUndoAction("boom", () => throw new InvalidOperationException("fail")));

        Assert.Throws<InvalidOperationException>(() => mgr.Undo());
        // 失败的操作不应留在栈里反复失败
        Assert.False(mgr.CanUndo);
    }
}
