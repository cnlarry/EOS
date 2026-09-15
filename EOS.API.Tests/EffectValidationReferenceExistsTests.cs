using System.Text.Json;
using EOS.API.Data.Effects;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// reference-exists 语句构造：断言对象必须是**当前单据**的引用行。脱离单据范围的
/// 全表扫描会让他单的历史坏引用拦下本次保存，这条正是目录校验从"配了不跑"转为
/// 真跑之后最容易踩的坑。
/// </summary>
public sealed class EffectValidationReferenceExistsTests
{
    private static ModuleEffectPlan Plan(
        string masterTable = "PUR_PURCHASE_M",
        string? detailTable = "PUR_PURCHASE_D",
        params string[] masterPkOrder) =>
        new(1606, masterTable, detailTable, "module-1606-v1",
            masterPkOrder.Length > 0 ? masterPkOrder : ["PURCHASE_TYPE", "PURCHASE_NO"],
            Array.Empty<EffectActionPlan>(), Array.Empty<EffectValidationPlan>());

    private static JsonElement Check(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public void 主表引用断言限定在当前单据上()
    {
        var compiled = EffectValidationExecutor.BuildReferenceExistsCheckSql(
            Plan(),
            Check("""
                {"refTable":"SUPPLIER","refKey":{"scope":"MASTER","field":"SUPPLIER_ID"},
                 "activeTag":{"field":"BUSINESS_TAG","expect":0},"message":"厂商编号不存在或已停止交易"}
                """),
            ["PUR", "P-1"]);

        Assert.Contains("FROM dbo.[PUR_PURCHASE_M] M CROSS JOIN dbo.[PUR_PURCHASE_D] D WHERE M.[PURCHASE_TYPE] = @mk0 AND M.[PURCHASE_NO] = @mk1", compiled.Sql);
        Assert.Contains("NOT EXISTS (SELECT 1 FROM dbo.[SUPPLIER] R WITH (NOLOCK) WHERE R.[SUPPLIER_ID] = M.[SUPPLIER_ID] AND R.[BUSINESS_TAG] = 0)", compiled.Sql);
        Assert.Equal(["PUR", "P-1"], compiled.Parameters.Select(parameter => parameter.Value));
        Assert.Equal("厂商编号不存在或已停止交易", compiled.Message);
    }

    [Fact]
    public void 明细引用断言按明细列关联且仍在当前单据内()
    {
        var compiled = EffectValidationExecutor.BuildReferenceExistsCheckSql(
            Plan("PUR_PURCHASE_M", "PUR_PURCHASE_D", "PURCHASE_TYPE", "PURCHASE_NO"),
            Check("""
                {"refTable":"SUPPLIER_PRICE_D","join":[{"target":"PRO_NO","source":{"scope":"DETAIL","field":"PRO_NO"}}],
                 "message":"产品计价资料不存在"}
                """),
            ["PUR", "P-1"]);

        Assert.Contains("R.[PRO_NO] = D.[PRO_NO]", compiled.Sql);
        Assert.Contains("M.[PURCHASE_TYPE] = @mk0 AND M.[PURCHASE_NO] = @mk1", compiled.Sql);
    }

    [Fact]
    public void 允许空值的引用断言把空来源视为通过()
    {
        var compiled = EffectValidationExecutor.BuildReferenceExistsCheckSql(
            Plan(),
            Check("""
                {"refTable":"SUPPLIER","refKey":{"scope":"MASTER","field":"SUPPLIER_ID"},"allowEmpty":true}
                """),
            ["PUR", "P-1"]);

        Assert.Contains("(M.[SUPPLIER_ID] IS NULL OR M.[SUPPLIER_ID] = '' OR R.[SUPPLIER_ID] = M.[SUPPLIER_ID])", compiled.Sql);
        Assert.Equal("引用数据不存在。", compiled.Message);
    }

    [Fact]
    public void 缺少单据主键上下文时fail_closed()
    {
        var exception = Assert.Throws<EffectConfigException>(() =>
            EffectValidationExecutor.BuildReferenceExistsCheckSql(
                Plan(),
                Check("""{"refTable":"SUPPLIER","refKey":{"scope":"MASTER","field":"SUPPLIER_ID"}}"""),
                Array.Empty<string>()));

        Assert.Contains("单据主键上下文", exception.Message);
    }

    [Fact]
    public void 缺少refTable或refKey时fail_closed()
    {
        Assert.Throws<EffectConfigException>(() =>
            EffectValidationExecutor.BuildReferenceExistsCheckSql(
                Plan(), Check("""{"refKey":{"scope":"MASTER","field":"SUPPLIER_ID"}}"""), ["PUR", "P-1"]));

        Assert.Throws<EffectConfigException>(() =>
            EffectValidationExecutor.BuildReferenceExistsCheckSql(
                Plan(), Check("""{"refTable":"SUPPLIER"}"""), ["PUR", "P-1"]));
    }
}
