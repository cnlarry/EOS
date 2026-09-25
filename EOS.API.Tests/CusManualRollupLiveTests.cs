using System.Data;
using System.Text.Json;
using EOS.API.Data.Effects;
using EOS.API.Data.Effects.ServiceEffectHandlers;
using Microsoft.Data.SqlClient;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 加工备案手册（3006，原 `cus-manual` C#）入效果目录后的真库验证：
/// SAVE 期 `detail-flag-and-rollup` 四步链（可用标记 → 主表价格下发 → 明细金额回算 → 主表汇总）。
/// 用例把**旧 C# 四条语句内联为基准**，对同一初始态分别执行"目录效果"与"旧语句"，比较两表最终状态；
/// 并专门覆盖"明细金额＝**原**单价 × 数量"这一 SQL Server SET 右值取旧值的语义。
/// 造数用 `ADR12CM` 前缀，事务结束回滚。
/// </summary>
[Collection("live-database")]
public sealed class CusManualRollupLiveTests
{
    private static readonly string ConnectionString =
        Environment.GetEnvironmentVariable("EOS_ERP_TEST_CONNECTION")
        ?? throw new InvalidOperationException(
            "真库集成测试需要 EOS_ERP_TEST_CONNECTION；未配置即失败（无连接跳过≠已验证）。");

    private const int ModuleId = 3006;
    private const string ManualNo = "ADR12CM001";

    [Fact]
    public async Task 加工备案手册_可用标记与金额汇总与旧语句一致()
    {
        var token = CancellationToken.None;
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync(token);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(token);
        try
        {
            await SeedAsync(connection, transaction, token);
            var action = await LoadActionAsync(connection, transaction, token);
            Assert.Equal("detail-flag-and-rollup", action.EffectKey);

            // ① 目录效果
            var affected = await new DetailFlagAndRollupHandler().ExecuteAsync(
                new ServiceEffectContext(connection, transaction,
                    new ModuleEffectPlan(ModuleId, "CUS_MANUAL_M", "CUS_MANUAL_PRO", "live-cus-manual",
                        ["MANUAL_NO"], [action], []),
                    action, EffectEvent.Save, ManualNo, [ManualNo], "live-test"), token);
            Assert.True(affected >= 4);
            var byEffect = await ReadAsync(connection, transaction, token);

            // 有 BOM 行的明细置可用；金额＝原单价×数量（33*2=66、0*5=0）；主表＝明细求和
            Assert.Equal((1, 12.5, 66.0), byEffect.Details[1]);
            Assert.Equal((0, 12.5, 0.0), byEffect.Details[2]);
            Assert.Equal((66.0, 7.0), byEffect.Master);

            // ② 回到同一初始态跑旧 C# 四条语句
            await SeedAsync(connection, transaction, token);
            await ExecuteAsync(connection, transaction, token,
                "UPDATE dbo.CUS_MANUAL_PRO SET USE_STATE=0 WHERE MANUAL_NO=@No;", ("@No", ManualNo));
            await ExecuteAsync(connection, transaction, token, """
                UPDATE dbo.CUS_MANUAL_PRO SET USE_STATE=1 WHERE MANUAL_NO=@No
                  AND EXISTS (SELECT 1 FROM dbo.CUS_MANUAL_BOM WHERE SERIAL_NO=CUS_MANUAL_PRO.SERIAL_NO AND MANUAL_NO=@No);
                """, ("@No", ManualNo));
            await ExecuteAsync(connection, transaction, token, """
                UPDATE p SET p.PROCESS_PRICE=m.PROCESS_PRICE, p.PROCESS_AMOUNT=p.PROCESS_PRICE*p.CUS_QTY
                FROM dbo.CUS_MANUAL_PRO p INNER JOIN dbo.CUS_MANUAL_M m ON m.MANUAL_NO=p.MANUAL_NO WHERE p.MANUAL_NO=@No;
                """, ("@No", ManualNo));
            await ExecuteAsync(connection, transaction, token, """
                UPDATE m SET m.PROCESS_AMOUNT=d.PROCESS_AMOUNT, m.CUS_QTY=d.CUS_QTY
                FROM dbo.CUS_MANUAL_M m INNER JOIN (SELECT MANUAL_NO, ROUND(SUM(PROCESS_AMOUNT),2) PROCESS_AMOUNT, ROUND(SUM(CUS_QTY),2) CUS_QTY
                                                    FROM dbo.CUS_MANUAL_PRO WHERE MANUAL_NO=@No GROUP BY MANUAL_NO) d
                  ON d.MANUAL_NO=m.MANUAL_NO WHERE m.MANUAL_NO=@No;
                """, ("@No", ManualNo));
            var byLegacy = await ReadAsync(connection, transaction, token);

            // ③ 两表状态逐项一致
            Assert.Equal(byLegacy.Master, byEffect.Master);
            Assert.Equal(byLegacy.Details, byEffect.Details);
        }
        finally
        {
            await transaction.RollbackAsync(token);
        }
    }

    private static async Task<((double Amount, double Qty) Master, Dictionary<int, (int State, double Price, double Amount)> Details)> ReadAsync(
        SqlConnection connection, SqlTransaction transaction, CancellationToken token)
    {
        double masterAmount, masterQty;
        await using (var master = new SqlCommand(
            "SELECT ISNULL(PROCESS_AMOUNT,0), ISNULL(CUS_QTY,0) FROM dbo.CUS_MANUAL_M WHERE MANUAL_NO=@No;", connection, transaction))
        {
            master.Parameters.Add("@No", SqlDbType.NVarChar, 50).Value = ManualNo;
            await using var reader = await master.ExecuteReaderAsync(token);
            Assert.True(await reader.ReadAsync(token));
            masterAmount = Convert.ToDouble(reader.GetValue(0));
            masterQty = Convert.ToDouble(reader.GetValue(1));
        }
        var details = new Dictionary<int, (int, double, double)>();
        await using (var detail = new SqlCommand("""
            SELECT SERIAL_NO, ISNULL(USE_STATE,0), ISNULL(PROCESS_PRICE,0), ISNULL(PROCESS_AMOUNT,0)
            FROM dbo.CUS_MANUAL_PRO WHERE MANUAL_NO=@No ORDER BY SERIAL_NO;
            """, connection, transaction))
        {
            detail.Parameters.Add("@No", SqlDbType.NVarChar, 50).Value = ManualNo;
            await using var reader = await detail.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
                details[Convert.ToInt32(reader.GetValue(0))] =
                    (Convert.ToInt32(reader.GetValue(1)), Convert.ToDouble(reader.GetValue(2)), Convert.ToDouble(reader.GetValue(3)));
        }
        return ((masterAmount, masterQty), details);
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

    private static async Task SeedAsync(SqlConnection connection, SqlTransaction transaction, CancellationToken token)
        => await ExecuteAsync(connection, transaction, """
            DELETE FROM dbo.CUS_MANUAL_BOM WHERE MANUAL_NO=@No;
            DELETE FROM dbo.CUS_MANUAL_PRO WHERE MANUAL_NO=@No;
            DELETE FROM dbo.CUS_MANUAL_M WHERE MANUAL_NO=@No;
            INSERT INTO dbo.CUS_MANUAL_M (MANUAL_NO, PROCESS_PRICE, PROCESS_AMOUNT, CUS_QTY) VALUES (@No, 12.5, 999, 999);
            INSERT INTO dbo.CUS_MANUAL_PRO (MANUAL_NO, SERIAL_NO, USE_STATE, PROCESS_PRICE, CUS_QTY)
            VALUES (@No, 1, 0, 33, 2), (@No, 2, 1, 0, 5);
            INSERT INTO dbo.CUS_MANUAL_BOM (MANUAL_NO, SERIAL_NO, BOM_SERIAL_NO) VALUES (@No, 1, 1);
            """, token, ("@No", ManualNo));

    private static async Task ExecuteAsync(
        SqlConnection connection, SqlTransaction transaction, string sql, CancellationToken token,
        params (string Name, object? Value)[] parameters)
    {
        await using var command = new SqlCommand(sql, connection, transaction);
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(token);
    }

    private static Task ExecuteAsync(
        SqlConnection connection, SqlTransaction transaction, CancellationToken token, string sql,
        params (string Name, object? Value)[] parameters)
        => ExecuteAsync(connection, transaction, sql, token, parameters);
}
