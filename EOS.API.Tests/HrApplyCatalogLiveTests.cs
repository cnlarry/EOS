using System.Data;
using System.Text.Json;
using EOS.API.Data.Effects;
using Microsoft.Data.SqlClient;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 加班申请（180206，原 `hr-apply` C#）入校验目录后的真库验证：
/// 保存期规则 `custom-validation` → 注册实现 `hr-apply-check` 的两条判据
/// （当月出勤参数未维护即拒存 + 当月**跨单据**按员工累计加班不超月度额度）。
/// 用例覆盖：缺月参数拒绝（文案带 yyyyMM）/ 跨单据累计超额命中（文案逐字）/ 恰好等于额度放行 /
/// 额度明细缺该员工且累计大于零亦拒绝。
/// 造数用 `ADR12HA` 前缀，事务结束回滚。
/// </summary>
[Collection("live-database")]
public sealed class HrApplyCatalogLiveTests
{
    private static readonly string ConnectionString =
        Environment.GetEnvironmentVariable("EOS_ERP_TEST_CONNECTION")
        ?? throw new InvalidOperationException(
            "真库集成测试需要 EOS_ERP_TEST_CONNECTION；未配置即失败（无连接跳过≠已验证）。");

    private static readonly EffectValidationExecutor Executor = new();

    private const int ModuleId = 180206;
    private const string Type = "ADR12HA";
    private const string No = "ADR12HA001";
    private const string OtherNo = "ADR12HA002";
    private const string EnaType = "ADR12EN";
    private const string EnaNo = "ADR12EN001";
    private const string Month = "202603";
    private const string Emp1 = "ADR12HA1";
    private const string Emp2 = "ADR12HA2";
    private static readonly DateTime CountDate = new(2026, 3, 10);
    private const string Header = "以下人员时间超出:\r\n工号--加班时--休息日加班时--节假日加班时\r\n";

    [Fact]
    public async Task 加班申请_月参数缺失拒绝且跨单据累计超额命中()
    {
        var token = CancellationToken.None;
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync(token);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(token);
        try
        {
            var plan = await LoadPlanAsync(connection, transaction, token);

            // ① 当月未维护出勤参数 ⇒ 先决条件型拒绝（文案带 yyyyMM）
            await SeedAsync(connection, transaction, token, monthParams: false);
            var missing = await Assert.ThrowsAsync<EffectValidationException>(() =>
                Executor.ValidateAsync(connection, transaction, plan, "SAVE", token, [Type, No]));
            Assert.Equal("未生成 202603 月度出勤参数，加班申请不能保存；请先在「每月出勤参数」维护本月的加班额度。",
                missing.Message);

            // ② 额度 8、当月累计（本单 6 + 同月另一单 4）= 10 ⇒ 命中，文案逐字（跨单据累计）
            await SeedAsync(connection, transaction, token, allowance: 8, thisDoc: 6, otherDoc: 4);
            var exceeded = await Assert.ThrowsAsync<EffectValidationException>(() =>
                Executor.ValidateAsync(connection, transaction, plan, "SAVE", token, [Type, No]));
            Assert.Equal(Header + $"{Emp1}  8  0  0  已录入  10  0  0", exceeded.Message);

            // ③ 额度 10、累计 10 ⇒ 放行（判据是"额度 < 累计"才算超）
            await SeedAsync(connection, transaction, token, allowance: 10, thisDoc: 6, otherDoc: 4);
            await Executor.ValidateAsync(connection, transaction, plan, "SAVE", token, [Type, No]);

            // ④ 额度明细缺该员工且累计大于零 ⇒ 同样拒绝（额度按 0 呈现）
            await SeedAsync(connection, transaction, token, allowance: 0, thisDoc: 5, otherDoc: 0, allowanceEmployee: Emp2);
            var noAllowanceRow = await Assert.ThrowsAsync<EffectValidationException>(() =>
                Executor.ValidateAsync(connection, transaction, plan, "SAVE", token, [Type, No]));
            Assert.Equal(Header + $"{Emp1}  0  0  0  已录入  5  0  0", noAllowanceRow.Message);
        }
        finally
        {
            await transaction.RollbackAsync(token);
        }
    }

    private static async Task<ModuleEffectPlan> LoadPlanAsync(
        SqlConnection connection, SqlTransaction transaction, CancellationToken token)
    {
        string masterTable, detailTable, pkJson, paramStruct;
        await using (var command = new SqlCommand("""
            SELECT m.MASTER_TABLE, m.DETAIL_TABLE, s.DEFINITION_JSON, r.PARAM_STRUCT
            FROM dbo.MODULES m
            JOIN dbo.WORKBENCH_DEFINITION_SNAPSHOT s ON s.MODULE_ID = m.M_IDX AND s.IS_CURRENT = 1
            JOIN dbo.MODULE_VALIDATION_RULE r ON r.MODULE_ID = m.M_IDX AND r.STAGE = N'SAVE'
                 AND r.VALIDATION_KEY = N'custom-validation'
            WHERE m.M_IDX = @ModuleId;
            """, connection, transaction))
        {
            command.Parameters.Add("@ModuleId", SqlDbType.Int).Value = ModuleId;
            await using var reader = await command.ExecuteReaderAsync(token);
            Assert.True(await reader.ReadAsync(token), "模块 180206 缺少当前快照或 SAVE 期 custom-validation 配置");
            masterTable = reader.GetString(0);
            detailTable = reader.GetString(1);
            using var definition = JsonDocument.Parse(reader.GetString(2));
            pkJson = definition.RootElement.GetProperty("MasterPkOrder").GetRawText();
            paramStruct = reader.GetString(3);
        }
        var pkOrder = JsonSerializer.Deserialize<List<string>>(pkJson)!;
        using var parameters = JsonDocument.Parse(paramStruct);
        return new ModuleEffectPlan(ModuleId, masterTable, detailTable, "live-hr-apply", pkOrder,
            Array.Empty<EffectActionPlan>(),
            [new EffectValidationPlan(2, "SAVE", "custom-validation", true, parameters.RootElement.Clone(), null)]);
    }

    private static async Task SeedAsync(SqlConnection connection, SqlTransaction transaction, CancellationToken token,
        bool monthParams = true, double allowance = 0, double thisDoc = 0, double otherDoc = 0,
        string allowanceEmployee = Emp1)
    {
        await ExecuteAsync(connection, transaction, token, """
            DELETE FROM dbo.HR_APPLY_D WHERE APPLY_TYPE=@Type;
            DELETE FROM dbo.HR_APPLY_M WHERE APPLY_TYPE=@Type;
            DELETE FROM dbo.HR_ENACTMENT_D WHERE ENACTMENT_TYPE=@EnaType;
            DELETE FROM dbo.HR_ENACTMENT_M WHERE ENACTMENT_TYPE=@EnaType;
            """,
            ("@Type", Type), ("@EnaType", EnaType));
        await ExecuteAsync(connection, transaction, token, """
            INSERT INTO dbo.HR_APPLY_M (APPLY_TYPE, APPLY_NO, COUNT_DATE) VALUES (@Type, @No, @CountDate);
            INSERT INTO dbo.HR_APPLY_D (APPLY_TYPE, APPLY_NO, SERIAL_NO, EMP_ID, OVERTIME, REST_OVERTIME, HOLIDAY_OVERTIME)
            VALUES (@Type, @No, 1, @Emp1, @ThisDoc, 0, 0);
            """,
            ("@Type", Type), ("@No", No), ("@CountDate", CountDate), ("@Emp1", Emp1), ("@ThisDoc", thisDoc));
        if (otherDoc > 0)
        {
            await ExecuteAsync(connection, transaction, token, """
                INSERT INTO dbo.HR_APPLY_M (APPLY_TYPE, APPLY_NO, COUNT_DATE) VALUES (@Type, @OtherNo, @CountDate);
                INSERT INTO dbo.HR_APPLY_D (APPLY_TYPE, APPLY_NO, SERIAL_NO, EMP_ID, OVERTIME, REST_OVERTIME, HOLIDAY_OVERTIME)
                VALUES (@Type, @OtherNo, 1, @Emp1, @OtherDoc, 0, 0);
                """,
                ("@Type", Type), ("@OtherNo", OtherNo), ("@CountDate", CountDate), ("@Emp1", Emp1),
                ("@OtherDoc", otherDoc));
        }
        if (!monthParams) return;
        await ExecuteAsync(connection, transaction, token, """
            INSERT INTO dbo.HR_ENACTMENT_M (ENACTMENT_TYPE, ENACTMENT_NO, COUNT_MONTH) VALUES (@EnaType, @EnaNo, @Month);
            INSERT INTO dbo.HR_ENACTMENT_D (ENACTMENT_TYPE, ENACTMENT_NO, SERIAL_NO, EMP_ID, OVERTIME, REST_OVERTIME, HOLIDAY_OVERTIME)
            VALUES (@EnaType, @EnaNo, 1, @Emp, @Allowance, 0, 0);
            """,
            ("@EnaType", EnaType), ("@EnaNo", EnaNo), ("@Month", Month),
            ("@Emp", allowanceEmployee), ("@Allowance", allowance));
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
