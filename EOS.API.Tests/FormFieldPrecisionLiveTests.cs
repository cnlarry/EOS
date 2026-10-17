using EOS.API.Data;
using Microsoft.Data.SqlClient;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 表单字段的精度/小数位必须来自**列的实际声明**（`sys.columns.precision/scale`），
/// 而不是类型上限（`sys.types.precision/scale`）。
///
/// 这个区分不是细节：`sys.types` 里内置 `decimal` 恒为 `(38,38)`（那是类型自身能表达的上限），
/// 取它等于告诉前端"每一列都是 `decimal(38,38)`"，于是 `RecordPayloadValidator` 的
/// `decimal.Round(value, 38)` 直接抛 `ArgumentOutOfRangeException`（只接受 0–28）——
/// 所有金额/数量字段一保存就 400，报的还是 .NET 异常文案。整条超精度校验因此形同虚设。
///
/// 本用例盯的是"读哪张表"，期望值来自 ADR-038 的五族标准类型：
/// <c>AMOUNT</c> = `decimal(18,4)`、<c>QTY</c> = `decimal(18,6)`。
/// </summary>
[Trait("Category", "Integration")]
[Collection("live-database")]
public sealed class FormFieldPrecisionLiveTests
{
    private static string RequireConnection()
    {
        var value = Environment.GetEnvironmentVariable("MSSQL_ERP_CONN");
        Assert.False(string.IsNullOrWhiteSpace(value),
            "真库集成测试需要 MSSQL_ERP_CONN；未配置即失败（无连接跳过≠已验证）。");
        return value!;
    }

    [Fact]
    public async Task DecimalColumns_ReportColumnPrecisionAndScale()
    {
        await using var connection = new SqlConnection(RequireConnection());
        await connection.OpenAsync();

        // ACC_DAILY_D 的两列分别属于 AMOUNT 族与 QTY 族，是 ADR-038 迁移后的标准类型
        var rows = await WorkbenchDefinitionBuilder.ReadFormFieldRows(
            connection, "ACC_DAILY_D", "ACC_DAILY_D", CancellationToken.None);

        var amount = rows.Single(row => row.Key == "AMOUNT");
        Assert.Equal(18, amount.TypePrecision);
        Assert.Equal(4, amount.TypeScale);

        var quantity = rows.Single(row => row.Key == "QTY");
        Assert.Equal(18, quantity.TypePrecision);
        Assert.Equal(6, quantity.TypeScale);
    }
}
