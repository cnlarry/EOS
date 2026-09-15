using System.Data;
using System.Text.Json;
using EOS.API.Data.Effects;
using Microsoft.Data.SqlClient;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// period-overlap 的真库行为：闭区间相交（端点相接算重叠）、包含关系、空结束日视为无限期、
/// 只比同维度键、编辑自身不算冲突。事务内造数并回滚，零残留。
/// 需要 EOS_ERP_TEST_CONNECTION（与本仓库其它真库测试一致的约定）。
/// </summary>
[Collection("live-database")]
public sealed class EffectValidationPeriodOverlapLiveTests
{
    private static readonly string ConnectionString =
        Environment.GetEnvironmentVariable("EOS_ERP_TEST_CONNECTION")
        ?? throw new InvalidOperationException(
            "真库集成测试需要 EOS_ERP_TEST_CONNECTION；未配置即失败（无连接跳过≠已验证）。");

    private static readonly EffectValidationExecutor Executor = new();

    private static JsonElement Params(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static ModuleEffectPlan Plan(string detailTable, string[] pkOrder, EffectValidationPlan rule) =>
        new(180106, "HR_M", detailTable, "live-period", pkOrder,
            Array.Empty<EffectActionPlan>(), [rule]);

    private static async Task<string?> RunAsync(string seedSql, string[] keyValues, string pkOrderJson, string table)
    {
        var token = CancellationToken.None;
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync(token);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(token);
        try
        {
            await using (var seed = new SqlCommand(seedSql, connection, transaction))
            {
                await seed.ExecuteNonQueryAsync(token);
            }
            var pkOrder = JsonSerializer.Deserialize<string[]>(pkOrderJson)!;
            var rule = new EffectValidationPlan(
                1, "SAVE", "period-overlap", true,
                Params("""
                    {"detailTable":"HR_CONTRACT_D",
                     "rangeFields":{"begin":"BEGIN_DATE","end":"END_DATE"},
                     "scopeFields":["CONT_TYPE","CONT_NO"],
                     "groupFields":["EMP_ID"],
                     "diagnosticFields":["EMP_ID","CONT_NO","BEGIN_DATE","END_DATE"]}
                    """),
                "以下人员的合同期间与其它单据重叠：\r\n{ROWS}");
            var plan = Plan(table, pkOrder, rule);
            try
            {
                await Executor.ValidateAsync(connection, transaction, plan, "SAVE", token, keyValues);
                return null;
            }
            catch (EffectValidationException exception)
            {
                return exception.Message;
            }
        }
        finally
        {
            await transaction.RollbackAsync(token);
        }
    }

    private const string Seed = """
        INSERT INTO dbo.HR_CONTRACT_D (CONT_TYPE, CONT_NO, SERIAL_NO, EMP_ID, CONTRACT_NO, BEGIN_DATE, END_DATE)
        VALUES (N'ADR12', N'P1', 1, N'E1', N'C1', '2026-01-01', '2026-12-31');
        INSERT INTO dbo.HR_CONTRACT_D (CONT_TYPE, CONT_NO, SERIAL_NO, EMP_ID, CONTRACT_NO, BEGIN_DATE, END_DATE)
        VALUES (N'ADR12', N'P2', 1, N'E1', N'C2', '2026-06-01', '2027-05-31');
        """;

    [Fact]
    public async Task 部分重叠被拒并回报冲突单据与期间()
    {
        var message = await RunAsync(Seed, ["ADR12", "P2"], """["CONT_TYPE","CONT_NO"]""", "HR_CONTRACT_D");

        Assert.NotNull(message);
        Assert.Contains("以下人员的合同期间与其它单据重叠", message);
        Assert.Contains("P1", message);
        Assert.Contains("2026-01-01", message);
        Assert.DoesNotContain("{ROWS}", message);
    }

    [Fact]
    public async Task 旧区间被新区间包含同样被拒()
    {
        // 旧实现只判"新单端点落在旧区间内"，这种包含关系会漏判；本用例即该缺陷的守卫。
        var message = await RunAsync("""
            INSERT INTO dbo.HR_CONTRACT_D (CONT_TYPE, CONT_NO, SERIAL_NO, EMP_ID, CONTRACT_NO, BEGIN_DATE, END_DATE)
            VALUES (N'ADR12', N'P1', 1, N'E1', N'C1', '2026-03-01', '2026-03-31');
            INSERT INTO dbo.HR_CONTRACT_D (CONT_TYPE, CONT_NO, SERIAL_NO, EMP_ID, CONTRACT_NO, BEGIN_DATE, END_DATE)
            VALUES (N'ADR12', N'P2', 1, N'E1', N'C2', '2026-01-01', '2026-12-31');
            """, ["ADR12", "P2"], """["CONT_TYPE","CONT_NO"]""", "HR_CONTRACT_D");

        Assert.NotNull(message);
        Assert.Contains("P1", message);
    }

    [Fact]
    public async Task 端点相接算重叠()
    {
        var message = await RunAsync("""
            INSERT INTO dbo.HR_CONTRACT_D (CONT_TYPE, CONT_NO, SERIAL_NO, EMP_ID, CONTRACT_NO, BEGIN_DATE, END_DATE)
            VALUES (N'ADR12', N'P1', 1, N'E1', N'C1', '2026-01-01', '2026-06-30');
            INSERT INTO dbo.HR_CONTRACT_D (CONT_TYPE, CONT_NO, SERIAL_NO, EMP_ID, CONTRACT_NO, BEGIN_DATE, END_DATE)
            VALUES (N'ADR12', N'P2', 1, N'E1', N'C2', '2026-06-30', '2026-12-31');
            """, ["ADR12", "P2"], """["CONT_TYPE","CONT_NO"]""", "HR_CONTRACT_D");

        Assert.NotNull(message);
    }

    [Fact]
    public async Task 不重叠放行()
    {
        var message = await RunAsync("""
            INSERT INTO dbo.HR_CONTRACT_D (CONT_TYPE, CONT_NO, SERIAL_NO, EMP_ID, CONTRACT_NO, BEGIN_DATE, END_DATE)
            VALUES (N'ADR12', N'P1', 1, N'E1', N'C1', '2026-01-01', '2026-06-29');
            INSERT INTO dbo.HR_CONTRACT_D (CONT_TYPE, CONT_NO, SERIAL_NO, EMP_ID, CONTRACT_NO, BEGIN_DATE, END_DATE)
            VALUES (N'ADR12', N'P2', 1, N'E1', N'C2', '2026-06-30', '2026-12-31');
            """, ["ADR12", "P2"], """["CONT_TYPE","CONT_NO"]""", "HR_CONTRACT_D");

        Assert.Null(message);
    }

    [Fact]
    public async Task 空结束日视为无限期()
    {
        var message = await RunAsync("""
            INSERT INTO dbo.HR_CONTRACT_D (CONT_TYPE, CONT_NO, SERIAL_NO, EMP_ID, CONTRACT_NO, BEGIN_DATE, END_DATE)
            VALUES (N'ADR12', N'P1', 1, N'E1', N'C1', '2026-01-01', NULL);
            INSERT INTO dbo.HR_CONTRACT_D (CONT_TYPE, CONT_NO, SERIAL_NO, EMP_ID, CONTRACT_NO, BEGIN_DATE, END_DATE)
            VALUES (N'ADR12', N'P2', 1, N'E1', N'C2', '2030-01-01', '2030-12-31');
            """, ["ADR12", "P2"], """["CONT_TYPE","CONT_NO"]""", "HR_CONTRACT_D");

        Assert.NotNull(message);
    }

    [Fact]
    public async Task 编辑自身不算冲突()
    {
        var message = await RunAsync("""
            INSERT INTO dbo.HR_CONTRACT_D (CONT_TYPE, CONT_NO, SERIAL_NO, EMP_ID, CONTRACT_NO, BEGIN_DATE, END_DATE)
            VALUES (N'ADR12', N'P1', 1, N'E1', N'C1', '2026-01-01', '2026-12-31');
            """, ["ADR12", "P1"], """["CONT_TYPE","CONT_NO"]""", "HR_CONTRACT_D");

        Assert.Null(message);
    }

    [Fact]
    public async Task 不同员工不互相冲突()
    {
        var message = await RunAsync("""
            INSERT INTO dbo.HR_CONTRACT_D (CONT_TYPE, CONT_NO, SERIAL_NO, EMP_ID, CONTRACT_NO, BEGIN_DATE, END_DATE)
            VALUES (N'ADR12', N'P1', 1, N'E1', N'C1', '2026-01-01', '2026-12-31');
            INSERT INTO dbo.HR_CONTRACT_D (CONT_TYPE, CONT_NO, SERIAL_NO, EMP_ID, CONTRACT_NO, BEGIN_DATE, END_DATE)
            VALUES (N'ADR12', N'P2', 1, N'E2', N'C2', '2026-06-01', '2027-05-31');
            """, ["ADR12", "P2"], """["CONT_TYPE","CONT_NO"]""", "HR_CONTRACT_D");

        Assert.Null(message);
    }
}
