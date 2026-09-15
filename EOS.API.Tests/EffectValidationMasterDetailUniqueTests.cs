using System.Text.Json;
using EOS.API.Data.Effects;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// duplicate-check 的 master-detail 形态（主从跨单复合唯一）：语句必须以当前单据为锚
/// 比较同维度的其它单据，按明细分组键判重，并把诊断列按组聚合成多行供 {ROWS} 回填。
/// </summary>
public sealed class EffectValidationMasterDetailUniqueTests
{
    private static ModuleEffectPlan Plan(
        string masterTable = "HR_PLAN_M",
        string? detailTable = "HR_PLAN_D",
        params string[] masterPkOrder) =>
        new(180211, masterTable, detailTable, "module-180211-v1",
            masterPkOrder.Length > 0 ? masterPkOrder : ["PLAN_TYPE", "PLAN_NO"],
            Array.Empty<EffectActionPlan>(), Array.Empty<EffectValidationPlan>());

    private static JsonElement Params(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public void 按主表维度与明细分组键跨单判重()
    {
        var compiled = EffectValidationExecutor.BuildDuplicateCheckSql(
            Plan(),
            Params("""
                {"mode":"master-detail","masterTable":"HR_PLAN_M","detailTable":"HR_PLAN_D",
                 "joinFields":{"master":["PLAN_TYPE","PLAN_NO"],"detail":["PLAN_TYPE","PLAN_NO"]},
                 "masterGroupFields":["COUNT_MONTH"],"groupFields":["EMP_ID"],
                 "documentDetailFields":["PLAN_TYPE","PLAN_NO"],"diagnosticFields":["SERIAL_NO"]}
                """),
            ["D", "P-1"]);

        Assert.True(compiled.MultiRow);
        Assert.Contains("FROM dbo.[HR_PLAN_M] cur CROSS JOIN dbo.[HR_PLAN_M] m INNER JOIN dbo.[HR_PLAN_D] d ON d.[PLAN_TYPE] = m.[PLAN_TYPE] AND d.[PLAN_NO] = m.[PLAN_NO]", compiled.Sql);
        Assert.Contains("cur.[PLAN_TYPE] = @mk0 AND cur.[PLAN_NO] = @mk1", compiled.Sql);
        Assert.Contains("m.[COUNT_MONTH] = cur.[COUNT_MONTH]", compiled.Sql);
        Assert.Contains("EXISTS (SELECT 1 FROM dbo.[HR_PLAN_D] x WHERE x.[PLAN_TYPE] = @mk0 AND x.[PLAN_NO] = @mk1 AND x.[EMP_ID] = d.[EMP_ID])", compiled.Sql);
        Assert.Contains("SELECT TOP 10 MAX(d.[SERIAL_NO])", compiled.Sql);
        Assert.Contains("GROUP BY d.[EMP_ID] HAVING COUNT(*) > 1", compiled.Sql);
        Assert.Equal(["SERIAL_NO"], compiled.Diagnostics);
    }

    [Fact]
    public void 整月扫描形态不追加本单限定()
    {
        var compiled = EffectValidationExecutor.BuildDuplicateCheckSql(
            Plan("HR_WAGE_M", "HR_WAGE_D", "WAGE_TYPE", "WAGE_NO"),
            Params("""
                {"mode":"master-detail","masterTable":"HR_WAGE_M","detailTable":"HR_WAGE_D",
                 "joinFields":{"master":["WAGE_TYPE","WAGE_NO"],"detail":["WAGE_TYPE","WAGE_NO"]},
                 "masterGroupFields":["COUNT_MONTH"],"groupFields":["EMP_ID"],
                 "diagnosticFields":["EMP_ID"]}
                """),
            ["W", "W-1"]);

        Assert.DoesNotContain("EXISTS (", compiled.Sql);
        Assert.Contains("MAX(d.[EMP_ID])", compiled.Sql);
    }

    [Fact]
    public void 无诊断列时按静态消息()
    {
        var compiled = EffectValidationExecutor.BuildDuplicateCheckSql(
            Plan("HR_ENACTMENT_M", "HR_ENACTMENT_D", "ENACTMENT_TYPE", "ENACTMENT_NO"),
            Params("""
                {"mode":"master-detail","masterTable":"HR_ENACTMENT_M","detailTable":"HR_ENACTMENT_D",
                 "joinFields":{"master":["ENACTMENT_TYPE","ENACTMENT_NO"],"detail":["ENACTMENT_TYPE","ENACTMENT_NO"]},
                 "masterGroupFields":["COUNT_MONTH"],"groupFields":["EMP_ID"]}
                """),
            ["E", "E-1"]);

        Assert.Empty(compiled.Diagnostics);
        Assert.Contains("SELECT TOP 10 1 FROM", compiled.Sql);
    }

    [Fact]
    public void maxRows越界被夹紧()
    {
        var compiled = EffectValidationExecutor.BuildDuplicateCheckSql(
            Plan(),
            Params("""
                {"mode":"master-detail","masterTable":"HR_PLAN_M","detailTable":"HR_PLAN_D",
                 "joinFields":{"master":["PLAN_TYPE"],"detail":["PLAN_TYPE"]},
                 "masterGroupFields":["COUNT_MONTH"],"groupFields":["EMP_ID"],"maxRows":5000}
                """),
            ["D", "P-1"]);

        Assert.Contains("SELECT TOP 100 1 FROM", compiled.Sql);
    }

    [Fact]
    public void 本单限定列数与单据主键不符时fail_closed()
    {
        var exception = Assert.Throws<EffectConfigException>(() =>
            EffectValidationExecutor.BuildDuplicateCheckSql(
                Plan(),
                Params("""
                    {"mode":"master-detail","masterTable":"HR_PLAN_M","detailTable":"HR_PLAN_D",
                     "joinFields":{"master":["PLAN_TYPE"],"detail":["PLAN_TYPE"]},
                     "masterGroupFields":["COUNT_MONTH"],"groupFields":["EMP_ID"],
                     "documentDetailFields":["PLAN_TYPE"]}
                    """),
                ["D", "P-1", "X"]));

        Assert.Contains("与单据主键数量", exception.Message);
    }

    [Fact]
    public void 缺少关联列时fail_closed()
    {
        var exception = Assert.Throws<EffectConfigException>(() =>
            EffectValidationExecutor.BuildDuplicateCheckSql(
                Plan(),
                Params("""
                    {"mode":"master-detail","masterTable":"HR_PLAN_M","detailTable":"HR_PLAN_D",
                     "masterGroupFields":["COUNT_MONTH"],"groupFields":["EMP_ID"]}
                    """),
                ["D", "P-1"]));

        Assert.Contains("缺少 joinFields", exception.Message);
    }

    [Fact]
    public void 消息未配置时回落到默认文案并支持ROWS占位()
    {
        var compiled = EffectValidationExecutor.BuildDuplicateCheckSql(
            Plan(),
            Params("""
                {"mode":"master-detail","masterTable":"HR_PLAN_M","detailTable":"HR_PLAN_D",
                 "joinFields":{"master":["PLAN_TYPE"],"detail":["PLAN_TYPE"]},
                 "masterGroupFields":["COUNT_MONTH"],"groupFields":["EMP_ID"],"diagnosticFields":["SERIAL_NO"]}
                """),
            ["D", "P-1"]);

        Assert.True(compiled.MultiRow);
        Assert.Equal(["SERIAL_NO"], compiled.Diagnostics);
    }

    [Fact]
    public void Blank算子按去空格判空并支持取反()
    {
        var compiler = new EffectConditionCompiler();
        var blank = compiler.Compile(
            JsonDocument.Parse("""
                {"logic":"AND","items":[{"type":"BLANK","field":{"scope":"MASTER","field":"COUNT_MONTH"}}]}
                """).RootElement.Clone(),
            (scope, _) => scope.Equals("MASTER", StringComparison.OrdinalIgnoreCase) ? "M" : null,
            _ => true);

        Assert.Contains("NULLIF(LTRIM(RTRIM(M.[COUNT_MONTH])), '') IS NULL", blank.Sql);

        var notBlank = compiler.Compile(
            JsonDocument.Parse("""
                {"logic":"AND","items":[{"type":"BLANK","field":{"scope":"MASTER","field":"EMP_NO"},"negate":true}]}
                """).RootElement.Clone(),
            (scope, _) => scope.Equals("MASTER", StringComparison.OrdinalIgnoreCase) ? "M" : null,
            _ => true);

        Assert.Contains("NOT (NULLIF(LTRIM(RTRIM(M.[EMP_NO])), '') IS NULL)", notBlank.Sql);
    }
}
