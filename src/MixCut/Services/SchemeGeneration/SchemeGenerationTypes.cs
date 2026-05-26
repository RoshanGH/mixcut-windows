using System.Text.Json;
using System.Text.Json.Serialization;
using MixCut.Models;

namespace MixCut.Services.SchemeGeneration;

/// <summary>方案策略（Step 1 输出）。对应 macOS 版 SchemeStrategy。</summary>
/// <remarks>
/// 字段访问器对 AI 返回的 null 做容错（System.Text.Json 默认会把 "x": null 反序列化为 null，
/// 覆盖掉构造期默认值；setter 检查后保留默认值）。
/// 数值类型用 nullable 接收（避免 "target_duration": null 抛 JsonException）。
/// </remarks>
public sealed class SchemeStrategy
{
    private string _name = "未命名方案";
    private string _style = "通用";
    private string _description = string.Empty;
    private string _targetAudience = "通用受众";
    private string _narrativeStructure = string.Empty;

    [JsonPropertyName("name")]
    public string Name
    {
        get => _name;
        set => _name = string.IsNullOrEmpty(value) ? "未命名方案" : value;
    }

    [JsonPropertyName("style")]
    public string Style
    {
        get => _style;
        set => _style = string.IsNullOrEmpty(value) ? "通用" : value;
    }

    [JsonPropertyName("description")]
    public string Description
    {
        get => _description;
        set => _description = value ?? string.Empty;
    }

    [JsonPropertyName("target_audience")]
    public string TargetAudience
    {
        get => _targetAudience;
        set => _targetAudience = string.IsNullOrEmpty(value) ? "通用受众" : value;
    }

    [JsonPropertyName("narrative_structure")]
    public string NarrativeStructure
    {
        get => _narrativeStructure;
        set => _narrativeStructure = value ?? string.Empty;
    }

    [JsonPropertyName("target_duration")]
    public double? TargetDurationRaw { get; set; }

    /// <summary>访问时给默认 60（AI 漏字段或返回 null 不影响）。</summary>
    [JsonIgnore]
    public double TargetDuration => TargetDurationRaw ?? 60;

    [JsonPropertyName("estimated_quality")]
    public double? EstimatedQualityRaw { get; set; }

    [JsonIgnore]
    public double EstimatedQuality => EstimatedQualityRaw ?? 7.0;
}

/// <summary>
/// 策略响应（Step 1）。对应 macOS 版 SchemeStrategyResponse。
/// 自适应吃下 3 种 AI 输出格式：
/// <list type="bullet">
/// <item><c>{"strategies": [{...}, ...]}</c> 对象包装（推荐，与 <c>response_format=json_object</c> 兼容）</item>
/// <item><c>[{...}, ...]</c> 裸数组（固执模型）</item>
/// <item><c>{...}</c> 单个对象（只输出 1 个）</item>
/// </list>
/// 数组内单条字段坏不影响整体（lossless 解码）。
/// </summary>
[JsonConverter(typeof(SchemeStrategyResponseConverter))]
public sealed class SchemeStrategyResponse
{
    public IReadOnlyList<SchemeStrategy> Strategies { get; }

    public SchemeStrategyResponse(IReadOnlyList<SchemeStrategy> strategies)
    {
        Strategies = strategies;
    }
}

/// <summary>SchemeStrategyResponse 的自适应解析器。</summary>
internal sealed class SchemeStrategyResponseConverter : JsonConverter<SchemeStrategyResponse>
{
    public override SchemeStrategyResponse Read(
        ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        var root = doc.RootElement;

        // 格式 A: 对象包装 {"strategies": [...]}
        if (root.ValueKind == JsonValueKind.Object)
        {
            if (root.TryGetProperty("strategies", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                var list = DecodeLossless<SchemeStrategy>(arr, options);
                if (list.Count > 0)
                {
                    return new SchemeStrategyResponse(list);
                }
            }
            // 退化：当作单个策略对象
            var single = JsonSerializer.Deserialize<SchemeStrategy>(root.GetRawText(), options);
            if (single is not null)
            {
                return new SchemeStrategyResponse(new[] { single });
            }
        }

        // 格式 B: 裸数组 [...]
        if (root.ValueKind == JsonValueKind.Array)
        {
            var list = DecodeLossless<SchemeStrategy>(root, options);
            return new SchemeStrategyResponse(list);
        }

        return new SchemeStrategyResponse(Array.Empty<SchemeStrategy>());
    }

    public override void Write(
        Utf8JsonWriter writer, SchemeStrategyResponse value, JsonSerializerOptions options)
    {
        // 一般不需要序列化此类型，但提供个对称实现以防万一。
        writer.WriteStartObject();
        writer.WritePropertyName("strategies");
        JsonSerializer.Serialize(writer, value.Strategies, options);
        writer.WriteEndObject();
    }

    /// <summary>
    /// lossless 数组解码：逐项 try-deserialize，单条失败只跳过这一项，不影响整体。
    /// 对齐 macOS 版 decodeLossless。
    /// </summary>
    internal static List<T> DecodeLossless<T>(JsonElement arr, JsonSerializerOptions options)
    {
        var list = new List<T>();
        foreach (var item in arr.EnumerateArray())
        {
            try
            {
                var v = item.Deserialize<T>(options);
                if (v is not null)
                {
                    list.Add(v);
                }
            }
            catch (JsonException)
            {
                // 单条坏掉不影响其他条
            }
        }
        return list;
    }
}

/// <summary>精简 AI 输出：单个组合（只有 segment ID 序列）。对应 macOS 版 AICompactComposition。</summary>
public sealed class AICompactComposition
{
    [JsonPropertyName("segments")]
    public List<string> Segments { get; set; } = new();

    [JsonPropertyName("desc")]
    public string Desc { get; set; } = string.Empty;
}

/// <summary>
/// 组合响应（Step 2）。对应 macOS 版 CompositionResponse。
/// 跟 <see cref="SchemeStrategyResponse"/> 同样的 3 格式自适应 + lossless 解码。
/// </summary>
[JsonConverter(typeof(CompositionResponseConverter))]
public sealed class CompositionResponse
{
    public IReadOnlyList<AICompactComposition> Compositions { get; }

    public CompositionResponse(IReadOnlyList<AICompactComposition> compositions)
    {
        Compositions = compositions;
    }
}

internal sealed class CompositionResponseConverter : JsonConverter<CompositionResponse>
{
    public override CompositionResponse Read(
        ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        var root = doc.RootElement;

        if (root.ValueKind == JsonValueKind.Object)
        {
            if (root.TryGetProperty("compositions", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                var list = SchemeStrategyResponseConverter
                    .DecodeLossless<AICompactComposition>(arr, options);
                if (list.Count > 0)
                {
                    return new CompositionResponse(list);
                }
            }
            // 退化：单个 composition
            var single = JsonSerializer.Deserialize<AICompactComposition>(root.GetRawText(), options);
            if (single is not null && single.Segments.Count > 0)
            {
                return new CompositionResponse(new[] { single });
            }
        }

        if (root.ValueKind == JsonValueKind.Array)
        {
            var list = SchemeStrategyResponseConverter
                .DecodeLossless<AICompactComposition>(root, options);
            return new CompositionResponse(list);
        }

        return new CompositionResponse(Array.Empty<AICompactComposition>());
    }

    public override void Write(
        Utf8JsonWriter writer, CompositionResponse value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WritePropertyName("compositions");
        JsonSerializer.Serialize(writer, value.Compositions, options);
        writer.WriteEndObject();
    }
}

/// <summary>分镜简要信息。对应 macOS 版 SegmentInfo。</summary>
public sealed record SegmentInfo(
    double Duration,
    IReadOnlyList<string> Types,
    string Text,
    string Position);

/// <summary>分镜目录（表格格式 + ID 映射）。对应 macOS 版 SegmentCatalog。</summary>
public sealed record SegmentCatalog(
    string CatalogText,
    string VideoAliases,
    IReadOnlyDictionary<string, Segment> IdMap,
    IReadOnlyDictionary<string, SegmentInfo> InfoMap);

/// <summary>素材分析报告。对应 macOS 版 SegmentAnalysis。</summary>
public sealed record SegmentAnalysis(
    int TotalSegments,
    double TotalDuration,
    IReadOnlyDictionary<string, int> TypeDistribution,
    int HighQualityCount,
    double AverageQuality,
    bool HasHook,
    bool HasCta,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> Suggestions);
