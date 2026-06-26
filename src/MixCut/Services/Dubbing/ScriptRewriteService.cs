using System.Text;
using Microsoft.Extensions.Logging;
using MixCut.Services.AI;

namespace MixCut.Services.Dubbing;

/// <summary>
/// AI 整片台词改写服务。对应 mac ScriptRewriteService + RewritePromptBuilder + RewriteResultMapper。
/// 加载模板 → 构建 prompt → 调当前 AIProvider（复用三层 JSON 解析）→ 按输入分镜保序映射结果。
/// </summary>
public sealed class ScriptRewriteService
{
    /// <summary>差异化改写风格池（最多 5 套变体；不足时按取模复用）。对齐 mac rewriteStyles。</summary>
    public static readonly IReadOnlyList<string> RewriteStyles = new[]
    {
        "口语化、活泼带感叹词，适合年轻受众的信息流广告",
        "干练直接、强调卖点与行动号召，适合效果类信息流广告",
        "场景化讲述、先痛点后给解决方案，代入感强的信息流广告",
        "突出信任与专业、强调品质与口碑背书的信息流广告",
        "限时促销、紧迫感强、强促单转化的信息流广告",
    };

    private readonly PromptLoader _promptLoader;
    private readonly AIProviderManager _aiManager;
    private readonly ILogger<ScriptRewriteService> _logger;

    public ScriptRewriteService(PromptLoader promptLoader, AIProviderManager aiManager,
        ILogger<ScriptRewriteService> logger)
    {
        _promptLoader = promptLoader;
        _aiManager = aiManager;
        _logger = logger;
    }

    /// <summary>风格池按变体序号取模。</summary>
    public static string StyleForVariant(int variantIndex) =>
        RewriteStyles[((variantIndex % RewriteStyles.Count) + RewriteStyles.Count) % RewriteStyles.Count];

    /// <summary>
    /// 改写一组「可重配」分镜的台词（明星保留原声分镜由调用方剔除后传入）。
    /// 返回与 inputs 同序的结果；AI 漏返回的回退原台词并标记 IsFallback。
    /// </summary>
    public async Task<IReadOnlyList<RewrittenSegment>> RewriteAsync(
        IReadOnlyList<RewriteSegmentInput> inputs, string style, CancellationToken ct = default)
    {
        if (inputs.Count == 0) return Array.Empty<RewrittenSegment>();

        var template = _promptLoader.LoadPrompt("script_rewrite_prompt") ?? FallbackTemplate;
        var prompt = BuildPrompt(template, inputs, style);

        var dto = await _aiManager.CurrentProvider().GenerateJsonAsync<RewriteResultDto>(prompt, ct);
        return Map(dto, inputs);
    }

    // ---- prompt 构建（对应 mac RewritePromptBuilder）----

    private static string BuildPrompt(string template, IReadOnlyList<RewriteSegmentInput> inputs, string style)
    {
        var sb = new StringBuilder();
        foreach (var input in inputs)
        {
            var origCount = input.OriginalText.Length;
            var budget = CharBudget.ForOriginalLength(origCount);
            var kw = input.Keywords.Count == 0 ? "无" : string.Join("、", input.Keywords);
            sb.Append("- segmentId=").Append(input.SegmentId)
              .Append(" | 原字数=").Append(origCount)
              .Append("字（目标≈").Append(origCount).Append("字，允许 ")
              .Append(budget.MinChars).Append('~').Append(budget.MaxChars)
              .Append("字，与原台词字数相当即可，配音会自动加速对齐到画面）| 时长=")
              .Append(input.DurationSeconds.ToString("F2")).Append("s | 必须保留关键事实=")
              .Append(kw).Append(" | 原台词：").Append(input.OriginalText).Append('\n');
        }

        return template
            .Replace("{{STYLE}}", style)
            .Replace("{{SEGMENTS}}", sb.ToString().TrimEnd('\n'));
    }

    // ---- 结果映射（对应 mac RewriteResultMapper）----

    private static IReadOnlyList<RewrittenSegment> Map(
        RewriteResultDto dto, IReadOnlyList<RewriteSegmentInput> inputs)
    {
        var byId = new Dictionary<string, string>();
        foreach (var item in dto.Segments)
        {
            if (!byId.ContainsKey(item.SegmentId)) byId[item.SegmentId] = item.RewrittenText;
        }

        return inputs.Select(input =>
        {
            var budget = CharBudget.ForOriginalLength(input.OriginalText.Length);
            var trimmed = (byId.TryGetValue(input.SegmentId, out var raw) ? raw : null)?.Trim() ?? string.Empty;

            if (trimmed.Length > 0)
            {
                var within = trimmed.Length >= budget.MinChars && trimmed.Length <= budget.MaxChars;
                return new RewrittenSegment(input.SegmentId, trimmed, IsFallback: false, WithinBudget: within);
            }
            // 漏返回或空白 → 回退原台词
            return new RewrittenSegment(input.SegmentId, input.OriginalText, IsFallback: true, WithinBudget: false);
        }).ToList();
    }

    /// <summary>模板文件缺失时的兜底，避免整功能因资源问题挂掉。</summary>
    private const string FallbackTemplate = """
        你是广告文案改写专家。把下列分镜原台词逐条改写为全新说法。
        硬性要求：①每条新台词字数尽量等于原字数，必须落在「允许区间」内；②语义绝对不能改变——价格指向（现价/恢复价）、时间、因果、数量、比较方向都不得反转或篡改，只能换表达；③保留关键事实；④禁用「啊呀哇呢吧嘛啦咯哦噢」等语气词/句末语气助词（会被 TTS 单独念出来很突兀），改用陈述句表达。
        风格：{{STYLE}}
        分镜：
        {{SEGMENTS}}
        严格输出 JSON：{"segments":[{"segmentId":"id","rewrittenText":"新台词"}]}
        """;
}
