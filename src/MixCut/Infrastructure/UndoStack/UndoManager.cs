using Serilog;

namespace MixCut.Infrastructure.UndoStack;

/// <summary>
/// 全局撤销栈（P0-10）。删除类操作压栈，Ctrl+Z 弹栈撤销。对齐 <c>ToastCenter.Shared</c> 的
/// 全局单例模式，避免给一堆 ViewModel / 窗口塞构造参数。
/// <para>关键约束：撤销操作里捕获的是某个项目的 DB 数据，<b>切换项目时必须 Clear()</b>，
/// 否则在项目 B 撤销项目 A 的删除会写脏数据。</para>
/// </summary>
public sealed class UndoManager
{
    public static UndoManager Shared { get; } = new();

    private readonly object _gate = new();
    private readonly LinkedList<IUndoableAction> _stack = new(); // 头部=最新
    private const int MaxDepth = 20;

    /// <summary>栈内容变化时触发（UI 可据此更新「撤销」入口的可用态/文案）。</summary>
    public event Action? Changed;

    public bool CanUndo
    {
        get { lock (_gate) { return _stack.Count > 0; } }
    }

    /// <summary>栈顶操作的描述（无则 null），用于提示「可撤销：删除 N 个分镜」。</summary>
    public string? TopDescription
    {
        get { lock (_gate) { return _stack.First?.Value.Description; } }
    }

    public void Push(IUndoableAction action)
    {
        lock (_gate)
        {
            _stack.AddFirst(action);
            while (_stack.Count > MaxDepth)
            {
                _stack.RemoveLast(); // 超深度丢最老的（撤销不回那么远是可接受的）
            }
        }
        Changed?.Invoke();
    }

    /// <summary>撤销最近一次操作。返回被撤销操作的描述（无可撤销则 null）。</summary>
    public string? Undo()
    {
        IUndoableAction? action;
        lock (_gate)
        {
            if (_stack.First is null)
            {
                return null;
            }
            action = _stack.First.Value;
            _stack.RemoveFirst();
        }

        try
        {
            action.Undo();
            Log.Information("[Undo] 已撤销: {Desc}", action.Description);
            Changed?.Invoke();
            return action.Description;
        }
        catch (Exception ex)
        {
            // 撤销失败：不重新压回（避免反复失败死循环），记日志由调用方提示用户。
            Log.Error(ex, "[Undo] 撤销失败: {Desc}", action.Description);
            Changed?.Invoke();
            throw;
        }
    }

    /// <summary>清空撤销栈。切换项目 / 关闭项目时必须调用，防跨项目脏写。</summary>
    public void Clear()
    {
        bool had;
        lock (_gate)
        {
            had = _stack.Count > 0;
            _stack.Clear();
        }
        if (had)
        {
            Changed?.Invoke();
        }
    }
}
