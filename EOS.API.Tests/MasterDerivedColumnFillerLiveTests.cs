using EOS.API.Data;
using EOS.API.Models;
using Microsoft.Data.SqlClient;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 主表派生列补齐（只读联动列）真库验收：币别带出汇率、采购变更单由明细带出采购单号。
///
/// 走真库的理由：取值来自业务主档（`dbo.CURR`）与真实表列结构，"来源有值而主档查不到必须拒绝"
/// 这条只有连真库才验得出来（替身会把 fail-closed 验成纸面断言）。用例只读，不改库内数据。
/// </summary>
[Collection("live-database")]
public class MasterDerivedColumnFillerLiveTests
{
    private static readonly string ConnectionString =
        Environment.GetEnvironmentVariable("MSSQL_ERP_CONN")
        ?? throw new InvalidOperationException("真库集成测试需要 MSSQL_ERP_CONN；未配置即失败（无连接跳过≠已验证）。");

    private static WorkbenchDefinition Definition(string masterTable, string? detailTable, string detailNoFields) =>
        new(0, "test", masterTable, detailTable, [], [], null, true, true, true, [], detailNoFields, false);

    private static FormFieldDefinition Field(string key, string dataType) =>
        new(key, key, dataType, 100, null, false, null, null, null, true, true, false, false, null,
            [], false, false, false, false, false, false, null);

    private static Dictionary<string, object?> Values(params (string Key, object? Value)[] items)
    {
        var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in items) values[key] = value;
        return values;
    }

    private static IReadOnlyList<IReadOnlyDictionary<string, string?>> Details(params Dictionary<string, string?>[] rows) => rows;

    private static async Task<SqlConnection> OpenAsync()
    {
        var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        return connection;
    }

    /// <summary>主档汇率是真库数据（不写死数值），补齐结果必须与主档逐值一致。</summary>
    [Fact]
    public async Task CurrencyRate_IsFilledFromCurrencyMaster()
    {
        await using var connection = await OpenAsync();
        await using var lookup = new SqlCommand("SELECT TOP 1 CURR_RATE FROM dbo.CURR WHERE CURR_ID=N'USD';", connection);
        var expected = await lookup.ExecuteScalarAsync();
        Assert.NotNull(expected);

        var values = Values(("CURR_ID", "USD"));
        var result = await MasterDerivedColumnFiller.FillAsync(
            connection, null, Definition("COP_SEND_M", "COP_SEND_D", "CURR_ID;CURR_RATE"),
            [Field("CURR_RATE", "float")], values, null, CancellationToken.None);

        Assert.Null(result.Error);
        Assert.Equal(expected, values["CURR_RATE"]);
        Assert.Single(result.Filled);
        Assert.Equal("CURR_RATE", result.Filled[0].Column);
    }

    /// <summary>规则不绑定模块：任何同时有 CURR_ID/CURR_RATE 的主表都按同一口径补齐。</summary>
    [Fact]
    public async Task CurrencyRate_AppliesToAnyMasterTableWithBothColumns()
    {
        await using var connection = await OpenAsync();
        var values = Values(("CURR_ID", "RMB"));
        var result = await MasterDerivedColumnFiller.FillAsync(
            connection, null, Definition("PUR_PREPAY_M", "PUR_PREPAY_D", "CURR_ID;CURR_RATE"),
            [Field("CURR_RATE", "float")], values, null, CancellationToken.None);

        Assert.Null(result.Error);
        Assert.Equal(1d, Convert.ToDouble(values["CURR_RATE"]));
    }

    /// <summary>没有来源就什么都不做：缺值仍由 DETAIL_NO_FIELDS 判据按原样拒绝（不是静默放行）。</summary>
    [Fact]
    public async Task CurrencyRate_IsNotFilled_WhenSourceIsEmpty()
    {
        await using var connection = await OpenAsync();
        var values = Values(("SEND_DATE", "2026-09-26"));
        var result = await MasterDerivedColumnFiller.FillAsync(
            connection, null, Definition("COP_SEND_M", "COP_SEND_D", "CURR_ID;CURR_RATE"),
            [Field("CURR_RATE", "float")], values, null, CancellationToken.None);

        Assert.Null(result.Error);
        Assert.Empty(result.Filled);
        Assert.False(values.ContainsKey("CURR_RATE"));
    }

    /// <summary>来源有值而主档查不到 ⇒ 具名拒绝，不静默填 1/0（填 0 会让外币单据按本币计价）。</summary>
    [Fact]
    public async Task CurrencyRate_UnknownCurrency_FailsClosed()
    {
        await using var connection = await OpenAsync();
        var values = Values(("CURR_ID", "ZZ-NO-SUCH-CURR"));
        var result = await MasterDerivedColumnFiller.FillAsync(
            connection, null, Definition("COP_SEND_M", "COP_SEND_D", "CURR_ID;CURR_RATE"),
            [Field("CURR_RATE", "float")], values, null, CancellationToken.None);

        Assert.NotNull(result.Error);
        Assert.Equal("CURRENCY_NOT_FOUND", result.Error!.Code);
        Assert.Equal("CURR_ID", result.Error.Field);
        Assert.Empty(result.Filled);
        Assert.False(values.ContainsKey("CURR_RATE"));
    }

    /// <summary>已提交的非空值不覆盖：可写汇率的模块由人填的数不会被主档值顶掉。</summary>
    [Fact]
    public async Task CurrencyRate_SubmittedValue_IsNotOverwritten()
    {
        await using var connection = await OpenAsync();
        var values = Values(("CURR_ID", "RMB"), ("CURR_RATE", 9.5d));
        var result = await MasterDerivedColumnFiller.FillAsync(
            connection, null, Definition("COP_ORDER_M", "COP_ORDER_D", "CLIENT_ID"),
            [Field("CURR_RATE", "float")], values, null, CancellationToken.None);

        Assert.Null(result.Error);
        Assert.Empty(result.Filled);
        Assert.Equal(9.5d, values["CURR_RATE"]);
    }

    /// <summary>采购变更单的主表采购单号由明细引用的采购单带出（单别不是唯一键，服务端无法反查）。</summary>
    [Fact]
    public async Task PurchaseNo_IsCarriedFromDetails()
    {
        await using var connection = await OpenAsync();
        var values = Values(("PURCHASE_TYPE", "CGD"));
        var details = Details(
            new Dictionary<string, string?> { ["PRO_NO"] = "P1", ["PURCHASE_NO"] = "ZZCARRY-B1" },
            new Dictionary<string, string?> { ["PRO_NO"] = "P2", ["PURCHASE_NO"] = "ZZCARRY-B1" });
        var result = await MasterDerivedColumnFiller.FillAsync(
            connection, null, Definition("PUR_PURCHASE_CHANGE_M", "PUR_PURCHASE_CHANGE_D", "PURCHASE_NO"),
            [Field("PURCHASE_NO", "nvarchar")], values, details, CancellationToken.None);

        Assert.Null(result.Error);
        Assert.Equal("ZZCARRY-B1", values["PURCHASE_NO"]);
        Assert.Single(result.Filled);
    }

    /// <summary>明细行引用了不同采购单 ⇒ 无法确定主表填哪个，具名拒绝（不猜）。</summary>
    [Fact]
    public async Task PurchaseNo_AmbiguousDetails_FailsClosed()
    {
        await using var connection = await OpenAsync();
        var values = Values(("PURCHASE_TYPE", "CGD"));
        var details = Details(
            new Dictionary<string, string?> { ["PURCHASE_NO"] = "ZZCARRY-B1" },
            new Dictionary<string, string?> { ["PURCHASE_NO"] = "ZZCARRY-B2" });
        var result = await MasterDerivedColumnFiller.FillAsync(
            connection, null, Definition("PUR_PURCHASE_CHANGE_M", "PUR_PURCHASE_CHANGE_D", "PURCHASE_NO"),
            [Field("PURCHASE_NO", "nvarchar")], values, details, CancellationToken.None);

        Assert.NotNull(result.Error);
        Assert.Equal("DERIVED_COLUMN_AMBIGUOUS", result.Error!.Code);
        Assert.False(values.ContainsKey("PURCHASE_NO"));
    }

    /// <summary>明细没带该列 ⇒ 不补，缺值仍由 DETAIL_NO_FIELDS 判据拒绝。</summary>
    [Fact]
    public async Task PurchaseNo_IsNotFilled_WhenDetailsDoNotCarryIt()
    {
        await using var connection = await OpenAsync();
        var values = Values(("PURCHASE_TYPE", "CGD"));
        var details = Details(new Dictionary<string, string?> { ["PRO_NO"] = "P1" });
        var result = await MasterDerivedColumnFiller.FillAsync(
            connection, null, Definition("PUR_PURCHASE_CHANGE_M", "PUR_PURCHASE_CHANGE_D", "PURCHASE_NO"),
            [Field("PURCHASE_NO", "nvarchar")], values, details, CancellationToken.None);

        Assert.Null(result.Error);
        Assert.Empty(result.Filled);
        Assert.False(values.ContainsKey("PURCHASE_NO"));
    }

    /// <summary>明细带出规则只对该主表生效：别的主表有同名明细列也不补。</summary>
    [Fact]
    public async Task PurchaseNo_DetailCarry_IsScopedToItsMasterTable()
    {
        await using var connection = await OpenAsync();
        var values = Values(("PREPAY_TYPE", "E2E"));
        var details = Details(new Dictionary<string, string?> { ["PURCHASE_NO"] = "ZZCARRY-B1" });
        var result = await MasterDerivedColumnFiller.FillAsync(
            connection, null, Definition("PUR_PREPAY_M", "PUR_PREPAY_D", "CURR_ID;CURR_RATE;PREPAY_NO"),
            [Field("PURCHASE_NO", "nvarchar")], values, details, CancellationToken.None);

        Assert.Null(result.Error);
        Assert.Empty(result.Filled);
        Assert.False(values.ContainsKey("PURCHASE_NO"));
    }
}
