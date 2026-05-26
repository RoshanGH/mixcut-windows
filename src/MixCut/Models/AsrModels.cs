using System.Text.Json.Serialization;

namespace MixCut.Models;

/// <summary>ASR 字级时间戳。对应 macOS 版 ASRWord（值类型）。</summary>
public sealed record AsrWord(
    [property: JsonPropertyName("word")] string Word,
    [property: JsonPropertyName("start")] double Start,
    [property: JsonPropertyName("end")] double End);

/// <summary>ASR 原生句子（Whisper segment 级）。对应 macOS 版 ASRSentence。</summary>
public sealed record AsrSentence(
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("start")] double Start,
    [property: JsonPropertyName("end")] double End);
