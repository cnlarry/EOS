using System.Text.Json;

using EOS.API.Data;
using EOS.API.Data.Effects;
using EOS.API.Models;

using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 效果配方目录的契约约束（纯内存，不连库）。
///
/// 配方层的唯一价值主张是"**只预填、不发明**"：它把实现键收敛成业务概念，但配置出来的东西
/// 必须与逐字段手工配**完全一样**。本文件锁住这条主张与它依赖的三件事：
/// ① 配方只能指向闭集内的效果键与事件；
/// ② 建议的反向 kind 必须落在该键的兼容集内（否则配方会教人配出一个跑不了的组合）；
/// ③ 配方路径与键级路径产出的动作行**逐字段一致**（等价性的最小判别式）。
/// </summary>
public sealed class EffectRecipeCatalogTests
{
    /// <summary>
    /// 配方路径的产出：界面按配方预填一条动作行。它只做三件可验证的事——
    /// 取配方的事件/效果键、按反向预设填 REVERSE_STRUCT、其余留空交给人。
    /// </summary>
    private static BusinessActionDto BuildFromRecipe(EffectRecipeCatalog.Recipe recipe, int seq = 1) =>
        new(
            Seq: seq,
            EventCode: recipe.EventCodes[0],
            EffectKey: recipe.EffectKeys[0],
            EffectName: null,
            Enabled: true,
            FailMode: "BLOCK",
            Condition: null,
            Params: null,
            Reverse: JsonSerializer.Serialize(new { kind = recipe.ReversePreset }),
            Remark: null,
            SourceRef: null,
            Ops: null);

    [Fact]
    public void 配方键唯一且非空()
    {
        Assert.NotEmpty(EffectRecipeCatalog.All);
        Assert.All(EffectRecipeCatalog.All, recipe =>
        {
            Assert.False(string.IsNullOrWhiteSpace(recipe.Key));
            Assert.False(string.IsNullOrWhiteSpace(recipe.Name));
            Assert.False(string.IsNullOrWhiteSpace(recipe.Summary));
        });
        var keys = EffectRecipeCatalog.All.Select(recipe => recipe.Key).ToList();
        Assert.Equal(keys.Count, keys.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void 配方只指向闭集内的效果键与事件()
    {
        Assert.All(EffectRecipeCatalog.All, recipe =>
        {
            Assert.NotEmpty(recipe.EffectKeys);
            Assert.NotEmpty(recipe.EventCodes);
            Assert.All(recipe.EffectKeys, key => Assert.Contains(key, BusinessActionCatalog.EffectKeys));
            Assert.All(recipe.EventCodes, code => Assert.Contains(code, BusinessActionCatalog.Events));
            // 自定义按钮不是效果键：配方不得把它们收进来（那是另一条轴，见目录文档 §4-1）。
            Assert.All(recipe.EventCodes, code => Assert.NotEqual(BusinessActionCatalog.ManualEvent, code));
        });
    }

    [Fact]
    public void 建议反向kind落在每个覆盖键的兼容集内()
    {
        Assert.All(EffectRecipeCatalog.All, recipe =>
        {
            Assert.False(string.IsNullOrWhiteSpace(recipe.ReversePreset));
            Assert.All(recipe.EffectKeys, key =>
            {
                var allowed = EffectReverseCompatibility.AllowedKinds(key, recipe.FormulaMode);
                Assert.True(
                    allowed.Contains(recipe.ReversePreset, StringComparer.OrdinalIgnoreCase),
                    $"配方 {recipe.Key} 的建议反向 {recipe.ReversePreset} 不在 {key} 的兼容集（{string.Join('/', allowed)}）内。");
            });
        });
    }

    /// <summary>
    /// 覆盖口径锁定：配方目录里映射到配方的 17 个在用效果键。
    /// 少一个都说明有人回退了配方层（把键踢回"未归类"），必须显式改文档再改本表。
    /// </summary>
    [Fact]
    public void 配方覆盖目录文档记载的在用键()
    {
        string[] documented =
        [
            "field-accumulate", "inventory-move", "half-stock-move", "completion-close", "adjust-projection",
            "set-state", "balance-adjust", "stamp-last-activity", "mrp-plan-alloc", "link-stamp",
            "detail-rollup", "cus-account-sync", "mould-ids-sync", "detail-flag-and-rollup",
            "order-change-apply", "produce-change-apply", "purchase-change-apply",
        ];
        var covered = EffectRecipeCatalog.CoveredEffectKeys();
        Assert.All(documented, key => Assert.Contains(key, covered));
        // 反向也要成立：配方不得收进文档里没写的键（多收等于偷偷扩面）。
        Assert.All(covered, key => Assert.Contains(key, documented));
    }

    /// <summary>
    /// **等价性**：同一份配置，走配方路径与走键级路径，产出的动作行必须逐字段一致。
    ///
    /// 这是"配方只是预填"的可执行定义：一旦有人在配方里塞进手工路径表达不了的东西
    /// （多一个字段、换一种参数形态），本用例即变红。
    /// </summary>
    [Fact]
    public void 配方产出与键级配置逐字段一致()
    {
        var recipe = EffectRecipeCatalog.Find("move-stock")!;
        var fromRecipe = BuildFromRecipe(recipe);
        var byHand = new BusinessActionDto(
            Seq: 1,
            EventCode: "APPROVE_EFFECT",
            EffectKey: "inventory-move",
            EffectName: null,
            Enabled: true,
            FailMode: "BLOCK",
            Condition: null,
            Params: null,
            Reverse: "{\"kind\":\"reverse-flow\"}",
            Remark: null,
            SourceRef: null,
            Ops: null);

        Assert.Equal(JsonSerializer.Serialize(byHand), JsonSerializer.Serialize(fromRecipe));
    }

    /// <summary>所有配方都要能这样等价地手工配出来——逐配方各校一次，避免只有样本那个键被守住。</summary>
    [Fact]
    public void 每个配方都能等价地手工配出()
    {
        foreach (var recipe in EffectRecipeCatalog.All)
        {
            var built = BuildFromRecipe(recipe, seq: 3);
            Assert.Equal(recipe.EventCodes[0], built.EventCode);
            Assert.Equal(recipe.EffectKeys[0], built.EffectKey);
            Assert.Equal($"{{'kind':'{recipe.ReversePreset}'}}".Replace('\'', '"'), built.Reverse);
            Assert.Null(built.Params);
            Assert.Null(built.Ops);
            Assert.Equal(3, built.Seq);
        }
    }

    /// <summary>
    /// 界面上"哪些事件配了不会跑"的口径：`InertEvents` 是**空集**——事件闭集里每个事件都已有派发点，
    /// 因此界面不该再对任何事件标注"该事件当前不会触发效果链"。
    ///
    /// 空集本身要有守卫：谁把某个事件重新变成"接不到效果链"（删派发点、加事件却忘了接），
    /// 就得在这里显式登记，否则界面会把能跑的事件说成"配了不跑"。
    /// </summary>
    [Fact]
    public void 惰性事件为空集且是事件闭集的子集()
    {
        Assert.Empty(BusinessActionCatalog.InertEvents);
        Assert.All(BusinessActionCatalog.InertEvents, code => Assert.Contains(code, BusinessActionCatalog.Events));
        // 会跑的事件不得被误标成惰性（否则界面会把能用的配置说成"配了不跑"）。
        Assert.DoesNotContain("ENDCASE", BusinessActionCatalog.InertEvents);
        Assert.DoesNotContain("UNENDCASE", BusinessActionCatalog.InertEvents);
        Assert.DoesNotContain("SAVE", BusinessActionCatalog.InertEvents);
        Assert.DoesNotContain("APPROVE_EFFECT", BusinessActionCatalog.InertEvents);
        Assert.DoesNotContain("DEAPPROVE", BusinessActionCatalog.InertEvents);
        Assert.DoesNotContain(BusinessActionCatalog.ManualEvent, BusinessActionCatalog.InertEvents);
    }
}
