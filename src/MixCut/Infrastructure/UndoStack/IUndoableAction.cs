namespace MixCut.Infrastructure.UndoStack;

/// <summary>
/// 可撤销操作（P0-10）。删除类操作实现它，压入 <see cref="UndoManager"/>，
/// 用户按 Ctrl+Z 时调 <see cref="Undo"/> 复原。
/// </summary>
public interface IUndoableAction
{
    /// <summary>给用户看的描述，用于 Toast，如「删除 5 个分镜」。</summary>
    string Description { get; }

    /// <summary>执行撤销（把删掉的数据恢复到 DB + 刷新 UI）。失败抛异常由 UndoManager 兜。</summary>
    void Undo();
}

/// <summary>用委托实现的通用可撤销操作，省去为每种删除单独建类。</summary>
public sealed class DelegateUndoAction : IUndoableAction
{
    private readonly Action _undo;

    public DelegateUndoAction(string description, Action undo)
    {
        Description = description;
        _undo = undo;
    }

    public string Description { get; }

    public void Undo() => _undo();
}
