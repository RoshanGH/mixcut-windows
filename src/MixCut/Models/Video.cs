using System.ComponentModel.DataAnnotations.Schema;
using MixCut.Utilities;

namespace MixCut.Models;

/// <summary>视频。对应 macOS 版 SwiftData 的 Video @Model。视频按内容哈希全局共享。</summary>
public class Video
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string LocalPath { get; set; } = string.Empty;
    public double Duration { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public double Fps { get; set; }
    public VideoStatus Status { get; set; } = VideoStatus.Imported;
    public string? ErrorMessage { get; set; }

    /// <summary>ASR 完整转录文本。</summary>
    public string? Transcript { get; set; }

    /// <summary>ASR 字级时间戳（JSON 存储，经 <see cref="AsrWords"/> 读写）。</summary>
    public string? AsrWordsJson { get; set; }

    /// <summary>Whisper 原生句子段（JSON 存储，经 <see cref="AsrSentences"/> 读写）。</summary>
    public string? AsrSentencesJson { get; set; }

    /// <summary>文件内容哈希（SHA-256），用于全局去重。</summary>
    public string? ContentHash { get; set; }

    /// <summary>缩略图路径。</summary>
    public string? ThumbnailPath { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.Now;

    // ---- 导航属性 ----

    public List<ProjectVideo> ProjectVideos { get; set; } = new();
    public List<Segment> Segments { get; set; } = new();

    // ---- 计算属性 ----

    /// <summary>ASR 字级时间戳。</summary>
    [NotMapped]
    public IReadOnlyList<AsrWord> AsrWords
    {
        get => JsonColumn.Read<AsrWord>(AsrWordsJson);
        set => AsrWordsJson = JsonColumn.Write(value);
    }

    /// <summary>Whisper 原生句子段。</summary>
    [NotMapped]
    public IReadOnlyList<AsrSentence> AsrSentences
    {
        get => JsonColumn.Read<AsrSentence>(AsrSentencesJson);
        set => AsrSentencesJson = JsonColumn.Write(value);
    }

    /// <summary>关联的项目列表。</summary>
    [NotMapped]
    public IEnumerable<Project> Projects =>
        ProjectVideos.Where(pv => pv.Project != null).Select(pv => pv.Project!);

    /// <summary>被多少个项目引用。</summary>
    [NotMapped]
    public int ReferenceCount => ProjectVideos.Count;

    /// <summary>分辨率描述，如 1920×1080。</summary>
    [NotMapped]
    public string Resolution => $"{Width}×{Height}";
}
