# 自定义叙事结构 实施计划（对齐 issue #6 / macOS v0.3.7）

> **For agentic workers:** REQUIRED SUB-SKILL: superpowers:subagent-driven-development 或 superpowers:executing-plans 逐任务实现。
> **Spec = GitHub issue #6**（RoshanGH/mixcut-windows/issues/6），含 11 固定标签、三级层级、编辑器、生成流水线、AI 提示词、验收清单。

**Goal:** Windows 端实现「自定义叙事结构」——用户用系统标签逐段定义叙事结构 → AI 在每段候选里选片生成多变体 → 台词连贯校验后只留通过的；复用现有方案实体/详情/导出 UI。

**Architecture:** 每个「结构」= 一条 `MixStrategy`（新增 `IsNarrativeTemplate=true` + `NarrativeSlotsJson`），归「自定义结构」分组（可多条，区别于单条的 CustomGroup）。生成走「用户定结构」替代 AI Step1，复用 Step2 选片 + 现有 SchemeSegment/详情/导出。

**Tech Stack:** C# / WPF / .NET8 / EF Core SQLite / CommunityToolkit.Mvvm

**关键约束:** 数据层用现成 `AddColumnIfMissing` 幂等迁移 + `JsonColumn` 读写；不破坏现有 AI 策略 / 自定义组合（§不破坏已有功能 SOP）；AI 选片环节**需 API Key 才能端到端验**，离线只能验数据层+候选池+校验纯逻辑。

---

## 文件结构

| 文件 | 责任 | 动作 |
|---|---|---|
| `Models/NarrativeSlot.cs` | 段位值对象 `{int Order; List<SemanticType> Tags}` + JSON 序列化 | Create |
| `Models/MixStrategy.cs` | 加 `IsNarrativeTemplate` + `NarrativeSlotsJson` + 计算属性 `NarrativeSlots` + `NarrativeDisplayName` | Modify |
| `App.xaml.cs` | `AddColumnIfMissing` 补两列 | Modify |
| `Services/SchemeGeneration/NarrativeCandidatePool.cs` | 纯逻辑：算候选池/可行变体上限/Top-30/程序侧二次校验 | Create |
| `Services/SchemeGeneration/SchemeGenerationService.cs` | 加 `GenerateNarrativeCompositionsAsync` | Modify |
| `Resources/Prompts/narrative_structure_prompt.md` | AI 选片+连贯自检提示词（issue §六骨架） | Create |
| `ViewModels/SchemeViewModel.cs` | `NarrativeTemplates` 分组、`CreateNarrativeStructureAsync`、生成命令（独立 loading） | Modify |
| `ViewModels/NarrativeEditorViewModel.cs` | 编辑器状态：段位增删/拖拽/加标签/候选数/预览名/生成数 | Create |
| `Views/Schemes/NarrativeStructureEditorView.xaml(.cs)` | 编辑器 UI（issue §四） | Create |
| `Views/SchemesView.xaml.cs` | 左栏加「自定义结构」分组 + 「＋添加结构」入口 | Modify |
| `Views/ExportView.xaml.cs` | 导出列表含 narrative 结构方案 | Modify |
| `MixCut.Tests/NarrativeSlotTests.cs` / `NarrativeCandidatePoolTests.cs` | 数据层+纯逻辑单测 | Create |

---

## Phase A — 数据模型（离线可全验）

### Task A1: NarrativeSlot 值对象 + 单测
- [ ] **测试先行** `MixCut.Tests/NarrativeSlotTests.cs`：
```csharp
[Fact] public void Slots_序列化往返不丢()
{
    var slots = new List<NarrativeSlot>
    {
        new(1, new(){ SemanticType.PainPoint, SemanticType.Hook }),
        new(2, new(){ SemanticType.CallToAction }),
    };
    var json = NarrativeSlot.Serialize(slots);
    var back = NarrativeSlot.Deserialize(json);
    Assert.Equal(2, back.Count);
    Assert.Equal(SemanticType.CallToAction, back[1].Tags[0]);
    Assert.Equal(2, back[0].Tags.Count);
}
[Fact] public void Deserialize_空或null_返回空列表() => Assert.Empty(NarrativeSlot.Deserialize(null));
```
- [ ] **实现** `Models/NarrativeSlot.cs`：record `NarrativeSlot(int Order, List<SemanticType> Tags)` + 静态 `Serialize`/`Deserialize`（用 `SemanticTypeExtensions.ToLabel/FromLabel` 存中文标签，复用 `JsonColumn` 风格）。
- [ ] 构建机 `dotnet test --filter NarrativeSlotTests` 绿；commit。

### Task A2: MixStrategy 新字段 + 迁移
- [ ] `Models/MixStrategy.cs` 加 `public bool IsNarrativeTemplate { get; set; }`、`public string? NarrativeSlotsJson { get; set; }`、计算属性 `NarrativeSlots`（读写 NarrativeSlotsJson）、`NarrativeDisplayName`（段标签按 ` · ` 拼、同段多标签 `/`，issue §二命名规则）。
- [ ] `App.xaml.cs` 在现有 `AddColumnIfMissing` 处加：
```csharp
AddColumnIfMissing(db, "Strategies", "IsNarrativeTemplate", "INTEGER NOT NULL DEFAULT 0");
AddColumnIfMissing(db, "Strategies", "NarrativeSlotsJson", "TEXT");
```
- [ ] `NarrativeDisplayName` 单测（拼接规则）；Mac 编译 0 错；构建机 publish 启动 grep `[SchemaMigration]` 补列成功；commit。

## Phase B — 候选池 + 校验纯逻辑（离线可全验，TDD 核心）

### Task B1: NarrativeCandidatePool + 单测
- [ ] **测试先行** `NarrativeCandidatePoolTests.cs`，覆盖：
  - `CandidatesForSlot`：分镜 SemanticTypes ∩ slot.Tags ≠ ∅ 才入选（标签取并集）。
  - `FeasibleVariantCap`：各段候选数乘积，封顶到请求数（issue §五.2）。
  - `TopN`：按质量分降序、再时长降序取前 30。
  - `ValidateComposition`：段数==slot 数、每段所选 segment 在该段候选池内、变体内无重复 → 不合法丢弃（issue §五.6）。
  - 边界：某段候选 0 → cap=0 / 阻止。
- [ ] **实现** `Services/SchemeGeneration/NarrativeCandidatePool.cs`（纯静态函数，入参 segments + slots）。
- [ ] 构建机测试绿；commit。

## Phase C — AI 生成流水线（需 API Key 才能端到端验）

### Task C1: 提示词
- [ ] `Resources/Prompts/narrative_structure_prompt.md`：照 issue §六骨架（固定段顺序、每段候选、硬性规则①-④、输出 `{"compositions":[{"segments":[...],"desc":...}]}`，键名与现有 Step2 解析一致）。csproj Content 已通配 `Resources/Prompts/*`，自动打包。

### Task C2: SchemeGenerationService.GenerateNarrativeCompositionsAsync
- [ ] 新方法：入参 slots + 每段 Top-30 候选目录 + variationCount；拼 prompt → `AIProvider.GenerateJsonAsync` → 复用 `CompositionResponse` 解析（自适应 3 格式）→ 返回 `AICompactComposition[]`。
- [ ] 单元可测部分：候选目录文本拼接（纯函数，抽出来测）。AI 调用本身需 Key，标注「构建机配 Key 后实跑验」。

## Phase D — 编辑器 ViewModel + View

### Task D1: NarrativeEditorViewModel
- [ ] 段位 ObservableCollection（增删/拖拽重排）、每段已选标签集合、`AvailableTags`（**只列当前项目库里真实有分镜的 SemanticType**）、每段实时候选数（含「>30 取前 30」上限显示）、预览名（实时拼）、生成数、`CanGenerate`（每段≥1 标签且候选>0）、独立 `IsGenerating`。

### Task D2: NarrativeStructureEditorView.xaml(.cs)
- [ ] 照 issue §四布局：只读预览名、段位行（标签 chip + ＋加标签 popup + 候选数 + 删除 + 拖拽）、＋添加一段、生成变体数下拉、生成按钮（loading）。某段标红 + 禁用规则。

## Phase E — 侧栏三级层级 + 落库

### Task E1: SchemeViewModel 分组与创建
- [ ] `NarrativeTemplates`（`Strategies.Where(s => s.IsNarrativeTemplate)`）；`OrderedStrategiesForDisplay` 末尾加「自定义结构」组（AI策略 → 自定义组合 → 自定义结构）。
- [ ] `CreateNarrativeStructureAsync(project, slots, variationCount)`：建 `MixStrategy{IsNarrativeTemplate=true, NarrativeSlotsJson=...}` → 算候选池/上限 → 调 C2 生成 → B1 程序侧二次校验过滤 → 通过的落库为 MixScheme(`变体一/二/…`)+SchemeSegment → 全未过提示不留空壳 → `LoadSchemes` 刷新。

### Task E2: SchemesView 左栏渲染
- [ ] 「自定义结构」分组：每条结构显示 `NarrativeDisplayName`，下挂变体；「＋添加结构」入口 → 打开编辑器。变体详情**复用现有方案详情页**（点进去与 AI 方案一致：预览/微调/导出）。
- [ ] `LoadProject` 重置编辑器可选标签/已建结构/展开态（切项目不串数据，§切换项目联动铁律）。

## Phase F — 导出集成
- [ ] `ExportView.LoadProject` 默认全选含 narrative 结构方案；导出列表枚举不能只算 AI策略+自定义组合（issue §七最后一条）。

## Phase G — 验证
- [ ] 离线全验：Phase A/B 单测全绿（数据往返、候选池、上限、Top-30、二次校验）。
- [ ] 构建机 publish 启动：`[SchemaMigration]` 补两列成功、无崩。
- [ ] **配 API Key 后实跑**（构建机或你本地）：建结构 → 生成 → 出变体 → 点进详情能预览/微调 → 导出页能选能导。
- [ ] 切项目数据不串；重启结构与变体仍在（落库）。
- [ ] issue §七验收清单逐条核对。

---

## 自验证边界（诚实声明）
- **离线能全验**：数据模型、迁移、候选池/上限/Top-30/程序侧二次校验（纯逻辑单测）。
- **需 API Key 才能验**：AI 选片质量 + 台词连贯校验效果（issue §六的成败关键）。这部分要构建机配 Key 或你本地实跑，我会把能测的纯逻辑测死，AI 环节标注「待配 Key 实跑」。
- **需真人验**：编辑器交互手感、拖拽、三级层级显示、详情复用。

## Self-Review
- 覆盖 issue §一-§八：层级→E；编辑器→D;生成流水线→B+C+E1;AI 提示词→C1;数据模型→A;导出→F;验收→G。§八「暂不做」(跨项目模板复用/手动命名) 不实现。
- 风险:AI 放水(什么都说连贯)→ issue §六预案「拆两次调用,第二次独立严格打分」,留作 C 的迭代后备。
