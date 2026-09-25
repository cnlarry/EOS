using System.Data;
using Microsoft.Data.SqlClient;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 月结明细（模块 1304 的子表）的字段元数据口径的真库用例（只读断言）。
///
/// 判据两条：
///   ⒜ **派生列只读**——数量与单价是生成快照算出来的时间点结存与移动加权单价；
///      允许人手改，快照就不再是"那一期的期末值"，而只是"某人写下的一个数"，报表期初会随之漂移。
///   ⒝ **维度列可见**——库位与批次是快照的维度键（按参数展开时行数由它们决定），
///      看不见就无法判断这张快照是按哪几个维度生成的；它们不是派生值，仍可人工指定。
///
/// 判别性：把 QTY / PRICE 的 `IS_READONLY` 置回 0 ⇒ 本用例第一条断言即红。
/// </summary>
[Collection("live-database")]
public sealed class MonthCloseFieldMetadataLiveTests
{
    private static readonly string ConnectionString =
        Environment.GetEnvironmentVariable("EOS_ERP_TEST_CONNECTION")
        ?? throw new InvalidOperationException(
            "真库集成测试需要 EOS_ERP_TEST_CONNECTION；未配置即失败（无连接跳过≠已验证）。");

    private const string DetailTable = "INV_PRO_MONTH_D";

    [Theory]
    [InlineData("QTY")]
    [InlineData("PRICE")]
    public async Task 月结明细的派生列必须只读(string field)
    {
        await using var connection = await OpenAsync();
        var metadata = await ReadFieldAsync(connection, field);

        Assert.True(metadata.Exists, $"{DetailTable}.{field} 缺少字段元数据行。");
        Assert.True(metadata.IsVisible, $"{DetailTable}.{field} 不可见：派生列要看得见但改不了。");
        Assert.True(metadata.IsReadonly, $"{DetailTable}.{field} 可人工修改：它是生成快照算出来的派生值，必须置只读。");
    }

    [Theory]
    [InlineData("LOCATION_NO")]
    [InlineData("BATCH_NO")]
    public async Task 月结明细的维度列必须可见且仍可指定(string field)
    {
        await using var connection = await OpenAsync();
        var metadata = await ReadFieldAsync(connection, field);

        Assert.True(metadata.Exists, $"{DetailTable}.{field} 缺少字段元数据行。");
        Assert.True(metadata.IsVisible, $"{DetailTable}.{field} 不可见：快照的粒度由它决定，看不见就判断不出粒度。");
        Assert.False(metadata.IsReadonly, $"{DetailTable}.{field} 被置为只读：补录时仍需要人工指定该维度。");
    }

    private static async Task<SqlConnection> OpenAsync()
    {
        var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        return connection;
    }

    private sealed record FieldMetadata(bool Exists, bool IsVisible, bool IsReadonly);

    private static async Task<FieldMetadata> ReadFieldAsync(SqlConnection connection, string field)
    {
        await using var command = new SqlCommand("""
            SELECT ISNULL(IS_VISIBLE, 0), ISNULL(IS_READONLY, 0)
              FROM dbo.FIELDS WHERE RTRIM(T_ID) = @table AND RTRIM(F_ID) = @field;
            """, connection);
        command.Parameters.Add("@table", SqlDbType.NVarChar, 60).Value = DetailTable;
        command.Parameters.Add("@field", SqlDbType.NVarChar, 60).Value = field;

        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return new FieldMetadata(false, false, false);
        return new FieldMetadata(
            true,
            Convert.ToInt32(reader.GetValue(0)) == 1,
            Convert.ToInt32(reader.GetValue(1)) == 1);
    }
}
