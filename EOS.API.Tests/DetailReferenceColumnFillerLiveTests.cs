using EOS.API.Data;
using EOS.API.Data.Workbench;
using EOS.API.Models;
using Microsoft.Data.SqlClient;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 明细侧「引用带出」真库验收：应付货款单明细的对账单价由被引用的收料明细行带出。
///
/// 走真库的理由：来源行与取值都来自真实业务表，"有引用但查不到来源必须拒绝"这条只有连真库
/// 才验得出来（替身会把 fail-closed 验成纸面断言）。用例只读，不改库内数据。
/// </summary>
[Trait("Category", "Integration")]
[Collection("live-database")]
public class DetailReferenceColumnFillerLiveTests
{
    private static readonly string ConnectionString =
        Environment.GetEnvironmentVariable("MSSQL_ERP_CONN")
        ?? throw new InvalidOperationException("真库集成测试需要 MSSQL_ERP_CONN；未配置即失败（无连接跳过≠已验证）。");

    private static FormFieldDefinition Field(string key, string dataType) =>
        new(key, key, dataType, 100, null, false, null, null, null, true, true, false, false, null,
            [], false, false, false, false, false, false, null);

    private static Dictionary<string, object?> Row(params (string Key, object? Value)[] items)
    {
        var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in items) row[key] = value;
        return row;
    }

    private static async Task<SqlConnection> OpenAsync()
    {
        var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        return connection;
    }

    private static readonly IReadOnlyList<FormFieldDefinition> DueFields = [Field("PRICE", "float")];

    /// <summary>带出值必须与来源行的值逐值一致（来源是真库数据，不写死数值）。</summary>
    [Fact]
    public async Task Price_IsCarriedFromReferencedReceiveLine()
    {
        await using var connection = await OpenAsync();
        // 取一条真实的、引用了收料明细的应付行作为参照
        await using var lookup = new SqlCommand("""
            SELECT TOP 1 LTRIM(RTRIM(RECEIVE_TYPE)), LTRIM(RTRIM(RECEIVE_NO)), RECEIVE_SERIAL_NO, PRICE
              FROM dbo.PUR_DUE_D WITH (NOLOCK)
             WHERE PRICE IS NOT NULL AND LTRIM(RTRIM(ISNULL(RECEIVE_NO,''))) <> '' AND ISNULL(RECEIVE_SERIAL_NO,0) > 0
             ORDER BY SERIAL_NO;
            """, connection);
        await using var reader = await lookup.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync(), "真库里没有可用的应付货款单明细行（需要一行带收料引用的数据）。");
        var receiveType = reader.GetString(0);
        var receiveNo = reader.GetString(1);
        // RECEIVE_SERIAL_NO 是 smallint：按 Int32 直取会抛 InvalidCast——统一走 Convert 转换
        var receiveSerial = Convert.ToInt32(reader.GetValue(2));
        var expected = reader.GetValue(3);
        await reader.CloseAsync();

        var row = Row(("RECEIVE_TYPE", receiveType), ("RECEIVE_NO", receiveNo), ("RECEIVE_SERIAL_NO", receiveSerial));
        var error = await DetailReferenceColumnFiller.FillAsync(
            connection, null, "PUR_DUE_D", DueFields, [row], CancellationToken.None);

        Assert.Null(error);
        Assert.Equal(expected, row["PRICE"]);
    }

    /// <summary>送货单（1406）的单价由被引用的客户订单明细带出（旧过程 P_WF_COP_SEND 同款关联键）。</summary>
    [Fact]
    public async Task SendPrice_IsCarriedFromReferencedOrderLine()
    {
        await using var connection = await OpenAsync();
        await using var lookup = new SqlCommand("""
            SELECT TOP 1 LTRIM(RTRIM(ORDER_NO)), SERIAL_NO, PRICE
              FROM dbo.COP_ORDER_D WITH (NOLOCK)
             WHERE PRICE IS NOT NULL AND LTRIM(RTRIM(ISNULL(ORDER_NO,''))) <> '' AND ISNULL(SERIAL_NO,0) > 0
             ORDER BY SERIAL_NO;
            """, connection);
        await using var reader = await lookup.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync(), "真库里没有可用的订单明细行（需要一行带单价的订单）。");
        var orderNo = reader.GetString(0);
        var serial = Convert.ToInt32(reader.GetValue(1));
        var expected = reader.GetValue(2);
        await reader.CloseAsync();

        // 与接口路径同形：送货单明细只带 ORDER_NO + 项次（ORDER_TYPE 在界面上不可见、也不会被提交）
        var row = Row(("ORDER_NO", orderNo), ("ORDER_SERIAL_NO", serial));
        var error = await DetailReferenceColumnFiller.FillAsync(
            connection, null, "COP_SEND_D", DueFields, [row], CancellationToken.None);

        Assert.Null(error);
        Assert.Equal(expected, row["PRICE"]);
    }

    /// <summary>没有引用键（无采购/无收料场景）不动该列，也不报错。</summary>
    [Fact]
    public async Task NoReference_LeavesColumnUntouched()
    {
        await using var connection = await OpenAsync();
        var row = Row(("RECEIVE_NO", ""));
        var error = await DetailReferenceColumnFiller.FillAsync(
            connection, null, "PUR_DUE_D", DueFields, [row], CancellationToken.None);

        Assert.Null(error);
        Assert.False(row.ContainsKey("PRICE"));
    }

    /// <summary>有引用却查不到来源行：fail-closed 报具名错误，不静默按 0 算金额。</summary>
    [Fact]
    public async Task MissingSourceLine_FailsClosed()
    {
        await using var connection = await OpenAsync();
        var row = Row(("RECEIVE_TYPE", "NO_SUCH_TYPE"), ("RECEIVE_NO", "NO_SUCH_RECEIVE"), ("RECEIVE_SERIAL_NO", 1));
        var error = await DetailReferenceColumnFiller.FillAsync(
            connection, null, "PUR_DUE_D", DueFields, [row], CancellationToken.None);

        Assert.NotNull(error);
        Assert.Equal("REFERENCED_RECEIVE_NOT_FOUND", error!.Code);
        Assert.Equal(0, error.RowIndex);
        Assert.False(row.ContainsKey("PRICE"));
    }

    /// <summary>规则绑定明细表：同样的引用键出现在别的单据上不生效。</summary>
    [Fact]
    public async Task RuleIsScopedToItsDetailTable()
    {
        await using var connection = await OpenAsync();
        var row = Row(("RECEIVE_TYPE", "NO_SUCH_TYPE"), ("RECEIVE_NO", "NO_SUCH_RECEIVE"), ("RECEIVE_SERIAL_NO", 1));
        var error = await DetailReferenceColumnFiller.FillAsync(
            connection, null, "COP_SEND_D", DueFields, [row], CancellationToken.None);

        Assert.Null(error);
        Assert.False(row.ContainsKey("PRICE"));
    }
}
