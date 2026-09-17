using System.Text.Json;
using EOS.API.Data.Effects;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// duplicate-check 语句构造：候选行必须在语句内真正可绑定（主表别名进入 FROM），
/// 且一律限定在当前单据内；过滤条件走闭式条件编译器，诊断列用于回填消息占位符。
/// </summary>
public sealed class EffectValidationDuplicateCheckTests
{
    private static ModuleEffectPlan Plan(
        string masterTable = "MOU_ASSESS_M",
        string? detailTable = null,
        params string[] masterPkOrder) =>
        new(2914, masterTable, detailTable, "module-2914-v1",
            masterPkOrder.Length > 0 ? masterPkOrder : ["ASSESS_TYPE", "ASSESS_NO"],
            Array.Empty<EffectActionPlan>(), Array.Empty<EffectValidationPlan>());

    private static JsonElement Params(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public void Entity_被本单引用的候选行按excludeVia排除()
    {
        var compiled = EffectValidationExecutor.BuildDuplicateCheckSql(
            Plan("COP_ORDER_CHANGE_M", "COP_ORDER_CHANGE_D", "CHANGE_ORDER_TYPE", "CHANGE_ORDER_NO"),
            Params("""
                {"mode":"entity","table":"COP_ORDER_M","keyFields":["CLIENT_ORDER_NO"],
                 "keySource":{"scope":"MASTER","fields":["CLIENT_ORDER_NO"]},
                 "filter":{"logic":"AND","items":[{"type":"blank","field":{"scope":"TARGET","field":"CLIENT_ORDER_NO"},"negate":true}]},
                 "excludeVia":{"table":"COP_ORDER_CHANGE_M",
                   "join":[{"target":"ORDER_TYPE","source":{"scope":"TARGET","field":"ORDER_TYPE"}},
                           {"target":"ORDER_NO","source":{"scope":"TARGET","field":"ORDER_NO"}}]}}
                """),
            ["E2E", "E2ESL01"]);

        // 候选行（同客户订单号的其它订单）中，被本单引用的原单由 excludeVia 子查询排除
        Assert.Contains("X.[CLIENT_ORDER_NO] = M.[CLIENT_ORDER_NO]", compiled.Sql);
        Assert.Contains("NOT EXISTS (SELECT 1 FROM dbo.[COP_ORDER_CHANGE_M] V WITH (NOLOCK) WHERE "
            + "V.[CHANGE_ORDER_TYPE] = @mk0 AND V.[CHANGE_ORDER_NO] = @mk1 "
            + "AND V.[ORDER_TYPE] = X.[ORDER_TYPE] AND V.[ORDER_NO] = X.[ORDER_NO])", compiled.Sql);
        // 空串客户订单号不参与重复判定（filter 的非空守卫）
        Assert.Contains("NOT (NULLIF(LTRIM(RTRIM(X.[CLIENT_ORDER_NO])), '') IS NULL)", compiled.Sql);
        Assert.Equal(["E2E", "E2ESL01"], compiled.Parameters.Select(parameter => parameter.Value).Distinct());
    }

    [Fact]
    public void Entity_候选行与主表别名都进入FROM_键值按参数绑定()
    {
        var compiled = EffectValidationExecutor.BuildDuplicateCheckSql(
            Plan(),
            Params("""
                {"mode":"entity","table":"MOU_ASSESS_M","keyFields":["PRO_NO"],
                 "excludeSelf":{"keyFields":["ASSESS_TYPE","ASSESS_NO"]}}
                """),
            ["OPEN", "A-1"]);

        Assert.Contains("FROM dbo.[MOU_ASSESS_M] M CROSS JOIN dbo.[MOU_ASSESS_M] X WITH (NOLOCK)", compiled.Sql);
        Assert.Contains("M.[ASSESS_TYPE] = @mk0 AND M.[ASSESS_NO] = @mk1", compiled.Sql);
        Assert.Contains("X.[PRO_NO] = M.[PRO_NO]", compiled.Sql);
        Assert.Contains("NOT (X.[ASSESS_TYPE] = @mk0 AND X.[ASSESS_NO] = @mk1)", compiled.Sql);
        Assert.Equal(["@mk0", "@mk1"], compiled.Parameters.Select(parameter => parameter.Name));
        Assert.Equal(["OPEN", "A-1"], compiled.Parameters.Select(parameter => parameter.Value));
        Assert.Equal("1", compiled.Sql["SELECT TOP 1 ".Length..].Split(" FROM")[0]);
    }

    [Fact]
    public void Entity_缺少单据主键上下文时fail_closed()
    {
        var exception = Assert.Throws<EffectConfigException>(() =>
            EffectValidationExecutor.BuildDuplicateCheckSql(
                Plan(),
                Params("""
                    {"mode":"entity","table":"MOU_ASSESS_M","keyFields":["PRO_NO"],
                     "excludeSelf":{"keyFields":["ASSESS_TYPE","ASSESS_NO"]}}
                    """),
                Array.Empty<string>()));

        Assert.Contains("单据主键上下文", exception.Message);
    }

    [Fact]
    public void Entity_自排除列数与单据主键数不一致时fail_closed()
    {
        var exception = Assert.Throws<EffectConfigException>(() =>
            EffectValidationExecutor.BuildDuplicateCheckSql(
                Plan(),
                Params("""
                    {"mode":"entity","table":"MOU_ASSESS_M","keyFields":["PRO_NO"],
                     "excludeSelf":{"keyFields":["ASSESS_TYPE"]}}
                    """),
                ["OPEN", "A-1"]));

        Assert.Contains("与单据主键数量", exception.Message);
    }

    [Fact]
    public void Entity_明细来源域进入FROM并按明细表限定单据()
    {
        var compiled = EffectValidationExecutor.BuildDuplicateCheckSql(
            Plan("HR_PLAN_M", "HR_PLAN_D", "PLAN_TYPE", "PLAN_NO"),
            Params("""
                {"mode":"entity","table":"HR_PLAN_M","keyFields":["EMP_ID"],
                 "keySource":{"scope":"DETAIL","fields":["EMP_ID"]},
                 "excludeSelf":{"keyFields":["PLAN_TYPE","PLAN_NO"]}}
                """),
            ["D", "P-1"]);

        Assert.Contains("FROM dbo.[HR_PLAN_M] M CROSS JOIN dbo.[HR_PLAN_D] D CROSS JOIN dbo.[HR_PLAN_M] X WITH (NOLOCK)", compiled.Sql);
        Assert.Contains("X.[EMP_ID] = D.[EMP_ID]", compiled.Sql);
    }

    [Fact]
    public void Entity_来源域不是MASTER或DETAIL时fail_closed()
    {
        var exception = Assert.Throws<EffectConfigException>(() =>
            EffectValidationExecutor.BuildDuplicateCheckSql(
                Plan(),
                Params("""
                    {"mode":"entity","table":"MOU_ASSESS_M","keyFields":["PRO_NO"],
                     "keySource":{"scope":"TABLE","fields":["PRO_NO"]}}
                    """),
                ["OPEN", "A-1"]));

        Assert.Contains("仅允许 MASTER/DETAIL", exception.Message);
    }

    [Fact]
    public void Entity_filter编译成参数化谓词并作用于候选行()
    {
        var compiled = EffectValidationExecutor.BuildDuplicateCheckSql(
            Plan("HR_EMPLOYEE", null, "EMP_ID"),
            Params("""
                {"mode":"entity","table":"HR_EMPLOYEE","keyFields":["EMP_NO"],
                 "excludeSelf":{"keyFields":["EMP_ID"]},
                 "filter":{"logic":"AND","items":[
                   {"type":"VALUE-NEQ","field":{"scope":"TARGET","field":"STATE"},"value":5,"nullAsMatch":true}]}}
                """),
            ["E-1"]);

        Assert.Contains("X.[STATE] <> @cp0 OR X.[STATE] IS NULL", compiled.Sql);
        Assert.Contains(compiled.Parameters, parameter => parameter.Name == "@cp0" && Convert.ToDouble(parameter.Value) == 5d);
        Assert.DoesNotContain("STATE <> 5", compiled.Sql);
    }

    [Fact]
    public void Entity_filter来源域不可用时fail_closed()
    {
        var exception = Assert.Throws<EffectConfigException>(() =>
            EffectValidationExecutor.BuildDuplicateCheckSql(
                Plan(),
                Params("""
                    {"mode":"entity","table":"MOU_ASSESS_M","keyFields":["PRO_NO"],
                     "excludeSelf":{"keyFields":["ASSESS_TYPE","ASSESS_NO"]},
                     "filter":{"logic":"AND","items":[
                       {"type":"VALUE-EQ","field":{"scope":"DETAIL","field":"STATE"},"value":1}]}}
                    """),
                ["OPEN", "A-1"]));

        Assert.Contains("DETAIL 不可用", exception.Message);
    }

    [Fact]
    public void Entity_诊断列进入选择列表并按占位符回填消息()
    {
        var compiled = EffectValidationExecutor.BuildDuplicateCheckSql(
            Plan("HR_EMPLOYEE", null, "EMP_ID"),
            Params("""
                {"mode":"entity","table":"HR_EMPLOYEE","keyFields":["EMP_NO"],
                 "excludeSelf":{"keyFields":["EMP_ID"]},
                 "diagnostics":["EMP_NO","EMP_NAME"]}
                """),
            ["E-1"]);

        Assert.StartsWith("SELECT TOP 1 X.[EMP_NO], X.[EMP_NAME] FROM dbo.[HR_EMPLOYEE] M", compiled.Sql);
        Assert.Equal(["EMP_NO", "EMP_NAME"], compiled.Diagnostics);

        var message = EffectValidationExecutor.RenderDiagnosticMessage(
            "员工工号：{EMP_NO} 已分配给：{emp_name}", compiled.Diagnostics, ["A001", " 张三 "]);
        Assert.Equal("员工工号：A001 已分配给：张三", message);
    }

    [Fact]
    public void Entity_目标表与主表同名却无自排除时fail_closed()
    {
        var exception = Assert.Throws<EffectConfigException>(() =>
            EffectValidationExecutor.BuildDuplicateCheckSql(
                Plan(),
                Params("""{"mode":"entity","table":"MOU_ASSESS_M","keyFields":["PRO_NO"]}"""),
                ["OPEN", "A-1"]));

        Assert.Contains("必须声明 excludeSelf.keyFields", exception.Message);
    }

    [Fact]
    public void Entity_目标表不是主表时无需自排除()
    {
        var compiled = EffectValidationExecutor.BuildDuplicateCheckSql(
            Plan("HR_WAGE_M", "HR_WAGE_D", "WAGE_TYPE", "WAGE_NO"),
            Params("""{"mode":"entity","table":"HR_EMPLOYEE","keyFields":["EMP_ID"]}"""),
            ["W", "W-1"]);

        Assert.Contains("CROSS JOIN dbo.[HR_EMPLOYEE] X WITH (NOLOCK)", compiled.Sql);
        Assert.DoesNotContain("NOT (", compiled.Sql);
    }

    [Fact]
    public void 消息未配置时回落到默认文案()
    {
        Assert.Equal(
            "数据重复。",
            EffectValidationExecutor.RenderDiagnosticMessage("  ", ["EMP_NO"], ["A001"]));
    }

    [Fact]
    public void WithinDoc_限定在当前单据的分组内()
    {
        var compiled = EffectValidationExecutor.BuildDuplicateCheckSql(
            Plan("HR_PLAN_M", "HR_PLAN_D", "PLAN_TYPE", "PLAN_NO"),
            Params("""{"mode":"within-doc","keyFields":["EMP_ID"]}"""),
            ["D", "P-1"]);

        Assert.Contains(
            "FROM dbo.[HR_PLAN_D] D WHERE D.[PLAN_TYPE] = @mk0 AND D.[PLAN_NO] = @mk1 GROUP BY D.[EMP_ID] HAVING COUNT(*) > 1",
            compiled.Sql);
    }

    [Fact]
    public void WithinDoc_无明细表时fail_closed()
    {
        var exception = Assert.Throws<EffectConfigException>(() =>
            EffectValidationExecutor.BuildDuplicateCheckSql(
                Plan(),
                Params("""{"mode":"within-doc","keyFields":["EMP_ID"]}"""),
                ["OPEN", "A-1"]));

        Assert.Contains("需要明细表", exception.Message);
    }

    [Fact]
    public void 非法标识符fail_closed()
    {
        var exception = Assert.Throws<EffectConfigException>(() =>
            EffectValidationExecutor.BuildDuplicateCheckSql(
                Plan("HR_EMPLOYEE", null, "EMP_ID"),
                Params("""
                    {"mode":"entity","table":"HR_EMPLOYEE","keyFields":["EMP_NO; DROP TABLE dbo.X"],
                     "excludeSelf":{"keyFields":["EMP_ID"]}}
                    """),
                ["E-1"]));

        Assert.Contains("标识符非法", exception.Message);
    }
}
