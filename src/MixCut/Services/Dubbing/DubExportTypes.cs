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

/// <summary>单分镜导出规格（值类型）。对应 mac DubSegmentSpec。</summary>
public sealed record DubSegmentSpec(
    string VideoPath, int StartFrame, int EndFrame, double Fps,
    string CaptionText, bool HasHardSubtitle, string MaskStyleRaw, SubtitleMaskRect MaskRect,
    bool IsVoiceLocked, string? DubAudioPath, int FreezePadFrames, double TrailingSilence,
    string? BgmAudioPath);

/// <summary>配音导出输入（整条成片）。对应 mac DubExportInput。</summary>
public sealed record DubExportInput(IReadOnlyList<DubSegmentSpec> Segments, int MaxWidth, int MaxHeight)
{
    /// <summary>
    /// 从某方案 + 一个组合(每槽选定 dubId，null=原声) 解析导出输入。combo=null 时退回
    /// 按 <see cref="SchemeSegment.SelectedSegmentDubId"/> 取定。锁定/找不到/无音频 → 原声回退。
    /// 配音段自动混入 demucs 分离出的 BGM。对应 mac DubExportInput.from。
    /// </summary>
    public static DubExportInput? From(MixScheme scheme, IReadOnlyList<Guid?>? combo = null)
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
            if (segment is null || video is null || string.IsNullOrEmpty(video.LocalPath) || !File.Exists(video.LocalPath))
            {
                continue;
            }

            var fps = video.Fps > 0 ? video.Fps : 30;
            maxW = Math.Max(maxW, video.Width);
            maxH = Math.Max(maxH, video.Height);

            // 该槽选定的变体：combo 优先，否则 SelectedSegmentDubId；null/找不到 → 原声
            var chosenId = combo is not null ? (idx < combo.Count ? combo[idx] : null) : schemeSeg.SelectedSegmentDubId;
            var chosen = chosenId is { } id ? segment.EffectiveDubVariants.FirstOrDefault(d => d.Id == id) : null;

            if (segment.IsVoiceLocked || chosen is null)
            {
                // 锁定/无选定 → 保留原声原字幕（不烧新字幕，hasHardSubtitle:false 防遮到要保留的原字幕）
                specs.Add(new DubSegmentSpec(
                    video.LocalPath, segment.StartFrame, segment.EndFrame, fps,
                    segment.Text, false, segment.MaskStyleRaw, segment.MaskRect,
                    IsVoiceLocked: true, DubAudioPath: null, 0, 0, BgmAudioPath: null));
            }
            else if (!string.IsNullOrEmpty(chosen.AudioFilePath) && File.Exists(chosen.AudioFilePath))
            {
                var caption = string.IsNullOrEmpty(chosen.RewrittenText) ? segment.Text : chosen.RewrittenText;
                specs.Add(new DubSegmentSpec(
                    video.LocalPath, segment.StartFrame, segment.EndFrame, fps,
                    caption, segment.HasHardSubtitle, segment.MaskStyleRaw, segment.MaskRect,
                    IsVoiceLocked: false, DubAudioPath: chosen.AudioFilePath,
                    chosen.FreezePadFrames, chosen.TrailingSilence, BgmPath(video)));
            }
            else
            {
                // 非锁定但无已生成配音 → 回退原声（不烧新字幕）
                specs.Add(new DubSegmentSpec(
                    video.LocalPath, segment.StartFrame, segment.EndFrame, fps,
                    segment.Text, false, segment.MaskStyleRaw, segment.MaskRect,
                    IsVoiceLocked: true, DubAudioPath: null, 0, 0, BgmAudioPath: null));
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

    /// <summary>理论组合总数（不真正生成；用于 UI「将生成 N 条」）。每非锁定槽选项数 = 1 + 已生成变体数。</summary>
    public static int FeasibleCount(MixScheme scheme)
    {
        var n = 1;
        foreach (var ss in scheme.OrderedSegments)
        {
            var seg = ss.Segment;
            if (seg is null) continue;
            n *= seg.IsVoiceLocked ? 1 : 1 + seg.EffectiveDubVariants.Count;
            if (n >= 1_000_000) return 1_000_000;
        }
        return n;
    }

    public static Plan Build(MixScheme scheme)
    {
        var ordered = scheme.OrderedSegments;
        if (ordered.Count == 0) return new Plan(Array.Empty<Combo>(), 0, false);

        var slots = ordered.Select(ss =>
        {
            var seg = ss.Segment;
            return seg is null
                ? new SlotOptions(true, Array.Empty<Guid>())
                : new SlotOptions(seg.IsVoiceLocked, seg.EffectiveDubVariants.Select(d => d.Id).ToList());
        }).ToList();

        var result = VariantCombinationGenerator.Generate(slots, MaxCombos, includeOriginal: true);

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

/// <summary>一个分镜槽的配音可选项（笛卡尔积输入）。对应 mac SlotOptions。</summary>
public readonly record struct SlotOptions(bool IsLocked, IReadOnlyList<Guid> DubIds);

/// <summary>组合采样结果。对应 mac CombinationResult。</summary>
public sealed record CombinationResult(IReadOnlyList<IReadOnlyList<Guid?>> Combinations, int FeasibleCount, bool Truncated);

/// <summary>
/// 把每槽配音可选项按笛卡尔积确定性采样成最多 limit 条互不相同的组合。对应 mac VariantCombinationGenerator。
/// 锁定槽或无变体槽恒取原声(null)。用混合进制计数枚举前 limit 个。
/// </summary>
public static class VariantCombinationGenerator
{
    private const int FeasibleCap = 1_000_000;

    public static CombinationResult Generate(IReadOnlyList<SlotOptions> slots, int limit, bool includeOriginal = false)
    {
        // 每槽实际可选集合（变体按 id 升序保证确定性）
        var choices = slots.Select(slot =>
        {
            if (slot.IsLocked) return new List<Guid?> { null };
            var variants = slot.DubIds.OrderBy(g => g.ToString()).Select(g => (Guid?)g).ToList();
            if (includeOriginal) { var l = new List<Guid?> { null }; l.AddRange(variants); return l; }
            return variants.Count == 0 ? new List<Guid?> { null } : variants;
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
