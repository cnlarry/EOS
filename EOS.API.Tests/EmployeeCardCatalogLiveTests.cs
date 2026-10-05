using System.Data;
using System.Text.Json;
using EOS.API.Data.Effects;
using EOS.API.Data.Effects.ServiceEffectHandlers;
using Microsoft.Data.SqlClient;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 员工发卡（180208，原 `employee-card` C#）入目录后的真库验证：
/// ① 主表行断言（失效日期不得早于生效日期，空值不违规）；② SAVE 期 `card-sibling-close`
/// 作废冲突旧卡——同一初始态下与**旧 C# 语句**比较整表结果，并覆盖"未填到期日"的旧卡。
/// 造数用 `ADR12EC` 前缀，事务结束回滚。
/// </summary>
[Trait("Category", "Integration")]
[Collection("live-database")]
public sealed class EmployeeCardCatalogLiveTests
{
    private static readonly string ConnectionString =
        Environment.GetEnvironmentVariable("MSSQL_ERP_CONN")
        ?? throw new InvalidOperationException(
            "真库集成测试需要 MSSQL_ERP_CONN；未配置即失败（无连接跳过≠已验证）。");

    private static readonly EffectValidationExecutor Validator = new();

    private const int ModuleId = 180208;
    private const string EmpA = "ADR12ECA";
    private const string EmpB = "ADR12ECB";
    private const string Card1 = "ADR12ECC1";
    private const string Card2 = "ADR12ECC2";
    private const string Card3 = "ADR12ECC3";

    private static readonly DateTime Begin = new(2024, 3, 1);
    private static readonly DateTime Expires = new(2024, 2, 29);

    [Fact]
    public async Task 员工发卡_冲突旧卡收口与旧语句一致且日期断言生效()
    {
        var token = CancellationToken.None;
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync(token);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(token);
        try
        {
            await SeedAsync(connection, transaction, token);
            var action = await LoadActionAsync(connection, transaction, token);
            Assert.Equal("card-sibling-close", action.EffectKey);
            var plan = new ModuleEffectPlan(ModuleId, "HR_EMPLOYEE_CARD", null, "live-card",
                ["EMP_ID", "CARD_ID"], [action], []);

            // ① 目录效果：作废同卡号的其他持卡人 + 同员工名下的其它卡
            //    （"未填到期日"的旧卡满足 END_DATE IS NULL；到期日早于本次生效日的不动）
            var affected = await new CardSiblingCloseHandler().ExecuteAsync(
                new ServiceEffectContext(connection, transaction, plan, action, EffectEvent.Save,
                    Card1, [EmpA, Card1], "live-test"), token);
            Assert.Equal(2, affected);
            var byEffect = await ReadAsync(connection, transaction, token);
            Assert.Equal(Expires, byEffect[(EmpB, Card1)]);              // 同卡号：未填到期日 ⇒ 收口
            Assert.Equal(Expires, byEffect[(EmpA, Card3)]);              // 同员工：未填到期日 ⇒ 收口
            Assert.Equal(new DateTime(2024, 1, 1), byEffect[(EmpA, Card2)]); // 到期日早于生效日 ⇒ 不动
            Assert.Null(byEffect[(EmpB, Card2)]);                        // 不冲突 ⇒ 不动
            Assert.Null(byEffect[(EmpA, Card1)]);                        // 本卡自身不动

            // ② 回到同一初始态后跑**旧 C# 语句**（内联基准），比较整表结果
            await SeedAsync(connection, transaction, token);
            foreach (var (column, value, otherColumn, otherValue) in new[]
                     {
                         ("CARD_ID", Card1, "EMP_ID", EmpA),
                         ("EMP_ID", EmpA, "CARD_ID", Card1),
                     })
            {
                await using var baseline = new SqlCommand($"""
                    UPDATE dbo.HR_EMPLOYEE_CARD SET END_DATE=@Expires
                    WHERE {column}=@Value AND {otherColumn}<>@Other AND (END_DATE IS NULL OR END_DATE>=@Begin);
                    """, connection, transaction);
                baseline.Parameters.Add("@Expires", SqlDbType.DateTime).Value = Expires;
                baseline.Parameters.Add("@Value", SqlDbType.NChar, 20).Value = value;
                baseline.Parameters.Add("@Other", SqlDbType.NChar, 20).Value = otherValue;
                baseline.Parameters.Add("@Begin", SqlDbType.DateTime).Value = Begin;
                Assert.Equal(1, await baseline.ExecuteNonQueryAsync(token));
            }
            var byBaseline = await ReadAsync(connection, transaction, token);
            Assert.Equal(byBaseline, byEffect);

            // ③ 行断言：失效日期早于生效日期即拒绝（旧文案逐字）
            await SetCardAsync(connection, transaction, token, EmpA, Card1, Begin, Begin.AddDays(-1));
            var blocked = await ValidateAsync(connection, transaction, token);
            Assert.Equal("失于日期应在生效日期后", blocked);

            // ④ 空值不违规（既有实现的可空比较为 false），生效日在后也不违规
            await SetCardAsync(connection, transaction, token, EmpA, Card1, Begin, null);
            Assert.Null(await ValidateAsync(connection, transaction, token));
            await SetCardAsync(connection, transaction, token, EmpA, Card1, Begin, Begin.AddDays(30));
            Assert.Null(await ValidateAsync(connection, transaction, token));
        }
        finally
        {
            await transaction.RollbackAsync(token);
        }
    }

    private static async Task<string?> ValidateAsync(SqlConnection connection, SqlTransaction transaction, CancellationToken token)
    {
        var plan = await LoadPlanAsync(connection, transaction, token);
        try
        {
            await Validator.ValidateAsync(connection, transaction, plan, "SAVE", token, [EmpA, Card1]);
            return null;
        }
        catch (EffectValidationException error)
        {
            return error.Message;
        }
    }

    private static async Task<ModuleEffectPlan> LoadPlanAsync(
        SqlConnection connection, SqlTransaction transaction, CancellationToken token)
    {
        string masterTable, pkJson, paramStruct, ruleMessage;
        await using (var command = new SqlCommand("""
            SELECT m.MASTER_TABLE, s.[DEFINITION_JSON], r.PARAM_STRUCT, ISNULL(r.MESSAGE, N'')
            FROM dbo.MODULES m
            JOIN dbo.WORKBENCH_DEFINITION_SNAPSHOT s ON s.M_IDX = m.M_IDX AND s.IS_CURRENT = 1
            JOIN dbo.MODULE_VALIDATION_RULE r ON r.M_IDX = m.M_IDX AND r.STAGE = N'SAVE' AND r.VALIDATION_KEY = N'line-require'
            WHERE m.M_IDX = @ModuleId;
            """, connection, transaction))
        {
            command.Parameters.Add("@ModuleId", SqlDbType.Int).Value = ModuleId;
            await using var reader = await command.ExecuteReaderAsync(token);
            Assert.True(await reader.ReadAsync(token), "模块 180208 缺少当前快照或 SAVE 期 line-require 配置");
            masterTable = reader.GetString(0);
            using var definition = JsonDocument.Parse(reader.GetString(1));
            pkJson = definition.RootElement.GetProperty("MasterPkOrder").GetRawText();
            paramStruct = reader.GetString(2);
            ruleMessage = reader.GetString(3);
        }
        var pkOrder = JsonSerializer.Deserialize<List<string>>(pkJson)!;
        using var parameters = JsonDocument.Parse(paramStruct);
        return new ModuleEffectPlan(ModuleId, masterTable, null, "live-card-validation", pkOrder,
            Array.Empty<EffectActionPlan>(),
            [new EffectValidationPlan(1, "SAVE", "line-require", true, parameters.RootElement.Clone(),
                string.IsNullOrWhiteSpace(ruleMessage) ? null : ruleMessage)]);
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

    private static async Task<Dictionary<(string Emp, string Card), DateTime?>> ReadAsync(
        SqlConnection connection, SqlTransaction transaction, CancellationToken token)
    {
        await using var command = new SqlCommand("""
            SELECT LTRIM(RTRIM(EMP_ID)), LTRIM(RTRIM(CARD_ID)), END_DATE
            FROM dbo.HR_EMPLOYEE_CARD WHERE EMP_ID LIKE 'ADR12EC%' OR CARD_ID LIKE 'ADR12EC%';
            """, connection, transaction);
        var result = new Dictionary<(string, string), DateTime?>();
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
            result[(reader.GetString(0), reader.GetString(1))] = reader.IsDBNull(2) ? null : reader.GetDateTime(2);
        return result;
    }

    private static async Task ResetAsync(SqlConnection connection, SqlTransaction transaction, CancellationToken token)
        => await ExecuteAsync(connection, transaction, """
            UPDATE dbo.HR_EMPLOYEE_CARD SET BEGIN_DATE=@Begin, END_DATE=NULL WHERE EMP_ID LIKE 'ADR12EC%' OR CARD_ID LIKE 'ADR12EC%';
            """, token, ("@Begin", Begin));

    private static async Task SetCardAsync(
        SqlConnection connection, SqlTransaction transaction, CancellationToken token,
        string empId, string cardId, DateTime begin, DateTime? end)
        => await ExecuteAsync(connection, transaction,
            "UPDATE dbo.HR_EMPLOYEE_CARD SET BEGIN_DATE=@Begin, END_DATE=@End WHERE EMP_ID=@Emp AND CARD_ID=@Card;",
            token, ("@Begin", begin), ("@End", (object?)end), ("@Emp", empId), ("@Card", cardId));

    private static async Task SeedAsync(SqlConnection connection, SqlTransaction transaction, CancellationToken token)
        => await ExecuteAsync(connection, transaction, """
            DELETE FROM dbo.HR_EMPLOYEE_CARD WHERE EMP_ID LIKE 'ADR12EC%' OR CARD_ID LIKE 'ADR12EC%';
            INSERT INTO dbo.HR_EMPLOYEE_CARD (EMP_ID, CARD_ID, BEGIN_DATE, END_DATE) VALUES
                (@EmpA, @Card1, @Begin, NULL),
                (@EmpB, @Card1, '2023-01-01', NULL),
                (@EmpA, @Card2, '2023-01-01', '2024-01-01'),
                (@EmpA, @Card3, '2023-01-01', NULL),
                (@EmpB, @Card2, '2023-01-01', NULL);
            """, token,
            ("@EmpA", EmpA), ("@EmpB", EmpB), ("@Card1", Card1), ("@Card2", Card2), ("@Card3", Card3), ("@Begin", Begin));

    private static async Task ExecuteAsync(
        SqlConnection connection, SqlTransaction transaction, string sql, CancellationToken token,
        params (string Name, object? Value)[] parameters)
    {
        await using var command = new SqlCommand(sql, connection, transaction);
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(token);
    }
}
