using System.Collections.Generic;
using MixCut.Models;
using Xunit;

namespace MixCut.Tests;

/// <summary>
/// 自定义叙事结构段位（NarrativeSlot）序列化往返单测。对应 issue #6 数据模型。
/// </summary>
public class NarrativeSlotTests
{
    [Fact]
    public void Slots_序列化往返不丢()
    {
        var slots = new List<NarrativeSlot>
        {
            new(1, new List<SemanticType> { SemanticType.PainPoint, SemanticType.Hook }),
            new(2, new List<SemanticType> { SemanticType.CallToAction }),
        };

        var json = NarrativeSlot.Serialize(slots);
        var back = NarrativeSlot.Deserialize(json);

        Assert.Equal(2, back.Count);
        Assert.Equal(1, back[0].Order);
        Assert.Equal(2, back[0].Tags.Count);
        Assert.Contains(SemanticType.PainPoint, back[0].Tags);
        Assert.Contains(SemanticType.Hook, back[0].Tags);
        Assert.Equal(SemanticType.CallToAction, back[1].Tags[0]);
    }

    [Fact]
    public void Deserialize_null或空_返回空列表()
    {
        Assert.Empty(NarrativeSlot.Deserialize(null));
        Assert.Empty(NarrativeSlot.Deserialize(""));
    }

    [Fact]
    public void Deserialize_损坏JSON_返回空列表不抛()
    {
        Assert.Empty(NarrativeSlot.Deserialize("{ 这不是合法 json"));
    }

    [Fact]
    public void NarrativeDisplayName_段间点号_同段斜杠()
    {
        var strat = new MixStrategy { IsNarrativeTemplate = true };
        strat.NarrativeSlots = new List<NarrativeSlot>
        {
            new(1, new List<SemanticType> { SemanticType.PainPoint }),
            new(2, new List<SemanticType> { SemanticType.Solution }),
            new(3, new List<SemanticType> { SemanticType.Results, SemanticType.SocialProof }),
            new(4, new List<SemanticType> { SemanticType.CallToAction }),
        };

        Assert.Equal("痛点 · 产品方案 · 效果展示/信任背书 · 行动号召", strat.NarrativeDisplayName);
    }

    [Fact]
    public void NarrativeSlots_经由Strategy读写JSON往返()
    {
        var strat = new MixStrategy();
        strat.NarrativeSlots = new List<NarrativeSlot>
        {
            new(1, new List<SemanticType> { SemanticType.Hook, SemanticType.PainPoint }),
        };
        Assert.False(string.IsNullOrEmpty(strat.NarrativeSlotsJson));
        Assert.Single(strat.NarrativeSlots);
        Assert.Equal(2, strat.NarrativeSlots[0].Tags.Count);
    }
}
