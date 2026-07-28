using System.IO;
using MixCut.Models;
using MixCut.Utilities;

namespace MixCut.Services.Dubbing;

/// <summary>硬字幕遮挡模式（导出滤镜用）。对齐 mac SubtitleMaskMode（dim 已砍，保留枚举位兼容）。</summary>
public enum SubtitleMaskMode { None, Blur, Solid, Dim }

public static class SubtitleMaskModeExtensions
{
    /// <summary>从底层字段还原导出遮挡模式。</summary>
    public static SubtitleMaskMode From(bool hasHardSubtitle, string? maskStyleRaw)
    {
        if (!hasHardSubtitle) return SubtitleMaskMode.None;
        return string.Equals(maskStyleRaw, nameof(MaskStyle.Solid), StringComparison.OrdinalIgnoreCase)
            ? SubtitleMaskMode.Solid : SubtitleMaskMode.Blur;
    }
}

/// <summary>遮挡框像素坐标（由归一化 <see cref="SubtitleMaskRect"/> × 输出尺寸换算，偶数对齐）。</summary>
public readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    public static PixelRect From(SubtitleMaskRect r, int outW, int outH)
    {
        int Even(double v) => Math.Max(0, (int)Math.Round(v) / 2 * 2);
        return new PixelRect(Even(r.X * outW), Even(r.Y * outH), Even(r.Width * outW), Even(r.Height * outH));
    }
}

/// <summary>
/// 原声段的 vocals 切片来源（issue #22 BGM 替换模式，对应 mac DubSegmentGraph.VocalsSource）：
/// 音频改用 <paramref name="SourceVideoHash"/> 对应的整轨 vocals.wav 按
/// [<paramref name="Start"/>, <paramref name="End"/>]（<b>原视频时间轴</b>，秒）切片。
/// <paramref name="SourceVideoPath"/> 保留原视频路径供「重试时重新分离」定位（画面替换分镜的
/// spec.VideoPath 是替换合成片，不能拿去分离）。
/// </summary>
public sealed record VocalsSlice(string SourceVideoPath, string SourceVideoHash, double Start, double End);

/// <summary>单分镜导出规格（值类型）。对应 mac DubSegmentSpec。</summary>
/// <remarks>
/// #15：<see cref="CaptionLines"/> = 逐句字幕（相对分镜起点秒）；空 = 不烧字幕。
/// issue #23：<see cref="FontRatio"/> = 逐分镜字号比例（来自 <see cref="Segment.SubtitleFontRatio"/>），
/// null = 未单独设置，烧录时跟随全局 AppSettings.SubtitleFontRatio（存量行为不变）。
/// </remarks>
public sealed record DubSegmentSpec(
    string VideoPath, int StartFrame, int EndFrame, double Fps,
    IReadOnlyList<CaptionLine> CaptionLines, bool HasHardSubtitle, string MaskStyleRaw, SubtitleMaskRect MaskRect,
    bool IsVoiceLocked, string? DubAudioPath, int FreezePadFrames, double TrailingSilence,
    string? BgmAudioPath, VocalsSlice? Vocals = null, double? FontRatio = null);

/// <summary>配音导出输入（整条成片）。对应 mac DubExportInput。</summary>
public sealed record DubExportInput(IReadOnlyList<DubSegmentSpec> Segments, int MaxWidth, int MaxHeight)
{
    /// <summary>
    /// 从某方案 + 一个组合(每槽选定 dubId，null=原声) 解析导出输入。combo=null 时退回
    /// 按 <see cref="SchemeSegment.SelectedSegmentDubId"/> 取定。锁定/找不到/无音频 → 原声回退。
    /// 配音段自动混入 demucs 分离出的 BGM。对应 mac DubExportInput.from。
    /// <paramref name="useVocalsAudio"/>（issue #22 BGM 替换模式）：原声/锁定/回退段改用整轨
    /// vocals.wav 切片（去原 BGM 只留口播）；配音段<b>不再混入</b>分离出的 bgm.wav —— 整片 BGM
    /// 由导出末步统一铺（<see cref="Bgm.BgmApplier"/>）。
    /// </summary>
    public static DubExportInput? From(
        MixScheme scheme, IReadOnlyList<Guid?>? combo = null, bool useVocalsAudio = false)
    {
        var ordered = scheme.OrderedSegments;
        if (ordered.Count == 0) return null;

        var specs = new List<DubSegmentSpec>();
        int maxW = 0, maxH = 0;
        for (var idx = 0; idx < ordered.Count; idx++)
        {
            var schemeSeg = ordered[idx];
            var segment = schemeSeg.Segment;
            var video = segment?.Video;
            if (segment is null || video is null)
            {
                continue;
            }
            // #12：画面源统一走 EffectivePicture（有替换用替换、否则原源）；未替换时返回原值，行为不变。
            var ep = segment.EffectivePicture;
            if (string.IsNullOrEmpty(ep.VideoPath) || !File.Exists(ep.VideoPath))
            {
                continue;
            }

            var fps = ep.Fps > 0 ? ep.Fps : 30;
            maxW = Math.Max(maxW, video.Width);
            maxH = Math.Max(maxH, video.Height);

            // 该槽选定的变体：combo 优先，否则 SelectedSegmentDubId；null/找不到 → 原声
            var chosenId = combo is not null ? (idx < combo.Count ? combo[idx] : null) : schemeSeg.SelectedSegmentDubId;
            var chosen = chosenId is { } id ? segment.EffectiveDubVariants.FirstOrDefault(d => d.Id == id) : null;

            // BGM 替换模式下原声/锁定/回退段的 vocals 切片来源（按原视频时间轴，画面替换分镜也一样）。
            var vocals = useVocalsAudio
                ? new VocalsSlice(video.LocalPath, video.ContentHash ?? string.Empty,
                    segment.StartTime, segment.EndTime)
                : null;

            if (segment.IsVoiceLocked || chosen is null)
            {
                // 锁定/无选定 → 保留原声原字幕（不烧新字幕，hasHardSubtitle:false 防遮到要保留的原字幕）
                specs.Add(new DubSegmentSpec(
                    ep.VideoPath, ep.StartFrame, ep.EndFrame, fps,
                    System.Array.Empty<CaptionLine>(), false, segment.MaskStyleRaw, segment.MaskRect,
                    IsVoiceLocked: true, DubAudioPath: null, 0, 0, BgmAudioPath: null, Vocals: vocals));
            }
            else if (!string.IsNullOrEmpty(chosen.AudioFilePath) && File.Exists(chosen.AudioFilePath))
            {
                // #15 逐句字幕：优先用对齐好的 CaptionLines；旧数据无对齐 → 整段一条兜底（等价旧整段烧法，防老数据丢字幕）
                var capLines = chosen.CaptionLines;
                if (capLines.Count == 0)
                {
                    var whole = string.IsNullOrEmpty(chosen.RewrittenText) ? segment.Text : chosen.RewrittenText;
                    capLines = string.IsNullOrEmpty(whole)
                        ? new List<CaptionLine>()
                        : new List<CaptionLine> { new(whole, 0, segment.Duration) };
                }
                specs.Add(new DubSegmentSpec(
                    ep.VideoPath, ep.StartFrame, ep.EndFrame, fps,
                    capLines, segment.HasHardSubtitle, segment.MaskStyleRaw, segment.MaskRect,
                    IsVoiceLocked: false, DubAudioPath: chosen.AudioFilePath,
                    chosen.FreezePadFrames, chosen.TrailingSilence,
                    // BGM 模式：配音段不混原视频分离出的 bgm.wav（整片 BGM 最后统一铺）
                    useVocalsAudio ? null : BgmPath(video),
                    FontRatio: segment.SubtitleFontRatio > 0 ? segment.SubtitleFontRatio : null));
            }
            else
            {
                // 非锁定但无已生成配音 → 回退原声（不烧新字幕）
                specs.Add(new DubSegmentSpec(
                    ep.VideoPath, ep.StartFrame, ep.EndFrame, fps,
                    System.Array.Empty<CaptionLine>(), false, segment.MaskStyleRaw, segment.MaskRect,
                    IsVoiceLocked: true, DubAudioPath: null, 0, 0, BgmAudioPath: null, Vocals: vocals));
            }
        }

        return specs.Count == 0 ? null : new DubExportInput(specs, maxW, maxH);
    }

    /// <summary>配音段 BGM 源：demucs 分离出的整轨 bgm.wav（按视频内容哈希定位）；不存在 → null。</summary>
    private static string? BgmPath(Video video)
    {
        if (string.IsNullOrEmpty(video.ContentHash)) return null;
        var p = Path.Combine(AppPaths.StemsDirectory(video.ContentHash), "bgm.wav");
        return File.Exists(p) ? p : null;
    }
}

/// <summary>把方案展开成「全部配音组合」（笛卡尔积）。对应 mac SchemeComboPlanner。</summary>
public static class SchemeComboPlanner
{
    /// <summary>单方案最多展开的组合数（防爆炸）。</summary>
    public const int MaxCombos = 256;

    public sealed record Combo(IReadOnlyList<Guid?> Choices, string NameSuffix);
    public sealed record Plan(IReadOnlyList<Combo> Combos, int FeasibleCount, bool Truncated);

    /// <summary>
    /// 理论组合总数（不真正生成；用于 UI「将生成 N 条」）。#13：每槽档数 = <see cref="Segment.CombinationSlotCount"/>
    /// （锁定恒 1；否则 (原版参与?1:0)+勾选参与的改写版数，兜底 ≥1）——与档数公式单一真源一致。
    /// </summary>
    public static int FeasibleCount(MixScheme scheme)
    {
        var n = 1;
        foreach (var ss in scheme.OrderedSegments)
        {
            var seg = ss.Segment;
            if (seg is null) continue;
            n *= seg.CombinationSlotCount;
            if (n >= 1_000_000) return 1_000_000;
        }
        return n;
    }

    public static Plan Build(MixScheme scheme)
    {
        var ordered = scheme.OrderedSegments;
        if (ordered.Count == 0) return new Plan(Array.Empty<Combo>(), 0, false);

        // #13：每槽可选集合 = 锁定→仅原声；否则 (原版参与?原声:∅) ∪ 勾选参与的改写版。
        var slots = ordered.Select(ss =>
        {
            var seg = ss.Segment;
            return seg is null
                ? new SlotOptions(true, true, Array.Empty<Guid>())
                : new SlotOptions(
                    seg.IsVoiceLocked,
                    seg.IsVoiceLocked || seg.OriginalParticipatesInCombination,
                    seg.CombinationDubVariants.Select(d => d.Id).ToList());
        }).ToList();

        var result = VariantCombinationGenerator.Generate(slots, MaxCombos);

        var combos = result.Combinations.Select(choices =>
        {
            var parts = choices.Select((dubId, idx) =>
            {
                if (dubId is not { } id || idx >= ordered.Count) return "原";
                var seg = ordered[idx].Segment;
                var dub = seg?.EffectiveDubVariants.FirstOrDefault(d => d.Id == id);
                return dub is null ? "原" : Letter(dub.TextVariantIndex);
            });
            return new Combo(choices, "[" + string.Join("·", parts) + "]");
        }).ToList();

        return new Plan(combos, result.FeasibleCount, result.Truncated);
    }

    /// <summary>改写版字母：0→A、1→B…</summary>
    private static string Letter(int index) => index is >= 0 and < 26 ? ((char)('A' + index)).ToString() : (index + 1).ToString();
}

/// <summary>
/// 一个分镜槽的配音可选项（笛卡尔积输入）。对应 mac SlotOptions。
/// #13：<paramref name="IncludeOriginal"/> = 原声是否作为该槽一个选项（锁定槽应恒 true）；
/// <paramref name="DubIds"/> 只含<b>勾选参与组合</b>的改写版。
/// </summary>
public readonly record struct SlotOptions(bool IsLocked, bool IncludeOriginal, IReadOnlyList<Guid> DubIds);

/// <summary>组合采样结果。对应 mac CombinationResult。</summary>
public sealed record CombinationResult(IReadOnlyList<IReadOnlyList<Guid?>> Combinations, int FeasibleCount, bool Truncated);

/// <summary>
/// 把每槽配音可选项按笛卡尔积确定性采样成最多 limit 条互不相同的组合。对应 mac VariantCombinationGenerator。
/// 锁定槽或无变体槽恒取原声(null)。用混合进制计数枚举前 limit 个。
/// </summary>
public static class VariantCombinationGenerator
{
    private const int FeasibleCap = 1_000_000;

    public static CombinationResult Generate(IReadOnlyList<SlotOptions> slots, int limit)
    {
        // 每槽实际可选集合（变体按 id 升序保证确定性）。#13：原声是否入选由每槽 IncludeOriginal 决定；
        // 集合为空（原版没勾、也没勾任何变体）→ 兜底回退仅原声，保证分镜不断档。
        var choices = slots.Select(slot =>
        {
            if (slot.IsLocked) return new List<Guid?> { null };
            var variants = slot.DubIds.OrderBy(g => g.ToString()).Select(g => (Guid?)g).ToList();
            var list = new List<Guid?>();
            if (slot.IncludeOriginal) list.Add(null);
            list.AddRange(variants);
            return list.Count == 0 ? new List<Guid?> { null } : list;
        }).ToList();

        long feasible = 1;
        foreach (var c in choices)
        {
            feasible *= Math.Max(1, c.Count);
            if (feasible >= FeasibleCap) { feasible = FeasibleCap; break; }
        }

        if (limit <= 0 || slots.Count == 0)
        {
            return new CombinationResult(Array.Empty<IReadOnlyList<Guid?>>(), (int)feasible, feasible > Math.Max(0, limit));
        }

        var take = (int)Math.Min(limit, feasible);
        var combinations = new List<IReadOnlyList<Guid?>>(take);
        for (var n = 0; n < take; n++)
        {
            var rem = n;
            var combo = new List<Guid?>(choices.Count);
            foreach (var c in choices)
            {
                var count = Math.Max(1, c.Count);
                combo.Add(c[rem % count]);
                rem /= count;
            }
            combinations.Add(combo);
        }
        return new CombinationResult(combinations, (int)feasible, feasible > take);
    }
}
