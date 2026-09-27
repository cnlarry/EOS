using System.Data;
using System.Text.Json;
using EOS.API.Data.Effects;
using EOS.API.Data.Effects.ServiceEffectHandlers;
using Microsoft.Data.SqlClient;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 工资项目设定（180301/180502，原 `hr-wage-item` / `hrm-wage-item` C#）入效果目录后的真库验证：
/// SAVE 期 `fields-metadata-sync` 把工资项目明细表的显隐/名称/格式/备注同步进 `FIELDS`。
/// 用例把**旧 C# 两条语句内联为基准**，对同一初始态分别执行"目录效果"与"旧语句"，
/// 比较 `FIELDS` 行的四项属性；另覆盖"前缀不匹配的字段先被置为不可见"。
/// 造数用 `ADR12WF` 前缀，事务结束回滚。
/// </summary>
[Collection("live-database")]
public sealed class WageItemFieldsSyncLiveTests
{
    private static readonly string ConnectionString =
        Environment.GetEnvironmentVariable("MSSQL_ERP_CONN")
        ?? throw new InvalidOperationException(
            "真库集成测试需要 MSSQL_ERP_CONN；未配置即失败（无连接跳过≠已验证）。");

    private const int ModuleId = 180301;
    private const string TargetId = "HR_WAGE_D";
    private const string FieldId = "WAGE_ITEM_ADR12WF";
    private const string StaleFieldId = "WAGE_ITEM_ADR12OLD";

    [Fact]
    public async Task 工资项目设定_字段元数据联动与旧语句一致()
    {
        var token = CancellationToken.None;
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync(token);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(token);
        try
        {
            await SeedAsync(connection, transaction, token);
            var action = await LoadActionAsync(connection, transaction, token);
            Assert.Equal("fields-metadata-sync", action.EffectKey);

            // ① 目录效果
            var affected = await new FieldsMetadataSyncHandler().ExecuteAsync(
                new ServiceEffectContext(connection, transaction,
                    new ModuleEffectPlan(ModuleId, "HR_WAGE", null, "live-wage-item", ["WAGE_ID"], [action], []),
                    action, EffectEvent.Save, FieldId, [FieldId], "live-test"), token);
            Assert.True(affected >= 2);
            var byEffect = await ReadAsync(connection, transaction, token);
            Assert.Equal((1, "ADR12 项目名", "ADR12 格式", "ADR12 备注"), byEffect[FieldId]);
            Assert.Equal(0, byEffect[StaleFieldId].Visible);   // 前缀匹配但来源已无 ⇒ 被置为不可见

            // ② 回到同一初始态跑旧 C# 两条语句
            await SeedAsync(connection, transaction, token);
            await using (var hide = new SqlCommand(
                "UPDATE dbo.FIELDS SET IS_VISIBLE=0 WHERE T_ID=@TId AND F_ID LIKE 'WAGE_ITEM%';", connection, transaction))
            {
                hide.Parameters.Add("@TId", SqlDbType.NVarChar, 50).Value = TargetId;
                await hide.ExecuteNonQueryAsync(token);
            }
            await using (var sync = new SqlCommand("""
                UPDATE f SET f.IS_VISIBLE=w.IS_USED, f.F_DESC=w.WAGE_NAME, f.DISPLAY_FORMAT=w.DISPLAY_FORMAT, f.F_REMARK=w.SQL_REMARK
                FROM dbo.FIELDS f INNER JOIN dbo.HR_WAGE w ON f.F_ID=w.WAGE_FIELD WHERE f.T_ID=@TId;
                """, connection, transaction))
            {
                sync.Parameters.Add("@TId", SqlDbType.NVarChar, 50).Value = TargetId;
                await sync.ExecuteNonQueryAsync(token);
            }
            var byLegacy = await ReadAsync(connection, transaction, token);

            // ③ 两侧 FIELDS 状态一致
            Assert.Equal(byLegacy[FieldId], byEffect[FieldId]);
            Assert.Equal(byLegacy[StaleFieldId], byEffect[StaleFieldId]);
        }
        finally
        {
            await transaction.RollbackAsync(token);
        }
    }

    private static async Task<Dictionary<string, (int Visible, string Desc, string Format, string Remark)>> ReadAsync(
        SqlConnection connection, SqlTransaction transaction, CancellationToken token)
    {
        await using var command = new SqlCommand("""
            SELECT LTRIM(RTRIM(F_ID)), ISNULL(IS_VISIBLE,0), LTRIM(RTRIM(ISNULL(F_DESC,''))),
                   LTRIM(RTRIM(ISNULL(DISPLAY_FORMAT,''))), LTRIM(RTRIM(ISNULL(F_REMARK,'')))
            FROM dbo.FIELDS WHERE T_ID=@TId AND F_ID IN (@F1, @F2);
            """, connection, transaction);
        command.Parameters.Add("@TId", SqlDbType.NVarChar, 50).Value = TargetId;
        command.Parameters.Add("@F1", SqlDbType.NVarChar, 50).Value = FieldId;
        command.Parameters.Add("@F2", SqlDbType.NVarChar, 50).Value = StaleFieldId;
        var result = new Dictionary<string, (int, string, string, string)>();
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
            result[reader.GetString(0)] = (Convert.ToInt32(reader.GetValue(1)), reader.GetString(2), reader.GetString(3), reader.GetString(4));
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

    private static async Task SeedAsync(SqlConnection connection, SqlTransaction transaction, CancellationToken token)
        => await ExecuteAsync(connection, transaction, """
            DELETE FROM dbo.HR_WAGE WHERE WAGE_FIELD IN (@F1, @F2);
            DELETE FROM dbo.FIELDS WHERE T_ID=@TId AND F_ID IN (@F1, @F2);
            INSERT INTO dbo.FIELDS (T_ID, F_ID, F_DESC, IS_VISIBLE, DISPLAY_FORMAT, F_REMARK)
            VALUES (@TId, @F1, N'旧名', 1, N'既有格式', N'旧备注'), (@TId, @F2, N'旧名', 1, N'既有格式', N'旧备注');
            INSERT INTO dbo.HR_WAGE (WAGE_ID, WAGE_FIELD, IS_USED, WAGE_NAME, DISPLAY_FORMAT, SQL_REMARK)
            VALUES (N'ADR12WFID', @F1, 1, N'ADR12 项目名', N'ADR12 格式', N'ADR12 备注');
            """, token,
            ("@TId", TargetId), ("@F1", FieldId), ("@F2", StaleFieldId));

    private static async Task ExecuteAsync(
        SqlConnection connection, SqlTransaction transaction, string sql, CancellationToken token,
        params (string Name, object? Value)[] parameters)
    {
        await using var command = new SqlCommand(sql, connection, transaction);
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(token);
    }
}
