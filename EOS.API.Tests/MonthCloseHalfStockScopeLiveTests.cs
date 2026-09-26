using System.Data;
using System.Text.Json;
using EOS.API.Data;
using EOS.API.Data.Effects;
using EOS.API.Data.Effects.ServiceEffectHandlers;
using EOS.API.Errors;
using EOS.API.Models;
using EOS.API.Security;
using EOS.API.Tests.Tools;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 半成品账的**月结范围参数**真库验收（ADR-020 §9.3 D3 / §10 WS-18 的"拦"这一半）。
///
/// 验的是"拦不拦这件事由参数决定，且与快照同源"：
///   ① 参数**关**（默认）⇒ 半成品移动**不因关账被拒**（这正是参数落地之前的实际行为——
///      ADR §9.3 原文写"今天是无条件拦 2603/2604"，实测**拦截代码只挂在主账移动引擎上**，
///      半成品路径此前根本没被拦过；本用例把这个实况钉住）；
///   ② 参数**开**（直连改库置位，保存路径会拒——见第三条）⇒ 同一笔移动**被关账拦**且点名期间；
///   ③ 保存路径**拒绝把参数打开**（快照侧 WS-18b 未落地时，开了就会出现"拦了但快照没有"的不对称）。
///
/// 夹具：一张已批核月结单（`ZZHS`，期末 2023-12-31）当作"已关账期"的边界；期别行本身当"源单"
/// （守卫只读它的日期列，因此不需要造单据）。守卫排在**任何写入之前**，所以本用例不碰余额表。
/// 判别性：把处理器里的参数判断摘掉 ⇒ 第①条变红。
/// </summary>
[Collection("live-database")]
public sealed class MonthCloseHalfStockScopeLiveTests : IAsyncLifetime
{
    private static readonly string ConnectionString =
        Environment.GetEnvironmentVariable("MSSQL_ERP_CONN")
        ?? throw new InvalidOperationException("真库集成测试需要 MSSQL_ERP_CONN；未配置即失败（无连接跳过≠已验证）。");

    private const string MonthType = "ZZHS";
    private const string MonthNo = "ZZHS-1";
    private const string ClosedOn = "2023-12-31";
    private const string Executor = "ZZHS0001";

    private DbConnectionFactory _connections = null!;
    private WorkbenchAuditWriter _audit = null!;

    public async Task InitializeAsync()
    {
        _connections = Connections();
        _audit = new WorkbenchAuditWriter(_connections, new HttpContextAccessor(),
            new WorkbenchDefinitionProvider(_connections, NullLogger<WorkbenchDefinitionProvider>.Instance),
            Options.Create(new AuditSettings()));
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        await CleanupAsync(connection);
        await ExecAsync(connection, """
            INSERT INTO dbo.INV_PRO_MONTH_M (MONTH_TYPE, MONTH_NO, MONTH_DATE, CONFIRM_TAG, CREATE_PERSON, CREATE_DATE, CI)
                VALUES (@type, @no, @date, 1, N'ZZHS', GETDATE(), 'ZZHS');
            """, ("@type", MonthType), ("@no", MonthNo), ("@date", ClosedOn));
    }

    public async Task DisposeAsync()
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        await CleanupAsync(connection);
    }

    private static async Task CleanupAsync(SqlConnection connection)
    {
        await ExecAsync(connection,
            "DELETE FROM dbo.INV_PRO_MONTH_D WHERE MONTH_TYPE = @type AND MONTH_NO = @no;",
            ("@type", MonthType), ("@no", MonthNo));
        await ExecAsync(connection,
            "DELETE FROM dbo.INV_PRO_MONTH_M WHERE MONTH_TYPE = @type AND MONTH_NO = @no;",
            ("@type", MonthType), ("@no", MonthNo));
        // 参数回到出厂态：关（别把测试的置位留给下一次）
        await ExecAsync(connection,
            "UPDATE dbo.DEPOT_STOCK_POLICY SET MONTH_CLOSE_SCOPE_HALF_STOCK = 0 WHERE DEPOT_ID = N'*';");
    }

    // ===== ① 参数关：不拦 =====

    [Fact]
    public async Task 参数关_半成品移动不因关账被拒()
    {
        await SetScopeAsync(false);

        // 参数关 ⇒ 守卫直接放行，后面的步骤照常走（夹具的期别行下没有明细行 ⇒ 影响 0 行）
        var affected = await RunMoveAsync();
        Assert.Equal(0, affected);
    }

    // ===== ② 参数开：拦，且点名期间 =====

    [Fact]
    public async Task 参数开_半成品移动被关账拦并点名期间()
    {
        await SetScopeAsync(true);

        var error = await Assert.ThrowsAsync<PeriodClosedException>(RunMoveAsync);
        Assert.Contains(MonthType, error.Message);
        Assert.Contains(MonthNo, error.Message);
        Assert.Contains(ClosedOn, error.Message);
    }

    // ===== ③ 口径：默认关；两侧都落地后允许打开，但库别行不许覆盖 =====

    [Fact]
    public async Task 参数默认关_可以打开_但库别行不得覆盖()
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync();

        var service = new DepotStockPolicyService(_connections, _audit);
        var deployment = await service.ResolveAsync(null, connection, transaction, CancellationToken.None);
        Assert.False(deployment.MonthCloseScopeHalfStock);

        // 直连改库置位之后求值能看到（判据只有一处：策略表那一列）
        await ExecAsync(connection, "UPDATE dbo.DEPOT_STOCK_POLICY SET MONTH_CLOSE_SCOPE_HALF_STOCK = 1 WHERE DEPOT_ID = N'*';", transaction);
        var flipped = await service.ResolveAsync(null, connection, transaction, CancellationToken.None);
        Assert.True(flipped.MonthCloseScopeHalfStock);

        // 两侧（拦 + 快照）都已落地 ⇒ 保存路径不再拒它（WS-18b 之前这里拒"开"，是为了不造出半生效的开关）
        Assert.Null(DepotStockPolicyService.ValidateMonthCloseScope(flipped, deployment));

        // 库别行仍不许覆盖它：月结范围是全库口径，按库别配会让同一个月的口径分裂
        var refusal = DepotStockPolicyService.ValidateMonthCloseScope(flipped with { DepotId = "ZZHS" }, deployment);
        Assert.NotNull(refusal);
        Assert.Contains("部署级", refusal);

        await transaction.RollbackAsync();
    }

    // ===== 装配 =====

    private async Task<int> RunMoveAsync()
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync();

        var handler = new HalfStockMoveHandler(new DepotStockPolicyService(_connections, _audit));
        var context = new ServiceEffectContext(
            connection,
            transaction,
            new ModuleEffectPlan(1304, "INV_PRO_MONTH_M", "INV_PRO_MONTH_D", "test",
                ["MONTH_TYPE", "MONTH_NO"], [], []),
            new EffectActionPlan(1, "APPROVE_EFFECT", "half-stock-move", "半成品入库（服务级）", true, "BLOCK",
                null,
                JsonSerializer.SerializeToElement(new
                {
                    direction = "IN",
                    fieldMap = new { masterDate = "MONTH_DATE", qty = "QTY" },
                }),
                JsonSerializer.SerializeToElement(new { kind = "reverse-flow" }),
                []),
            EffectEvent.ApproveEffect,
            $"{MonthType},{MonthNo}",
            [MonthType, MonthNo],
            Executor);

        try
        {
            return await handler.ExecuteAsync(context, CancellationToken.None);
        }
        finally
        {
            await transaction.RollbackAsync();
        }
    }

    private static async Task SetScopeAsync(bool value)
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        await ExecAsync(connection,
            $"UPDATE dbo.DEPOT_STOCK_POLICY SET MONTH_CLOSE_SCOPE_HALF_STOCK = {(value ? 1 : 0)} WHERE DEPOT_ID = N'*';");
    }

    private static DbConnectionFactory Connections() =>
        new(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:ErpDatabase"] = ConnectionString })
            .Build());

    private static async Task ExecAsync(SqlConnection connection, string sql, params (string Name, object? Value)[] parameters)
        => await ExecAsync(connection, sql, null, parameters);

    private static async Task ExecAsync(
        SqlConnection connection, string sql, SqlTransaction? transaction, params (string Name, object? Value)[] parameters)
    {
        await using var command = new SqlCommand(sql, connection, transaction);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }
        await command.ExecuteNonQueryAsync();
    }
}
