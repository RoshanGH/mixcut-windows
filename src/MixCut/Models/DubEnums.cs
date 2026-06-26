namespace MixCut.Models;

/// <summary>
/// 单分镜配音（<see cref="SegmentDub"/>）的状态。对应 macOS DubEnums.SegmentDubStatus。
/// </summary>
public enum SegmentDubStatus
{
    /// <summary>待生成音频。</summary>
    Pending,
    /// <summary>已生成音频。</summary>
    Generated,
    /// <summary>生成失败（可单独重生成）。</summary>
    Failed,
}

/// <summary>
/// 硬字幕遮挡底层样式（存 <see cref="Segment.MaskStyleRaw"/>）。对应 macOS DubEnums.MaskStyle。
/// 注意：v0.5.0 已砍掉「半透明 dim」，旧数据归入 <see cref="Blur"/>。
/// </summary>
public enum MaskStyle
{
    /// <summary>高斯模糊。</summary>
    Blur,
    /// <summary>纯色遮挡。</summary>
    Solid,
}

/// <summary>
/// 分镜「字幕处理」方式（UI 三选一，不直接入库）。对应 macOS DubEnums.SubtitleTreatment。
/// 映射到底层 (<see cref="Segment.HasHardSubtitle"/>, <see cref="Segment.MaskStyleRaw"/>) 两字段，避免数据库迁移。
/// </summary>
public enum SubtitleTreatment
{
    /// <summary>直接烧录：不处理背景，直接把新字幕烧上去。</summary>
    Direct,
    /// <summary>模糊虚化：把字幕区域糊掉再烧（不管原片有没有旧字幕都可用）。</summary>
    Blur,
    /// <summary>纯色遮挡：深色条盖住该区域再烧。</summary>
    Solid,
}

/// <summary>
/// 字幕遮挡框的归一化坐标（0..1，相对输出画面）。对应 macOS SubtitleMaskRect。
/// 存 <see cref="Segment.MaskRectJson"/>，导出时按输出像素换算。
/// </summary>
public readonly record struct SubtitleMaskRect(double X, double Y, double Width, double Height)
{
    /// <summary>默认遮挡框：底部偏下、横向居中的一条（与剪映字幕位置接近）。</summary>
    public static SubtitleMaskRect Default => new(0.1, 0.78, 0.8, 0.12);
}

/// <summary>
/// <see cref="MaskStyle"/> / <see cref="SubtitleTreatment"/> 与底层字段互转。
/// </summary>
public static class SubtitleTreatmentExtensions
{
    public static MaskStyle ParseMaskStyle(string? raw) =>
        string.Equals(raw, nameof(MaskStyle.Solid), StringComparison.OrdinalIgnoreCase)
            ? MaskStyle.Solid
            : MaskStyle.Blur;

    /// <summary>是否需要遮挡框（blur/solid 需要定位区域；direct 不需要）。</summary>
    public static bool NeedsMask(this SubtitleTreatment t) => t != SubtitleTreatment.Direct;

    /// <summary>从底层字段还原。旧「半透明 dim」统一归入「模糊虚化」。</summary>
    public static SubtitleTreatment FromFields(bool hasHardSubtitle, string? maskStyleRaw)
    {
        if (!hasHardSubtitle) return SubtitleTreatment.Direct;
        return ParseMaskStyle(maskStyleRaw) == MaskStyle.Solid
            ? SubtitleTreatment.Solid
            : SubtitleTreatment.Blur;
    }

    /// <summary>映射回底层 (hasHardSubtitle, maskStyleRaw)。</summary>
    public static (bool HasHardSubtitle, string MaskStyleRaw) ToFields(this SubtitleTreatment t) => t switch
    {
        SubtitleTreatment.Direct => (false, nameof(MaskStyle.Blur)),
        SubtitleTreatment.Solid => (true, nameof(MaskStyle.Solid)),
        _ => (true, nameof(MaskStyle.Blur)),
    };
}
