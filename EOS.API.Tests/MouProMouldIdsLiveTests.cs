using System.Data;
using System.Text.Json;
using EOS.API.Data.Effects;
using EOS.API.Data.Effects.ServiceEffectHandlers;
using Microsoft.Data.SqlClient;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 产品模具对照（2911，原 `mou-pro` C#）入效果目录后的真库验证：
/// SAVE 期 `mould-ids-sync` 按产品回写 `dbo.f_get_pro_moulds` 汇总。
/// 用例把**旧 C# 语句内联为基准**，对同一初始态分别执行"目录效果"与"旧语句"，比较最终值；
/// 并断言参数校验是 fail-closed（keyField 非本模块主键首列即拒）。造数用 `ADR12MOU` 前缀，事务回滚。
/// </summary>
[Collection("live-database")]
public sealed class MouProMouldIdsLiveTests
{
    private static readonly string ConnectionString =
        Environment.GetEnvironmentVariable("MSSQL_ERP_CONN")
        ?? throw new InvalidOperationException(
            "真库集成测试需要 MSSQL_ERP_CONN；未配置即失败（无连接跳过≠已验证）。");

    private const int ModuleId = 2911;
    private const string ProNo = "ADR12MOUP1";

    [Fact]
    public async Task 产品模具对照_目录效果与旧语句回写结果一致且参数闭合()
    {
        var token = CancellationToken.None;
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync(token);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(token);
        try
        {
            await SeedAsync(connection, transaction, token);

            var action = await LoadSaveActionAsync(connection, transaction, token);
            Assert.Equal("mould-ids-sync", action.EffectKey);
            var plan = new ModuleEffectPlan(ModuleId, "MOU_PRO_M", null, "live-mou-pro",
                ["PRO_NO"], [action], []);

            // ① 目录效果
            var affected = await new MouldIdsSyncHandler().ExecuteAsync(
                new ServiceEffectContext(connection, transaction, plan, action, EffectEvent.Save,
                    ProNo, [ProNo], "live-test"), token);
            Assert.Equal(1, affected);
            var byEffect = await ReadAsync(connection, transaction, token);

            // ② 复位后跑旧 C# 语句
            await ResetAsync(connection, transaction, token);
            await using (var legacy = new SqlCommand(
                "UPDATE dbo.MOU_PRO_M SET MOULD_IDS=dbo.f_get_pro_moulds(PRO_NO) WHERE PRO_NO=@ProNo;",
                connection, transaction))
            {
                legacy.Parameters.Add("@ProNo", SqlDbType.NVarChar, 30).Value = ProNo;
                Assert.Equal(1, await legacy.ExecuteNonQueryAsync(token));
            }
            var byLegacy = await ReadAsync(connection, transaction, token);

            // ③ 两侧一致，且等于函数本身的输出
            Assert.Equal(byLegacy, byEffect);
            Assert.Equal(await ScalarAsync(connection, transaction, token), byEffect);

            // ④ fail-closed：keyField 必须是本模块主键首列（禁止回写他表）
            var columns = await new EffectPhysicalColumns().LoadAsync(connection, token, transaction);
            var badParams = JsonDocument.Parse("""{"table":"MOU_PRO_M","keyField":"MOULD_IDS","valueField":"MOULD_IDS"}""");
            var error = Assert.Throws<EffectConfigException>(() =>
                MouldIdsSyncHandler.Parse(badParams.RootElement, plan, columns));
            Assert.Contains("必须是本模块主键首列", error.Message);
        }
        finally
        {
            await transaction.RollbackAsync(token);
        }
    }

    private static async Task<string> ScalarAsync(SqlConnection connection, SqlTransaction transaction, CancellationToken token)
    {
        await using var command = new SqlCommand("SELECT ISNULL(dbo.f_get_pro_moulds(@ProNo), N'');", connection, transaction);
        command.Parameters.Add("@ProNo", SqlDbType.NVarChar, 30).Value = ProNo;
        return Convert.ToString(await command.ExecuteScalarAsync(token))?.Trim() ?? string.Empty;
    }

    private static async Task<string> ReadAsync(SqlConnection connection, SqlTransaction transaction, CancellationToken token)
    {
        await using var command = new SqlCommand(
            "SELECT LTRIM(RTRIM(ISNULL(MOULD_IDS, N''))) FROM dbo.MOU_PRO_M WHERE PRO_NO=@ProNo;", connection, transaction);
        command.Parameters.Add("@ProNo", SqlDbType.NVarChar, 30).Value = ProNo;
        return Convert.ToString(await command.ExecuteScalarAsync(token))?.Trim() ?? string.Empty;
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
        => string.IsNullOrWhiteSpace(json) ? null : JsonDocument.Parse(json).RootElement.Clone();

    private static async Task ResetAsync(SqlConnection connection, SqlTransaction transaction, CancellationToken token)
        => await ExecuteAsync(connection, transaction,
            "UPDATE dbo.MOU_PRO_M SET MOULD_IDS=N'STALE' WHERE PRO_NO=@ProNo;", token, ("@ProNo", ProNo));

    private static async Task SeedAsync(SqlConnection connection, SqlTransaction transaction, CancellationToken token)
        => await ExecuteAsync(connection, transaction, """
            DELETE FROM dbo.MOU_PRO_M WHERE PRO_NO=@ProNo;
            INSERT INTO dbo.MOU_PRO_M (PRO_NO, MOULD_IDS) VALUES (@ProNo, N'STALE');
            """, token, ("@ProNo", ProNo));

    private static async Task ExecuteAsync(
        SqlConnection connection, SqlTransaction transaction, string sql, CancellationToken token,
        params (string Name, object? Value)[] parameters)
    {
        await using var command = new SqlCommand(sql, connection, transaction);
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(token);
    }
}
