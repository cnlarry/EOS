using System.Text.Json;
using EOS.API.Data.Effects;
using EOS.API.Models;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 效果计划加载器对"空白串＝未设置"的容忍度：既有配置用空串表达未设置的列
/// （公式行的聚合/来源域、动作的来源表/字段等），闭式算子集不含空串，
/// 按原样透传会让整个模块的保存/批核在加载阶段失败。
/// </summary>
public sealed class EffectPlanLoaderBlankFieldTests
{
    private static WorkbenchDefinition Definition(JsonElement definition) =>
        JsonSerializer.Deserialize<WorkbenchDefinition>(definition.GetRawText(), new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
        })!;

    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

    [Fact]
    public void 公式行的空白聚合与来源域按未设置处理()
    {
        var definition = Definition(Json("""
            {"ModuleId":1502,"Title":"制令单","MasterTable":"MOC_PRODUCE_M","DetailTable":"MOC_PRODUCE_D",
             "MasterPkOrder":["PRODUCE_TYPE","PRODUCE_NO"],
             "BusinessActions":[
               {"seq":1,"eventCode":"APPROVE_EFFECT","effectKey":"inventory-move","effectName":"领料","enabled":true,
                "ops":[{"opSeq":1,"targetTable":"PRODUCT","targetField":"QTY","opCode":"ACCUM",
                        "sourceScope":"","sourceTable":"","sourceField":"APPLY_QTY","sourceAgg":"",
                        "sourceConstant":"","sourceTerms":"","remark":""}]}]}
            """));

        var plan = new EffectPlanLoader().Load(definition);

        var op = Assert.Single(Assert.Single(plan.Actions).Ops!);
        Assert.Null(op.SourceAgg);
        Assert.Equal("MASTER", op.Source.Scope);
        Assert.Null(op.Remark);
    }

    [Fact]
    public void 空串事件与效果键等同缺失而非未知值()
    {
        var definition = Definition(Json("""
            {"ModuleId":1502,"Title":"制令单","MasterTable":"MOC_PRODUCE_M",
             "MasterPkOrder":["PRODUCE_TYPE","PRODUCE_NO"],
             "BusinessActions":[
               {"seq":1,"eventCode":"APPROVE_EFFECT","effectKey":"inventory-move","enabled":true,
                "ops":[{"opSeq":1,"targetTable":"PRODUCT","targetField":"QTY","opCode":"ACCUM","sourceScope":"MASTER","sourceField":"APPLY_QTY","sourceAgg":"  "}]}]}
            """));

        // 只有空白（含空格）的聚合同样按缺省处理
        var plan = new EffectPlanLoader().Load(definition);
        Assert.Null(Assert.Single(Assert.Single(plan.Actions).Ops!).SourceAgg);
    }

    [Fact]
    public void 常量来源的空串是清空取值而非未设置()
    {
        // "结案后不再满足条件时把经手人清空"在配置里就是常量空串，属真实取值；
        // 若按"空白即未设置"归一，加载后执行器即报"缺少 sourceConstant"，该模块批核全部失败。
        var definition = Definition(Json("""
            {"ModuleId":1607,"Title":"收料单","MasterTable":"PUR_RECEIVE_M","DetailTable":"PUR_RECEIVE_D",
             "MasterPkOrder":["RECEIVE_TYPE","RECEIVE_NO"],
             "BusinessActions":[
               {"seq":5,"eventCode":"APPROVE_EFFECT","effectKey":"field-accumulate","effectName":"结案","enabled":true,
                "ops":[{"opSeq":7,"targetTable":"PUR_PURCHASE_M","targetField":"FINISHED_PERSON","opCode":"SET_WHEN",
                        "sourceScope":"CONSTANT","sourceConstant":""}]}]}
            """));

        var plan = new EffectPlanLoader().Load(definition);

        var op = Assert.Single(Assert.Single(plan.Actions).Ops!);
        Assert.Equal("CONSTANT", op.Source.Scope);
        Assert.Equal(string.Empty, op.Source.Constant);
    }

    [Fact]
    public void 常量来源缺少取值时保持未设置()
    {
        var definition = Definition(Json("""
            {"ModuleId":1607,"Title":"收料单","MasterTable":"PUR_RECEIVE_M","DetailTable":"PUR_RECEIVE_D",
             "MasterPkOrder":["RECEIVE_TYPE","RECEIVE_NO"],
             "BusinessActions":[
               {"seq":5,"eventCode":"APPROVE_EFFECT","effectKey":"field-accumulate","effectName":"结案","enabled":true,
                "ops":[{"opSeq":7,"targetTable":"PUR_PURCHASE_M","targetField":"FINISHED_PERSON","opCode":"SET_WHEN",
                        "sourceScope":"CONSTANT"}]}]}
            """));

        // 属性缺失才是"未设置"：加载后为 null，执行期由执行器 fail-closed 拒绝。
        var plan = new EffectPlanLoader().Load(definition);

        var op = Assert.Single(Assert.Single(plan.Actions).Ops!);
        Assert.Null(op.Source.Constant);
    }
}
