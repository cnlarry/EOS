using System.Data;
using System.Text.Json;
using System.Text.RegularExpressions;
using EOS.API.Data.Effects;
using Microsoft.Data.SqlClient;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 用**库内真实配置**（迁移 086 落地的期重叠族规则）驱动执行器，验证"单权威"下的表现：
/// 同单重复与旧过程文案一致、跨单期间重叠按闭区间拒绝、不重叠/不同维度键放行。
/// 与 EffectValidationPeriodOverlap*Tests 的区别：那两组用手写参数验语句与语义，
/// 本组直接读工作区配置，验的是**真正要发布的那份参数**。
/// </summary>
[Trait("Category", "Integration")]
[Collection("live-database")]
public sealed class EffectValidationPeriodOverlapConfigLiveTests
{
    private static readonly string ConnectionString =
        Environment.GetEnvironmentVariable("MSSQL_ERP_CONN")
        ?? throw new InvalidOperationException(
            "真库集成测试需要 MSSQL_ERP_CONN；未配置即失败（无连接跳过≠已验证）。");

    private static readonly EffectValidationExecutor Executor = new();

    private static string Normalize(string? text) => Regex.Replace(text ?? string.Empty, @"\s+", " ").Trim();

    private sealed record Family(int ModuleId, string MasterTable, string DetailTable, string TypeCol, string NoCol);

    private static readonly Family Contract = new(180106, "HR_CONTRACT_M", "HR_CONTRACT_D", "CONT_TYPE", "CONT_NO");
    private static readonly Family Safe = new(180107, "HR_SAFE_M", "HR_SAFE_D", "SAFE_TYPE", "SAFE_NO");
    private static readonly Family Certify = new(180108, "HR_CERTIFY_M", "HR_CERTIFY_D", "CERTIFY_TYPE", "CERTIFY_NO");

    private static async Task<ModuleEffectPlan> LoadPlanAsync(
        SqlConnection connection, SqlTransaction transaction, Family family, CancellationToken token)
    {
        var rules = new List<EffectValidationPlan>();
        await using (var command = new SqlCommand("""
            SELECT SEQ, STAGE, VALIDATION_KEY, ENABLED, PARAM_STRUCT, MESSAGE
            FROM dbo.MODULE_VALIDATION_RULE
            WHERE M_IDX = @ModuleId AND STAGE = N'SAVE' AND ENABLED = 1
            ORDER BY SEQ;
            """, connection, transaction))
        {
            command.Parameters.Add("@ModuleId", SqlDbType.Int).Value = family.ModuleId;
            await using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
            {
                var seq = reader.GetInt32(0);
                var stage = reader.GetString(1);
                var key = reader.GetString(2);
                var enabled = reader.GetBoolean(3);
                using var parameters = JsonDocument.Parse(reader.GetString(4));
                var message = reader.IsDBNull(5) ? null : reader.GetString(5);
                rules.Add(new EffectValidationPlan(seq, stage, key, enabled, parameters.RootElement.Clone(), message));
            }
        }
        Assert.NotEmpty(rules);
        return new ModuleEffectPlan(family.ModuleId, family.MasterTable, family.DetailTable,
            "live-config", [family.TypeCol, family.NoCol], Array.Empty<EffectActionPlan>(), rules);
    }

    private static async Task<string?> RunAsync(
        Family family, string[] keyValues, string seedSql, CancellationToken token)
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync(token);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(token);
        try
        {
            await using (var seed = new SqlCommand(seedSql, connection, transaction))
                await seed.ExecuteNonQueryAsync(token);
            var plan = await LoadPlanAsync(connection, transaction, family, token);
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

    private static async Task<string> EmployeeNameAsync(SqlConnection connection, SqlTransaction transaction, CancellationToken token)
    {
        await using var pick = new SqlCommand(
            "SELECT TOP 1 LTRIM(RTRIM(EMP_NAME)) FROM dbo.HR_EMPLOYEE WHERE LTRIM(RTRIM(EMP_ID))<>'';",
            connection, transaction);
        return (string)(await pick.ExecuteScalarAsync(token))!;
    }

    private static async Task<string> EmployeeIdAsync(SqlConnection connection, SqlTransaction transaction, CancellationToken token)
    {
        await using var pick = new SqlCommand(
            "SELECT TOP 1 LTRIM(RTRIM(EMP_ID)) FROM dbo.HR_EMPLOYEE WHERE LTRIM(RTRIM(EMP_ID))<>'';",
            connection, transaction);
        return (string)(await pick.ExecuteScalarAsync(token))!;
    }

    [Fact]
    public async Task 合同_同单重复按库内配置拒绝且文案与既有实现一致()
    {
        var token = CancellationToken.None;
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync(token);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(token);
        try
        {
            var emp = await EmployeeIdAsync(connection, transaction, token);
            var name = await EmployeeNameAsync(connection, transaction, token);
            var seed = $"""
                INSERT INTO dbo.HR_CONTRACT_D (CONT_TYPE,CONT_NO,SERIAL_NO,EMP_ID,CONTRACT_NO,BEGIN_DATE,END_DATE)
                VALUES (N'ADR12K',N'K1',1,N'{emp}',N'CON1','2026-01-01','2026-12-31');
                INSERT INTO dbo.HR_CONTRACT_D (CONT_TYPE,CONT_NO,SERIAL_NO,EMP_ID,CONTRACT_NO,BEGIN_DATE,END_DATE)
                VALUES (N'ADR12K',N'K1',2,N'{emp}',N'CON1','2026-01-01','2026-12-31');
                """;
            await using (var seedCommand = new SqlCommand(seed, connection, transaction))
                await seedCommand.ExecuteNonQueryAsync(token);

            var plan = await LoadPlanAsync(connection, transaction, Contract, token);
            var message = (await Assert.ThrowsAsync<EffectValidationException>(() =>
                Executor.ValidateAsync(connection, transaction, plan, "SAVE", token, ["ADR12K", "K1"]))).Message;

            Assert.Equal(Normalize($"以下人员资料重复 \r\n{name}\t"), Normalize(message));
        }
        finally
        {
            await transaction.RollbackAsync(token);
        }
    }

    [Fact]
    public async Task 合同_跨单期间重叠按库内配置拒绝并回报冲突单据()
    {
        var token = CancellationToken.None;
        var message = await RunAsync(Contract, ["ADR12K", "K2"], """
            INSERT INTO dbo.HR_CONTRACT_D (CONT_TYPE,CONT_NO,SERIAL_NO,EMP_ID,CONTRACT_NO,BEGIN_DATE,END_DATE)
            SELECT N'ADR12K',N'K1',1,EMP_ID,N'CON1','2026-01-01','2026-12-31' FROM (SELECT TOP 1 EMP_ID FROM dbo.HR_EMPLOYEE WHERE LTRIM(RTRIM(EMP_ID))<>'') AS src(EMP_ID) ;
            INSERT INTO dbo.HR_CONTRACT_D (CONT_TYPE,CONT_NO,SERIAL_NO,EMP_ID,CONTRACT_NO,BEGIN_DATE,END_DATE)
            SELECT N'ADR12K',N'K2',1,EMP_ID,N'CON2','2026-06-01','2027-05-31' FROM (SELECT TOP 1 EMP_ID FROM dbo.HR_EMPLOYEE WHERE LTRIM(RTRIM(EMP_ID))<>'') AS src(EMP_ID) ;
            """, token);

        Assert.NotNull(message);
        Assert.Contains("以下人员的合同期间与其它单据重叠", message);
        Assert.Contains("K1", message);
        Assert.Contains("2026-01-01", message);
        Assert.DoesNotContain("{ROWS}", message);
    }

    [Fact]
    public async Task 合同_不重叠放行()
    {
        var token = CancellationToken.None;
        var message = await RunAsync(Contract, ["ADR12K", "K2"], """
            INSERT INTO dbo.HR_CONTRACT_D (CONT_TYPE,CONT_NO,SERIAL_NO,EMP_ID,CONTRACT_NO,BEGIN_DATE,END_DATE)
            SELECT N'ADR12K',N'K1',1,EMP_ID,N'CON1','2026-01-01','2026-06-29' FROM (SELECT TOP 1 EMP_ID FROM dbo.HR_EMPLOYEE WHERE LTRIM(RTRIM(EMP_ID))<>'') AS src(EMP_ID) ;
            INSERT INTO dbo.HR_CONTRACT_D (CONT_TYPE,CONT_NO,SERIAL_NO,EMP_ID,CONTRACT_NO,BEGIN_DATE,END_DATE)
            SELECT N'ADR12K',N'K2',1,EMP_ID,N'CON2','2026-06-30','2026-12-31' FROM (SELECT TOP 1 EMP_ID FROM dbo.HR_EMPLOYEE WHERE LTRIM(RTRIM(EMP_ID))<>'') AS src(EMP_ID) ;
            """, token);

        Assert.Null(message);
    }

    [Fact]
    public async Task 保险_同单同险种重复按库内配置拒绝且文案与既有实现一致()
    {
        var token = CancellationToken.None;
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync(token);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(token);
        try
        {
            var emp = await EmployeeIdAsync(connection, transaction, token);
            var name = await EmployeeNameAsync(connection, transaction, token);
            await using (var seedCommand = new SqlCommand($"""
                INSERT INTO dbo.HR_SAFE_D (SAFE_TYPE,SAFE_NO,SERIAL_NO,EMP_ID,SAFE_ID,SAFE_NUMBER,BEGIN_DATE,END_DATE)
                VALUES (N'ADR12K',N'K1',1,N'{emp}',N'INS1',N'SN1','2026-01-01','2026-12-31');
                INSERT INTO dbo.HR_SAFE_D (SAFE_TYPE,SAFE_NO,SERIAL_NO,EMP_ID,SAFE_ID,SAFE_NUMBER,BEGIN_DATE,END_DATE)
                VALUES (N'ADR12K',N'K1',2,N'{emp}',N'INS1',N'SN2','2026-01-01','2026-12-31');
                """, connection, transaction))
                await seedCommand.ExecuteNonQueryAsync(token);

            var plan = await LoadPlanAsync(connection, transaction, Safe, token);
            var message = (await Assert.ThrowsAsync<EffectValidationException>(() =>
                Executor.ValidateAsync(connection, transaction, plan, "SAVE", token, ["ADR12K", "K1"]))).Message;

            Assert.Equal(Normalize($"以下人员重复投保 \r\n{name}\tINS1"), Normalize(message));
        }
        finally
        {
            await transaction.RollbackAsync(token);
        }
    }

    [Fact]
    public async Task 保险_不同险种期间重叠放行()
    {
        var token = CancellationToken.None;
        var message = await RunAsync(Safe, ["ADR12K", "K2"], """
            INSERT INTO dbo.HR_SAFE_D (SAFE_TYPE,SAFE_NO,SERIAL_NO,EMP_ID,SAFE_ID,SAFE_NUMBER,BEGIN_DATE,END_DATE)
            SELECT N'ADR12K',N'K1',1,EMP_ID,N'INS1',N'SN1','2026-01-01','2026-12-31' FROM (SELECT TOP 1 EMP_ID FROM dbo.HR_EMPLOYEE WHERE LTRIM(RTRIM(EMP_ID))<>'') AS src(EMP_ID) ;
            INSERT INTO dbo.HR_SAFE_D (SAFE_TYPE,SAFE_NO,SERIAL_NO,EMP_ID,SAFE_ID,SAFE_NUMBER,BEGIN_DATE,END_DATE)
            SELECT N'ADR12K',N'K2',1,EMP_ID,N'INS2',N'SN2','2026-06-01','2027-05-31' FROM (SELECT TOP 1 EMP_ID FROM dbo.HR_EMPLOYEE WHERE LTRIM(RTRIM(EMP_ID))<>'') AS src(EMP_ID) ;
            """, token);

        Assert.Null(message);
    }

    [Fact]
    public async Task 保险_同单同险种期间重叠时由同单重复先拦()
    {
        var token = CancellationToken.None;
        var message = await RunAsync(Safe, ["ADR12K", "K2"], """
            INSERT INTO dbo.HR_SAFE_D (SAFE_TYPE,SAFE_NO,SERIAL_NO,EMP_ID,SAFE_ID,SAFE_NUMBER,BEGIN_DATE,END_DATE)
            SELECT N'ADR12K',N'K1',1,EMP_ID,N'INS1',N'SN1','2026-01-01','2026-12-31' FROM (SELECT TOP 1 EMP_ID FROM dbo.HR_EMPLOYEE WHERE LTRIM(RTRIM(EMP_ID))<>'') AS src(EMP_ID) ;
            INSERT INTO dbo.HR_SAFE_D (SAFE_TYPE,SAFE_NO,SERIAL_NO,EMP_ID,SAFE_ID,SAFE_NUMBER,BEGIN_DATE,END_DATE)
            SELECT N'ADR12K',N'K2',1,EMP_ID,N'INS1',N'SN2','2026-06-01','2027-05-31' FROM (SELECT TOP 1 EMP_ID FROM dbo.HR_EMPLOYEE WHERE LTRIM(RTRIM(EMP_ID))<>'') AS src(EMP_ID) ;
            """, token);

        Assert.NotNull(message);
        Assert.Contains("以下人员在此期间重复投保", message);
    }

    [Fact]
    public async Task 证件_同单同证件重复按库内配置拒绝且文案与既有实现一致()
    {
        var token = CancellationToken.None;
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync(token);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(token);
        try
        {
            var emp = await EmployeeIdAsync(connection, transaction, token);
            var name = await EmployeeNameAsync(connection, transaction, token);
            await using (var seedCommand = new SqlCommand($"""
                INSERT INTO dbo.HR_CERTIFY_D (CERTIFY_TYPE,CERTIFY_NO,SERIAL_NO,EMP_ID,CERTIFY_ID,CERTIFY_NUMBER,BEGIN_DATE,END_DATE)
                VALUES (N'ADR12K',N'K1',1,N'{emp}',N'CER1',N'CN1','2026-01-01','2026-12-31');
                INSERT INTO dbo.HR_CERTIFY_D (CERTIFY_TYPE,CERTIFY_NO,SERIAL_NO,EMP_ID,CERTIFY_ID,CERTIFY_NUMBER,BEGIN_DATE,END_DATE)
                VALUES (N'ADR12K',N'K1',2,N'{emp}',N'CER1',N'CN2','2026-01-01','2026-12-31');
                """, connection, transaction))
                await seedCommand.ExecuteNonQueryAsync(token);

            var plan = await LoadPlanAsync(connection, transaction, Certify, token);
            var message = (await Assert.ThrowsAsync<EffectValidationException>(() =>
                Executor.ValidateAsync(connection, transaction, plan, "SAVE", token, ["ADR12K", "K1"]))).Message;

            Assert.Equal(Normalize($"以下人员证件重复 \r\n{name}\tCER1"), Normalize(message));
        }
        finally
        {
            await transaction.RollbackAsync(token);
        }
    }
}
