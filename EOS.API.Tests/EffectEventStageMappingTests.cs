using EOS.API.Data;
using EOS.API.Data.Effects;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 「事件 → 校验阶段」映射的契约测试：映射表必须**逐成员覆盖** <see cref="EffectEvent"/>
/// （集合相等，不是单点包含），只有阶段闭集内的名字能被映射出来，查不到映射的取值一律
/// "不带校验闸"（null）而不是静默落到保存期。
///
/// 判别力：从映射表删掉任一键 ⇒ 覆盖率测试红；把 <see cref="EffectEvent.Endcase"/> 改成
/// "SAVE" ⇒ 语义测试红。
/// </summary>
public sealed class EffectEventStageMappingTests
{
    [Fact]
    public void 映射表与事件闭集成员集合相等()
    {
        var declared = Enum.GetValues<EffectEvent>().ToHashSet();
        var mapped = EffectPipeline.ValidationStagesByEvent.Keys.ToHashSet();

        Assert.True(declared.SetEquals(mapped),
            "映射表必须覆盖 EffectEvent 的每个成员：缺少 ["
            + string.Join("/", declared.Except(mapped)) + "]，多余 ["
            + string.Join("/", mapped.Except(declared))
            + "]。新增事件成员时要在 EffectPipeline.ValidationStagesByEvent 里显式登记（可登记为 null）。");
    }

    [Theory]
    [InlineData(EffectEvent.Save, "SAVE")]
    [InlineData(EffectEvent.ApproveEffect, "APPROVE")]
    [InlineData(EffectEvent.Deapprove, "DEAPPROVE")]
    [InlineData(EffectEvent.Delete, "DELETE")]
    public void 阶段事件映射到自己的阶段(EffectEvent executionEvent, string stage) =>
        Assert.Equal(stage, EffectPipeline.StageFor(executionEvent));

    [Theory]
    [InlineData(EffectEvent.Endcase)]
    [InlineData(EffectEvent.Unendcase)]
    [InlineData(EffectEvent.Manual)]
    public void 非阶段事件不带校验闸(EffectEvent executionEvent) =>
        Assert.Null(EffectPipeline.StageFor(executionEvent));

    [Fact]
    public void 未登记与越界的取值返回空且不抛()
    {
        // 兜底语义是"无校验闸"：抛异常会让未知事件打断整笔单据操作，
        // 落 "SAVE" 则会让未知事件跑一遍保存期规则（错的那一个阶段）。
        Assert.Null(EffectPipeline.StageFor((EffectEvent)999));
        Assert.Null(EffectPipeline.StageFor((EffectEvent)(-1)));
    }

    [Fact]
    public void 映射出的阶段名必须属于阶段闭集()
    {
        var outside = EffectPipeline.ValidationStagesByEvent
            .Where(pair => pair.Value is not null && !BusinessActionCatalog.IsKnownValidationStage(pair.Value))
            .Select(pair => pair.Key + "→" + pair.Value)
            .ToArray();
        Assert.True(outside.Length == 0, "映射出了阶段闭集之外的名字：" + string.Join("、", outside));

        // 反向：闭集里每个阶段都必须有事件映射到它，否则闭集里存在永远跑不到的阶段。
        var mapped = EffectPipeline.ValidationStagesByEvent.Values
            .Where(stage => stage is not null)
            .Select(stage => stage!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.True(BusinessActionCatalog.ValidationStages.IsSubsetOf(mapped),
            "阶段闭集里有事件映射不到的阶段：" + string.Join("、", BusinessActionCatalog.ValidationStages.Except(mapped)));
    }
}
