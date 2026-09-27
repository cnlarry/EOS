using System.Data;
using System.Text.Json;
using EOS.API.Data.Effects;
using EOS.API.Data.Effects.ServiceEffectHandlers;
using Microsoft.Data.SqlClient;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 工单 BOM（1506，原 `moc-bom-stru` C#）入效果目录后的真库验证：
/// SAVE 期 `doc-orphan-prune` 循环清理"主/明细互不引用"的行（含两处排除项：本行产品、制令主产品）。
/// 用例把**旧 C# 的循环内联为基准**，在同一初始态下比较两表剩余行集合；
/// 并覆盖"明细缺主行（主行被上一轮删掉）需要第二轮才清干净"的迭代语义。
/// 造数用 `ADR12MB` 前缀，事务结束回滚。
/// </summary>
[Collection("live-database")]
public sealed class MocBomStruPruneLiveTests
{
    private static readonly string ConnectionString =
        Environment.GetEnvironmentVariable("MSSQL_ERP_CONN")
        ?? throw new InvalidOperationException(
            "真库集成测试需要 MSSQL_ERP_CONN；未配置即失败（无连接跳过≠已验证）。");

    private const int ModuleId = 1506;
    private const string Type = "ADR12MB";
    private const string No = "ADR12MB001";
    private const string Row = "ADR12MBROW";     // 本行产品（不删）
    private const string Root = "ADR12MBROOT";   // 制令主产品（不删）
    private const string Leaf = "ADR12MBLEAF";   // 被明细引用的产品（保留）
    private const string Orphan = "ADR12MBORPH"; // 无明细引用（应删）

    [Fact]
    public async Task 工单BOM_孤儿行清理循环与既有实现一致()
    {
        var token = CancellationToken.None;
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync(token);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(token);
        try
        {
            await SeedAsync(connection, transaction, token);
            var action = await LoadActionAsync(connection, transaction, token);
            Assert.Equal("doc-orphan-prune", action.EffectKey);
            var plan = new ModuleEffectPlan(ModuleId, "MOC_BOM_STRU_M", "MOC_BOM_STRU_D", "live-moc-bom-stru",
                ["PRODUCE_TYPE", "PRODUCE_NO", "PRO_NO"], [action], []);

            // ① 目录效果：第一轮删主表 2 行（Orphan、ORP2）+ 明细 1 行（其主行 ORP2 已删）
            var affected = await new DocOrphanPruneHandler().ExecuteAsync(
                new ServiceEffectContext(connection, transaction, plan, action, EffectEvent.Save,
                    Row, [Type, No, Row], "live-test"), token);
            Assert.Equal(3, affected);
            var byEffect = await ReadAsync(connection, transaction, token);

            // ② 回到同一初始态跑旧 C# 的循环
            await SeedAsync(connection, transaction, token);
            while (true)
            {
                var master = await ExecuteAsync(connection, transaction, token, """
                    DELETE m FROM dbo.MOC_BOM_STRU_M m
                    WHERE m.PRODUCE_TYPE=@Type AND m.PRODUCE_NO=@No AND m.PRO_NO<>@Row AND m.PRO_NO<>@Root
                      AND m.PRO_NO NOT IN (SELECT ELEMENT_PRO_NO FROM dbo.MOC_BOM_STRU_D WHERE PRODUCE_TYPE=@Type AND PRODUCE_NO=@No);
                    """, ("@Type", Type), ("@No", No), ("@Row", Row), ("@Root", Root));
                var detail = await ExecuteAsync(connection, transaction, token, """
                    DELETE d FROM dbo.MOC_BOM_STRU_D d
                    WHERE d.PRODUCE_TYPE=@Type AND d.PRODUCE_NO=@No
                      AND d.PRO_NO NOT IN (SELECT PRO_NO FROM dbo.MOC_BOM_STRU_M WHERE PRODUCE_TYPE=@Type AND PRODUCE_NO=@No);
                    """, ("@Type", Type), ("@No", No));
                if (master == 0 && detail == 0) break;
            }
            var byLegacy = await ReadAsync(connection, transaction, token);

            // ③ 两表剩余行集合一致：主表保留"制令根产品 / 本行产品 / 被明细引用者"，
            //    明细只保留主行仍在的那条（另一条的主行已在上一步被删 ⇒ 需要第二轮才清掉）
            Assert.Equal(byLegacy.Master, byEffect.Master);
            Assert.Equal(byLegacy.Detail, byEffect.Detail);
            Assert.Equal(["ADR12MBLEAF", "ADR12MBROOT", "ADR12MBROW"],
                byEffect.Master.OrderBy(item => item, StringComparer.Ordinal));
            Assert.Equal(["ADR12MBLEAF"], byEffect.Detail.OrderBy(item => item, StringComparer.Ordinal));
        }
        finally
        {
            await transaction.RollbackAsync(token);
        }
    }

    private static async Task<(List<string> Master, List<string> Detail)> ReadAsync(
        SqlConnection connection, SqlTransaction transaction, CancellationToken token)
    {
        async Task<List<string>> RowsAsync(string table, string column)
        {
            await using var command = new SqlCommand(
                $"SELECT LTRIM(RTRIM({column})) FROM dbo.{table} WHERE PRODUCE_TYPE=@Type AND PRODUCE_NO=@No;",
                connection, transaction);
            command.Parameters.Add("@Type", SqlDbType.NVarChar, 10).Value = Type;
            command.Parameters.Add("@No", SqlDbType.NVarChar, 20).Value = No;
            var rows = new List<string>();
            await using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token)) rows.Add(reader.GetString(0));
            return rows;
        }
        var master = await RowsAsync("MOC_BOM_STRU_M", "PRO_NO");
        var detail = await RowsAsync("MOC_BOM_STRU_D", "ELEMENT_PRO_NO");
        return (master, detail);
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
        => await ExecuteAsync(connection, transaction, token, """
            DELETE FROM dbo.MOC_BOM_STRU_D WHERE PRODUCE_TYPE=@Type AND PRODUCE_NO=@No;
            DELETE FROM dbo.MOC_BOM_STRU_M WHERE PRODUCE_TYPE=@Type AND PRODUCE_NO=@No;
            DELETE FROM dbo.MOC_PRODUCE_M WHERE PRODUCE_TYPE=@Type AND PRODUCE_NO=@No;
            INSERT INTO dbo.MOC_PRODUCE_M (PRODUCE_TYPE, PRODUCE_NO, PRO_NO) VALUES (@Type, @No, @Root);
            INSERT INTO dbo.MOC_BOM_STRU_M (PRODUCE_TYPE, PRODUCE_NO, PRO_NO) VALUES
                (@Type, @No, @Root), (@Type, @No, @Row), (@Type, @No, @Leaf), (@Type, @No, @Orphan),
                (@Type, @No, 'ADR12MBORP2');
            INSERT INTO dbo.MOC_BOM_STRU_D (PRODUCE_TYPE, PRODUCE_NO, SERIAL_NO, PRO_NO, ELEMENT_PRO_NO) VALUES
                (@Type, @No, 1, @Root, @Leaf),
                (@Type, @No, 2, N'ADR12MBORP2', 'ADR12MBORP3');
            """, ("@Type", Type), ("@No", No), ("@Root", Root), ("@Row", Row), ("@Leaf", Leaf), ("@Orphan", Orphan));

    private static async Task<int> ExecuteAsync(
        SqlConnection connection, SqlTransaction transaction, CancellationToken token, string sql,
        params (string Name, object? Value)[] parameters)
    {
        await using var command = new SqlCommand(sql, connection, transaction);
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return await command.ExecuteNonQueryAsync(token);
    }
}
