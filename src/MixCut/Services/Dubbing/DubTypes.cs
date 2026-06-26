namespace MixCut.Services.Dubbing;

/// <summary>配音流程的人话错误（消息直接给用户看，已翻译为中文，不含 stack trace）。</summary>
public sealed class DubException : Exception
{
    public DubException(string message) : base(message) { }
    public DubException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>人声分离产物：纯人声 + 纯背景音乐（两条 wav 的本地路径）。对应 mac SeparatedStems。</summary>
public sealed record SeparatedStems(string VocalsPath, string BgmPath);

/// <summary>克隆 TTS 合成结果：wav 路径 + 原始时长（秒）。对应 mac TTSResult。</summary>
public sealed record TtsResult(string WavPath, double RawDuration);

/// <summary>
/// 把一段配音音频塞进固定画面时长的对齐方案（导出时照此拼）。对应 mac AlignmentPlan。
/// 铁律：<see cref="FreezePadFrames"/> 恒为 0（绝不靠定格延长画面）。
/// </summary>
public sealed record AlignmentPlan(double AtempoFactor, int FreezePadFrames, double TrailingSilence);

/// <summary>改写输入：原台词 + 时长 + 关键词。对应 mac RewriteSegmentInput。</summary>
public sealed record RewriteSegmentInput(
    string SegmentId, string OriginalText, double DurationSeconds, IReadOnlyList<string> Keywords);

/// <summary>改写结果：与输入同序；AI 漏返回的回退原台词并标记 <see cref="IsFallback"/>。对应 mac RewrittenSegment。</summary>
public sealed record RewrittenSegment(
    string SegmentId, string RewrittenText, bool IsFallback, bool WithinBudget);

/// <summary>
/// 改写台词的字数预算。由原台词字数算：目标=原字数，允许区间 [原字数, 1.15×原字数]。
/// 下限锚定原字数（<b>不允许比原台词更短</b>）——克隆嗓音常更快，台词偏短会致配音短于画面、末尾留空挡。
/// 对应 mac CharBudget.forOriginalLength。
/// </summary>
public readonly record struct CharBudget(int Target, int MinChars, int MaxChars)
{
    public static CharBudget ForOriginalLength(int count)
    {
        if (count <= 0) return new CharBudget(0, 0, 0);
        var maxC = Math.Max(count, (int)Math.Round(count * 1.15));
        return new CharBudget(count, count, maxC);
    }
}

/// <summary>AI 改写返回 DTO：<c>{"segments":[{"segmentId","rewrittenText"}]}</c>。</summary>
public sealed class RewriteResultDto
{
    [System.Text.Json.Serialization.JsonPropertyName("segments")]
    public List<RewriteItemDto> Segments { get; set; } = new();
}

public sealed class RewriteItemDto
{
    [System.Text.Json.Serialization.JsonPropertyName("segmentId")]
    public string SegmentId { get; set; } = string.Empty;

    [System.Text.Json.Serialization.JsonPropertyName("rewrittenText")]
    public string RewrittenText { get; set; } = string.Empty;
}
