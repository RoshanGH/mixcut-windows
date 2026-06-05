using System.Globalization;
using System.Text;
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

    /// <summary>
    /// 自定义叙事结构生成（issue #6 §六）：固定段序，每段从【该段自己的候选】里挑 1 条，
    /// 生成 N 个变体，AI 自检台词连贯只输出通过的。段位别名 <c>S{段}_{候选序}</c>（如 S1_3），
    /// 调用方按 <paramref name="candidatesPerSlot"/> 同序解析别名→Segment，再做程序侧二次校验
    /// （<see cref="NarrativeCandidatePool.ValidateComposition"/>，不能只信 AI）。
    /// </summary>
    public async Task<IReadOnlyList<AICompactComposition>> GenerateNarrativeCompositionsAsync(
        IReadOnlyList<NarrativeSlot> slots,
        IReadOnlyList<IReadOnlyList<Segment>> candidatesPerSlot,
        int variationCount,
        CancellationToken cancellationToken = default)
    {
        if (slots.Count == 0 || candidatesPerSlot.Count != slots.Count || variationCount <= 0)
        {
            return Array.Empty<AICompactComposition>();
        }

        var catalog = BuildNarrativeCatalog(slots, candidatesPerSlot);
        var prompt = $$"""
        你是信息流广告混剪专家。下面是一个固定的「叙事结构」(若干段,顺序不可变),
        每段给了一组候选分镜。任务:为每段从它自己的候选里挑 1 条,组成成片;
        生成 {{variationCount}} 个不同变体;对每个变体做台词连贯性自检,只输出连贯的。

        ## 叙事结构(段顺序=成片顺序,固定;你只决定每段选哪条)
        {{catalog}}
        ## 硬性规则
        1. 每段必须且只能从【该段自己的候选】里选 1 条;不能借别段候选、不能漏段、不能改段序。
        2. 同一变体内不得重复使用同一条分镜。
        3. 生成 {{variationCount}} 个变体,彼此要有差异,不得有两个完全相同的别名组合。
        4. 连贯性自检——只输出通过的,不通过的直接丢弃:
           a. 相邻段台词语义衔接,整体像一条完整口播,不跳脱;
           b. ⚠️ 台词逻辑顺序不能排反:若两条台词本身有先后(同句上/下半句、"首先…其次…"),
              后半不能排在前半之前;
           c. 整体符合信息流节奏(开头抓人→中间种草→结尾促单)。

        ## 输出(JSON,只含通过自检的变体;segments 用候选别名,每段 1 个,顺序同段序)
        {"compositions":[{"segments":["S1_3","S2_1","S3_2"],"desc":"痛点→产品→促单,台词顺承"}]}
        只输出 JSON,不要其他文字。
        """;

        try
        {
            var provider = _providerManager.CurrentProvider();
            var response = await provider.GenerateJsonAsync<CompositionResponse>(prompt, cancellationToken);
            // 只留段数匹配的；进一步的「别名合法 + 无重复 + 在候选池」由调用方用
            // NarrativeCandidatePool.ValidateComposition 程序侧二次校验。
            var sized = response.Compositions.Where(c => c.Segments.Count == slots.Count).ToList();
            _logger.LogInformation("[NarrativeGen] AI 返回 {Total} 变体, 段数匹配 {Sized}",
                response.Compositions.Count, sized.Count);
            return sized;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("[NarrativeGen] 生成/解析失败: {Message}", ex.Message);
            return Array.Empty<AICompactComposition>();
        }
    }

    /// <summary>按「段 → 候选列表」组织目录，每条候选带别名 S{段}_{序}、时长、台词（issue §六）。</summary>
    private static string BuildNarrativeCatalog(
        IReadOnlyList<NarrativeSlot> slots, IReadOnlyList<IReadOnlyList<Segment>> candidatesPerSlot)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < slots.Count; i++)
        {
            var tagLabels = string.Join("/", slots[i].Tags.Select(t => t.ToLabel()));
            sb.AppendLine($"段{i + 1} [标签:{tagLabels}] 候选:");
            var cands = candidatesPerSlot[i];
            for (var j = 0; j < cands.Count; j++)
            {
                var seg = cands[j];
                var text = seg.Text.Length > 60 ? seg.Text[..60] : seg.Text;
                sb.AppendLine($"  S{i + 1}_{j + 1} | {seg.Duration.ToString("F1", CultureInfo.InvariantCulture)}s | 台词:{text}");
            }
        }
        return sb.ToString();
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

    // ---- v0.3.0：自定义方案 AI 反推 ----

    /// <summary>
    /// 根据用户手动选择的分镜反推方案元信息。失败返回 null（让调用方落默认元信息 + Toast 提示），
    /// 不抛错阻断 —— 用户已经手动挑了分镜，方案落库不能因为 AI 失败丢失。
    /// 内部 30 秒超时兜底防 hang。
    /// </summary>
    public async Task<CustomSchemeMetadata?> InferMetadataAsync(
        IReadOnlyList<Segment> orderedSegments,
        CancellationToken cancellationToken = default)
    {
        if (orderedSegments.Count == 0)
        {
            return null;
        }

        try
        {
            var template = _promptLoader.LoadPrompt("custom_scheme_inference")
                ?? throw new InvalidOperationException("custom_scheme_inference prompt 未加载");

            var segmentsData = orderedSegments.Select((seg, idx) => new
            {
                position = idx + 1,
                semanticTypes = seg.SemanticTypes.Select(t => t.ToLabel()).ToArray(),
                text = seg.Text ?? string.Empty,
                duration = Math.Round(seg.Duration, 1),
            }).ToArray();
            var segmentsJson = System.Text.Json.JsonSerializer.Serialize(segmentsData);
            var prompt = template.Replace("{{SEGMENTS_JSON}}", segmentsJson);

            // 30 秒超时兜底（防止 AI 提供商网络 hang），与外部 cancellationToken 联动
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(30));

            var provider = _providerManager.CurrentProvider();
            var meta = await provider.GenerateJsonAsync<CustomSchemeMetadata>(prompt, cts.Token);
            _logger.LogInformation("[CustomSchemeInference] 反推完成: name={Name} style={Style}",
                meta?.Name, meta?.Style);
            return meta;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[CustomSchemeInference] 反推失败，调用方将落默认元信息");
            return null;
        }
    }
}
