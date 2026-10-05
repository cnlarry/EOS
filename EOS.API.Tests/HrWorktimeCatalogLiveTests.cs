using System.Data;
using System.Text.Json;
using EOS.API.Data.Effects;
using Microsoft.Data.SqlClient;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 工时录入（180207，原 `hr-worktime` C#）入校验目录后的真库验证：
/// 保存期规则 `custom-validation` → 注册实现 `hr-worktime-check`
/// （本单工时明细按员工聚合 vs 本单出勤日所属月的加班申请明细按员工聚合；受业务设置表门控）。
/// 用例覆盖：门控关闭即放行 / 门控打开且超申请即命中（文案逐字）/ 恰好等于申请量放行 /
/// 申请侧无该员工时按 0 呈现（**移植按意图修正的旧缺陷**，既有实现该分支必抛"列名无效"）。
/// 造数用 `ADR12WT` 前缀，事务结束回滚。
/// </summary>
[Trait("Category", "Integration")]
[Collection("live-database")]
public sealed class HrWorktimeCatalogLiveTests
{
    private static readonly string ConnectionString =
        Environment.GetEnvironmentVariable("MSSQL_ERP_CONN")
        ?? throw new InvalidOperationException(
            "真库集成测试需要 MSSQL_ERP_CONN；未配置即失败（无连接跳过≠已验证）。");

    private static readonly EffectValidationExecutor Executor = new();

    private const int ModuleId = 180207;
    private const string WtType = "ADR12WT";
    private const string WtNo = "ADR12WT001";
    private const string ApplyType = "ADR12WTAP";
    private const string ApplyNo = "ADR12WTAP001";
    private const string Emp1 = "ADR12WT1";
    private const string Emp2 = "ADR12WT2";
    private static readonly DateTime CountDate = new(2026, 3, 5);
    private const string Header = "以下人员时间超出:\r\n工号---加班时--休息日加班时--节假日加班时\r\n";

    [Fact]
    public async Task 工时录入_超申请加班工时命中且门控与零申请分支正确()
    {
        var token = CancellationToken.None;
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync(token);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(token);
        try
        {
            var plan = await LoadPlanAsync(connection, transaction, token);

            // ① 门控关闭（默认）：即使工时远超申请也不判（与既有实现一致——既有实现在此分支前就返回）
            await SeedAsync(connection, transaction, token, gateOn: false, appliedOverTime: 4, overTime: 100);
            await Executor.ValidateAsync(connection, transaction, plan, "SAVE", token, [WtType, WtNo]);

            // ② 门控打开 + 工时 10 > 申请 4 ⇒ 命中，文案逐字
            await SeedAsync(connection, transaction, token, gateOn: true, appliedOverTime: 4, overTime: 10);
            var exceeded = await Assert.ThrowsAsync<EffectValidationException>(() =>
                Executor.ValidateAsync(connection, transaction, plan, "SAVE", token, [WtType, WtNo]));
            Assert.Equal(Header + $"{Emp1}  4  0  0  已录入   10  0  0", exceeded.Message);

            // ③ 工时 10 = 申请 10 ⇒ 放行（判据是严格大于）
            await SeedAsync(connection, transaction, token, gateOn: true, appliedOverTime: 10, overTime: 10);
            await Executor.ValidateAsync(connection, transaction, plan, "SAVE", token, [WtType, WtNo]);

            // ④ 申请侧没有该员工的行 ⇒ 申请量按 0 呈现并命中（移植按意图修正：既有实现此处抛"列名无效"）
            await SeedAsync(connection, transaction, token, gateOn: true, appliedOverTime: 0, overTime: 7,
                appliedEmployee: Emp2);
            var noApplyRow = await Assert.ThrowsAsync<EffectValidationException>(() =>
                Executor.ValidateAsync(connection, transaction, plan, "SAVE", token, [WtType, WtNo]));
            Assert.Equal(Header + $"{Emp1}  0  0  0  已录入   7  0  0", noApplyRow.Message);

            // ⑤ 既有实现的缺陷事实（移植依据）：员工与加班列在**明细**表上，不在工时主表上
            Assert.True(await HasColumnAsync(connection, transaction, "HR_WORKTIME_D", "EMP_ID", token));
            Assert.False(await HasColumnAsync(connection, transaction, "HR_WORKTIME_M", "EMP_ID", token));
        }
        finally
        {
            await transaction.RollbackAsync(token);
        }
    }

    private static async Task<bool> HasColumnAsync(
        SqlConnection connection, SqlTransaction transaction, string table, string column, CancellationToken token)
    {
        await using var command = new SqlCommand("""
            SELECT TOP 1 1 FROM sys.columns c JOIN sys.objects o ON o.object_id=c.object_id
            WHERE o.name=@Table AND c.name=@Column;
            """, connection, transaction);
        command.Parameters.Add("@Table", SqlDbType.NVarChar, 128).Value = table;
        command.Parameters.Add("@Column", SqlDbType.NVarChar, 128).Value = column;
        return await command.ExecuteScalarAsync(token) is not null;
    }

    private static async Task<ModuleEffectPlan> LoadPlanAsync(
        SqlConnection connection, SqlTransaction transaction, CancellationToken token)
    {
        string masterTable, detailTable, pkJson, paramStruct;
        await using (var command = new SqlCommand("""
            SELECT m.MASTER_TABLE, m.DETAIL_TABLE, s.DEFINITION_JSON, r.PARAM_STRUCT
            FROM dbo.MODULES m
            JOIN dbo.WORKBENCH_DEFINITION_SNAPSHOT s ON s.M_IDX = m.M_IDX AND s.IS_CURRENT = 1
            JOIN dbo.MODULE_VALIDATION_RULE r ON r.M_IDX = m.M_IDX AND r.STAGE = N'SAVE'
                 AND r.VALIDATION_KEY = N'custom-validation'
            WHERE m.M_IDX = @ModuleId;
            """, connection, transaction))
        {
            command.Parameters.Add("@ModuleId", SqlDbType.Int).Value = ModuleId;
            await using var reader = await command.ExecuteReaderAsync(token);
            Assert.True(await reader.ReadAsync(token), "模块 180207 缺少当前快照或 SAVE 期 custom-validation 配置");
            masterTable = reader.GetString(0);
            detailTable = reader.GetString(1);
            using var definition = JsonDocument.Parse(reader.GetString(2));
            pkJson = definition.RootElement.GetProperty("MasterPkOrder").GetRawText();
            paramStruct = reader.GetString(3);
        }
        var pkOrder = JsonSerializer.Deserialize<List<string>>(pkJson)!;
        using var parameters = JsonDocument.Parse(paramStruct);
        return new ModuleEffectPlan(ModuleId, masterTable, detailTable, "live-hr-worktime", pkOrder,
            Array.Empty<EffectActionPlan>(),
            [new EffectValidationPlan(1, "SAVE", "custom-validation", true, parameters.RootElement.Clone(), null)]);
    }

    private static async Task SeedAsync(SqlConnection connection, SqlTransaction transaction, CancellationToken token,
        bool gateOn, double appliedOverTime, double overTime, string appliedEmployee = Emp1)
    {
        await ExecuteAsync(connection, transaction, token, """
            DELETE FROM dbo.HR_WORKTIME_D WHERE WORKTIME_TYPE=@WtType;
            DELETE FROM dbo.HR_WORKTIME_M WHERE WORKTIME_TYPE=@WtType;
            DELETE FROM dbo.HR_APPLY_D WHERE APPLY_TYPE=@ApplyType;
            DELETE FROM dbo.HR_APPLY_M WHERE APPLY_TYPE=@ApplyType;
            UPDATE dbo.SYSSS SET PARAM_VALUE=CONVERT(nvarchar(4000), @Gate)
                WHERE OWNER_MODULE=180213 AND PARAM_KEY=N'REQUIRE_ENACTMENT';
            """,
            ("@WtType", WtType), ("@ApplyType", ApplyType), ("@Gate", gateOn ? 1 : 0));
        await ExecuteAsync(connection, transaction, token, """
            INSERT INTO dbo.HR_WORKTIME_M (WORKTIME_TYPE, WORKTIME_NO, COUNT_DATE)
            VALUES (@WtType, @WtNo, @CountDate);
            INSERT INTO dbo.HR_WORKTIME_D (WORKTIME_TYPE, WORKTIME_NO, SERIAL_NO, EMP_ID, OVERTIME,
                                           REST_OVERTIME, HOLIDAY_OVERTIME)
            VALUES (@WtType, @WtNo, 1, @Emp1, @OverTime, 0, 0);
            """,
            ("@WtType", WtType), ("@WtNo", WtNo), ("@CountDate", CountDate), ("@Emp1", Emp1),
            ("@OverTime", overTime));
        if (appliedOverTime > 0)
        {
            await ExecuteAsync(connection, transaction, token, """
                INSERT INTO dbo.HR_APPLY_M (APPLY_TYPE, APPLY_NO, COUNT_DATE)
                VALUES (@ApplyType, @ApplyNo, @CountDate);
                INSERT INTO dbo.HR_APPLY_D (APPLY_TYPE, APPLY_NO, SERIAL_NO, EMP_ID, OVERTIME,
                                            REST_OVERTIME, HOLIDAY_OVERTIME)
                VALUES (@ApplyType, @ApplyNo, 1, @Emp, @OverTime, 0, 0);
                """,
                ("@ApplyType", ApplyType), ("@ApplyNo", ApplyNo), ("@CountDate", CountDate),
                ("@Emp", appliedEmployee), ("@OverTime", appliedOverTime));
        }
    }

    private static async Task ExecuteAsync(
        SqlConnection connection, SqlTransaction transaction, CancellationToken token, string sql,
        params (string Name, object? Value)[] parameters)
    {
        await using var command = new SqlCommand(sql, connection, transaction);
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(token);
    }
}
