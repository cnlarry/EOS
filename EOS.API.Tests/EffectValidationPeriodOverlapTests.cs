using System.Data;
using System.Text.Json;
using EOS.API.Data.Effects;
using Microsoft.Data.SqlClient;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// period-overlap（跨单期间重叠）语句构造与真库行为：
/// 闭区间相交（端点相接算重叠）、结束日为空视为无限期、同维度键才相比、
/// 排除当前单据自身、诊断按"人员+冲突单据+冲突期间"多行回报。
/// </summary>
public sealed class EffectValidationPeriodOverlapTests
{
    private static JsonElement Params(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private const string ContractParams = """
        {"detailTable":"HR_CONTRACT_D",
         "rangeFields":{"begin":"BEGIN_DATE","end":"END_DATE"},
         "scopeFields":["CONT_TYPE","CONT_NO"],
         "groupFields":["EMP_ID"],
         "displayLookup":{"table":"HR_EMPLOYEE","linkField":"EMP_ID","displayField":"EMP_NAME"},
         "diagnosticFields":["@display","CONT_NO","BEGIN_DATE","END_DATE"],
         "maxRows":10}
        """;

    [Fact]
    public void 语句按闭区间相交并排除本单()
    {
        var compiled = EffectValidationExecutor.BuildPeriodOverlapSql(Params(ContractParams), ["D", "P-1"]);

        Assert.Contains("FROM dbo.[HR_CONTRACT_D] d JOIN dbo.[HR_CONTRACT_D] x ON x.[EMP_ID] = d.[EMP_ID]", compiled.Sql);
        Assert.Contains("NOT (x.[CONT_TYPE] = @mk0 AND x.[CONT_NO] = @mk1)", compiled.Sql);
        Assert.Contains("(d.[BEGIN_DATE] <= x.[END_DATE] OR x.[END_DATE] IS NULL)", compiled.Sql);
        Assert.Contains("(x.[BEGIN_DATE] <= d.[END_DATE] OR d.[END_DATE] IS NULL)", compiled.Sql);
        Assert.Contains("LEFT JOIN dbo.[HR_EMPLOYEE] dp ON dp.[EMP_ID] = x.[EMP_ID]", compiled.Sql);
        Assert.Contains("SELECT TOP 10 dp.[EMP_NAME], x.[CONT_NO], x.[BEGIN_DATE], x.[END_DATE]", compiled.Sql);
        Assert.Contains("WHERE d.[CONT_TYPE] = @mk0 AND d.[CONT_NO] = @mk1 AND d.[BEGIN_DATE] IS NOT NULL", compiled.Sql);
        Assert.Contains("GROUP BY dp.[EMP_NAME], x.[CONT_NO], x.[BEGIN_DATE], x.[END_DATE]", compiled.Sql);
        Assert.Equal(["@mk0", "@mk1"], compiled.Parameters.Select(parameter => parameter.Name));
    }

    [Fact]
    public void 无诊断列时按存在性判断()
    {
        var compiled = EffectValidationExecutor.BuildPeriodOverlapSql(
            Params("""
                {"detailTable":"HR_SAFE_D","rangeFields":{"begin":"BEGIN_DATE","end":"END_DATE"},
                 "scopeFields":["SAFE_TYPE","SAFE_NO"],"groupFields":["EMP_ID","SAFE_ID"]}
                """),
            ["S", "N-1"]);

        Assert.Contains("SELECT TOP 10 1 FROM dbo.[HR_SAFE_D] d", compiled.Sql);
        Assert.Contains("x.[SAFE_ID] = d.[SAFE_ID]", compiled.Sql);
        Assert.Empty(compiled.Diagnostics);
    }

    [Fact]
    public void 单据主键数量不一致时fail_closed()
    {
        var exception = Assert.Throws<EffectConfigException>(() =>
            EffectValidationExecutor.BuildPeriodOverlapSql(Params(ContractParams), ["D"]));
        Assert.Contains("与单据主键数量", exception.Message);
    }

    [Fact]
    public void 缺少单据主键上下文时fail_closed()
    {
        var exception = Assert.Throws<EffectConfigException>(() =>
            EffectValidationExecutor.BuildPeriodOverlapSql(Params(ContractParams), Array.Empty<string>()));
        Assert.Contains("缺少单据主键上下文", exception.Message);
    }

    [Fact]
    public void 使用display诊断但缺displayLookup时fail_closed()
    {
        var exception = Assert.Throws<EffectConfigException>(() =>
            EffectValidationExecutor.BuildPeriodOverlapSql(
                Params("""
                    {"detailTable":"HR_CERTIFY_D","rangeFields":{"begin":"BEGIN_DATE","end":"END_DATE"},
                     "scopeFields":["CERTIFY_TYPE","CERTIFY_NO"],"groupFields":["EMP_ID"],
                     "diagnosticFields":["@display"]}
                    """),
                ["C", "N-1"]));
        Assert.Contains("@display 时必须提供 displayLookup", exception.Message);
    }

    [Fact]
    public void 同单重复用within_doc并可回报人员名()
    {
        var plan = new ModuleEffectPlan(
            180106, "HR_CONTRACT_M", "HR_CONTRACT_D", "test", ["CONT_TYPE", "CONT_NO"],
            Array.Empty<EffectActionPlan>(), Array.Empty<EffectValidationPlan>());

        var compiled = EffectValidationExecutor.BuildDuplicateCheckSql(
            plan,
            Params("""
                {"mode":"within-doc","keyFields":["EMP_ID"],
                 "displayLookup":{"table":"HR_EMPLOYEE","linkField":"EMP_ID","displayField":"EMP_NAME"},
                 "diagnosticFields":["@display","EMP_ID"]}
                """),
            ["D", "P-1"]);

        Assert.True(compiled.MultiRow);
        Assert.Contains("SELECT TOP 10 dp.[EMP_NAME], D.[EMP_ID]", compiled.Sql);
        Assert.Contains("LEFT JOIN dbo.[HR_EMPLOYEE] dp ON dp.[EMP_ID] = D.[EMP_ID]", compiled.Sql);
        Assert.Contains("GROUP BY D.[EMP_ID], dp.[EMP_NAME], D.[EMP_ID] HAVING COUNT(*) > 1", compiled.Sql);
        Assert.Equal(["@display", "EMP_ID"], compiled.Diagnostics);
    }
}
