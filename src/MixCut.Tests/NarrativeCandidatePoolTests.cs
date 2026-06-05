using System;
using System.Collections.Generic;
using System.Linq;
using MixCut.Models;
using MixCut.Services.SchemeGeneration;
using Xunit;

namespace MixCut.Tests;

/// <summary>
/// 自定义叙事结构候选池/上限/Top-N/二次校验纯逻辑单测（issue #6 §五）。
/// </summary>
public class NarrativeCandidatePoolTests
{
    private static Segment Seg(SemanticType[] tags, double quality = 8.0, double dur = 2.0) =>
        new()
        {
            QualityScore = quality,
            StartTime = 0,
            EndTime = dur,
            SemanticTypes = tags,
        };

    private static NarrativeSlot Slot(int order, params SemanticType[] tags) =>
        new(order, tags.ToList());

    [Fact]
    public void CandidatesForSlot_标签取并集_带任一标签即入选()
    {
        var segs = new[]
        {
            Seg(new[] { SemanticType.PainPoint }),
            Seg(new[] { SemanticType.Hook }),
            Seg(new[] { SemanticType.Results, SemanticType.SocialProof }),
            Seg(new[] { SemanticType.CallToAction }),
        };
        var slot = Slot(1, SemanticType.PainPoint, SemanticType.Hook);

        var pool = NarrativeCandidatePool.CandidatesForSlot(segs, slot);

        Assert.Equal(2, pool.Count); // 痛点 + 噱头，不含 效果/行动
    }

    [Fact]
    public void TopN_质量降序再时长降序()
    {
        var segs = new[]
        {
            Seg(new[] { SemanticType.PainPoint }, quality: 6, dur: 5),
            Seg(new[] { SemanticType.PainPoint }, quality: 9, dur: 2),
            Seg(new[] { SemanticType.PainPoint }, quality: 9, dur: 4),
        };

        var top = NarrativeCandidatePool.TopN(segs, 2);

        Assert.Equal(2, top.Count);
        Assert.Equal(9, top[0].QualityScore);
        Assert.Equal(4, top[0].Duration); // 同质量取时长更长的在前
        Assert.Equal(9, top[1].QualityScore);
        Assert.Equal(2, top[1].Duration);
    }

    [Fact]
    public void FeasibleVariantCap_各段候选数乘积封顶请求数()
    {
        var segs = new[]
        {
            Seg(new[] { SemanticType.PainPoint }),
            Seg(new[] { SemanticType.PainPoint }),
            Seg(new[] { SemanticType.Solution }),
            Seg(new[] { SemanticType.CallToAction }),
            Seg(new[] { SemanticType.CallToAction }),
            Seg(new[] { SemanticType.CallToAction }),
        };
        var slots = new[]
        {
            Slot(1, SemanticType.PainPoint),     // 2 候选
            Slot(2, SemanticType.Solution),      // 1 候选
            Slot(3, SemanticType.CallToAction),  // 3 候选
        };

        // 乘积 2*1*3=6，请求 10 → 取 6
        Assert.Equal(6, NarrativeCandidatePool.FeasibleVariantCap(slots, segs, 10));
        // 请求 4 < 6 → 取 4
        Assert.Equal(4, NarrativeCandidatePool.FeasibleVariantCap(slots, segs, 4));
    }

    [Fact]
    public void FeasibleVariantCap_某段候选0_返回0()
    {
        var segs = new[] { Seg(new[] { SemanticType.PainPoint }) };
        var slots = new[]
        {
            Slot(1, SemanticType.PainPoint),
            Slot(2, SemanticType.CallToAction), // 库里没有，候选 0
        };

        Assert.Equal(0, NarrativeCandidatePool.FeasibleVariantCap(slots, segs, 5));
    }

    [Fact]
    public void ValidateComposition_合法变体通过()
    {
        var p = Seg(new[] { SemanticType.PainPoint });
        var s = Seg(new[] { SemanticType.Solution });
        var c = Seg(new[] { SemanticType.CallToAction });
        var segs = new[] { p, s, c };
        var slots = new[]
        {
            Slot(1, SemanticType.PainPoint),
            Slot(2, SemanticType.Solution),
            Slot(3, SemanticType.CallToAction),
        };

        Assert.True(NarrativeCandidatePool.ValidateComposition(
            slots, segs, new[] { p.Id, s.Id, c.Id }));
    }

    [Fact]
    public void ValidateComposition_段数不符_失败()
    {
        var p = Seg(new[] { SemanticType.PainPoint });
        var slots = new[] { Slot(1, SemanticType.PainPoint), Slot(2, SemanticType.Solution) };

        Assert.False(NarrativeCandidatePool.ValidateComposition(slots, new[] { p }, new[] { p.Id }));
    }

    [Fact]
    public void ValidateComposition_所选不在该段候选池_失败()
    {
        var p = Seg(new[] { SemanticType.PainPoint });
        var c = Seg(new[] { SemanticType.CallToAction });
        var segs = new[] { p, c };
        var slots = new[] { Slot(1, SemanticType.PainPoint), Slot(2, SemanticType.Solution) };

        // 第2段要 Solution，但给了 CallToAction 分镜 → 不在候选池
        Assert.False(NarrativeCandidatePool.ValidateComposition(slots, segs, new[] { p.Id, c.Id }));
    }

    [Fact]
    public void ValidateComposition_变体内重复分镜_失败()
    {
        var p = Seg(new[] { SemanticType.PainPoint, SemanticType.Solution });
        var segs = new[] { p };
        var slots = new[] { Slot(1, SemanticType.PainPoint), Slot(2, SemanticType.Solution) };

        // 同一条 p 被两段都选 → 重复
        Assert.False(NarrativeCandidatePool.ValidateComposition(slots, segs, new[] { p.Id, p.Id }));
    }
}
