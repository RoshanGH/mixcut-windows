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

    /// <summary>加载项目的所有策略和方案。</summary>
    public void LoadSchemes(Project project)
    {
        _context?.Dispose();
        _context = _dbFactory.CreateDbContext();

        var projectId = project.Id;
        // 不能 ThenInclude(seg => seg.Video) —— EF Core fix-up 自动填充反向导航，重复 Include 会
        // 触发 NavigationBaseIncludeIgnored 警告并升级为 InvalidOperationException。
        // 通过单独预加载 Videos 让跟踪上下文自动 fix-up segment.Video。
        var strategies = _context.Strategies
            .Include(s => s.Schemes).ThenInclude(sc => sc.SchemeSegments).ThenInclude(ss => ss.Segment!)
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

        try
        {
            // 注意：Segments.Video 是反向导航，EF Core 会自动 fix-up，不能再 ThenInclude
            // （会触发 NavigationBaseIncludeIgnored 警告并升级为 InvalidOperationException）。
            var dbProject = _context.Projects
                .Include(p => p.ProjectVideos).ThenInclude(pv => pv.Video!).ThenInclude(v => v.Segments)
                .First(p => p.Id == project.Id);

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
        catch (Exception ex)
        {
            ErrorMessage = $"方案生成失败: {ex.Message}";
            // 用 LogError 的 exception 重载，stack trace 才会进日志（之前写法只塞了 ex.ToString()）。
            _logger.LogError(ex, "方案生成失败: {Message}", ex.Message);
        }
        finally
        {
            IsGenerating = false;
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

    /// <summary>删除整个策略及其所有变体。</summary>
    public void DeleteStrategy(MixStrategy strategy)
    {
        if (SelectedStrategy?.Id == strategy.Id)
        {
            SelectedStrategy = null;
            SelectedScheme = null;
        }
        if (_context is not null)
        {
            var tracked = _context.Strategies.FirstOrDefault(s => s.Id == strategy.Id);
            if (tracked is not null)
            {
                _context.Strategies.Remove(tracked);
                SaveContext();
            }
        }
        var existing = Strategies.FirstOrDefault(s => s.Id == strategy.Id);
        if (existing is not null)
        {
            Strategies.Remove(existing);
        }
    }

    /// <summary>删除单个方案变体。</summary>
    public void DeleteScheme(MixScheme scheme)
    {
        if (SelectedScheme?.Id == scheme.Id)
        {
            SelectedScheme = null;
        }
        if (_context is not null)
        {
            var tracked = _context.Schemes.FirstOrDefault(s => s.Id == scheme.Id);
            if (tracked is not null)
            {
                _context.Schemes.Remove(tracked);
                SaveContext();
                tracked.Strategy?.Schemes.Remove(tracked);
            }
        }
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

    /// <summary>从方案中移除一个分镜。</summary>
    public void RemoveSegment(SchemeSegment schemeSeg, MixScheme scheme)
    {
        if (_context is null)
        {
            return;
        }

        var deletedId = schemeSeg.Id;
        var tracked = _context.SchemeSegments.FirstOrDefault(ss => ss.Id == schemeSeg.Id);
        if (tracked is not null)
        {
            _context.SchemeSegments.Remove(tracked);
        }

        var remaining = scheme.OrderedSegments.Where(ss => ss.Id != deletedId).ToList();
        for (var i = 0; i < remaining.Count; i++)
        {
            remaining[i].Position = i + 1;
        }
        SaveContext();
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
