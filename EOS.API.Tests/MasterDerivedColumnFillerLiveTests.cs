using EOS.API.Data;
using EOS.API.Data.Workbench;
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
[Trait("Category", "Integration")]
[Collection("live-database")]
public class MasterDerivedColumnFillerLiveTests
{
    private static readonly string ConnectionString =
        Environment.GetEnvironmentVariable("MSSQL_ERP_CONN")
        ?? throw new InvalidOperationException("真库集成测试需要 MSSQL_ERP_CONN；未配置即失败（无连接跳过≠已验证）。");

    private static WorkbenchDefinition Definition(string masterTable, string? detailTable, string detailNoFields) =>
        new(0, "test", masterTable, detailTable, [], [], null, true, true, true, [], detailNoFields, false);

    /// <summary>构造表单字段定义；默认只读（联动列在单据上普遍只读），要验"用户可填"时显式传 false。</summary>
    private static FormFieldDefinition Field(string key, string dataType, bool isReadonly = true) =>
        new(key, key, dataType, 100, null, false, null, null, null, isReadonly, true, false, false, null,
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

    /// <summary>单据侧的税率由税别带出：期望值是主档里的税率（真库数据，不写死数值）。</summary>
    [Fact]
    public async Task TaxRate_IsFilledFromTaxMaster()
    {
        await using var connection = await OpenAsync();
        await using var lookup = new SqlCommand("SELECT TOP 1 TAX_RATE FROM dbo.TAX WHERE LTRIM(RTRIM(TAX_ID))=N'TAX02';", connection);
        var expected = await lookup.ExecuteScalarAsync();
        Assert.NotNull(expected);

        var values = Values(("TAX_ID", "TAX02"));
        var result = await MasterDerivedColumnFiller.FillAsync(
            connection, null, Definition("COP_ORDER_M", "COP_ORDER_D", "CLIENT_ID"),
            [Field("TAX_RATE", "float")], values, null, CancellationToken.None);

        Assert.Null(result.Error);
        Assert.Equal(Convert.ToDouble(expected), Convert.ToDouble(values["TAX_RATE"]));
        Assert.Single(result.Filled);
        Assert.Equal("TAX_RATE", result.Filled[0].Column);
    }

    /// <summary>没有来源就不补：缺值仍由 DETAIL_NO_FIELDS 判据按原样拒绝。</summary>
    [Fact]
    public async Task TaxRate_IsNotFilled_WhenSourceIsEmpty()
    {
        await using var connection = await OpenAsync();
        var values = Values(("CLIENT_ID", "C0001"));
        var result = await MasterDerivedColumnFiller.FillAsync(
            connection, null, Definition("COP_ORDER_M", "COP_ORDER_D", "CLIENT_ID"),
            [Field("TAX_RATE", "float")], values, null, CancellationToken.None);

        Assert.Null(result.Error);
        Assert.Empty(result.Filled);
        Assert.False(values.ContainsKey("TAX_RATE"));
    }

    /// <summary>来源有值而主档查不到 ⇒ 具名拒绝，不静默按 0% 算税额。</summary>
    [Fact]
    public async Task TaxRate_UnknownTax_FailsClosed()
    {
        await using var connection = await OpenAsync();
        var values = Values(("TAX_ID", "ZZ-NO-SUCH-TAX"));
        var result = await MasterDerivedColumnFiller.FillAsync(
            connection, null, Definition("COP_ORDER_M", "COP_ORDER_D", "CLIENT_ID"),
            [Field("TAX_RATE", "float")], values, null, CancellationToken.None);

        Assert.NotNull(result.Error);
        Assert.Equal("TAX_NOT_FOUND", result.Error!.Code);
        Assert.Equal("TAX_ID", result.Error.Field);
        Assert.Empty(result.Filled);
        Assert.False(values.ContainsKey("TAX_RATE"));
    }

    /// <summary>税率可写的模块：本次提交了就认用户填的值，不拿主档顶掉它。</summary>
    [Fact]
    public async Task TaxRate_SubmittedValue_IsNotOverwritten_WhenFieldIsWritable()
    {
        await using var connection = await OpenAsync();
        var values = Values(("TAX_ID", "TAX02"), ("TAX_RATE", 5d));
        var result = await MasterDerivedColumnFiller.FillAsync(
            connection, null, Definition("COP_ORDER_M", "COP_ORDER_D", "CLIENT_ID"),
            [Field("TAX_RATE", "float", isReadonly: false)], values, null, CancellationToken.None,
            ["TAX_ID", "TAX_RATE"]);

        Assert.Null(result.Error);
        Assert.Empty(result.Filled);
        Assert.Equal(5d, values["TAX_RATE"]);
    }

    /// <summary>
    /// 税率只读的模块：提交进来的税率一概不作数。构造请求塞一个假税率（99）就能改写税额基础，
    /// 而它本应由主档决定——这里断言落库前被主档值覆盖。
    /// </summary>
    [Fact]
    public async Task TaxRate_SubmittedValue_IsOverridden_WhenFieldIsReadonly()
    {
        await using var connection = await OpenAsync();
        await using var lookup = new SqlCommand("SELECT TOP 1 TAX_RATE FROM dbo.TAX WHERE LTRIM(RTRIM(TAX_ID))=N'TAX02';", connection);
        var expected = await lookup.ExecuteScalarAsync();
        Assert.NotNull(expected);

        var values = Values(("TAX_ID", "TAX02"), ("TAX_RATE", 99d));
        var result = await MasterDerivedColumnFiller.FillAsync(
            connection, null, Definition("COP_ORDER_M", "COP_ORDER_D", "CLIENT_ID"),
            [Field("TAX_RATE", "float")], values, null, CancellationToken.None,
            ["TAX_ID", "TAX_RATE"]);

        Assert.Null(result.Error);
        Assert.Equal(Convert.ToDouble(expected), Convert.ToDouble(values["TAX_RATE"]));
        Assert.Single(result.Filled);
    }

    /// <summary>
    /// 改了税别就必须按新税别重取税率：记录里带过来的旧值（999 是刻意造的旧值）不能留在
    /// 改过来源的单据上——留着就是"税别与税率不一致"，而这正是联动列要防的事。
    /// </summary>
    [Fact]
    public async Task TaxRate_IsRefilled_WhenSourceColumnIsResubmitted()
    {
        await using var connection = await OpenAsync();
        await using var lookup = new SqlCommand("SELECT TOP 1 TAX_RATE FROM dbo.TAX WHERE LTRIM(RTRIM(TAX_ID))=N'TAX02';", connection);
        var expected = await lookup.ExecuteScalarAsync();
        Assert.NotNull(expected);

        var values = Values(("TAX_ID", "TAX02"), ("TAX_RATE", 999d));
        var result = await MasterDerivedColumnFiller.FillAsync(
            connection, null, Definition("COP_ORDER_M", "COP_ORDER_D", "CLIENT_ID"),
            [Field("TAX_RATE", "float")], values, null, CancellationToken.None,
            ["TAX_ID"]);

        Assert.Null(result.Error);
        Assert.Equal(Convert.ToDouble(expected), Convert.ToDouble(values["TAX_RATE"]));
        Assert.Single(result.Filled);
    }

    /// <summary>来源列没动（值来自记录）⇒ 保持原值：不因"顺带保存"把人工维护过的联动值顶掉。</summary>
    [Fact]
    public async Task TaxRate_IsKept_WhenSourceColumnIsNotResubmitted()
    {
        await using var connection = await OpenAsync();
        var values = Values(("TAX_ID", "TAX02"), ("TAX_RATE", 999d));
        var result = await MasterDerivedColumnFiller.FillAsync(
            connection, null, Definition("COP_ORDER_M", "COP_ORDER_D", "CLIENT_ID"),
            [Field("TAX_RATE", "float")], values, null, CancellationToken.None,
            ["CLIENT_ID"]);

        Assert.Null(result.Error);
        Assert.Empty(result.Filled);
        Assert.Equal(999d, values["TAX_RATE"]);
    }
}
