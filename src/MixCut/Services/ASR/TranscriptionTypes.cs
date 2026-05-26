using MixCut.Models;

namespace MixCut.Services.ASR;

/// <summary>转录句子。对应 macOS 版 TranscriptionSentence。</summary>
public sealed record TranscriptionSentence(
    string Text,
    double StartTime,
    double EndTime,
    IReadOnlyList<AsrWord> Words)
{
    public double Duration => EndTime - StartTime;
}

/// <summary>ASR 识别结果。对应 macOS 版 TranscriptionResult。</summary>
public sealed class TranscriptionResult
{
    /// <summary>完整转录文本。</summary>
    public string Text { get; init; } = string.Empty;

    /// <summary>字级时间戳。</summary>
    public IReadOnlyList<AsrWord> Words { get; init; } = Array.Empty<AsrWord>();

    /// <summary>Whisper 原生句子（segment 级）。</summary>
    public IReadOnlyList<AsrSentence> RawSentences { get; init; } = Array.Empty<AsrSentence>();

    /// <summary>检测到的语言。</summary>
    public string Language { get; init; } = "zh";

    /// <summary>音频时长。</summary>
    public double Duration { get; init; }

    public static TranscriptionResult Empty(string language = "zh") => new() { Language = language };

    /// <summary>
    /// 句子列表：优先用 Whisper 原生 segments，降级从 words 聚合。
    /// 关键防御（对齐 Mac v0.2.4）：whisper.cpp 在「短视频 + 一气呵成口播」下经常把整段压成 1 个长 segment，
    /// 导致下游 AI 分镜、UI 显示、台词编辑全部失效。
    /// 因此即使有 rawSentences，遇到「单 segment + >8s + >30 字」也要走 textFallback 重切。
    /// </summary>
    public IReadOnlyList<TranscriptionSentence> Sentences
    {
        get
        {
            var isSingleLongSegment = RawSentences.Count == 1
                && (RawSentences[0].End - RawSentences[0].Start) > 8.0
                && RawSentences[0].Text.Length > 30;

            if (RawSentences.Count > 0 && !isSingleLongSegment)
            {
                // 正常路径：用 whisper 原生切分（多 segment 走这里，文字 100% 完整）
                return RawSentences.Select(s =>
                {
                    var sentenceWords = Words.Where(w => w.Start < s.End && w.End > s.Start).ToList();
                    return new TranscriptionSentence(s.Text, s.Start, s.End, sentenceWords);
                }).ToList();
            }

            // 单 segment 整段压成一行 → 按标点重切（文字完整，只重切粒度）
            if (RawSentences.Count > 0)
            {
                return BuildSentencesFromTextFallback();
            }

            return BuildSentencesFromWords();
        }
    }

    /// <summary>
    /// 终极兜底：rawSentences 太粗 + 没有可用 words 时，用标点切分 + 按字符比例均分时间。
    /// 用例：whisper.cpp 没启用 word timestamps，只有 1 个长 segment。
    /// 对齐 Mac buildSentencesFromTextFallback。
    /// </summary>
    private IReadOnlyList<TranscriptionSentence> BuildSentencesFromTextFallback()
    {
        if (RawSentences.Count == 0) return Array.Empty<TranscriptionSentence>();
        var totalStart = RawSentences[0].Start;
        var totalEnd = RawSentences[^1].End;
        var fullText = string.Concat(RawSentences.Select(r => r.Text));
        var totalDuration = Math.Max(0.1, totalEnd - totalStart);

        // 按标点切（句号/问号/感叹号/逗号/分号），单段至少 8 字
        var separators = new HashSet<char> { '。', '！', '？', '，', '、', ';', '；', '.', '!', '?', ',' };
        var pieces = new List<string>();
        var current = new System.Text.StringBuilder();
        foreach (var ch in fullText)
        {
            current.Append(ch);
            if (separators.Contains(ch) && current.Length >= 8)
            {
                pieces.Add(current.ToString());
                current.Clear();
            }
        }
        var remainder = current.ToString().Trim();
        if (remainder.Length > 0) pieces.Add(remainder);

        // 没标点的极端情况：按 15 字硬切
        if (pieces.Count <= 1)
        {
            pieces.Clear();
            var buf = new System.Text.StringBuilder();
            foreach (var ch in fullText)
            {
                buf.Append(ch);
                if (buf.Length >= 15)
                {
                    pieces.Add(buf.ToString());
                    buf.Clear();
                }
            }
            if (buf.Length > 0) pieces.Add(buf.ToString());
        }

        if (pieces.Count == 0)
        {
            return new[] { new TranscriptionSentence(RawSentences[0].Text, totalStart, totalEnd, Array.Empty<AsrWord>()) };
        }

        // 按字数比例分配时间戳
        var totalChars = pieces.Sum(p => p.Length);
        var t = totalStart;
        var result = new List<TranscriptionSentence>(pieces.Count);
        foreach (var piece in pieces)
        {
            var ratio = totalChars > 0 ? (double)piece.Length / totalChars : 0;
            var segDur = totalDuration * ratio;
            var segEnd = Math.Min(t + segDur, totalEnd);
            result.Add(new TranscriptionSentence(piece, t, segEnd, Array.Empty<AsrWord>()));
            t = segEnd;
        }
        return result;
    }

    /// <summary>降级方案：从 words 聚合句子。</summary>
    private IReadOnlyList<TranscriptionSentence> BuildSentencesFromWords()
    {
        if (Words.Count == 0)
        {
            return Array.Empty<TranscriptionSentence>();
        }

        var sentences = new List<TranscriptionSentence>();
        var currentWords = new List<AsrWord>();
        const double pauseThreshold = 0.5;

        for (var index = 0; index < Words.Count; index++)
        {
            var word = Words[index];
            currentWords.Add(word);

            var isSentenceEnd =
                word.Word.EndsWith('。') || word.Word.EndsWith('！') || word.Word.EndsWith('？') ||
                word.Word.EndsWith('.') || word.Word.EndsWith('!') || word.Word.EndsWith('?');

            var hasLongPause = index + 1 < Words.Count
                && Words[index + 1].Start - word.End >= pauseThreshold;

            var currentText = string.Concat(currentWords.Select(w => w.Word));
            var isLongWithComma = currentText.Length > 30 &&
                (word.Word.EndsWith('，') || word.Word.EndsWith('、') || word.Word.EndsWith(','));

            if ((isSentenceEnd || hasLongPause || isLongWithComma) && currentWords.Count > 0)
            {
                sentences.Add(MakeSentence(currentWords));
                currentWords = new List<AsrWord>();
            }
        }

        if (currentWords.Count > 0)
        {
            sentences.Add(MakeSentence(currentWords));
        }

        return sentences;
    }

    private static TranscriptionSentence MakeSentence(List<AsrWord> words)
    {
        var text = string.Concat(words.Select(w => w.Word));
        return new TranscriptionSentence(text, words[0].Start, words[^1].End, words.ToList());
    }
}
