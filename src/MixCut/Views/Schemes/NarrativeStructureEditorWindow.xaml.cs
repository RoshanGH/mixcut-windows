using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MixCut.Models;
using MixCut.Services.SchemeGeneration;
using MixCut.ViewModels;

namespace MixCut.Views.Schemes;

/// <summary>
/// 自定义叙事结构编辑器（issue #6 §四）：逐段选系统标签 → 实时候选数/预览名 → 生成方案。
/// 状态与渲染在 code-behind（对齐 SchemesView 动态构建风格）；生成走
/// <see cref="SchemeViewModel.CreateNarrativeStructureAsync"/>。
/// </summary>
public partial class NarrativeStructureEditorWindow : Window
{
    private readonly SchemeViewModel _vm;
    private readonly Project _project;
    private readonly IReadOnlyList<Segment> _segments;
    private readonly List<SemanticType> _availableTags;       // 只列库里真实有分镜的标签
    private readonly List<List<SemanticType>> _slots = new();  // 各段已选标签
    private bool _generating;

    /// <summary>生成成功后非 null，供调用方刷新左栏并选中。</summary>
    public MixStrategy? CreatedStructure { get; private set; }

    public NarrativeStructureEditorWindow(SchemeViewModel vm, Project project, IReadOnlyList<Segment> segments)
    {
        _vm = vm;
        _project = project;
        _segments = segments;
        InitializeComponent();

        // 可选标签 = 库里真实出现过的语义类型（没素材的不出现，issue 核心原则①）
        _availableTags = SemanticTypeExtensions.All
            .Where(t => _segments.Any(s => s.SemanticTypes.Contains(t)))
            .ToList();

        _slots.Add(new List<SemanticType>()); // 起始给一段空的
        RenderSlots();
    }

    private int VariationCount =>
        VariationCombo.SelectedItem is ComboBoxItem item && int.TryParse(item.Content?.ToString(), out var n)
            ? n
            : 5;

    private int CandidateCount(IReadOnlyList<SemanticType> tags) =>
        tags.Count == 0
            ? 0
            : NarrativeCandidatePool.CandidatesForSlot(_segments, new NarrativeSlot(0, tags.ToList())).Count;

    private void RenderSlots()
    {
        SlotsPanel.Children.Clear();

        if (_availableTags.Count == 0)
        {
            SlotsPanel.Children.Add(new TextBlock
            {
                Text = "当前项目还没有带语义标签的分镜，请先在分镜素材库分析视频。",
                FontSize = 12, Foreground = Brushes.Gray, TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(4, 10, 4, 10),
            });
            GenerateButton.IsEnabled = false;
            PreviewNameText.Text = "（无可用标签）";
            return;
        }

        for (var i = 0; i < _slots.Count; i++)
        {
            SlotsPanel.Children.Add(BuildSlotRow(i));
        }

        // ＋ 添加一段
        var addBtn = new Button
        {
            Content = "＋ 添加一段",
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 4, 0, 0),
            Padding = new Thickness(12, 6, 12, 6),
            Background = Brushes.Transparent,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x1D, 0x6B, 0xE5)),
            BorderThickness = new Thickness(1),
            Foreground = new SolidColorBrush(Color.FromRgb(0x1D, 0x6B, 0xE5)),
            Cursor = Cursors.Hand,
        };
        addBtn.Click += (_, _) => { _slots.Add(new List<SemanticType>()); RenderSlots(); };
        SlotsPanel.Children.Add(addBtn);

        UpdatePreviewAndGate();
    }

    private UIElement BuildSlotRow(int index)
    {
        var tags = _slots[index];
        var cand = CandidateCount(tags);
        var hasError = tags.Count == 0 || cand == 0;

        var border = new Border
        {
            Background = Brushes.White,
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 10, 12, 10),
            Margin = new Thickness(0, 0, 0, 8),
            BorderBrush = hasError
                ? new SolidColorBrush(Color.FromRgb(0xD3, 0x3A, 0x3A))
                : new SolidColorBrush(Color.FromRgb(0xE3, 0xE3, 0xE6)),
            BorderThickness = new Thickness(hasError ? 1.5 : 1),
        };

        var root = new StackPanel();

        // 第一行：序号 + 候选数 + 上移/下移/删除
        var top = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 0, 0, 6) };
        var numText = new TextBlock
        {
            Text = $"第 {index + 1} 段", FontSize = 12, FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        };
        DockPanel.SetDock(numText, Dock.Left);
        top.Children.Add(numText);

        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        DockPanel.SetDock(actions, Dock.Right);

        var candText = new TextBlock
        {
            Text = tags.Count == 0
                ? "未选标签"
                : (cand > 30 ? $"候选 30（共 {cand}，按质量取前 30）" : $"候选 {cand}"),
            FontSize = 11,
            Foreground = hasError
                ? new SolidColorBrush(Color.FromRgb(0xD3, 0x3A, 0x3A))
                : Brushes.Gray,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0),
        };
        actions.Children.Add(candText);

        if (index > 0)
        {
            actions.Children.Add(MiniButton("▲", () => { Swap(index, index - 1); }));
        }
        if (index < _slots.Count - 1)
        {
            actions.Children.Add(MiniButton("▼", () => { Swap(index, index + 1); }));
        }
        if (_slots.Count > 1)
        {
            actions.Children.Add(MiniButton("🗑", () => { _slots.RemoveAt(index); RenderSlots(); }));
        }
        top.Children.Add(actions);
        root.Children.Add(top);

        // 第二行：标签 chips + ＋加标签
        var wrap = new WrapPanel();
        foreach (var tag in tags)
        {
            wrap.Children.Add(BuildTagChip(index, tag));
        }
        wrap.Children.Add(BuildAddTagButton(index));
        root.Children.Add(wrap);

        border.Child = root;
        return border;
    }

    private UIElement BuildTagChip(int slotIndex, SemanticType tag)
    {
        Color color;
        try { color = (Color)ColorConverter.ConvertFromString(tag.ToColorHex()); }
        catch { color = Color.FromRgb(0x1D, 0x6B, 0xE5); }

        var chip = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0x22, color.R, color.G, color.B)),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(8, 3, 6, 3),
            Margin = new Thickness(0, 0, 6, 6),
        };
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        sp.Children.Add(new TextBlock
        {
            Text = tag.ToLabel(), FontSize = 11,
            Foreground = new SolidColorBrush(color), VerticalAlignment = VerticalAlignment.Center,
        });
        var del = new TextBlock
        {
            Text = "✕", FontSize = 10, Margin = new Thickness(5, 0, 0, 0),
            Foreground = new SolidColorBrush(color), Cursor = Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Center,
        };
        del.MouseLeftButtonUp += (_, _) => { _slots[slotIndex].Remove(tag); RenderSlots(); };
        sp.Children.Add(del);
        chip.Child = sp;
        return chip;
    }

    private UIElement BuildAddTagButton(int slotIndex)
    {
        var btn = new Button
        {
            Content = "＋加标签", FontSize = 11, Padding = new Thickness(8, 3, 8, 3),
            Margin = new Thickness(0, 0, 6, 6), Cursor = Cursors.Hand,
            Background = Brushes.Transparent,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xD0)),
            BorderThickness = new Thickness(1),
        };
        var menu = new ContextMenu();
        var remaining = _availableTags.Where(t => !_slots[slotIndex].Contains(t)).ToList();
        if (remaining.Count == 0)
        {
            menu.Items.Add(new MenuItem { Header = "（已全部添加）", IsEnabled = false });
        }
        foreach (var tag in remaining)
        {
            var item = new MenuItem { Header = tag.ToLabel() };
            var captured = tag;
            item.Click += (_, _) => { _slots[slotIndex].Add(captured); RenderSlots(); };
            menu.Items.Add(item);
        }
        btn.Click += (_, _) =>
        {
            menu.PlacementTarget = btn;
            menu.IsOpen = true;
        };
        return btn;
    }

    private Button MiniButton(string glyph, Action onClick)
    {
        var b = new Button
        {
            Content = glyph, Width = 26, Height = 24, FontSize = 11, Margin = new Thickness(2, 0, 0, 0),
            Background = Brushes.Transparent, BorderBrush = new SolidColorBrush(Color.FromRgb(0xDD, 0xDD, 0xE0)),
            BorderThickness = new Thickness(1), Cursor = Cursors.Hand,
        };
        b.Click += (_, _) => onClick();
        return b;
    }

    private void Swap(int a, int b)
    {
        (_slots[a], _slots[b]) = (_slots[b], _slots[a]);
        RenderSlots();
    }

    private void UpdatePreviewAndGate()
    {
        var preview = string.Join(" · ", _slots
            .Where(s => s.Count > 0)
            .Select(s => string.Join("/", s.Select(t => t.ToLabel()))));
        PreviewNameText.Text = string.IsNullOrEmpty(preview) ? "（先给每段添加标签）" : preview;

        var allValid = _slots.Count > 0 && _slots.All(s => s.Count > 0 && CandidateCount(s) > 0);
        GenerateButton.IsEnabled = allValid && !_generating;
    }

    private async void OnGenerateClick(object sender, RoutedEventArgs e)
    {
        if (_generating)
        {
            return;
        }
        try
        {
            _generating = true;
            GenerateButton.IsEnabled = false;
            StatusText.Text = "正在生成方案（AI 选片 + 台词连贯校验）…";

            var slots = _slots
                .Select((tags, i) => new NarrativeSlot(i + 1, tags.ToList()))
                .ToList();

            var result = await _vm.CreateNarrativeStructureAsync(_project, slots, _segments, VariationCount);

            if (result is not null)
            {
                CreatedStructure = result;
                DialogResult = true;
                Close();
            }
            else
            {
                StatusText.Text = "未生成连贯变体，建议调整段位标签或增加素材";
                _generating = false;
                UpdatePreviewAndGate();
            }
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "[NarrativeGen] 编辑器生成异常");
            StatusText.Text = "生成失败：" + ex.Message;
            _generating = false;
            UpdatePreviewAndGate();
        }
    }
}
