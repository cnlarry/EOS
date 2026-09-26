using System.Data;
using System.Text.Json;
using EOS.API.Data.Effects;
using EOS.API.Data.Effects.ServiceEffectHandlers;
using Microsoft.Data.SqlClient;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 制令单族（1502/1512/1522/2803/2804，原 `moc-produce` C#）入效果目录后的真库验证：
/// SAVE 期 `link-stamp` 动作把主表订单号三列回填本单明细。
/// 用例把**旧 C# 的 UPDATE 内联为基准**，在两组同形明细行上分别执行"目录效果"与"旧语句"，
/// 逐列比较最终值（含"先写入垃圾值再被覆盖"的行），证明移植等价。
/// 造数用 `ADR12MP` 前缀，事务结束回滚。
/// </summary>
[Collection("live-database")]
public sealed class MocProduceStampLiveTests
{
    private static readonly string ConnectionString =
        Environment.GetEnvironmentVariable("MSSQL_ERP_CONN")
        ?? throw new InvalidOperationException(
            "真库集成测试需要 MSSQL_ERP_CONN；未配置即失败（无连接跳过≠已验证）。");

    private const int ModuleId = 1502;
    private const string ProduceType = "ADR12MP";
    private const string ProduceNo = "ADR12MP001";
    private const string OrderType = "ADR12OT";
    private const string OrderNo = "ADR12OT001";
    private const int OrderSerial = 7;

    [Fact]
    public async Task 制令单保存_目录效果与旧实现语句回填结果逐列一致()
    {
        var token = CancellationToken.None;
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync(token);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(token);
        try
        {
            await SeedAsync(connection, transaction, token);

            // ① 目录效果：SAVE 期 link-stamp（参数取自库内配置，保证测的是真配置）
            var action = await LoadSaveActionAsync(connection, transaction, token);
            Assert.Equal("link-stamp", action.EffectKey);
            var plan = new ModuleEffectPlan(ModuleId, "MOC_PRODUCE_M", "MOC_PRODUCE_D", "live-moc-produce",
                ["PRODUCE_TYPE", "PRODUCE_NO"], [action], []);
            var affected = await new LinkStampHandler().ExecuteAsync(
                new ServiceEffectContext(connection, transaction, plan, action, EffectEvent.Save,
                    ProduceNo, [ProduceType, ProduceNo], "live-test"), token);
            // 与旧语句同为"整单明细"，不是逐行挑选
            Assert.Equal(4, affected);
            var byEffect = await ReadAsync(connection, transaction, token);

            // ② 复位成同一初始态后跑旧 C# 语句，比较最终值
            await ResetStaleAsync(connection, transaction, token);
            await using (var legacy = new SqlCommand("""
                UPDATE d SET d.ORDER_TYPE=m.ORDER_TYPE, d.ORDER_NO=m.ORDER_NO, d.ORDER_SERIAL_NO=m.ORDER_SERIAL_NO
                FROM dbo.MOC_PRODUCE_D d INNER JOIN dbo.MOC_PRODUCE_M m
                  ON m.PRODUCE_TYPE=d.PRODUCE_TYPE AND m.PRODUCE_NO=d.PRODUCE_NO
                WHERE d.PRODUCE_TYPE=@Type AND d.PRODUCE_NO=@No;
                """, connection, transaction))
            {
                legacy.Parameters.Add("@Type", SqlDbType.NVarChar, 10).Value = ProduceType;
                legacy.Parameters.Add("@No", SqlDbType.NVarChar, 20).Value = ProduceNo;
                Assert.Equal(4, await legacy.ExecuteNonQueryAsync(token));
            }
            var byLegacy = await ReadAsync(connection, transaction, token);

            // ③ 两侧结果逐列一致，且等于主表值（覆盖了原来的垃圾值）
            Assert.Equal(byLegacy, byEffect);
            Assert.Equal(4, byEffect.Count);
            foreach (var row in byEffect)
            {
                Assert.Equal(OrderType, row.OrderType);
                Assert.Equal(OrderNo, row.OrderNo);
                Assert.Equal(OrderSerial.ToString(), row.OrderSerial);
            }
        }
        finally
        {
            await transaction.RollbackAsync(token);
        }
    }

    private static async Task<EffectActionPlan> LoadSaveActionAsync(
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
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    /// <summary>把明细的订单号三列复位为哨兵值，便于对两侧结果做"同一初始态"比较。</summary>
    private static async Task ResetStaleAsync(SqlConnection connection, SqlTransaction transaction, CancellationToken token)
        => await ExecuteAsync(connection, transaction, """
            UPDATE dbo.MOC_PRODUCE_D SET ORDER_TYPE='STALE', ORDER_NO='STALE', ORDER_SERIAL_NO=999
            WHERE PRODUCE_TYPE=@Type AND PRODUCE_NO=@No;
            """, token, ("@Type", ProduceType), ("@No", ProduceNo));

    private static async Task<List<(string Serial, string OrderType, string OrderNo, string OrderSerial)>> ReadAsync(
        SqlConnection connection, SqlTransaction transaction, CancellationToken token)
    {
        await using var command = new SqlCommand("""
            SELECT CONVERT(nvarchar(10), SERIAL_NO), LTRIM(RTRIM(ISNULL(ORDER_TYPE,''))),
                   LTRIM(RTRIM(ISNULL(ORDER_NO,''))), LTRIM(RTRIM(ISNULL(ORDER_SERIAL_NO,'')))
            FROM dbo.MOC_PRODUCE_D WHERE PRODUCE_TYPE=@Type AND PRODUCE_NO=@No ORDER BY SERIAL_NO;
            """, connection, transaction);
        command.Parameters.Add("@Type", SqlDbType.NVarChar, 10).Value = ProduceType;
        command.Parameters.Add("@No", SqlDbType.NVarChar, 20).Value = ProduceNo;
        var result = new List<(string, string, string, string)>();
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
            result.Add((reader.GetString(0).Trim(), reader.GetString(1), reader.GetString(2), reader.GetString(3)));
        return result;
    }

    private static async Task SeedAsync(SqlConnection connection, SqlTransaction transaction, CancellationToken token)
    {
        await ExecuteAsync(connection, transaction, """
            DELETE FROM dbo.MOC_PRODUCE_D WHERE PRODUCE_TYPE=@Type AND PRODUCE_NO=@No;
            DELETE FROM dbo.MOC_PRODUCE_M WHERE PRODUCE_TYPE=@Type AND PRODUCE_NO=@No;
            INSERT INTO dbo.MOC_PRODUCE_M (PRODUCE_TYPE, PRODUCE_NO, ORDER_TYPE, ORDER_NO, ORDER_SERIAL_NO)
                VALUES (@Type, @No, @OrderType, @OrderNo, @OrderSerial);
            INSERT INTO dbo.MOC_PRODUCE_D (PRODUCE_TYPE, PRODUCE_NO, SERIAL_NO, PRO_NO, ORDER_TYPE, ORDER_NO, ORDER_SERIAL_NO)
                VALUES (@Type, @No, 1, 'ADR12MPP1', 'STALE', 'STALE', 999),
                       (@Type, @No, 2, 'ADR12MPP2', 'STALE', 'STALE', 999),
                       (@Type, @No, 3, 'ADR12MPP3', 'STALE', 'STALE', 999),
                       (@Type, @No, 4, 'ADR12MPP4', 'STALE', 'STALE', 999);
            """, token,
            ("@Type", ProduceType), ("@No", ProduceNo),
            ("@OrderType", OrderType), ("@OrderNo", OrderNo), ("@OrderSerial", OrderSerial));
    }

    private static async Task ExecuteAsync(
        SqlConnection connection, SqlTransaction transaction, string sql, CancellationToken token,
        params (string Name, object? Value)[] parameters)
    {
        await using var command = new SqlCommand(sql, connection, transaction);
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(token);
    }
}
