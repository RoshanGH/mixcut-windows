using System.Globalization;
using Microsoft.Extensions.Logging;
using MixCut.Models;
using MixCut.Services.AI;

namespace MixCut.Services.SchemeGeneration;

/// <summary>
/// 方案生成服务。对应 macOS 版 SchemeGenerationService。
/// 两步架构：Step 1 生成策略 → Step 2 批量生成分镜组合。注册为单例服务。
/// </summary>
public sealed class SchemeGenerationService
{
    private readonly AIProviderManager _providerManager;
    private readonly PromptLoader _promptLoader;
    private readonly ILogger<SchemeGenerationService> _logger;

    public SchemeGenerationService(
        AIProviderManager providerManager, PromptLoader promptLoader,
        ILogger<SchemeGenerationService> logger)
    {
        _providerManager = providerManager;
        _promptLoader = promptLoader;
        _logger = logger;
    }

    // ---- 素材分析 ----

    /// <summary>分析分镜素材，生成报告。</summary>
    public SegmentAnalysis AnalyzeSegments(IReadOnlyList<Segment> segments)
    {
        var totalDuration = segments.Sum(s => s.Duration);

        var typeDistribution = new Dictionary<string, int>();
        foreach (var seg in segments)
        {
            foreach (var t in seg.SemanticTypes)
            {
                var label = t.ToLabel();
                typeDistribution[label] = typeDistribution.GetValueOrDefault(label) + 1;
            }
        }

        var highQuality = segments.Where(s => s.QualityScore >= 8.5).ToList();
        var avgQuality = segments.Count == 0 ? 0 : segments.Sum(s => s.QualityScore) / segments.Count;
        var hasHook = segments.Any(s => s.SemanticTypes.Contains(SemanticType.Hook));
        var hasCta = segments.Any(s => s.SemanticTypes.Contains(SemanticType.CallToAction));

        var warnings = new List<string>();
        var suggestions = new List<string>();
        if (!hasHook)
        {
            warnings.Add("缺少开场类型片段（噱头引入），可能影响开场效果");
        }
        if (!hasCta)
        {
            warnings.Add("缺少行动号召类型片段，可能影响转化效果");
        }
        if (highQuality.Count < 3)
        {
            warnings.Add("高质量片段（评分>=8.5）数量不足，可能影响方案质量");
        }
        if (segments.Count < 5)
        {
            suggestions.Add("建议导入更多视频以获得更丰富的素材");
        }

        return new SegmentAnalysis(
            segments.Count, totalDuration, typeDistribution,
            highQuality.Count, avgQuality, hasHook, hasCta, warnings, suggestions);
    }

    // ---- Step 1: 生成策略 ----

    /// <summary>生成 N 个差异化的混剪方案策略。</summary>
    /// <remarks>
    /// 单次调用 —— SchemeStrategyResponse 自适应吃下 3 种 AI 输出格式
    /// （对象包装 / 裸数组 / 单对象），数组内单条坏掉不影响整体（lossless）。
    /// 移除了之前「先试 batch 失败再裸数组重发」的反模式：那个做法用同一个 prompt
    /// 重发请求，不仅烧 token，结果还跟第一次完全一样，纯无效重试。
    /// 对齐 macOS commit 3406d61。
    /// </remarks>
    public async Task<IReadOnlyList<SchemeStrategy>> GenerateStrategiesAsync(
        IReadOnlyList<Segment> segments,
        int count = 3,
        string? customPrompt = null,
        CancellationToken cancellationToken = default)
    {
        var adStyles = _promptLoader.LoadPrompt("ad_styles") ?? string.Empty;
        var principles = _promptLoader.LoadPrompt("recombination_principles") ?? string.Empty;
        var segmentSummary = BuildSegmentSummary(segments);
        var customSection = string.IsNullOrEmpty(customPrompt)
            ? string.Empty
            : $"## 用户自定义要求（必须严格遵守）\n{customPrompt}";

        var prompt = $$"""
        # 任务：生成 {{count}} 个差异化的视频混剪方案策略

        你是一个专业的视频混剪策划师。请基于以下素材信息，设计 {{count}} 个差异化的方案策略。

        ## 可用素材摘要
        {{segmentSummary}}

        ## 广告风格参考
        {{adStyles}}

        ## 混剪原则参考
        {{principles}}

        {{customSection}}

        ## 输出格式（JSON 对象）
        必须输出一个顶层对象，包含 strategies 数组，恰好 {{count}} 个元素：
        {"strategies": [
          {
            "name": "方案名",
            "style": "广告风格",
            "description": "一句话描述核心策略",
            "target_audience": "目标受众",
            "narrative_structure": "开场 → 中段 → 结尾",
            "target_duration": 30,
            "estimated_quality": 8.5
          }
        ]}

        确保 {{count}} 个策略之间有明显差异（不同风格、不同受众、不同时长）。
        直接输出 JSON 对象，不要包含其他内容。
        """;

        var provider = _providerManager.CurrentProvider();

        // 单次调用 + 自适应类型 —— 不再重发请求做格式 fallback。
        var response = await provider.GenerateJsonAsync<SchemeStrategyResponse>(prompt, cancellationToken);
        if (response.Strategies.Count == 0)
        {
            throw AIProviderException.JsonParsingFailed(
                "AI 返回内容未能解析出任何策略。可能原因：模型输出被 max_tokens 截断，或返回格式异常。" +
                "请重试，或更换更大的模型。");
        }
        _logger.LogInformation("策略生成: {Count} 个策略", response.Strategies.Count);
        return response.Strategies;
    }

    // ---- Step 2: 批量组合生成 ----

    /// <summary>基于策略批量生成分镜组合。</summary>
    public async Task<IReadOnlyList<AICompactComposition>> GenerateBatchCompositionsAsync(
        SchemeStrategy strategy,
        string catalogText,
        string videoAliases,
        int variationCount = 20,
        int batchSize = 10,
        string? customPrompt = null,
        CancellationToken cancellationToken = default)
    {
        var allCompositions = new List<AICompactComposition>();
        var remaining = variationCount;
        var provider = _providerManager.CurrentProvider();

        while (remaining > 0)
        {
            var batchCount = Math.Min(remaining, batchSize);
            var fingerprints = BuildExistingFingerprints(allCompositions);
            var customSection = string.IsNullOrEmpty(customPrompt)
                ? string.Empty
                : $"## 用户自定义要求（必须严格遵守）\n{customPrompt}\n";
            var minDuration = Math.Max(30, strategy.TargetDuration - 20);
            var maxDuration = strategy.TargetDuration + 20;

            var prompt = $$"""
            你是信息流广告混剪专家。基于策略和素材，生成 **{{batchCount}} 个**不同的分镜排列组合。

            ## 策略
            风格: {{strategy.Style}} | 受众: {{strategy.TargetAudience}} | 叙事: {{strategy.NarrativeStructure}} | 时长: {{F1(strategy.TargetDuration)}}s

            ## 视频别名
            {{videoAliases}}

            ## 可用片段（ID|位置|类型|时长|台词）
            {{catalogText}}

            ## 已有变体（不要重复相同的 ID 顺序）
            {{fingerprints}}

            {{customSection}}
            ## 规则
            1. position="开头"的片段只放视频开头，"结尾"只放结尾，"中间"可灵活排列
            2. 相邻片段台词衔接自然，符合信息流广告叙事
            3. 每个组合之间必须有差异（换开场/换中间段/换结尾/调整顺序）
            4. 同一组合内不重复同一片段，不同组合间可重复使用相同片段
            5. 时长 {{F0(minDuration)}}-{{F0(maxDuration)}}s
            6. 必须生成恰好 {{batchCount}} 个组合，不能少于这个数量

            ## 输出格式（JSON，只需片段 ID 序列）
            {"compositions":[{"segments":["V1_01","V2_03","V1_05"],"desc":"一句话描述"},{"segments":["V1_02","V2_01","V1_04"],"desc":"另一个描述"}]}

            直接输出 JSON，不要其他文字。必须包含 {{batchCount}} 个 composition。
            """;

            // 单次调用 + CompositionResponse 自适应 3 格式 + lossless 解码。
            // 解析失败不再重发请求（之前嵌套 try-catch 烧 2-3 倍 token 但结果一样）。
            IReadOnlyList<AICompactComposition> batchResult;
            try
            {
                var response = await provider.GenerateJsonAsync<CompositionResponse>(prompt, cancellationToken);
                batchResult = response.Compositions;
            }
            catch (Exception ex)
            {
                _logger.LogWarning("批量生成解析失败，本批跳过: {Message}", ex.Message);
                batchResult = Array.Empty<AICompactComposition>();
            }

            var valid = batchResult.Where(c => c.Segments.Count > 0).ToList();
            allCompositions.AddRange(valid);
            remaining -= valid.Count;

            if (valid.Count == 0)
            {
                _logger.LogInformation("AI 返回变体为空，提前结束");
                break;
            }

            _logger.LogInformation("批次完成: +{Count} 个变体，累计 {Total}/{Target}",
                valid.Count, allCompositions.Count, variationCount);
        }

        return allCompositions;
    }

    // ---- 辅助方法 ----

    private string BuildSegmentSummary(IReadOnlyList<Segment> segments)
    {
        var analysis = AnalyzeSegments(segments);
        var summary =
            $"总片段数: {analysis.TotalSegments}\n" +
            $"总时长: {F1(analysis.TotalDuration)} 秒\n" +
            $"平均质量: {F1(analysis.AverageQuality)}\n" +
            $"高质量片段: {analysis.HighQualityCount} 个\n\n类型分布:\n";

        foreach (var (type, count) in analysis.TypeDistribution.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            summary += $"- {type}: {count} 个\n";
        }
        return summary;
    }

    private static string BuildExistingFingerprints(IReadOnlyList<AICompactComposition> compositions)
    {
        if (compositions.Count == 0)
        {
            return "无（这是第一批）";
        }
        return $"已有{compositions.Count}个变体:\n" +
            string.Join("\n", compositions.Select((c, i) =>
                $"{i + 1}: {string.Join("→", c.Segments)}"));
    }

    private static string F0(double v) => v.ToString("F0", CultureInfo.InvariantCulture);
    private static string F1(double v) => v.ToString("F1", CultureInfo.InvariantCulture);
}
