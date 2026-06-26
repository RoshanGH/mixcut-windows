using System.ComponentModel.DataAnnotations.Schema;

namespace MixCut.Models;

/// <summary>
/// 某分镜的一个配音变体（= 分镜 × 改写版 × 音色）。一个 <see cref="Segment"/> 挂多个 = 变体池。
/// 对应 macOS 版 SwiftData 的 SegmentDub @Model。
/// </summary>
public class SegmentDub
{
    public Guid Id { get; set; } = Guid.NewGuid();

    // ---- 导航属性 ----

    public Guid? SegmentId { get; set; }
    public Segment? Segment { get; set; }

    // ---- 配音标识 ----

    /// <summary>克隆音色 id（= 所属视频的 <see cref="Video.ClonedVoiceId"/>）。</summary>
    public string VoiceId { get; set; } = string.Empty;

    /// <summary>音色提供方（当前恒 "qwen"）。</summary>
    public string VoiceProvider { get; set; } = "qwen";

    /// <summary>第几个改写版（0..K-1，→ 展示字母 A/B/C…）。</summary>
    public int TextVariantIndex { get; set; }

    /// <summary>改写后的台词（同 <see cref="TextVariantIndex"/> 的各音色行共享同文本）。</summary>
    public string RewrittenText { get; set; } = string.Empty;

    // ---- 生成产物 ----

    /// <summary>按需生成的对齐后配音音频路径；初始 null 表示未生成。</summary>
    public string? AudioFilePath { get; set; }

    /// <summary>atempo 对齐后的实际音频时长（秒，展示用）。</summary>
    public double AudioDuration { get; set; }

    /// <summary>atempo 变速系数（&gt;1 加速，&lt;1 减速，1.0 不变速）。</summary>
    public double AtempoFactor { get; set; } = 1.0;

    /// <summary>末尾定格补帧数。铁律：恒为 0（绝不靠定格延长画面，见 TRD §1/§决策5）。保留字段避免迁移。</summary>
    public int FreezePadFrames { get; set; }

    /// <summary>变速后音频比画面短时，末尾补的静音秒数。</summary>
    public double TrailingSilence { get; set; }

    // ---- 失效追踪（生成那一刻快照；边界/文本变了即「过期」）----

    public int GeneratedForStartFrame { get; set; } = -1;
    public int GeneratedForEndFrame { get; set; } = -1;
    public string GeneratedForTextHash { get; set; } = string.Empty;

    /// <summary><see cref="Status"/> 的底层字符串存储（数据库可读）。</summary>
    public string StatusRaw { get; set; } = nameof(SegmentDubStatus.Pending);

    // ---- 计算属性 ----

    /// <summary>生成状态。底层存 <see cref="StatusRaw"/> 字符串。</summary>
    [NotMapped]
    public SegmentDubStatus Status
    {
        get => Enum.TryParse<SegmentDubStatus>(StatusRaw, ignoreCase: true, out var s)
            ? s
            : SegmentDubStatus.Pending;
        set => StatusRaw = value.ToString();
    }
}
