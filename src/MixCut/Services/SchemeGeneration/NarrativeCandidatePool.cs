using MixCut.Models;

namespace MixCut.Services.SchemeGeneration;

/// <summary>
/// 自定义叙事结构的候选池与校验纯逻辑（issue #6 §五）。无 IO、无 AI、可单测。
///
/// 一段 = 成片里一条分镜；段上多个标签是「候选池」（并集：分镜带其中任一标签即入选）。
/// AI 只负责从每段候选里挑 1 条；这里负责：算候选池 / 可行变体上限 / Top-N 送审 /
/// 对 AI 返回的变体做程序侧二次校验（不能只信 AI）。
/// </summary>
public static class NarrativeCandidatePool
{
    /// <summary>某段的候选池：分镜语义标签 ∩ 段标签 ≠ 空（并集）。</summary>
    public static List<Segment> CandidatesForSlot(IEnumerable<Segment> segments, NarrativeSlot slot)
    {
        var tags = slot.Tags.ToHashSet();
        return segments.Where(s => s.SemanticTypes.Any(tags.Contains)).ToList();
    }

    /// <summary>每段送 AI 的候选：质量分降序、再时长降序，取前 <paramref name="n"/>（issue §五.3）。</summary>
    public static List<Segment> TopN(IEnumerable<Segment> candidates, int n) =>
        candidates
            .OrderByDescending(s => s.QualityScore)
            .ThenByDescending(s => s.Duration)
            .Take(Math.Max(0, n))
            .ToList();

    /// <summary>
    /// 可行变体上限 = 各段候选数乘积，封顶到 <paramref name="requested"/>；
    /// 任一段候选为 0 → 0（无法生成）。issue §五.2。
    /// </summary>
    public static int FeasibleVariantCap(
        IReadOnlyList<NarrativeSlot> slots, IEnumerable<Segment> segments, int requested)
    {
        if (slots.Count == 0 || requested <= 0)
        {
            return 0;
        }
        var segList = segments.ToList();
        long product = 1;
        foreach (var slot in slots)
        {
            var count = CandidatesForSlot(segList, slot).Count;
            if (count == 0)
            {
                return 0;
            }
            product *= count;
            if (product >= requested)
            {
                return requested; // 已够 + 防乘积溢出
            }
        }
        return (int)Math.Min(product, requested);
    }

    /// <summary>
    /// 程序侧二次校验（不能只信 AI，issue §五.6）：
    /// 段数正确 + 无重复分镜 + 每段所选分镜确实在该段候选池内。任一不满足 → false（丢弃该变体）。
    /// </summary>
    public static bool ValidateComposition(
        IReadOnlyList<NarrativeSlot> slots, IEnumerable<Segment> segments, IReadOnlyList<Guid> chosenIds)
    {
        if (chosenIds.Count != slots.Count)
        {
            return false;
        }
        if (chosenIds.Distinct().Count() != chosenIds.Count)
        {
            return false;
        }
        var segList = segments.ToList();
        for (var i = 0; i < slots.Count; i++)
        {
            var pool = CandidatesForSlot(segList, slots[i]);
            if (pool.All(s => s.Id != chosenIds[i]))
            {
                return false;
            }
        }
        return true;
    }
}
