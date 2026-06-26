using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MixCut.Data;
using MixCut.Models;
using MixCut.Services.SchemeGeneration;

namespace MixCut.ViewModels;

/// <summary>方案浏览 ViewModel。对应 macOS 版 SchemeViewModel。</summary>
public partial class SchemeViewModel : ObservableObject, IDisposable
{
    private readonly IDbContextFactory<MixCutDbContext> _dbFactory;
    private readonly SchemeGenerationService _schemeService;
    private readonly ILogger<SchemeViewModel> _logger;

    private MixCutDbContext? _context;
    private Project? _loadedProject; // P0-10：当前加载的项目，撤销恢复后重查用

    /// <summary>项目的所有策略。</summary>
    public ObservableCollection<MixStrategy> Strategies { get; } = new();

    [ObservableProperty]
    private MixStrategy? _selectedStrategy;

    [ObservableProperty]
    private MixScheme? _selectedScheme;

    [ObservableProperty]
    private bool _isGenerating;

    [ObservableProperty]
    private string _generationProgress = string.Empty;

    [ObservableProperty]
    private string? _errorMessage;

    public SchemeViewModel(
        IDbContextFactory<MixCutDbContext> dbFactory,
        SchemeGenerationService schemeService,
        ILogger<SchemeViewModel> logger)
    {
        _dbFactory = dbFactory;
        _schemeService = schemeService;
        _logger = logger;
    }

    /// <summary>所有方案的扁平列表。</summary>
    public IReadOnlyList<MixScheme> Schemes =>
        Strategies.SelectMany(s => s.OrderedSchemes).ToList();

    /// <summary>AI 生成的策略（排除自定义组合容器 + 自定义叙事结构）。供 SchemeListView 在「AI 方案」分组渲染。</summary>
    public IReadOnlyList<MixStrategy> AIStrategies =>
        Strategies.Where(s => !s.IsCustomGroup && !s.IsNarrativeTemplate).ToList();

    /// <summary>当前项目的「自定义组合」容器策略（Phase 1 保证恰好 1 条或 null）。</summary>
    public MixStrategy? CustomGroup =>
        Strategies.FirstOrDefault(s => s.IsCustomGroup);

    /// <summary>当前项目的「自定义叙事结构」模板策略（issue #6，可多条）。</summary>
    public IReadOnlyList<MixStrategy> NarrativeTemplates =>
        Strategies.Where(s => s.IsNarrativeTemplate).OrderBy(s => s.CreatedAt).ToList();

    /// <summary>
    /// SchemeListView 左栏渲染用的策略顺序：AI 策略 → 自定义组合 → 自定义叙事结构（issue #6）。
    /// 对齐 Mac SchemeViewModel.orderedStrategiesForDisplay。
    /// </summary>
    public IReadOnlyList<MixStrategy> OrderedStrategiesForDisplay
    {
        get
        {
            var list = new List<MixStrategy>(AIStrategies);
            var custom = CustomGroup;
            if (custom is not null)
            {
                list.Add(custom);
            }
            list.AddRange(NarrativeTemplates);
            return list;
        }
    }

    /// <summary>加载项目的所有策略和方案。</summary>
    public void LoadSchemes(Project project)
    {
        _context?.Dispose();
        _context = _dbFactory.CreateDbContext();
        _loadedProject = project; // P0-10：记住当前项目，撤销恢复后重查用

        var projectId = project.Id;
        // 不能 ThenInclude(seg => seg.Video) —— EF Core fix-up 自动填充反向导航，重复 Include 会
        // 触发 NavigationBaseIncludeIgnored 警告并升级为 InvalidOperationException。
        // 通过单独预加载 Videos 让跟踪上下文自动 fix-up segment.Video。
        var strategies = _context.Strategies
            .Include(s => s.Schemes).ThenInclude(sc => sc.SchemeSegments).ThenInclude(ss => ss.Segment!)
                // v0.5.0：带上各分镜的配音变体，供方案选变体 / 配音组合导出用 EffectiveDubVariants。
                .ThenInclude(seg => seg.SegmentDubs)
            // P0-8：Schemes×SchemeSegments 嵌套集合 SingleQuery 会笛卡尔放大；拆分查询更快、结果一致。
            // 仍是跟踪查询，identity map 保证 segment.Video 反向 fix-up（见上）照常工作。
            .AsSplitQuery()
            .Where(s => s.ProjectId == projectId)
            .OrderBy(s => s.CreatedAt)
            .ToList();
        // 预加载该项目所有 Videos 进上下文，让 segment.Video 反向引用被 fix-up
        _ = _context.Videos
            .Where(v => v.ProjectVideos.Any(pv => pv.ProjectId == projectId))
            .ToList();

        Strategies.Clear();
        foreach (var strategy in strategies)
        {
            Strategies.Add(strategy);
        }

        if (SelectedStrategy is null && Strategies.Count > 0)
        {
            SelectedStrategy = Strategies[0];
            SelectedScheme = SelectedStrategy.OrderedSchemes.FirstOrDefault();
        }
    }

    // ---- 生成 ----

    /// <summary>生成混剪方案（策略 + 批量组合）。</summary>
    public async Task GenerateSchemesAsync(
        Project project, int targetVideoCount = 50, string? customPrompt = null,
        CancellationToken cancellationToken = default)
    {
        var clampedTarget = Math.Min(targetVideoCount, 100);

        IsGenerating = true;
        ErrorMessage = null;

        _context?.Dispose();
        _context = _dbFactory.CreateDbContext();

        Project? dbProject = null;
        try
        {
            // 注意：Segments.Video 是反向导航，EF Core 会自动 fix-up，不能再 ThenInclude
            // （会触发 NavigationBaseIncludeIgnored 警告并升级为 InvalidOperationException）。
            // P0-25：生成是异步任务，期间用户可能删掉项目 → 原 First() 会抛 InvalidOperationException
            // 让进程崩。改 FirstOrDefault + null 检查，友好提示并安全退出。
            dbProject = _context.Projects
                .Include(p => p.ProjectVideos).ThenInclude(pv => pv.Video!).ThenInclude(v => v.Segments)
                .AsSplitQuery() // P0-8：嵌套集合拆分查询，避免笛卡尔放大
                .FirstOrDefault(p => p.Id == project.Id);
            if (dbProject is null)
            {
                ErrorMessage = "项目已被删除，已取消生成";
                _logger.LogWarning("[SchemeGen] 项目在生成前已被删除，取消生成 project={Id}", project.Id);
                return;
            }

            dbProject.Status = ProjectStatus.Generating;
            _context.SaveChanges();

            var allSegments = dbProject.ProjectVideos
                .Where(pv => pv.Video is not null)
                .SelectMany(pv => pv.Video!.Segments)
                .ToList();

            if (allSegments.Count == 0)
            {
                ErrorMessage = "没有可用的分镜素材，请先导入并分析视频";
                dbProject.Status = ProjectStatus.Ready;
                _context.SaveChanges();
                return;
            }

            var catalog = BuildSegmentCatalog(allSegments);
            _logger.LogInformation("分镜目录: {Count} 个片段, {Chars} 字符",
                catalog.IdMap.Count, catalog.CatalogText.Length);

            var numStrategies = StrategyCount(clampedTarget);
            var variationsPerStrategy = Math.Max(5,
                (int)Math.Ceiling(clampedTarget / (double)numStrategies));

            // Step 1: 生成策略。
            GenerationProgress = $"正在生成 {numStrategies} 个方案策略...";
            var strategyResults = await _schemeService.GenerateStrategiesAsync(
                allSegments, numStrategies, customPrompt, cancellationToken);

            // Step 2: 所有策略并行生成组合。
            GenerationProgress = $"正在并行生成 {strategyResults.Count} 个策略的变体...";
            var completed = 0;
            var tasks = strategyResults.Select(async (sr, index) =>
            {
                try
                {
                    var compositions = await _schemeService.GenerateBatchCompositionsAsync(
                        sr, catalog.CatalogText, catalog.VideoAliases,
                        variationsPerStrategy, customPrompt: customPrompt,
                        cancellationToken: cancellationToken);
                    var done = Interlocked.Increment(ref completed);
                    GenerationProgress = $"已完成 {done}/{strategyResults.Count} 个策略...";
                    return (Index: index, Strategy: sr, Compositions: compositions);
                }
                catch (Exception ex)
                {
                    _logger.LogInformation("策略「{Name}」生成失败: {Message}", sr.Name, ex.Message);
                    return (Index: index, Strategy: sr,
                        Compositions: (IReadOnlyList<AICompactComposition>)Array.Empty<AICompactComposition>());
                }
            }).ToList();

            var allResults = (await Task.WhenAll(tasks)).OrderBy(r => r.Index).ToList();

            // 创建数据模型。
            foreach (var (index, strategyResult, compositions) in allResults)
            {
                if (strategyResult is null)
                {
                    _logger.LogWarning("跳过 index={Index} 的空策略结果", index);
                    continue;
                }
                var strategy = new MixStrategy
                {
                    Name = strategyResult.Name ?? $"策略 {index + 1}",
                    Style = strategyResult.Style ?? string.Empty,
                    StrategyDescription = strategyResult.Description ?? string.Empty,
                    TargetAudience = strategyResult.TargetAudience ?? string.Empty,
                    NarrativeStructure = strategyResult.NarrativeStructure ?? string.Empty,
                    TargetDuration = strategyResult.TargetDuration,
                    ProjectId = dbProject.Id,
                };
                _context.Strategies.Add(strategy);

                // 全方位防御：compositions 本身可能 null（AI 返回异常）
                var safeCompositions = compositions ?? (IReadOnlyList<AICompactComposition>)Array.Empty<AICompactComposition>();
                for (var vi = 0; vi < safeCompositions.Count; vi++)
                {
                    var comp = safeCompositions[vi];
                    if (comp is null)
                    {
                        _logger.LogWarning("跳过 null composition: strategy={Name}, vi={Vi}",
                            strategyResult.Name, vi);
                        continue;
                    }
                    // AI 可能返回 "segments": null，反序列化会把 List<string> 置 null。
                    var segIds = comp.Segments ?? new List<string>();
                    if (segIds.Count == 0)
                    {
                        continue;
                    }

                    var matched = segIds
                        .Select(id => string.IsNullOrEmpty(id) ? null : catalog.IdMap.GetValueOrDefault(id))
                        .Where(s => s is not null)
                        .Select(s => s!)
                        .ToList();
                    var totalDuration = matched.Sum(s => s.Duration);

                    var scheme = new MixScheme
                    {
                        VariationIndex = vi + 1,
                        SchemeIndex = $"scheme_{index + 1:D3}_{vi + 1:D3}",
                        Name = string.IsNullOrEmpty(comp.Desc)
                            ? $"{strategyResult.Name ?? "策略"} #{vi + 1}"
                            : comp.Desc,
                        Style = strategyResult.Style ?? string.Empty,
                        SchemeDescription = strategyResult.Description ?? string.Empty,
                        TargetAudience = strategyResult.TargetAudience ?? string.Empty,
                        NarrativeStructure = strategyResult.NarrativeStructure ?? string.Empty,
                        EstimatedDuration = totalDuration,
                        Strategy = strategy,
                        ProjectId = dbProject.Id,
                    };
                    _context.Schemes.Add(scheme);

                    CreateSchemeSegments(segIds, scheme, catalog.IdMap);
                }

                _context.SaveChanges();
                _logger.LogInformation("策略「{Name}」: {Count} 个变体",
                    strategyResult.Name, compositions?.Count ?? 0);
            }

            dbProject.Status = ProjectStatus.Completed;
            dbProject.UpdatedAt = DateTime.Now;
            _context.SaveChanges();

            LoadSchemes(project);
            GenerationProgress = $"生成完成：{Strategies.Count} 个策略，共 {Schemes.Count} 个视频方案";
        }
        catch (OperationCanceledException)
        {
            // 用户主动取消（ESC / 取消按钮）—— 不当作错误，但必须回滚项目状态避免卡 Generating。
            ErrorMessage = null;
            RollbackGeneratingStatus(dbProject);
            _logger.LogInformation("[SchemeGen] 用户取消生成，已回滚项目状态 project={Id}", dbProject?.Id);
        }
        catch (Exception ex)
        {
            ErrorMessage = $"方案生成失败：{ExceptionTranslator.ToUserMessage(ex)}";
            // 用 LogError 的 exception 重载，stack trace 才会进日志（之前写法只塞了 ex.ToString()）。
            _logger.LogError(ex, "方案生成失败: {Message}", ex.Message);
            // P0-22/P1-72：失败后必须回滚 ProjectStatus，否则项目永久卡在「生成方案中」，
            // 用户无法再次生成也看不到任何出口（无解死锁）。
            RollbackGeneratingStatus(dbProject);
        }
        finally
        {
            IsGenerating = false;
        }
    }

    /// <summary>
    /// 把卡在 Generating 的项目状态回滚到 Ready（生成失败 / 用户取消时调用）。
    /// 防止项目永久卡在「生成方案中」导致用户无法再次生成（P0-22/P1-72）。
    /// </summary>
    private void RollbackGeneratingStatus(Project? dbProject)
    {
        if (dbProject is null || _context is null)
        {
            return;
        }
        try
        {
            if (dbProject.Status == ProjectStatus.Generating)
            {
                dbProject.Status = ProjectStatus.Ready;
                _context.SaveChanges();
                _logger.LogInformation("[SchemeGen] 已回滚项目状态 Generating→Ready project={Id}", dbProject.Id);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning("[SchemeGen] 回滚项目状态失败: {Msg}", ex.Message);
        }
    }

    /// <summary>自动计算策略数量。</summary>
    private static int StrategyCount(int targetTotal) => targetTotal switch
    {
        <= 30 => 3,
        <= 80 => 4,
        _ => 5,
    };

    /// <summary>构建分镜目录（全局唯一 ID：V{视频序号}_{分镜序号}）。</summary>
    private static SegmentCatalog BuildSegmentCatalog(IReadOnlyList<Segment> segments)
    {
        var videoNames = new List<string>();
        var videoIndexMap = new Dictionary<string, int>();
        foreach (var seg in segments)
        {
            var name = seg.Video?.Name ?? "unknown";
            if (!videoIndexMap.ContainsKey(name))
            {
                videoIndexMap[name] = videoNames.Count + 1;
                videoNames.Add(name);
            }
        }

        var aliases = string.Join("\n", videoNames.Select((name, i) => $"V{i + 1} = {name}"));

        var sortedSegments = segments
            .OrderBy(s => s.Video?.Name ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(s => s.StartTime)
            .ToList();

        var idMap = new Dictionary<string, Segment>();
        var infoMap = new Dictionary<string, SegmentInfo>();
        var catalogLines = new List<string>();
        var segCountPerVideo = new Dictionary<string, int>();

        foreach (var seg in sortedSegments)
        {
            var videoName = seg.Video?.Name ?? "unknown";
            var vIdx = videoIndexMap.GetValueOrDefault(videoName, 1);
            var segNum = segCountPerVideo.GetValueOrDefault(videoName) + 1;
            segCountPerVideo[videoName] = segNum;

            var globalId = $"V{vIdx}_{segNum:D2}";
            idMap[globalId] = seg;

            var types = seg.SemanticTypes.Select(t => t.ToLabel()).ToList();
            var dur = seg.Duration.ToString("F1", CultureInfo.InvariantCulture);
            var pos = seg.PositionType.ToLabel();

            infoMap[globalId] = new SegmentInfo(seg.Duration, types, seg.Text, pos);
            catalogLines.Add($"{globalId}|{pos}|{string.Join(",", types)}|{dur}s|{seg.Text}");
        }

        return new SegmentCatalog(
            string.Join("\n", catalogLines), aliases, idMap, infoMap);
    }

    /// <summary>用全局 ID 字典直接匹配创建方案分镜。</summary>
    private void CreateSchemeSegments(
        IReadOnlyList<string> segmentIds, MixScheme scheme, IReadOnlyDictionary<string, Segment> idMap)
    {
        var matchedCount = 0;
        for (var pos = 0; pos < segmentIds.Count; pos++)
        {
            var matched = idMap.GetValueOrDefault(segmentIds[pos]);
            if (matched is not null)
            {
                matchedCount++;
            }
            _context!.SchemeSegments.Add(new SchemeSegment
            {
                Position = pos + 1,
                Reasoning = string.Empty,
                PositionReasoning = string.Empty,
                Scheme = scheme,
                Segment = matched,
            });
        }
        _logger.LogInformation("变体「{Name}」: {Matched}/{Total} 分镜匹配",
            scheme.Name, matchedCount, segmentIds.Count);
    }

    // ---- 删除 ----

    /// <summary>
    /// 删除整个策略及其所有变体。返回策略快照（含全部 Scheme + SchemeSegment）供 P0-10 撤销，
    /// 未删成功返回 null。
    /// </summary>
    public MixStrategy? DeleteStrategy(MixStrategy strategy)
    {
        if (_context is null) return null;
        // 重查带全图（Schemes + SchemeSegments），保证快照完整可恢复。
        var tracked = _context.Strategies
            .Include(s => s.Schemes).ThenInclude(sc => sc.SchemeSegments)
            .FirstOrDefault(s => s.Id == strategy.Id);
        if (tracked is null) return null;

        var snapshot = Infrastructure.UndoStack.UndoClone.CloneStrategy(tracked);

        if (SelectedStrategy?.Id == strategy.Id)
        {
            SelectedStrategy = null;
            SelectedScheme = null;
        }
        _context.Strategies.Remove(tracked);
        SaveContext();

        var existing = Strategies.FirstOrDefault(s => s.Id == strategy.Id);
        if (existing is not null)
        {
            Strategies.Remove(existing);
        }
        return snapshot;
    }

    /// <summary>删除单个方案变体。返回方案快照（含 SchemeSegment）供 P0-10 撤销，未删成功返回 null。</summary>
    public MixScheme? DeleteScheme(MixScheme scheme)
    {
        if (_context is null) return null;
        var tracked = _context.Schemes
            .Include(s => s.SchemeSegments)
            .FirstOrDefault(s => s.Id == scheme.Id);
        if (tracked is null) return null;

        var snapshot = Infrastructure.UndoStack.UndoClone.CloneScheme(tracked);

        if (SelectedScheme?.Id == scheme.Id)
        {
            SelectedScheme = null;
        }
        _context.Schemes.Remove(tracked);
        SaveContext();
        tracked.Strategy?.Schemes.Remove(tracked);
        return snapshot;
    }

    /// <summary>P0-10：撤销删除策略 —— 把策略整图（含变体 + 方案分镜）重新插回 DB 并重查。</summary>
    public bool RestoreStrategy(MixStrategy snapshot)
    {
        if (_context is null || _loadedProject is null) return false;
        try
        {
            if (_context.Strategies.Any(s => s.Id == snapshot.Id)) return false;
            _context.Strategies.Add(Infrastructure.UndoStack.UndoClone.CloneStrategy(snapshot));
            SaveContext();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Undo] 恢复策略失败");
            return false;
        }
        LoadSchemes(_loadedProject);
        return true;
    }

    /// <summary>P0-10：撤销删除方案变体 —— 把方案（含方案分镜）重新插回 DB 并重查。</summary>
    public bool RestoreScheme(MixScheme snapshot)
    {
        if (_context is null || _loadedProject is null) return false;
        try
        {
            if (_context.Schemes.Any(s => s.Id == snapshot.Id)) return false;
            _context.Schemes.Add(Infrastructure.UndoStack.UndoClone.CloneScheme(snapshot));
            SaveContext();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Undo] 恢复方案失败");
            return false;
        }
        LoadSchemes(_loadedProject);
        return true;
    }

    // ---- 分镜编辑 ----

    /// <summary>调整方案内分镜顺序。</summary>
    public void MoveSegment(MixScheme scheme, int source, int destination)
    {
        var ordered = scheme.OrderedSegments.ToList();
        if (source < 0 || source >= ordered.Count || destination < 0 || destination > ordered.Count)
        {
            return;
        }

        var moved = ordered[source];
        ordered.RemoveAt(source);
        var adjustedDest = destination > source ? destination - 1 : destination;
        ordered.Insert(adjustedDest, moved);

        for (var i = 0; i < ordered.Count; i++)
        {
            ordered[i].Position = i + 1;
        }
        SaveContext();
    }

    /// <summary>
    /// 在方案的指定位置插入一个新分镜（position 从 1 开始；position=count+1 = 尾部追加）。
    /// 返回 false 表示方案已包含该分镜（拒绝重复插入，保留 UI 简化态）；true = 成功落库。
    /// 调用方负责刷新 UI（InsertSegment 不主动 LoadSchemes）。
    /// </summary>
    public bool InsertSegment(Segment segment, int position, MixScheme scheme)
    {
        if (_context is null)
        {
            return false;
        }

        // 重复校验：方案已含该分镜 → 拒绝
        if (SchemeContainsSegment(scheme, segment))
        {
            return false;
        }

        // 拿 db 上下文追踪的 scheme + segment（避免插入断开实体导致 EF 异常）
        var trackedScheme = _context.Schemes
            .Include(s => s.SchemeSegments)
            .FirstOrDefault(s => s.Id == scheme.Id);
        if (trackedScheme is null)
        {
            return false;
        }
        var trackedSegment = _context.Segments.FirstOrDefault(s => s.Id == segment.Id);
        if (trackedSegment is null)
        {
            return false;
        }

        var clampedPos = Math.Clamp(position, 1, trackedScheme.SchemeSegments.Count + 1);

        // 后续位置整体后移 1
        foreach (var ss in trackedScheme.SchemeSegments)
        {
            if (ss.Position >= clampedPos)
            {
                ss.Position++;
            }
        }

        _context.SchemeSegments.Add(new SchemeSegment
        {
            Position = clampedPos,
            Reasoning = string.Empty,
            PositionReasoning = string.Empty,
            Scheme = trackedScheme,
            Segment = trackedSegment,
        });

        MarkAsEdited(trackedScheme);
        SaveContext();
        return true;
    }

    /// <summary>
    /// 把指定 SchemeSegment 替换为新分镜（保留原 Position）。
    /// 返回 false 表示新分镜已在方案中（拒绝重复替换 —— 同一分镜自替换不算重复）；true = 成功。
    /// </summary>
    public bool ReplaceSegment(SchemeSegment schemeSeg, Segment newSegment, MixScheme scheme)
    {
        if (_context is null)
        {
            return false;
        }

        // 重复校验：方案中已有该分镜，且不是被替换的这一条 → 拒绝（避免出现重复）
        if (scheme.SchemeSegments.Any(ss => ss.SegmentId == newSegment.Id && ss.Id != schemeSeg.Id))
        {
            return false;
        }

        var tracked = _context.SchemeSegments.FirstOrDefault(ss => ss.Id == schemeSeg.Id);
        if (tracked is null)
        {
            return false;
        }
        var trackedSegment = _context.Segments.FirstOrDefault(s => s.Id == newSegment.Id);
        if (trackedSegment is null)
        {
            return false;
        }

        tracked.SegmentId = trackedSegment.Id;
        tracked.Segment = trackedSegment;
        // 替换后原 AI reasoning 不再适用，清空避免误导用户
        tracked.Reasoning = string.Empty;
        tracked.PositionReasoning = string.Empty;

        var trackedScheme = _context.Schemes.FirstOrDefault(s => s.Id == scheme.Id);
        if (trackedScheme is not null)
        {
            MarkAsEdited(trackedScheme);
        }
        SaveContext();
        return true;
    }

    /// <summary>
    /// 从方案中移除一个分镜。
    /// 返回 false 表示方案至少需保留 1 条分镜（拒绝删除，UI 应弹 Toast 提示）；true = 成功删除。
    /// </summary>
    public bool RemoveSegment(SchemeSegment schemeSeg, MixScheme scheme)
    {
        if (_context is null)
        {
            return false;
        }

        // 至少保留 1 条兜底（对齐 Mac removeSegment 行为）
        if (scheme.SchemeSegments.Count <= 1)
        {
            return false;
        }

        var deletedId = schemeSeg.Id;
        var tracked = _context.SchemeSegments.FirstOrDefault(ss => ss.Id == schemeSeg.Id);
        if (tracked is not null)
        {
            _context.SchemeSegments.Remove(tracked);
        }

        // 删完后 1-N 连续重排
        var remaining = scheme.OrderedSegments.Where(ss => ss.Id != deletedId).ToList();
        for (var i = 0; i < remaining.Count; i++)
        {
            remaining[i].Position = i + 1;
        }

        var trackedScheme = _context.Schemes.FirstOrDefault(s => s.Id == scheme.Id);
        if (trackedScheme is not null)
        {
            MarkAsEdited(trackedScheme);
        }
        SaveContext();
        return true;
    }

    // ---- v0.3.0 手动编辑工具方法 ----

    /// <summary>
    /// 把 AI 方案标记为「已修改」（自定义组合策略下的方案跳过 —— 用户创建的本就是手动的）。
    /// UI 层据此渲染方案名后缀「·已修改」灰字。
    /// </summary>
    private void MarkAsEdited(MixScheme scheme)
    {
        if (scheme.Strategy?.IsCustomGroup == true)
        {
            return;
        }
        if (!scheme.IsManuallyEdited)
        {
            scheme.IsManuallyEdited = true;
        }
    }

    /// <summary>判断方案是否已包含指定分镜（按 SegmentId）。用于 Insert/Replace 的重复校验。</summary>
    private static bool SchemeContainsSegment(MixScheme scheme, Segment segment) =>
        scheme.SchemeSegments.Any(ss => ss.SegmentId == segment.Id);

    // ---- v0.3.0：自定义方案创建（端到端：建占位 → AI 反推 → 刷新） ----

    /// <summary>
    /// 用户在分镜库勾选 ≥2 个分镜并确认顺序后调用。
    /// 1) 在「自定义组合」容器策略下立即建占位 scheme（默认名「自定义 #N」），UI 可立即展示
    /// 2) 异步调 InferMetadataAsync 反推 5 字段元信息，覆盖默认名
    /// 3) 反推失败 → 保留占位名 + 调用方应 Toast 提示「方案已生成（AI 反推失败，可手动改名）」
    /// 返回最终 MixScheme（含落库 + 内存集合刷新；调用方可用 SelectedStrategy/SelectedScheme 选中）。
    /// </summary>
    public async Task<MixScheme?> CreateCustomSchemeAsync(
        IReadOnlyList<Segment> orderedSegments,
        Project project,
        CancellationToken cancellationToken = default)
    {
        if (_context is null)
        {
            return null;
        }
        if (orderedSegments.Count < 2)
        {
            return null;
        }

        // 1. 找/建自定义组合容器策略（Phase 1 已经保证每个项目都有 1 条，但容灾兜底再建一次）
        var customGroup = _context.Strategies
            .Include(s => s.Schemes)
            .FirstOrDefault(s => s.ProjectId == project.Id && s.IsCustomGroup);
        if (customGroup is null)
        {
            customGroup = new MixStrategy
            {
                Name = "自定义组合",
                Style = string.Empty,
                StrategyDescription = "手动挑选分镜组合的方案",
                TargetAudience = string.Empty,
                NarrativeStructure = string.Empty,
                TargetDuration = 0,
                IsCustomGroup = true,
                ProjectId = project.Id,
            };
            _context.Strategies.Add(customGroup);
            _context.SaveChanges();
            _logger.LogInformation("[CustomScheme] 容灾兜底：补建自定义组合策略 project={ProjectId}", project.Id);
        }

        var nextIdx = (customGroup.Schemes.Count == 0 ? 0 : customGroup.Schemes.Max(s => s.VariationIndex)) + 1;
        var placeholderName = $"自定义 #{nextIdx}";
        var totalDuration = orderedSegments.Sum(s => s.Duration);

        var scheme = new MixScheme
        {
            VariationIndex = nextIdx,
            SchemeIndex = $"custom_{nextIdx:D3}",
            Name = placeholderName,
            Style = string.Empty,
            SchemeDescription = string.Empty,
            TargetAudience = string.Empty,
            NarrativeStructure = string.Empty,
            EstimatedDuration = totalDuration,
            // 自定义方案不打 IsManuallyEdited（用户创建的本就是手动的；该字段是给 AI 方案后被改的）
            IsManuallyEdited = false,
            StrategyId = customGroup.Id,
            ProjectId = project.Id,
        };
        _context.Schemes.Add(scheme);

        // 2. 按用户给的顺序建 SchemeSegment
        for (var i = 0; i < orderedSegments.Count; i++)
        {
            var trackedSeg = _context.Segments.FirstOrDefault(s => s.Id == orderedSegments[i].Id);
            if (trackedSeg is null)
            {
                _logger.LogWarning("[CustomScheme] 分镜 {Id} 不在 db 上下文，跳过", orderedSegments[i].Id);
                continue;
            }
            _context.SchemeSegments.Add(new SchemeSegment
            {
                Position = i + 1,
                Reasoning = string.Empty,
                PositionReasoning = string.Empty,
                Scheme = scheme,
                Segment = trackedSeg,
            });
        }

        _context.SaveChanges();
        _logger.LogInformation("[CustomScheme] 占位方案已落库: name={Name} segments={Count}",
            placeholderName, orderedSegments.Count);

        // 3. 异步反推元信息（失败不阻断，保留占位名）
        var meta = await _schemeService.InferMetadataAsync(orderedSegments, cancellationToken);
        if (meta is not null && !string.IsNullOrWhiteSpace(meta.Name))
        {
            scheme.Name = meta.Name!;
            scheme.NarrativeStructure = meta.NarrativeStructure ?? string.Empty;
            scheme.TargetAudience = meta.TargetAudience ?? string.Empty;
            scheme.SchemeDescription = meta.SchemeDescription ?? string.Empty;
            scheme.Style = meta.Style ?? string.Empty;
            _context.SaveChanges();
            _logger.LogInformation("[CustomScheme] 元信息反推完成: {Name}", scheme.Name);
        }

        // 4. 刷新内存集合，让 UI Strategies / CustomGroup / Schemes 全部联动
        LoadSchemes(project);
        return Schemes.FirstOrDefault(s => s.Id == scheme.Id);
    }

    /// <summary>查当前项目的全部分镜（含语义标签），供叙事结构编辑器算候选数/可选标签。</summary>
    public IReadOnlyList<Segment> LoadProjectSegments(Project project)
    {
        if (_context is null)
        {
            return Array.Empty<Segment>();
        }
        var dbProject = _context.Projects
            .Include(p => p.ProjectVideos).ThenInclude(pv => pv.Video!).ThenInclude(v => v.Segments)
            .AsSplitQuery()
            .FirstOrDefault(p => p.Id == project.Id);
        if (dbProject is null)
        {
            return Array.Empty<Segment>();
        }
        return dbProject.ProjectVideos
            .Where(pv => pv.Video is not null)
            .SelectMany(pv => pv.Video!.Segments)
            .ToList();
    }

    // ---- issue #6：自定义叙事结构创建（候选池 → AI 选片 → 程序侧二次校验 → 落库） ----

    /// <summary>
    /// 用户在叙事结构编辑器定义好段位（每段一组系统标签）+ 变体数后调用。
    /// 候选池(并集) → 每段 Top-30 送 AI 选片 → AI 自检连贯 → 程序侧二次校验(段数/无重复/在候选池) →
    /// 通过的落库为变体「变体一/二…」。全未通过则不留空壳（删除结构）。返回结构策略（或 null）。
    /// </summary>
    public async Task<MixStrategy?> CreateNarrativeStructureAsync(
        Project project,
        IReadOnlyList<NarrativeSlot> slots,
        IReadOnlyList<Segment> projectSegments,
        int variationCount,
        CancellationToken cancellationToken = default)
    {
        if (_context is null || slots.Count == 0 || variationCount <= 0)
        {
            return null;
        }

        // 1. 每段候选池 → Top-30 送审；算可行上限
        var topPerSlot = slots
            .Select(slot => (IReadOnlyList<Segment>)NarrativeCandidatePool.TopN(
                NarrativeCandidatePool.CandidatesForSlot(projectSegments, slot), 30))
            .ToList();
        var cap = NarrativeCandidatePool.FeasibleVariantCap(slots, projectSegments, variationCount);
        if (cap <= 0)
        {
            _logger.LogWarning("[NarrativeGen] 有段候选为 0，无法生成 project={ProjectId}", project.Id);
            return null;
        }

        // 2. 建结构策略（IsNarrativeTemplate），显示名由段位拼接
        var strategy = new MixStrategy
        {
            IsNarrativeTemplate = true,
            ProjectId = project.Id,
            TargetDuration = 0,
        };
        strategy.NarrativeSlots = slots.ToList(); // 写 NarrativeSlotsJson
        strategy.Name = strategy.NarrativeDisplayName;
        _context.Strategies.Add(strategy);
        _context.SaveChanges();

        // 3. AI 选片
        var comps = await _schemeService.GenerateNarrativeCompositionsAsync(
            slots, topPerSlot, cap, cancellationToken);

        // 4. 别名解析 + 二次校验 + 去重 + 落库
        var accepted = new HashSet<string>();
        var variantIdx = 0;
        foreach (var comp in comps)
        {
            var chosen = ResolveNarrativeAliases(comp.Segments, topPerSlot);
            if (chosen is null)
            {
                continue; // 别名非法
            }
            var ids = chosen.Select(s => s.Id).ToList();
            if (!NarrativeCandidatePool.ValidateComposition(slots, projectSegments, ids))
            {
                continue; // 段数/重复/不在候选池
            }
            if (!accepted.Add(string.Join(",", ids)))
            {
                continue; // 与已收变体重复
            }

            variantIdx++;
            var scheme = new MixScheme
            {
                VariationIndex = variantIdx,
                SchemeIndex = $"narrative_{variantIdx:D3}",
                Name = ChineseVariantName(variantIdx),
                Style = string.Empty,
                SchemeDescription = comp.Desc,
                TargetAudience = string.Empty,
                NarrativeStructure = strategy.NarrativeDisplayName,
                EstimatedDuration = chosen.Sum(s => s.Duration),
                IsManuallyEdited = false,
                StrategyId = strategy.Id,
                ProjectId = project.Id,
            };
            _context.Schemes.Add(scheme);
            for (var i = 0; i < chosen.Count; i++)
            {
                var tracked = _context.Segments.FirstOrDefault(s => s.Id == chosen[i].Id);
                if (tracked is null)
                {
                    continue;
                }
                _context.SchemeSegments.Add(new SchemeSegment
                {
                    Position = i + 1,
                    Reasoning = string.Empty,
                    PositionReasoning = string.Empty,
                    Scheme = scheme,
                    Segment = tracked,
                });
            }
        }

        if (variantIdx == 0)
        {
            // 全未通过 → 不留空壳
            _context.Strategies.Remove(strategy);
            _context.SaveChanges();
            _logger.LogWarning("[NarrativeGen] 无连贯变体通过，已移除空结构 project={ProjectId}", project.Id);
            LoadSchemes(project);
            return null;
        }

        _context.SaveChanges();
        _logger.LogInformation("[NarrativeGen] 结构 '{Name}' 生成 {N} 个变体",
            strategy.NarrativeDisplayName, variantIdx);
        LoadSchemes(project);
        return Strategies.FirstOrDefault(s => s.Id == strategy.Id);
    }

    /// <summary>把 AI 返回的别名序列（S{段}_{候选序}）按 topPerSlot 同序解析回分镜；任一非法→null。</summary>
    private static List<Segment>? ResolveNarrativeAliases(
        IReadOnlyList<string> aliases, IReadOnlyList<IReadOnlyList<Segment>> topPerSlot)
    {
        var result = new List<Segment>(aliases.Count);
        foreach (var alias in aliases)
        {
            if (!TryParseNarrativeAlias(alias, out var si, out var ci)
                || si < 0 || si >= topPerSlot.Count
                || ci < 0 || ci >= topPerSlot[si].Count)
            {
                return null;
            }
            result.Add(topPerSlot[si][ci]);
        }
        return result;
    }

    /// <summary>解析别名 S{段}_{序}（1-based）→ 0-based 下标。</summary>
    private static bool TryParseNarrativeAlias(string alias, out int slotIdx, out int candIdx)
    {
        slotIdx = candIdx = -1;
        if (string.IsNullOrEmpty(alias) || alias[0] != 'S')
        {
            return false;
        }
        var us = alias.IndexOf('_');
        if (us < 2 || us >= alias.Length - 1)
        {
            return false;
        }
        if (!int.TryParse(alias.AsSpan(1, us - 1), out var s) || !int.TryParse(alias.AsSpan(us + 1), out var c))
        {
            return false;
        }
        slotIdx = s - 1;
        candIdx = c - 1;
        return true;
    }

    /// <summary>变体名「变体一/二/三…」（支持 1-99）。</summary>
    private static string ChineseVariantName(int n)
    {
        string[] d = { "零", "一", "二", "三", "四", "五", "六", "七", "八", "九" };
        string num;
        if (n <= 0)
        {
            num = n.ToString();
        }
        else if (n < 10)
        {
            num = d[n];
        }
        else if (n < 20)
        {
            num = "十" + (n % 10 == 0 ? "" : d[n % 10]);
        }
        else if (n < 100)
        {
            num = d[n / 10] + "十" + (n % 10 == 0 ? "" : d[n % 10]);
        }
        else
        {
            num = n.ToString();
        }
        return "变体" + num;
    }

    private void SaveContext()
    {
        try
        {
            _context?.SaveChanges();
        }
        catch (Exception ex)
        {
            _logger.LogError("方案数据保存失败: {Message}", ex.Message);
        }
    }

    public void Dispose()
    {
        _context?.Dispose();
        _context = null;
    }
}
