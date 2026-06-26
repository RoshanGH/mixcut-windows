using System.ComponentModel.DataAnnotations.Schema;
using MixCut.Utilities;

namespace MixCut.Models;

/// <summary>分镜。对应 macOS 版 SwiftData 的 Segment @Model。分镜随视频全局共享。</summary>
public class Segment
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>分镜序号，seg_001 格式。</summary>
    public string SegmentIndex { get; set; } = string.Empty;

    public double StartTime { get; set; }
    public double EndTime { get; set; }

    /// <summary>
    /// 起始帧号。issue #7：帧是离散最小单位，作为边界<b>真值</b>；StartTime = StartFrame/Fps 为派生缓存。
    /// 0 表示旧数据尚未回填帧号（启动迁移会按视频 fps 回填）。
    /// </summary>
    public int StartFrame { get; set; }

    /// <summary>结束帧号。见 <see cref="StartFrame"/>。</summary>
    public int EndFrame { get; set; }

    /// <summary>
    /// 该分镜的帧率（冗余存储，便于不加载 Video 导航属性即可做 帧/秒/时间码 换算）。
    /// 0 表示未知（回退到 <see cref="Video"/>.Fps）。
    /// </summary>
    public double Fps { get; set; }

    /// <summary>台词文本。</summary>
    public string Text { get; set; } = string.Empty;

    /// <summary>语义类型列表（中文标签 JSON 数组，经 <see cref="SemanticTypes"/> 读写，可多个）。</summary>
    public string? SemanticTypesJson { get; set; }

    public PositionType PositionType { get; set; } = PositionType.Middle;

    public double Confidence { get; set; } = 0.8;
    public double QualityScore { get; set; } = 8.0;

    /// <summary>画面描述。</summary>
    public string? VisualDescription { get; set; }

    /// <summary>缩略图路径。</summary>
    public string? ThumbnailPath { get; set; }

    /// <summary>关键词列表（JSON 数组，经 <see cref="Keywords"/> 读写）。</summary>
    public string? KeywordsJson { get; set; }

    /// <summary>数据质量详情说明。</summary>
    public string? QualityReasoning { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.Now;

    // ---- 配音（v0.5.0 分镜级 AI 配音）----

    /// <summary>🔒 保留原声：明星出镜等不可替换镜头，不参与改写/配音/换字幕。</summary>
    public bool IsVoiceLocked { get; set; }

    /// <summary>是否对字幕区域做遮挡（false=直接烧录；true 时看 <see cref="MaskStyleRaw"/>）。</summary>
    public bool HasHardSubtitle { get; set; }

    /// <summary>遮挡底层样式（<see cref="MaskStyle"/> 的字符串："Blur" / "Solid"）。</summary>
    public string MaskStyleRaw { get; set; } = nameof(Models.MaskStyle.Blur);

    /// <summary>遮挡框归一化坐标 JSON（经 <see cref="MaskRect"/> 读写）。</summary>
    public string? MaskRectJson { get; set; }

    // ---- 导航属性 ----

    public Guid? VideoId { get; set; }
    public Video? Video { get; set; }

    public List<SchemeSegment> SchemeSegments { get; set; } = new();

    /// <summary>本分镜的配音变体池（= 改写版 × 音色）。</summary>
    public List<SegmentDub> SegmentDubs { get; set; } = new();

    // ---- 计算属性 ----

    /// <summary>语义类型（支持多个）。存储为中文标签 JSON 数组。</summary>
    [NotMapped]
    public IReadOnlyList<SemanticType> SemanticTypes
    {
        get => JsonColumn.Read<string>(SemanticTypesJson)
            .Select(SemanticTypeExtensions.FromLabel)
            .ToList();
        set => SemanticTypesJson = JsonColumn.Write(value.Select(t => t.ToLabel()));
    }

    /// <summary>主语义类型（第一个，兼容旧代码）。</summary>
    [NotMapped]
    public SemanticType PrimarySemanticType =>
        SemanticTypes.Count > 0 ? SemanticTypes[0] : SemanticType.Transition;

    /// <summary>关键词。</summary>
    [NotMapped]
    public IReadOnlyList<string> Keywords
    {
        get => JsonColumn.Read<string>(KeywordsJson);
        set => KeywordsJson = JsonColumn.Write(value);
    }

    // ---- 配音计算属性 ----

    /// <summary>遮挡底层样式（读写 <see cref="MaskStyleRaw"/>）。</summary>
    [NotMapped]
    public MaskStyle MaskStyle
    {
        get => SubtitleTreatmentExtensions.ParseMaskStyle(MaskStyleRaw);
        set => MaskStyleRaw = value.ToString();
    }

    /// <summary>字幕处理方式（UI 三选一，映射底层 <see cref="HasHardSubtitle"/>+<see cref="MaskStyleRaw"/>）。</summary>
    [NotMapped]
    public SubtitleTreatment SubtitleTreatment
    {
        get => SubtitleTreatmentExtensions.FromFields(HasHardSubtitle, MaskStyleRaw);
        set
        {
            var (has, raw) = value.ToFields();
            HasHardSubtitle = has;
            MaskStyleRaw = raw;
        }
    }

    /// <summary>遮挡框归一化坐标（读写 <see cref="MaskRectJson"/>）。未设置时返回默认框。</summary>
    [NotMapped]
    public SubtitleMaskRect MaskRect
    {
        get => string.IsNullOrWhiteSpace(MaskRectJson)
            ? SubtitleMaskRect.Default
            : System.Text.Json.JsonSerializer.Deserialize<SubtitleMaskRect>(MaskRectJson!);
        set => MaskRectJson = System.Text.Json.JsonSerializer.Serialize(value);
    }

    /// <summary>
    /// 导出/组合用的有效配音变体：仅取已生成音频者，并按 <see cref="SegmentDub.TextVariantIndex"/> 去重
    /// （同一改写版若有多个音色，优先保留本视频「克隆原声」那条）。clone-only 下每版唯一。
    /// 升序返回，供 _A/_B… 命名与笛卡尔积组合使用。对应 macOS Segment.effectiveDubVariants。
    /// </summary>
    [NotMapped]
    public IReadOnlyList<SegmentDub> EffectiveDubVariants
    {
        get
        {
            var cloned = Video?.ClonedVoiceId;
            var byIndex = new Dictionary<int, SegmentDub>();
            foreach (var d in SegmentDubs)
            {
                if (d.AudioFilePath is null) continue;
                if (byIndex.TryGetValue(d.TextVariantIndex, out var existing))
                {
                    if (d.VoiceId == cloned && existing.VoiceId != cloned)
                    {
                        byIndex[d.TextVariantIndex] = d;
                    }
                }
                else
                {
                    byIndex[d.TextVariantIndex] = d;
                }
            }
            return byIndex.Values.OrderBy(d => d.TextVariantIndex).ToList();
        }
    }

    /// <summary>时长（保证非负）。</summary>
    [NotMapped]
    public double Duration => EffectiveFps > 0
        ? FrameTime.FrameToSeconds(DurationFrames, EffectiveFps)
        : Math.Max(0, EndTime - StartTime);

    /// <summary>片段帧数。StartFrame 包含、EndFrame 不包含。</summary>
    [NotMapped]
    public int DurationFrames => Math.Max(0, EndFrame - StartFrame);

    /// <summary>预览定格与边界核对使用的最后一帧。</summary>
    [NotMapped]
    public int LastFrame => FrameTime.LastIncludedFrame(StartFrame, EndFrame);

    /// <summary>有效帧率：优先用自身 <see cref="Fps"/>，回退到所属视频的 fps。0 表示未知。</summary>
    [NotMapped]
    public double EffectiveFps => Fps > 0 ? Fps : (Video?.Fps ?? 0);

    /// <summary>起点剪映式时间码（时:分:秒:帧，按 fps 进位）。fps 未知时退化为 分:秒。</summary>
    [NotMapped]
    public string StartTimecode => EffectiveFps > 0
        ? FrameTime.ToTimecode(StartFrame, EffectiveFps)
        : FrameTime.ToTimecodeFromSeconds(StartTime, 0);

    /// <summary>终点剪映式时间码。</summary>
    [NotMapped]
    public string EndTimecode => EffectiveFps > 0
        ? FrameTime.ToTimecode(EndFrame, EffectiveFps)
        : FrameTime.ToTimecodeFromSeconds(EndTime, 0);

    /// <summary>当前片段实际包含的最后一帧时间码。</summary>
    [NotMapped]
    public string LastFrameTimecode => FrameTime.ToTimecode(LastFrame, EffectiveFps);

    /// <summary>
    /// 以「秒」设置边界，<b>量化到最近帧</b>并同步帧号与派生秒（issue #7：消除「落在两帧之间」歧义）。
    /// fps 来源优先级：显式传入 &gt; 自身 <see cref="Fps"/> &gt; 所属视频。fps 未知时退化为按秒（保证非负）。
    /// </summary>
    public void SetBoundsSeconds(double startSec, double endSec, double fps = 0)
    {
        var f = fps > 0 ? fps : EffectiveFps;
        if (f > 0)
        {
            Fps = f;
            StartFrame = FrameTime.SecondsToFrame(startSec, f);
            EndFrame = FrameTime.SecondsToFrame(endSec, f);
            StartTime = FrameTime.FrameToSeconds(StartFrame, f);
            EndTime = FrameTime.FrameToSeconds(EndFrame, f);
        }
        else
        {
            // fps 未知：退化为按秒存储，不破坏旧行为。
            StartTime = Math.Max(0, startSec);
            EndTime = Math.Max(0, endSec);
        }
    }

    /// <summary>
    /// 直接以帧设置边界。StartFrame 为 inclusive，EndFrame 为 exclusive，至少保留一帧。
    /// 秒字段只是兼容数据库和旧绑定的派生缓存。
    /// </summary>
    public void SetBoundsFrames(int startFrame, int endFrame, double fps = 0)
    {
        var f = fps > 0 ? fps : EffectiveFps;
        if (f <= 0)
        {
            return;
        }

        Fps = f;
        StartFrame = Math.Max(0, startFrame);
        EndFrame = Math.Max(StartFrame + 1, endFrame);
        StartTime = FrameTime.FrameToSeconds(StartFrame, f);
        EndTime = FrameTime.FrameToSeconds(EndFrame, f);
    }
}
