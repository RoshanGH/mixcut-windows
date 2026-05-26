using System.Text.Json;
using System.Text.Json.Serialization;

namespace MixCut.Services.AI;

/// <summary>
/// 分镜数据质量。对应 macOS 版 AnnotatedSegment.DataQuality。
/// 所有字段宽容化：AI 漏字段或返回 null 都不会破整条 segment 的反序列化。
/// </summary>
public sealed class SegmentDataQuality
{
    [JsonPropertyName("score")]
    public double? ScoreRaw { get; set; }

    [JsonIgnore]
    public double Score => ScoreRaw ?? 7.0;

    private string _reasoning = string.Empty;
    [JsonPropertyName("reasoning")]
    public string Reasoning
    {
        get => _reasoning;
        set => _reasoning = value ?? string.Empty;
    }
}

/// <summary>
/// AI 标注后的分镜结果。对应 macOS 版 AnnotatedSegment。
/// 所有可选字段宽容化（null → 默认值）；types 优先数组、降级单字符串、再降级「过渡」。
/// </summary>
public sealed class AnnotatedSegment
{
    private string _id = string.Empty;
    [JsonPropertyName("id")]
    public string Id
    {
        get => _id;
        set => _id = value ?? string.Empty;
    }

    [JsonPropertyName("start_time")]
    public double StartTime { get; set; }

    [JsonPropertyName("end_time")]
    public double EndTime { get; set; }

    [JsonPropertyName("duration")]
    public double? DurationRaw { get; set; }

    /// <summary>访问时若 raw 缺失/为 0，回退用 end-start 计算。</summary>
    [JsonIgnore]
    public double Duration
    {
        get
        {
            if (DurationRaw is { } d && d > 0) return d;
            var fallback = EndTime - StartTime;
            return fallback > 0 ? fallback : 0;
        }
    }

    private string _text = string.Empty;
    [JsonPropertyName("text")]
    public string Text
    {
        get => _text;
        set => _text = value ?? string.Empty;
    }

    /// <summary>语义类型数组（新格式）。</summary>
    [JsonPropertyName("types")]
    public List<string>? Types { get; set; }

    /// <summary>语义类型单值（兼容旧格式）。</summary>
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    private string _position = "中间";
    [JsonPropertyName("position")]
    public string Position
    {
        get => _position;
        set => _position = string.IsNullOrEmpty(value) ? "中间" : value;
    }

    private SegmentDataQuality _dataQuality = new();
    [JsonPropertyName("data_quality")]
    public SegmentDataQuality DataQuality
    {
        get => _dataQuality;
        set => _dataQuality = value ?? new SegmentDataQuality();
    }

    private List<string> _keywords = new();
    [JsonPropertyName("keywords")]
    public List<string> Keywords
    {
        get => _keywords;
        set => _keywords = value ?? new List<string>();
    }

    /// <summary>有效语义类型：优先 types 数组，降级 type 单值，再降级「过渡」。</summary>
    [JsonIgnore]
    public IReadOnlyList<string> EffectiveTypes =>
        Types is { Count: > 0 } ? Types
        : !string.IsNullOrEmpty(Type) ? new List<string> { Type! }
        : new List<string> { "过渡" };
}

/// <summary>
/// AI 分析输出。对应 macOS 版 AISegmentationResult。
/// segments 数组用 lossless 解码：单条 segment 字段错只丢这一条，前面已解的留下。
/// 对齐 macOS commit 3406d61。
/// </summary>
[JsonConverter(typeof(AISegmentationResultConverter))]
public sealed class AISegmentationResult
{
    public string VideoId { get; set; } = string.Empty;
    public double TotalDuration { get; set; }
    public int TotalSegments { get; set; }
    public List<AnnotatedSegment> Segments { get; set; } = new();
}

/// <summary>
/// AISegmentationResult 的自定义解码器：
/// segments 字段逐项 try-deserialize，单条失败不破坏其余条目。
/// </summary>
internal sealed class AISegmentationResultConverter : JsonConverter<AISegmentationResult>
{
    public override AISegmentationResult Read(
        ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        var root = doc.RootElement;
        var result = new AISegmentationResult();

        if (root.ValueKind != JsonValueKind.Object)
        {
            return result;
        }

        if (root.TryGetProperty("video_id", out var vid) && vid.ValueKind == JsonValueKind.String)
        {
            result.VideoId = vid.GetString() ?? string.Empty;
        }
        if (root.TryGetProperty("total_duration", out var td) && TryGetDouble(td, out var tdVal))
        {
            result.TotalDuration = tdVal;
        }
        if (root.TryGetProperty("total_segments", out var ts) && ts.ValueKind == JsonValueKind.Number
            && ts.TryGetInt32(out var tsVal))
        {
            result.TotalSegments = tsVal;
        }

        // lossless 解码 segments
        if (root.TryGetProperty("segments", out var segs) && segs.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in segs.EnumerateArray())
            {
                try
                {
                    var seg = item.Deserialize<AnnotatedSegment>(options);
                    if (seg is not null && !string.IsNullOrEmpty(seg.Id))
                    {
                        result.Segments.Add(seg);
                    }
                }
                catch (JsonException)
                {
                    // 单条坏掉不影响其他条
                }
            }
        }

        return result;
    }

    public override void Write(
        Utf8JsonWriter writer, AISegmentationResult value, JsonSerializerOptions options)
    {
        // 一般用不到序列化此类型。
        writer.WriteStartObject();
        writer.WriteString("video_id", value.VideoId);
        writer.WriteNumber("total_duration", value.TotalDuration);
        writer.WriteNumber("total_segments", value.TotalSegments);
        writer.WritePropertyName("segments");
        JsonSerializer.Serialize(writer, value.Segments, options);
        writer.WriteEndObject();
    }

    private static bool TryGetDouble(JsonElement el, out double v)
    {
        if (el.ValueKind == JsonValueKind.Number && el.TryGetDouble(out v))
        {
            return true;
        }
        v = 0;
        return false;
    }
}
