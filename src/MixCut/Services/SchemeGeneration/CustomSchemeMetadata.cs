using System.Text.Json.Serialization;

namespace MixCut.Services.SchemeGeneration;

/// <summary>
/// AI 反推自定义方案元信息的返回结构。对应 Mac CustomSchemeMetadata。
/// 全 nullable —— AI 偶发只返回部分字段也不丢，由 ViewModel 用 ?? 兜底。
/// </summary>
public sealed class CustomSchemeMetadata
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("narrativeStructure")]
    public string? NarrativeStructure { get; set; }

    [JsonPropertyName("targetAudience")]
    public string? TargetAudience { get; set; }

    [JsonPropertyName("schemeDescription")]
    public string? SchemeDescription { get; set; }

    [JsonPropertyName("style")]
    public string? Style { get; set; }
}
