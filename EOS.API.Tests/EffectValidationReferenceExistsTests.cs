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
    public void 主表引用断言限定在当前单据上且不牵入明细()
    {
        var compiled = EffectValidationExecutor.BuildReferenceExistsCheckSql(
            Plan(),
            Check("""
                {"refTable":"SUPPLIER","refKey":{"scope":"MASTER","field":"SUPPLIER_ID"},
                 "activeTag":{"field":"BUSINESS_TAG","expect":0},"message":"厂商编号不存在或已停止交易"}
                """),
            ["PUR", "P-1"]);

        // 主表级断言只看单据头：牵入明细会让"无明细的单据"整项跳过校验。
        Assert.Contains("FROM dbo.[PUR_PURCHASE_M] M WHERE M.[PURCHASE_TYPE] = @mk0 AND M.[PURCHASE_NO] = @mk1", compiled.Sql);
        Assert.DoesNotContain("CROSS JOIN", compiled.Sql);
        Assert.Contains("NOT EXISTS (SELECT 1 FROM dbo.[SUPPLIER] R WITH (NOLOCK) WHERE R.[SUPPLIER_ID] = M.[SUPPLIER_ID] AND R.[BUSINESS_TAG] = 0)", compiled.Sql);
        Assert.Equal(["PUR", "P-1"], compiled.Parameters.Select(parameter => parameter.Value));
        Assert.Equal("厂商编号不存在或已停止交易", compiled.Message);
        Assert.Null(compiled.LineSql);
    }

    [Fact]
    public void 明细引用断言按明细列关联且明细侧同样限定在当前单据内()
    {
        var compiled = EffectValidationExecutor.BuildReferenceExistsCheckSql(
            Plan("PUR_PURCHASE_M", "PUR_PURCHASE_D", "PURCHASE_TYPE", "PURCHASE_NO"),
            Check("""
                {"refTable":"SUPPLIER_PRICE_D","join":[{"target":"PRO_NO","source":{"scope":"DETAIL","field":"PRO_NO"}}],
                 "message":"产品计价资料不存在"}
                """),
            ["PUR", "P-1"]);

        Assert.Contains("R.[PRO_NO] = D.[PRO_NO]", compiled.Sql);
        Assert.Contains("CROSS JOIN dbo.[PUR_PURCHASE_D] D", compiled.Sql);
        // 明细表按约定携带主表主键列；两侧作用域共用同一批参数，不重复登记。
        Assert.Contains("M.[PURCHASE_TYPE] = @mk0 AND M.[PURCHASE_NO] = @mk1 AND D.[PURCHASE_TYPE] = @mk0 AND D.[PURCHASE_NO] = @mk1", compiled.Sql);
        Assert.Equal(["PUR", "P-1"], compiled.Parameters.Select(parameter => parameter.Value));
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

        // 命中集是"缺失行"：来源键为空的行必须被守卫排除，而不是被当成命中。
        Assert.Contains("WHERE NOT (M.[SUPPLIER_ID] IS NULL OR M.[SUPPLIER_ID] = '') AND M.[PURCHASE_TYPE] = @mk0", compiled.Sql);
        Assert.DoesNotContain(") OR M.[PURCHASE_TYPE]", compiled.Sql);
        // 断言未自带文案时返回 null，由调用方回落到规则级 message。
        Assert.Null(compiled.Message);
    }

    [Fact]
    public void 复合键里任一可空列为空即整项放行()
    {
        var compiled = EffectValidationExecutor.BuildReferenceExistsCheckSql(
            Plan("COP_RETURN_M", "COP_RETURN_D", "RETURN_TYPE", "RETURN_NO"),
            Check("""
                {"refTable":"COP_ORDER_M",
                 "join":[{"target":"ORDER_TYPE","source":{"scope":"DETAIL","field":"ORDER_TYPE"}},
                         {"target":"ORDER_NO","source":{"scope":"DETAIL","field":"ORDER_NO"}}],
                 "allowEmpty":[{"scope":"DETAIL","field":"ORDER_TYPE"}],
                 "message":"以下序号项订单不存在 \r\n"}
                """),
            ["R", "R-1"]);

        // 订单别为空的行视为"未引用订单"，不参与存在性断言。
        Assert.Contains("NOT (D.[ORDER_TYPE] IS NULL OR D.[ORDER_TYPE] = '') AND M.[RETURN_TYPE] = @mk0", compiled.Sql);
        Assert.Contains("R.[ORDER_TYPE] = D.[ORDER_TYPE] AND R.[ORDER_NO] = D.[ORDER_NO]", compiled.Sql);
    }

    [Fact]
    public void 缺失行清单按明细行号升序取前maxRows行()
    {
        var compiled = EffectValidationExecutor.BuildReferenceExistsCheckSql(
            Plan("INV_OCCUR_IN_M", "INV_OCCUR_IN_D", "OCCUR_TYPE", "OCCUR_NO"),
            Check("""
                {"refTable":"PRODUCT","refKey":{"scope":"DETAIL","field":"PRO_NO"},
                 "lineField":"SERIAL_NO","maxRows":10,"message":"以下序号项产品编号不存在 \r\n{ROWS}"}
                """),
            ["I", "I-1"]);

        Assert.NotNull(compiled.LineSql);
        Assert.Contains("SELECT TOP (10) D.[SERIAL_NO] FROM dbo.[INV_OCCUR_IN_M] M CROSS JOIN dbo.[INV_OCCUR_IN_D] D", compiled.LineSql);
        Assert.Contains("AND D.[OCCUR_TYPE] = @mk0 AND D.[OCCUR_NO] = @mk1", compiled.LineSql);
        Assert.EndsWith("ORDER BY D.[SERIAL_NO]", compiled.LineSql);
    }

    [Fact]
    public void 多引用目标表按任一存在即通过()
    {
        var compiled = EffectValidationExecutor.BuildReferenceExistsCheckSql(
            Plan("COP_ACCOUNT_M", "COP_ACCOUNT_D", "ACCOUNT_TYPE", "ACCOUNT_NO"),
            Check("""
                {"targets":[
                   {"refTable":"COP_SEND_D","join":[
                      {"target":"SEND_TYPE","source":{"scope":"DETAIL","field":"S_R_TYPE"}},
                      {"target":"SEND_NO","source":{"scope":"DETAIL","field":"S_R_NO"}},
                      {"target":"SERIAL_NO","source":{"scope":"DETAIL","field":"S_R_SERIAL_NO"}}]},
                   {"refTable":"COP_RETURN_D","join":[
                      {"target":"RETURN_TYPE","source":{"scope":"DETAIL","field":"S_R_TYPE"}},
                      {"target":"RETURN_NO","source":{"scope":"DETAIL","field":"S_R_NO"}},
                      {"target":"SERIAL_NO","source":{"scope":"DETAIL","field":"S_R_SERIAL_NO"}}]}],
                 "message":"以下序号项送/退货单不存在 \r\n"}
                """),
            ["A", "A-1"]);

        Assert.Contains("NOT EXISTS (SELECT 1 FROM dbo.[COP_SEND_D] R WITH (NOLOCK) WHERE R.[SEND_TYPE] = D.[S_R_TYPE]", compiled.Sql);
        Assert.Contains("NOT EXISTS (SELECT 1 FROM dbo.[COP_RETURN_D] R WITH (NOLOCK) WHERE R.[RETURN_TYPE] = D.[S_R_TYPE]", compiled.Sql);
    }

    [Fact]
    public void 反向一致性断言用存在且不等表达()
    {
        var compiled = EffectValidationExecutor.BuildReferenceExistsCheckSql(
            Plan("COP_RETURN_M", "COP_RETURN_D", "RETURN_TYPE", "RETURN_NO"),
            Check("""
                {"refTable":"COP_ORDER_M",
                 "join":[{"target":"ORDER_TYPE","source":{"scope":"DETAIL","field":"ORDER_TYPE"}},
                         {"target":"ORDER_NO","source":{"scope":"DETAIL","field":"ORDER_NO"}}],
                 "mismatch":{"target":"CLIENT_ID","source":{"scope":"MASTER","field":"CLIENT_ID"}},
                 "lineField":"SERIAL_NO","message":"以下序号项退货单与订单客户不符 \r\n{ROWS}"}
                """),
            ["R", "R-1"]);

        // 必须 EXISTS + <>：用"NOT EXISTS 相等"会把"订单不存在"误报成"客户不符"。
        Assert.Contains("EXISTS (SELECT 1 FROM dbo.[COP_ORDER_M] R WITH (NOLOCK) WHERE R.[ORDER_TYPE] = D.[ORDER_TYPE] AND R.[ORDER_NO] = D.[ORDER_NO] AND R.[CLIENT_ID] <> M.[CLIENT_ID])", compiled.Sql);
        Assert.DoesNotContain("NOT EXISTS (SELECT 1 FROM dbo.[COP_ORDER_M]", compiled.Sql);
    }

    [Fact]
    public void 明细级断言缺少明细表时fail_closed()
    {
        var exception = Assert.Throws<EffectConfigException>(() =>
            EffectValidationExecutor.BuildReferenceExistsCheckSql(
                Plan("SUPPLIER", null, "SUPPLIER_ID"),
                Check("""{"refTable":"PRODUCT","refKey":{"scope":"DETAIL","field":"PRO_NO"}}"""),
                ["S-1"]));

        Assert.Contains("明细表", exception.Message);
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

    [Fact]
    public void 空的targets数组fail_closed()
    {
        Assert.Throws<EffectConfigException>(() =>
            EffectValidationExecutor.BuildReferenceExistsCheckSql(
                Plan(), Check("""{"targets":[]}"""), ["PUR", "P-1"]));
    }
}
