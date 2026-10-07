using System.Data;
using System.Text.Json;
using EOS.API.Data;
using EOS.API.Data.Effects;
using EOS.API.Data.Inventory;
using EOS.API.Errors;
using EOS.API.Models;
using EOS.API.Tests.Tools;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 反结账与审计的真库验收（/ WS-11），全程在事务内、结束回滚。
///
/// 一条完整的弧：**批核（=关账）→ 记账被拒 → 解批（=反结账）→ 记账恢复 → 再次批核 → 又被拒**，
/// 每一步都断言 `AUDIT_EVENT` + `AUDIT_FIELD_CHANGE` 的前后值。
///
/// 这条弧里最要紧的一条断言是**解批审计的旧值**：解批会把 `CONFIRM_PERSON` / `CONFIRM_DATE`
/// 一并覆盖（那三列只存"最后一位操作人"），"当初是谁在何时结的账"在状态列上就此消失，
/// 只留在审计的旧值里。只断言"有事件"而不看前后值，等于没验。
///
/// 直接调用 <see cref="WorkbenchApprovalService.RunApprovalCoreAsync"/>（公开的事务内核心），
/// 用测试自己的事务，因此批核/解批/审计都在同一份可回滚的事务里，且走的就是真实路径。
/// </summary>
[Trait("Category", "Integration")]
[Collection("live-database")]
public sealed class MonthCloseReopenLiveTests
{
    private static readonly string ConnectionString =
        Environment.GetEnvironmentVariable("MSSQL_ERP_CONN")
        ?? throw new InvalidOperationException(
            "真库集成测试需要 MSSQL_ERP_CONN；未配置即失败（无连接跳过≠已验证）。");

    private const string MonthType = "ADR20D";
    private const string MonthNo = "ADR20D001";
    private const string MasterTable = "INV_PRO_MONTH_M";
    private const string Confirmer = "结账人甲";
    private const string Reopener = "反结账乙";
    private const string Confirmer2 = "结账人丙";

    /// <summary>批核/解批的审计记录键 = 主键值按序拼接（与实现同口径）。</summary>
    private static string ResourceKey => $"{MonthType},{MonthNo}";

    [Fact]
    public async Task 批核_解批_再批核的全程留痕与写入开关()
    {
        var service = CreateService(Connections());
        var definition = Definition();

        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync();
        try
        {
            await SeedAsync(connection, transaction);
            var keys = new[] { MonthType, MonthNo };

            // ① 批核 = 关账
            var approved = await service.RunApprovalCoreAsync(
                connection, transaction, definition, keys, approve: true, Confirmer, "tester", CancellationToken.None);
            Assert.Null(approved.Blocked);

            var (tag, person, date) = await ReadConfirmAsync(connection, transaction);
            Assert.True(tag);
            Assert.Equal(Confirmer, person);
            Assert.NotNull(date);

            // 批核审计：状态位 0→1，且经办人从"空"变成具体的人
            var approveEvent = await LatestEventAsync(connection, transaction, "APPROVE");
            var approveChanges = await ChangesAsync(connection, transaction, approveEvent);
            Assert.Equal("0", approveChanges["CONFIRM_TAG"].Old);
            Assert.Equal("1", approveChanges["CONFIRM_TAG"].New);
            Assert.Null(approveChanges["CONFIRM_PERSON"].Old);
            Assert.Equal(Confirmer, approveChanges["CONFIRM_PERSON"].New);
            Assert.False(string.IsNullOrWhiteSpace(approveChanges["CONFIRM_DATE"].New));

            // ② 关账生效：落在该期末之前的记账被拒
            await Assert.ThrowsAsync<PeriodClosedException>(() => InventoryPeriodService.EnsureLedgerWritableAsync(
                connection, transaction, new DateTime(2023, 12, 20), MonthType, "ADR20DANY", CancellationToken.None));

            // ③ 解批 = 反结账
            var reopened = await service.RunApprovalCoreAsync(
                connection, transaction, definition, keys, approve: false, Reopener, "tester", CancellationToken.None);
            Assert.Null(reopened.Blocked);

            var (tagAfter, personAfter, _) = await ReadConfirmAsync(connection, transaction);
            Assert.False(tagAfter);
            Assert.Equal(Reopener, personAfter);

            // 解批审计：**旧值里留着原批核人与原批核时间**——状态列已被覆盖，这是唯一痕迹
            var deapproveEvent = await LatestEventAsync(connection, transaction, "DEAPPROVE");
            var deapproveChanges = await ChangesAsync(connection, transaction, deapproveEvent);
            Assert.Equal("1", deapproveChanges["CONFIRM_TAG"].Old);
            Assert.Equal("0", deapproveChanges["CONFIRM_TAG"].New);
            Assert.Equal(Confirmer, deapproveChanges["CONFIRM_PERSON"].Old);
            Assert.Equal(Reopener, deapproveChanges["CONFIRM_PERSON"].New);
            Assert.Equal(
                DateText(date!.Value), deapproveChanges["CONFIRM_DATE"].Old);

            // ④ 反结账后写入恢复
            await InventoryPeriodService.EnsureLedgerWritableAsync(
                connection, transaction, new DateTime(2023, 12, 20), MonthType, "ADR20DANY", CancellationToken.None);

            // ⑤ 再次批核：再进审计（同一张单两次 APPROVE），关账随之重新生效
            var reApproved = await service.RunApprovalCoreAsync(
                connection, transaction, definition, keys, approve: true, Confirmer2, "tester", CancellationToken.None);
            Assert.Null(reApproved.Blocked);
            Assert.Equal(2, await CountEventsAsync(connection, transaction, "APPROVE"));
            await Assert.ThrowsAsync<PeriodClosedException>(() => InventoryPeriodService.EnsureLedgerWritableAsync(
                connection, transaction, new DateTime(2023, 12, 20), MonthType, "ADR20DANY", CancellationToken.None));
        }
        finally
        {
            await transaction.RollbackAsync();
        }
    }

    /// <summary>没有任何月结单时，关账判据为空 ⇒ 记账一律放行（守卫不会误拦）。</summary>
    [Fact]
    public async Task 无已批核月结单时记账不被拦()
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync();
        try
        {
            await ExecuteAsync(connection, transaction,
                "DELETE FROM dbo.INV_PRO_MONTH_M WHERE MONTH_TYPE = @Type;", ("@Type", MonthType));

            await InventoryPeriodService.EnsureLedgerWritableAsync(
                connection, transaction, new DateTime(2023, 12, 20), MonthType, "ADR20DANY", CancellationToken.None);
        }
        finally
        {
            await transaction.RollbackAsync();
        }
    }

    private static string DateText(DateTime value) => value.ToString("yyyy-MM-dd HH:mm:ss");

    private static DbConnectionFactory Connections()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:ErpDatabase"] = ConnectionString })
            .Build();
        return new DbConnectionFactory(configuration);
    }

    /// <summary>与 AutoApproveEffectLiveTests 同款：真实审计写入器 + 真实引擎（空计划）。</summary>
    private static WorkbenchApprovalService CreateService(DbConnectionFactory connections)
    {
        var provider = new WorkbenchDefinitionProvider(connections, NullLogger<WorkbenchDefinitionProvider>.Instance);
        var auditWriter = new WorkbenchAuditWriter(
            connections, new HttpContextAccessor(), provider,
            Options.Create(new AuditSettings { FieldChangesEnabled = true }));
        var engine = new EffectEngineInvoker(
            new EffectEngineSettings { Enabled = true },
            new EffectPlanLoader(),
            EffectShadowRunner.BuildPipelineFor(ConnectionString),
            NullLogger<EffectEngineInvoker>.Instance);
        var workflow = new WorkflowEngine(connections, auditWriter, provider, engine, NullLogger<WorkflowEngine>.Instance);
        return new WorkbenchApprovalService(
            connections, auditWriter, workflow, engine, new WorkbenchIdempotency(),
            NullLogger<WorkbenchApprovalService>.Instance);
    }

    /// <summary>
    /// 手写定义：主表取月结单头、主键取单头身份。不带效果链与审批流程——
    /// 本用例验的是批核/解批这条链与审计，效果链另有专门用例。
    /// </summary>
    private static WorkbenchDefinition Definition() =>
        new(1304, "测试模块 1304", MasterTable, null, [], [], null, true, true, false,
            ["MONTH_TYPE", "MONTH_NO"], string.Empty, false);

    private static async Task SeedAsync(SqlConnection connection, SqlTransaction transaction) =>
        await ExecuteAsync(connection, transaction, """
            DELETE FROM dbo.INV_PRO_MONTH_M WHERE MONTH_TYPE = @Type;
            INSERT INTO dbo.INV_PRO_MONTH_M
                (MONTH_TYPE, MONTH_NO, MONTH_DATE, CONFIRM_TAG, CONFIRM_PERSON, CONFIRM_DATE, CREATE_PERSON, CREATE_DATE)
                VALUES (@Type, @No, '2023-12-31', 0, NULL, NULL, N'ADR20D', GETDATE());
            """, ("@Type", MonthType), ("@No", MonthNo));

    private static async Task<(bool? Tag, string? Person, DateTime? Date)> ReadConfirmAsync(
        SqlConnection connection, SqlTransaction transaction)
    {
        await using var command = new SqlCommand(
            "SELECT CONFIRM_TAG, CONFIRM_PERSON, CONFIRM_DATE FROM dbo.INV_PRO_MONTH_M "
            + "WHERE MONTH_TYPE = @Type AND MONTH_NO = @No;", connection, transaction);
        command.Parameters.Add("@Type", SqlDbType.NVarChar, 20).Value = MonthType;
        command.Parameters.Add("@No", SqlDbType.NVarChar, 40).Value = MonthNo;
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync(), "月结单夹具应当存在。");
        return (
            reader.IsDBNull(0) ? null : reader.GetBoolean(0),
            // CONFIRM_PERSON 是定长 NCHAR，直接读会带尾随空格
            reader.IsDBNull(1) ? null : reader.GetString(1).Trim(),
            reader.IsDBNull(2) ? null : reader.GetDateTime(2));
    }

    private static async Task<long> LatestEventAsync(
        SqlConnection connection, SqlTransaction transaction, string action)
    {
        await using var command = new SqlCommand(
            "SELECT TOP 1 EVENT_ID FROM dbo.AUDIT_EVENT WHERE RESOURCE_KEY = @Key AND ACTION = @Action "
            + "ORDER BY EVENT_ID DESC;", connection, transaction);
        command.Parameters.Add("@Key", SqlDbType.NVarChar, 200).Value = ResourceKey;
        command.Parameters.Add("@Action", SqlDbType.NVarChar, 50).Value = action;
        var value = await command.ExecuteScalarAsync();
        Assert.NotNull(value);
        return Convert.ToInt64(value);
    }

    private static async Task<int> CountEventsAsync(
        SqlConnection connection, SqlTransaction transaction, string action)
    {
        await using var command = new SqlCommand(
            "SELECT COUNT(*) FROM dbo.AUDIT_EVENT WHERE RESOURCE_KEY = @Key AND ACTION = @Action;",
            connection, transaction);
        command.Parameters.Add("@Key", SqlDbType.NVarChar, 200).Value = ResourceKey;
        command.Parameters.Add("@Action", SqlDbType.NVarChar, 50).Value = action;
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    /// <summary>该事件的前后值：字段 → (旧值, 新值)。</summary>
    private static async Task<Dictionary<string, (string? Old, string? New)>> ChangesAsync(
        SqlConnection connection, SqlTransaction transaction, long eventId)
    {
        var result = new Dictionary<string, (string?, string?)>(StringComparer.Ordinal);
        await using var command = new SqlCommand(
            "SELECT FIELD_NAME, OLD_VALUE, NEW_VALUE FROM dbo.AUDIT_FIELD_CHANGE WHERE EVENT_ID = @Id;",
            connection, transaction);
        command.Parameters.Add("@Id", SqlDbType.BigInt).Value = eventId;
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            result[reader.GetString(0)] = (
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2));
        }
        Assert.True(result.Count > 0, $"事件 {eventId} 应当带着前后值（AUDIT_FIELD_CHANGE）。");
        return result;
    }

    private static async Task ExecuteAsync(
        SqlConnection connection, SqlTransaction transaction, string sql,
        params (string Name, object? Value)[] parameters)
    {
        await using var command = new SqlCommand(sql, connection, transaction);
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        await command.ExecuteNonQueryAsync();
    }
}
