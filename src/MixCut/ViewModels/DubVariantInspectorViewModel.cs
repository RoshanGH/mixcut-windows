using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MixCut.Models;
using MixCut.Utilities;

namespace MixCut.ViewModels;

/// <summary>配音变体检视器的内容状态。</summary>
public enum DubInspectorState
{
    /// <summary>未选中分镜，面板隐藏。</summary>
    Hidden,
    /// <summary>保留原声（不参与配音）。</summary>
    Locked,
    /// <summary>还没任何改写版。</summary>
    Empty,
    /// <summary>已有改写版（变体池）。</summary>
    Pool,
}

/// <summary>
/// 配音变体检视器 VM（v0.5.0）。对应 mac「配音变体池」检视器。单击分镜 → Load 显示该分镜的变体池。
/// 数据来自 <see cref="DubbingViewModel"/>（短上下文读写），与分镜库长 context 解耦。
/// </summary>
public sealed partial class DubVariantInspectorViewModel : ObservableObject
{
    private readonly DubbingViewModel _dubbing;

    public DubVariantInspectorViewModel(DubbingViewModel dubbing)
    {
        _dubbing = dubbing;
    }

    /// <summary>当前分镜（null = 未选中）。</summary>
    public Segment? Segment { get; private set; }

    [ObservableProperty] private DubInspectorState _state = DubInspectorState.Hidden;
    [ObservableProperty] private string _headerSubtitle = string.Empty;
    [ObservableProperty] private bool _isBusy;

    public bool IsNotBusy => !IsBusy;
    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(IsNotBusy));

    /// <summary>该分镜的原台词字数（字数提示基准）。</summary>
    private int _originalLength;

    public ObservableCollection<DubVariantItemViewModel> Variants { get; } = new();

    public bool IsVisible => State != DubInspectorState.Hidden;

    public bool IsLocked => State == DubInspectorState.Locked;
    public bool IsEmpty => State == DubInspectorState.Empty;
    public bool IsPool => State == DubInspectorState.Pool;

    partial void OnStateChanged(DubInspectorState value)
    {
        OnPropertyChanged(nameof(IsVisible));
        OnPropertyChanged(nameof(IsLocked));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(IsPool));
    }

    /// <summary>清空（切项目 / 取消选中）。</summary>
    public void Clear()
    {
        _dubbing.StopDubPlayback();
        Segment = null;
        Variants.Clear();
        State = DubInspectorState.Hidden;
    }

    /// <summary>关闭检视器（✕ 按钮）。</summary>
    [RelayCommand]
    private void Close() => Clear();

    /// <summary>单击分镜后加载其变体池。</summary>
    public async Task LoadAsync(Segment segment)
    {
        Segment = segment;
        _originalLength = segment.Text?.Length ?? 0;
        HeaderSubtitle = $"分镜 {segment.SegmentIndex} · 时长 {segment.Duration:F1}s";

        if (segment.IsVoiceLocked)
        {
            Variants.Clear();
            State = DubInspectorState.Locked;
            return;
        }

        await RefreshVariantsAsync();
    }

    /// <summary>重新从 DB 读变体（任何增删改后调用）。</summary>
    public async Task RefreshVariantsAsync()
    {
        if (Segment is null) return;
        var dubs = await _dubbing.LoadVariantsAsync(Segment.Id);
        Variants.Clear();
        foreach (var d in dubs)
        {
            Variants.Add(new DubVariantItemViewModel(d, Segment.Duration, _originalLength, this, _dubbing));
        }
        State = Variants.Count == 0 ? DubInspectorState.Empty : DubInspectorState.Pool;
        Serilog.Log.Information("[DubDiag] 检视器加载 seg={Seg} variants={N} state={State}",
            Segment.SegmentIndex, Variants.Count, State);
    }

    // ---- 命令 ----

    /// <summary>↻ 重新改写本分镜（基于当前原台词重出 N 套）。</summary>
    [RelayCommand]
    private async Task RewriteSegmentAsync()
    {
        if (Segment is null) return;
        IsBusy = true;
        try { await _dubbing.RewriteSegmentAsync(Segment.Id); await RefreshVariantsAsync(); }
        finally { IsBusy = false; }
    }

    /// <summary>+ 手动添加一版（自己写台词）。</summary>
    [RelayCommand]
    private async Task AddManualAsync()
    {
        if (Segment is null) return;
        IsBusy = true;
        try
        {
            var idx = await _dubbing.AddManualVariantAsync(Segment.Id);
            await RefreshVariantsAsync();
            // 新建版自动进入编辑态
            if (idx is { } i && Variants.FirstOrDefault(v => v.TextVariantIndex == i) is { } added)
            {
                added.BeginEdit();
            }
        }
        finally { IsBusy = false; }
    }

    internal bool IsPlaying(Guid dubId) => _dubbing.PlayingDubId == dubId;
}

/// <summary>变体池里的一张「改写版卡」VM。</summary>
public sealed partial class DubVariantItemViewModel : ObservableObject
{
    private readonly DubVariantInspectorViewModel _inspector;
    private readonly DubbingViewModel _dubbing;
    private readonly SegmentDub _dub;
    private readonly double _targetDuration;
    private readonly int _originalLength;

    public DubVariantItemViewModel(SegmentDub dub, double targetDuration, int originalLength,
        DubVariantInspectorViewModel inspector, DubbingViewModel dubbing)
    {
        _dub = dub;
        _targetDuration = targetDuration;
        _originalLength = originalLength;
        _inspector = inspector;
        _dubbing = dubbing;
        _rewrittenText = dub.RewrittenText;
        _editingText = dub.RewrittenText;
    }

    public Guid DubId => _dub.Id;
    public int TextVariantIndex => _dub.TextVariantIndex;

    /// <summary>改写版字母 A/B/C…。</summary>
    public string VariantLetter => ((char)('A' + _dub.TextVariantIndex)).ToString();

    [ObservableProperty] private string _rewrittenText;
    [ObservableProperty] private bool _isEditing;
    [ObservableProperty] private string _editingText;

    public bool HasAudio => !string.IsNullOrEmpty(_dub.AudioFilePath);
    public bool IsPlaying => _inspector.IsPlaying(_dub.Id);

    /// <summary>配音是否「过期」（已生成但边界/文本变了——简化：靠 Status==Pending 且无音频判断由上层刷新）。</summary>
    public bool IsGenerated => _dub.Status == SegmentDubStatus.Generated && HasAudio;

    // ---- 字数提示（基准 = 原分镜台词字数，PRD §3.5）----

    public string CharCountText
    {
        get
        {
            var n = RewrittenText.Length;
            var m = _originalLength;
            if (m > 0 && n > m + Math.Max(5, (int)(m * 0.2))) return $"{n} 字";
            if (m > 0 && n < m / 2) return $"{n} 字";
            return $"{n} 字 / 原 {m} 字";
        }
    }

    /// <summary>字数提示色：red=太多 / orange=太少 / gray=正常。</summary>
    public string CharCountLevel
    {
        get
        {
            var n = RewrittenText.Length;
            var m = _originalLength;
            if (m > 0 && n > m + Math.Max(5, (int)(m * 0.2))) return "warn";   // 红
            if (m > 0 && n < m / 2) return "short";                            // 橙
            return "ok";
        }
    }

    public string CharCountHint => CharCountLevel switch
    {
        "warn" => $"比原台词({_originalLength}字)多很多，配音会明显加速，建议精简",
        "short" => $"比原台词({_originalLength}字)少很多，末尾会留白，建议加字",
        _ => string.Empty,
    };

    // ---- 配音时长 ----

    public string DurationText => HasAudio ? $"{_dub.AudioDuration:F1}s" : string.Empty;

    /// <summary>时长色：green=贴合画面 / orange=不对齐。</summary>
    public bool DurationAligned => HasAudio && Math.Abs(_dub.AudioDuration - _targetDuration) <= 0.2;

    public string StatusText => _dub.Status switch
    {
        SegmentDubStatus.Generated => HasAudio ? "已生成" : "待生成",
        SegmentDubStatus.Failed => "生成失败",
        _ => "待生成",
    };

    // ---- 命令 ----

    public void BeginEdit() { EditingText = RewrittenText; IsEditing = true; }

    [RelayCommand] private void Edit() => BeginEdit();
    [RelayCommand] private void CancelEdit() { IsEditing = false; EditingText = RewrittenText; }

    [RelayCommand]
    private async Task SaveEditAsync()
    {
        var t = EditingText.Trim();
        if (t.Length == 0 || _inspector.Segment is null) { IsEditing = false; return; }
        IsEditing = false;
        if (t == RewrittenText) return;
        await _dubbing.UpdateVariantTextAsync(_inspector.Segment.Id, _dub.TextVariantIndex, t);
        await _inspector.RefreshVariantsAsync();
    }

    [RelayCommand]
    private async Task GenerateAsync()
    {
        await _dubbing.RegenerateAudioAsync(_dub.Id);
        await _inspector.RefreshVariantsAsync();
    }

    [RelayCommand]
    private void Play()
    {
        _dubbing.PlayDub(_dub);
        OnPropertyChanged(nameof(IsPlaying));
    }

    [RelayCommand]
    private async Task DeleteAsync()
    {
        if (_inspector.Segment is null) return;
        var segId = _inspector.Segment.Id;
        var snapshot = await _dubbing.DeleteVariantAsync(segId, _dub.TextVariantIndex);
        await _inspector.RefreshVariantsAsync();
        if (snapshot.Count > 0)
        {
            Infrastructure.UndoStack.UndoManager.Shared.Push(new Infrastructure.UndoStack.DelegateUndoAction(
                $"删除改写版 {VariantLetter}",
                () => { _ = RestoreAndRefreshAsync(snapshot); }));
            Views.Components.ToastService.Show($"已删除改写版 {VariantLetter}", Views.Components.ToastStyle.Warning,
                "撤销", () => Infrastructure.UndoStack.UndoManager.Shared.Undo());
        }
    }

    private async Task RestoreAndRefreshAsync(IReadOnlyList<SegmentDub> snapshot)
    {
        await _dubbing.RestoreVariantsAsync(snapshot);
        await _inspector.RefreshVariantsAsync();
        Views.Components.ToastService.Show("已恢复改写版", Views.Components.ToastStyle.Success);
    }
}
