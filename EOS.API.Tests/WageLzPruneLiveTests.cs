using System.Data;
using System.Text.Json;
using EOS.API.Data.Effects;
using EOS.API.Data.Effects.ServiceEffectHandlers;
using Microsoft.Data.SqlClient;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 离职工资表（180310/1803101，原 `hr-wage-lz` C#）入效果目录后的真库验证：
/// SAVE 期 `wage-month-doc-prune` 是"先删同月旧档、再判每人每月一份"这一对**有序步骤**的整体承接。
/// 用例把**旧 C# 的两步内联为基准**，在同一初始态下比较"剩余行集合"与"判重结论"；
/// 并单独覆盖"本单自身重复 ⇒ 拒绝且文案逐字"。造数用 `ADR12WL` 前缀，事务结束回滚。
/// </summary>
[Collection("live-database")]
public sealed class WageLzPruneLiveTests
{
    private static readonly string ConnectionString =
        Environment.GetEnvironmentVariable("MSSQL_ERP_CONN")
        ?? throw new InvalidOperationException(
            "真库集成测试需要 MSSQL_ERP_CONN；未配置即失败（无连接跳过≠已验证）。");

    private const int ModuleId = 180310;
    private const string Type = "ADR12WL";
    private const string DocA = "ADR12WL001";
    private const string DocB = "ADR12WL002";
    private const string Month = "202601";

    [Fact]
    public async Task 离职工资表_先删同月旧档再判重_与既有实现一致()
    {
        var token = CancellationToken.None;
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync(token);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(token);
        try
        {
            await SeedAsync(connection, transaction, token, duplicateInDocA: false);
            var action = await LoadActionAsync(connection, transaction, token);
            Assert.Equal("wage-month-doc-prune", action.EffectKey);
            var plan = new ModuleEffectPlan(ModuleId, "HR_WAGE_M", "HR_WAGE_D", "live-wage-lz",
                ["WAGE_TYPE", "WAGE_NO"], [action], []);

            // ① 目录效果：删掉 B 单里与 A 单员工重叠的明细，A 单保留（无重复 ⇒ 不抛）
            await new WageMonthDocPruneHandler().ExecuteAsync(
                new ServiceEffectContext(connection, transaction, plan, action, EffectEvent.Save,
                    DocA, [Type, DocA], "live-test"), token);
            var byEffect = await ReadAsync(connection, transaction, token);

            // ② 回到同一初始态跑旧 C# 两步
            await SeedAsync(connection, transaction, token, duplicateInDocA: false);
            await ExecuteAsync(connection, transaction, token, """
                DELETE d FROM dbo.HR_WAGE_D d
                WHERE EXISTS (SELECT 1 FROM dbo.HR_WAGE_M m
                              WHERE m.COUNT_MONTH=@Month AND m.WAGE_TYPE=d.WAGE_TYPE AND m.WAGE_NO=d.WAGE_NO)
                  AND NOT (d.WAGE_TYPE=@Type AND d.WAGE_NO=@No)
                  AND d.EMP_ID IN (SELECT EMP_ID FROM dbo.HR_WAGE_D WHERE WAGE_TYPE=@Type AND WAGE_NO=@No);
                """, ("@Month", Month), ("@Type", Type), ("@No", DocA));
            await ExecuteAsync(connection, transaction, token, """
                DELETE d FROM dbo.HR_WAGE_D d
                WHERE EXISTS (SELECT 1 FROM dbo.HR_WAGE_M m
                              WHERE m.COUNT_MONTH=@Month AND m.WAGE_TYPE=d.WAGE_TYPE AND m.WAGE_NO=d.WAGE_NO)
                  AND NOT (d.WAGE_TYPE=@Type AND d.WAGE_NO=@No)
                  AND d.EMP_ID IN (SELECT EMP_ID FROM dbo.HR_WAGE_D WHERE WAGE_TYPE=@Type AND WAGE_NO=@No);
                """, ("@Month", Month), ("@Type", Type), ("@No", DocA));
            var byBaseline = await ReadAsync(connection, transaction, token);

            // ③ 剩余行集合一致：B 单的 EMP1 被删、B 单 EMP9 保留；A 单两行都在
            Assert.Equal(byBaseline, byEffect);
            Assert.DoesNotContain(byEffect, row => row.No == DocB && row.Emp == "ADR12WL1");
            Assert.Contains(byEffect, row => row.No == DocB && row.Emp == "ADR12WL9");

            // ④ 本单自身重复 ⇒ 拒绝且文案逐字（旧 C# 的第二种结局）
            await SeedAsync(connection, transaction, token, duplicateInDocA: true);
            var error = await Assert.ThrowsAsync<EffectValidationException>(() =>
                new WageMonthDocPruneHandler().ExecuteAsync(
                    new ServiceEffectContext(connection, transaction, plan, action, EffectEvent.Save,
                        DocA, [Type, DocA], "live-test"), token));
            Assert.StartsWith("以下人员当月工资表重复", error.Message);
            Assert.Contains("ADR12WL1", error.Message);
        }
        finally
        {
            await transaction.RollbackAsync(token);
        }
    }

    private static async Task<List<(string Type, string No, string Emp)>> ReadAsync(
        SqlConnection connection, SqlTransaction transaction, CancellationToken token)
    {
        await using var command = new SqlCommand("""
            SELECT LTRIM(RTRIM(WAGE_TYPE)), LTRIM(RTRIM(WAGE_NO)), LTRIM(RTRIM(EMP_ID))
            FROM dbo.HR_WAGE_D WHERE WAGE_TYPE=@Type ORDER BY WAGE_NO, EMP_ID;
            """, connection, transaction);
        command.Parameters.Add("@Type", SqlDbType.NVarChar, 10).Value = Type;
        var result = new List<(string, string, string)>();
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token)) result.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        return result;
    }

    private static async Task<EffectActionPlan> LoadActionAsync(
        SqlConnection connection, SqlTransaction transaction, CancellationToken token)
    {
        await using var command = new SqlCommand("""
            SELECT SEQ, EVENT_CODE, EFFECT_KEY, EFFECT_NAME, ENABLED, FAIL_MODE,
                   CONDITION_STRUCT, PARAM_STRUCT, REVERSE_STRUCT
            FROM dbo.MODULE_BUSINESS_ACTION
            WHERE M_IDX=@ModuleId AND EVENT_CODE=N'SAVE' AND SEQ=1;
            """, connection, transaction);
        command.Parameters.Add("@ModuleId", SqlDbType.Int).Value = ModuleId;
        await using var reader = await command.ExecuteReaderAsync(token);
        Assert.True(await reader.ReadAsync(token), $"模块 {ModuleId} 缺少 SAVE 期动作");
        return new EffectActionPlan(
            reader.GetInt32(0), reader.GetString(1), reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetBoolean(4), reader.GetString(5),
            Parse(reader.IsDBNull(6) ? null : reader.GetString(6)),
            Parse(reader.IsDBNull(7) ? null : reader.GetString(7)),
            Parse(reader.IsDBNull(8) ? null : reader.GetString(8)),
            Array.Empty<EffectOpPlan>());
    }

    private static JsonElement? Parse(string? json)
        => string.IsNullOrWhiteSpace(json) ? null : JsonDocument.Parse(json).RootElement.Clone();

    private static async Task SeedAsync(
        SqlConnection connection, SqlTransaction transaction, CancellationToken token, bool duplicateInDocA)
    {
        await ExecuteAsync(connection, transaction, token, """
            DELETE FROM dbo.HR_WAGE_D WHERE WAGE_TYPE=@Type;
            DELETE FROM dbo.HR_WAGE_M WHERE WAGE_TYPE=@Type;
            INSERT INTO dbo.HR_WAGE_M (WAGE_TYPE, WAGE_NO, COUNT_MONTH) VALUES (@Type, @A, @Month), (@Type, @B, @Month);
            """, ("@Type", Type), ("@A", DocA), ("@B", DocB), ("@Month", Month));
        await ExecuteAsync(connection, transaction, token,
            "INSERT INTO dbo.HR_WAGE_D (WAGE_TYPE, WAGE_NO, SERIAL_NO, EMP_ID) VALUES (@Type, @No, @Serial, @Emp);",
            ("@Type", Type), ("@No", DocA), ("@Serial", 1), ("@Emp", "ADR12WL1"));
        if (duplicateInDocA)
        {
            await ExecuteAsync(connection, transaction, token,
                "INSERT INTO dbo.HR_WAGE_D (WAGE_TYPE, WAGE_NO, SERIAL_NO, EMP_ID) VALUES (@Type, @No, 2, @Emp);",
                ("@Type", Type), ("@No", DocA), ("@Emp", "ADR12WL1"));
        }
        else
        {
            await ExecuteAsync(connection, transaction, token,
                "INSERT INTO dbo.HR_WAGE_D (WAGE_TYPE, WAGE_NO, SERIAL_NO, EMP_ID) VALUES (@Type, @No, 2, @Emp);",
                ("@Type", Type), ("@No", DocA), ("@Emp", "ADR12WL2"));
        }
        await ExecuteAsync(connection, transaction, token,
            "INSERT INTO dbo.HR_WAGE_D (WAGE_TYPE, WAGE_NO, SERIAL_NO, EMP_ID) VALUES (@Type, @No, 1, @Emp);",
            ("@Type", Type), ("@No", DocB), ("@Emp", "ADR12WL1"));
        await ExecuteAsync(connection, transaction, token,
            "INSERT INTO dbo.HR_WAGE_D (WAGE_TYPE, WAGE_NO, SERIAL_NO, EMP_ID) VALUES (@Type, @No, 2, @Emp);",
            ("@Type", Type), ("@No", DocB), ("@Emp", "ADR12WL9"));
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
