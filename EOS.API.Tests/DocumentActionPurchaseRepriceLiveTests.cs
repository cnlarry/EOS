using System.Data;
using System.Text.Json;
using EOS.API.Data;
using EOS.API.Data.DocumentActions;
using EOS.API.Data.DocumentActions.Handlers;
using EOS.API.Data.Effects;
using EOS.API.Models;
using EOS.API.Telemetry;
using EOS.API.Tests.Tools;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 采购单「重新取价」（`purchase-reprice`）真库验收：按厂商计价重算本单明细的单价与金额。
///
/// 口径：有计价的行更新单价七件套，无计价的行保留原价（只重算金额）；金额按税种公式逐行重算，
/// 主表按明细折算汇总；主表汇率为 0 拒绝（除零）；已完工结案拒绝；探路零写入（含审计）。
/// 不要求已批核（取价的正常时机在批核之前）。
///
/// 夹具全自造（ZZRP 前缀），用完即删：不碰真实采购单与计价。
/// 需要 MSSQL_ERP_CONN。
/// </summary>
[Collection("live-database")]
public sealed class DocumentActionPurchaseRepriceLiveTests : IAsyncLifetime
{
    private static readonly string ConnectionString =
        Environment.GetEnvironmentVariable("MSSQL_ERP_CONN")
        ?? throw new InvalidOperationException("真库集成测试需要 MSSQL_ERP_CONN；未配置即失败（无连接跳过≠已验证）。");

    private const int ModuleId = 1606;
    private const string MasterTable = "PUR_PURCHASE_M";
    private const string DetailTable = "PUR_PURCHASE_D";
    private const string PriceTable = "SUPPLIER_PRICE_D";
    private const string TestType = "ZZRP";
    private const string TestNo = "ZZRP00001";
    private const string TestUser = "ZZRP00001";
    /// <summary>本单的审计资源键（与执行器写入口径一致：主键值以逗号相连）。</summary>
    private const string RecordKey = TestType + "," + TestNo;
    private const string TestSupplier = "ZZSUP001";
    private const string PricedProduct = "ZZRPPRO01";
    private const string UnpricedProduct = "ZZRPPRO02";

    private static async Task<SqlConnection> OpenAsync()
    {
        var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        return connection;
    }

    private static async Task ExecAsync(SqlConnection connection, string sql, params (string Name, object? Value)[] parameters)
    {
        await using var command = new SqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<T?> ScalarAsync<T>(SqlConnection connection, string sql, params (string Name, object? Value)[] parameters)
    {
        await using var command = new SqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }
        var scalar = await command.ExecuteScalarAsync();
        return scalar is null or DBNull ? default : (T)Convert.ChangeType(scalar, typeof(T));
    }

    public async Task InitializeAsync()
    {
        await using var connection = await OpenAsync();
        // 厂商计价行：只给第一个料号配价（单价 10，外含税 13%，折扣 100）
        await ExecAsync(connection,
            $"""
            IF NOT EXISTS (SELECT 1 FROM dbo.{PriceTable} WHERE SUPPLIER_ID=@sup AND PRO_NO=@pro AND UNIT_ID=@unit AND CURR_ID=@curr AND TAX_ID=@tax)
                INSERT INTO dbo.{PriceTable} (SUPPLIER_ID,PRO_NO,UNIT_ID,CURR_ID,TAX_ID,PRICE,TAX_TYPE,TAX_RATE,CURR_RATE,REBATE,SUPPLIER_PRO_NO)
                VALUES (@sup,@pro,@unit,@curr,@tax,10,N'O',13,1,100,N'SP-ZZ-01');
            """,
            ("@sup", TestSupplier), ("@pro", PricedProduct),
            ("@unit", "PCS"), ("@curr", "RMB"), ("@tax", "T01"));
        // 采购单：主表汇率 1，未完工；明细两行（第 1 行有计价、第 2 行无计价）
        await ExecAsync(connection,
            $"""
            IF NOT EXISTS (SELECT 1 FROM dbo.{MasterTable} WHERE PURCHASE_TYPE=@type AND PURCHASE_NO=@no)
                INSERT INTO dbo.{MasterTable} (PURCHASE_TYPE,PURCHASE_NO,SUPPLIER_ID,CURR_RATE,FINISHED_TAG,CREATE_PERSON,CREATE_DATE,CI)
                VALUES (@type,@no,@sup,1,0,N'DbUp',SYSDATETIME(),N'');
            INSERT INTO dbo.{DetailTable}
                (PURCHASE_TYPE,PURCHASE_NO,SERIAL_NO,PRO_NO,UNIT_ID,CURR_ID,TAX_ID,TAX_TYPE,TAX_RATE,PRICE,REBATE,QTY,CURR_RATE)
            VALUES (@type,@no,1,@pro1,@unit,@curr,@tax,N'O',13,5,100,2,1),
                   (@type,@no,2,@pro2,@unit,@curr,@tax,N'O',13,7,100,1,1);
            """,
            ("@type", TestType), ("@no", TestNo), ("@sup", TestSupplier),
            ("@pro1", PricedProduct), ("@pro2", UnpricedProduct),
            ("@unit", "PCS"), ("@curr", "RMB"), ("@tax", "T01"));
        await ExecAsync(connection,
            """
            IF NOT EXISTS (SELECT 1 FROM dbo.SYSDD_BUTTON WHERE USER_ID=@user AND M_IDX=@module AND BUTTON_KEY=@key)
                INSERT INTO dbo.SYSDD_BUTTON (USER_ID,M_IDX,BUTTON_KEY,ALLOW_TAG,CREATE_PERSON,CREATE_DATE,LAST_UPDATE_BY,LAST_UPDATE_DATE)
                VALUES (@user,@module,@key,1,N'DbUp',SYSDATETIME(),N'DbUp',SYSDATETIME());
            """,
            ("@user", TestUser), ("@module", ModuleId), ("@key", PurchaseRepriceHandler.ActionKey));
    }

    public async Task DisposeAsync()
    {
        await using var connection = await OpenAsync();
        await ExecAsync(connection,
            $"DELETE FROM dbo.{DetailTable} WHERE PURCHASE_TYPE=@type AND PURCHASE_NO=@no;",
            ("@type", TestType), ("@no", TestNo));
        await ExecAsync(connection,
            $"DELETE FROM dbo.{MasterTable} WHERE PURCHASE_TYPE=@type AND PURCHASE_NO=@no;",
            ("@type", TestType), ("@no", TestNo));
        await ExecAsync(connection,
            $"DELETE FROM dbo.{PriceTable} WHERE SUPPLIER_ID=@sup;",
            ("@sup", TestSupplier));
        await ExecAsync(connection, "DELETE FROM dbo.SYSDD_BUTTON WHERE USER_ID=@user;", ("@user", TestUser));
        await ExecAsync(connection,
            "DELETE FROM dbo.WORKBENCH_IDEMPOTENCY WHERE M_IDX=@module AND ACTION=N'ACTION';", ("@module", ModuleId));
    }

    // ===== 装配（与 Program.cs 同源：真实服务） =====

    private static DbConnectionFactory Connections()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:ErpDatabase"] = ConnectionString })
            .Build();
        return new DbConnectionFactory(configuration);
    }

    private DocumentActionExecutor Executor()
    {
        var connections = Connections();
        var provider = new WorkbenchDefinitionProvider(connections, NullLogger<WorkbenchDefinitionProvider>.Instance);
        return new(connections,
            new DocumentActionRegistry([new PurchaseRepriceHandler()], NullLogger<DocumentActionRegistry>.Instance),
            new DocumentActionAuthorization(connections),
            new WorkbenchScopeFilter(new ApiMetrics()),
            new WorkbenchIdempotency(),
            EffectShadowRunner.BuildPipelineFor(ConnectionString),
            new WorkbenchAuditWriter(connections, new HttpContextAccessor(), provider,
                Options.Create(new AuditSettings())),
            NullLogger<DocumentActionExecutor>.Instance);
    }

    private static WorkbenchDefinition Definition() =>
        new(ModuleId: ModuleId, Title: "采购单", MasterTable: MasterTable, DetailTable: DetailTable,
            MasterFields: [], DetailFields: [], DefaultSort: null, HasAdd: true, HasEdit: true, DetailNoSave: false,
            MasterPkOrder: ["PURCHASE_TYPE", "PURCHASE_NO"], DetailNoFields: string.Empty, HasWorkflow: false,
            UserId: TestUser, ExecTag: "Z",
            FilterFieldKeys: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "PURCHASE_TYPE" },
            BusinessActions: JsonSerializer.SerializeToElement(new object[]
            {
                new
                {
                    seq = 1,
                    eventCode = "MANUAL",
                    effectKey = PurchaseRepriceHandler.ActionKey,
                    enabled = true,
                    label = "重新取价",
                    confirmTag = true,
                    failMode = "BLOCK",
                },
            }));

    private static FormDefinition Form() =>
        new(ModuleId, "采购单", MasterTable, DetailTable, true, true, "view", [], [],
            ["PURCHASE_TYPE", "PURCHASE_NO"], string.Empty, string.Empty);

    private Task<DocumentActionExecution> RunAsync(bool confirm = true) =>
        Executor().ExecuteAsync(Definition(), Form(), PurchaseRepriceHandler.ActionKey,
            new DocumentActionRequest([TestType, TestNo], null, Confirm: confirm),
            TestUser, "测试经办人", null, Guid.NewGuid().ToString("N"), CancellationToken.None);

    private async Task<(double Price, double Amount, double AmountTax, double TaxSum)> DetailAmountsAsync(int serial)
    {
        await using var connection = await OpenAsync();
        await using var command = new SqlCommand(
            $"SELECT ISNULL(PRICE,0), ISNULL(AMOUNT,0), ISNULL(AMOUNT_TAX,0), ISNULL(TAX_SUM,0) "
            + $"FROM dbo.{DetailTable} WHERE PURCHASE_TYPE=@type AND PURCHASE_NO=@no AND SERIAL_NO=@serial;",
            connection);
        command.Parameters.AddWithValue("@type", TestType);
        command.Parameters.AddWithValue("@no", TestNo);
        command.Parameters.AddWithValue("@serial", serial);
        await using var reader = await command.ExecuteReaderAsync();
        await reader.ReadAsync();
        return (Convert.ToDouble(reader.GetValue(0)), Convert.ToDouble(reader.GetValue(1)),
            Convert.ToDouble(reader.GetValue(2)), Convert.ToDouble(reader.GetValue(3)));
    }

    private async Task<(double Amount, double AmountTax, double TaxSum)> MasterAmountsAsync()
    {
        await using var connection = await OpenAsync();
        await using var command = new SqlCommand(
            $"SELECT ISNULL(AMOUNT,0), ISNULL(AMOUNT_TAX,0), ISNULL(TAX_SUM,0) "
            + $"FROM dbo.{MasterTable} WHERE PURCHASE_TYPE=@type AND PURCHASE_NO=@no;",
            connection);
        command.Parameters.AddWithValue("@type", TestType);
        command.Parameters.AddWithValue("@no", TestNo);
        await using var reader = await command.ExecuteReaderAsync();
        await reader.ReadAsync();
        return (Convert.ToDouble(reader.GetValue(0)), Convert.ToDouble(reader.GetValue(1)),
            Convert.ToDouble(reader.GetValue(2)));
    }

    /// <summary>审计是追加型的：取当前最大事件号作基线，之后只看基线之上的增量。</summary>
    private static async Task<long> AuditBaselineAsync()
    {
        await using var connection = await OpenAsync();
        return await ScalarAsync<long>(connection, "SELECT ISNULL(MAX(EVENT_ID),0) FROM dbo.AUDIT_EVENT;");
    }

    /// <summary>基线之上、本单（模块 + 动作 + 资源键）的审计条数。</summary>
    private async Task<int> AuditCountAsync(long baseline)
    {
        await using var connection = await OpenAsync();
        return await ScalarAsync<int>(connection,
            """
            SELECT COUNT(*) FROM dbo.AUDIT_EVENT
             WHERE EVENT_ID > @baseline AND M_IDX=@module AND ACTION=@action AND RESOURCE_KEY=@key;
            """,
            ("@baseline", baseline), ("@module", ModuleId),
            ("@action", PurchaseRepriceHandler.ActionKey), ("@key", RecordKey));
    }

    // ===== 用例 =====

    [Fact]
    public async Task Reprice_UpdatesPricedRow_KeepsUnpricedRow_AndRollsUp()
    {
        var auditBaseline = await AuditBaselineAsync();
        var result = await RunAsync();

        Assert.True(result.Status == DocumentActionStatus.Ok, result.ErrorMessage);
        Assert.Equal(DocumentActionOutcome.Refreshed, result.Result!.Outcome);
        Assert.Contains("1 行按厂商计价更新单价", result.Result.Message!);
        // 第 1 行：单价 5→10（外含税 13%，数量 2）：金额 20 / 价税 22.6 / 税额 2.6
        var priced = await DetailAmountsAsync(1);
        Assert.Equal(10d, priced.Price, 6);
        Assert.Equal(20d, priced.Amount, 6);
        Assert.Equal(22.6d, priced.AmountTax, 6);
        Assert.Equal(2.6d, priced.TaxSum, 6);
        // 第 2 行无计价：单价 7 不动，金额按原值重算（1×7×1.13=7.91，税 0.91）
        var unpriced = await DetailAmountsAsync(2);
        Assert.Equal(7d, unpriced.Price, 6);
        Assert.Equal(7d, unpriced.Amount, 6);
        Assert.Equal(7.91d, unpriced.AmountTax, 6);
        Assert.Equal(0.91d, unpriced.TaxSum, 6);
        // 主表：汇率 1，金额＝明细合计
        var master = await MasterAmountsAsync();
        Assert.Equal(27d, master.Amount, 6);
        Assert.Equal(30.51d, master.AmountTax, 6);
        Assert.Equal(3.51d, master.TaxSum, 6);
        // 本次取价只留一条自己的审计（按本单资源键限定，别的采购单不算）
        Assert.Equal(1, await AuditCountAsync(auditBaseline));
    }

    [Fact]
    public async Task ProbeOnly_ReportsAndWritesNothing()
    {
        var before = await DetailAmountsAsync(1);
        var auditBaseline = await AuditBaselineAsync();

        var probe = await RunAsync(confirm: false);

        Assert.Equal(DocumentActionStatus.Ok, probe.Status);
        Assert.True(probe.RequiresConfirmation);
        Assert.Equal(before, await DetailAmountsAsync(1));
        Assert.Equal(0, await AuditCountAsync(auditBaseline));
    }

    [Fact]
    public async Task FinishedOrder_IsRefused_AndWritesNothing()
    {
        await using var connection = await OpenAsync();
        await ExecAsync(connection,
            $"UPDATE dbo.{MasterTable} SET FINISHED_TAG=1 WHERE PURCHASE_TYPE=@type AND PURCHASE_NO=@no;",
            ("@type", TestType), ("@no", TestNo));

        var result = await RunAsync();

        Assert.Equal(DocumentActionStatus.Failed, result.Status);
        Assert.Contains("完工结案", result.ErrorMessage!);
        Assert.Equal(5d, (await DetailAmountsAsync(1)).Price, 6);
    }

    [Fact]
    public async Task ZeroMasterRate_IsRefused()
    {
        await using var connection = await OpenAsync();
        await ExecAsync(connection,
            $"UPDATE dbo.{MasterTable} SET CURR_RATE=0 WHERE PURCHASE_TYPE=@type AND PURCHASE_NO=@no;",
            ("@type", TestType), ("@no", TestNo));

        var result = await RunAsync();

        Assert.Equal(DocumentActionStatus.Failed, result.Status);
        Assert.Contains("汇率", result.ErrorMessage!);
    }

    [Fact]
    public async Task EmptyOrder_ReturnsMessage()
    {
        await using var connection = await OpenAsync();
        await ExecAsync(connection,
            $"DELETE FROM dbo.{DetailTable} WHERE PURCHASE_TYPE=@type AND PURCHASE_NO=@no;",
            ("@type", TestType), ("@no", TestNo));

        var result = await RunAsync();

        Assert.Equal(DocumentActionStatus.Ok, result.Status);
        Assert.Equal(DocumentActionOutcome.Message, result.Result!.Outcome);
    }
}
