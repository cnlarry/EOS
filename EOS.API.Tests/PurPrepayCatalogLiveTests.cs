using System.Data;
using System.Text.Json;
using System.Text.RegularExpressions;
using EOS.API.Data.Effects;
using EOS.API.Data.Effects.ServiceEffectHandlers;
using Microsoft.Data.SqlClient;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 预付帐款单（170203，原 `pur-prepay` C#）入效果目录后的真库验证：
///   ① 写段 `pur-prepay-rollup`：主表金额＝明细金额合计（舍入三位）——与旧过程 `P_PUR_PREPAY_After_Save` 对拍；
///   ② 引用三件套完整性（`line-require`：填了采购单号就必须填单别与序号）——按 C# 判据语义断言；
///   ③ 门控段 `qty-not-exceed`：预付金额不超采购行未结金额——按旧过程 `P_PUR_PREPAY_CHECK` 的语句链
///      比对"拒绝与否 + 文案"（诊断首列是组内最大明细序号），并覆盖门控关与额度内放行。
/// 造数用 `ADR12PP` 前缀，事务结束回滚。
/// </summary>
[Collection("live-database")]
public sealed class PurPrepayCatalogLiveTests
{
    private static readonly string ConnectionString =
        Environment.GetEnvironmentVariable("MSSQL_ERP_CONN")
        ?? throw new InvalidOperationException(
            "真库集成测试需要 MSSQL_ERP_CONN；未配置即失败（无连接跳过≠已验证）。");

    private static readonly EffectValidationExecutor Executor = new();

    private const int ModuleId = 170203;
    private const string Type = "ADR12PP";
    private const string No = "ADR12PP001";
    private const string PurType = "ADR12PP";
    private const string PurNo = "ADR12PPPO1";
    private const string Supplier = "ADR12PPSUP";

    [Fact]
    public async Task 预付帐款单_金额汇总与旧过程一致()
    {
        var token = CancellationToken.None;
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync(token);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(token);
        try
        {
            await SeedAsync(connection, transaction, token);
            var action = await LoadActionAsync(connection, transaction, token);
            Assert.Equal("detail-rollup", action.EffectKey);
            var plan = new ModuleEffectPlan(ModuleId, "PUR_PREPAY_M", "PUR_PREPAY_D", "live-pur-prepay",
                ["PREPAY_TYPE", "PREPAY_NO"], [action], []);

            await new DetailRollupHandler().ExecuteAsync(
                new ServiceEffectContext(connection, transaction, plan, action, EffectEvent.Save,
                    No, [Type, No], "live-test"), token);
            var byEffect = await ReadAmountAsync(connection, transaction, token);
            Assert.Equal(3.333, byEffect);

            await SeedAsync(connection, transaction, token);
            await ExecuteAsync(connection, transaction, token, """
                UPDATE m SET m.AMOUNT=s.AMOUNT_SUM FROM dbo.PUR_PREPAY_M m INNER JOIN (
                    SELECT d.PREPAY_TYPE, d.PREPAY_NO, ROUND(SUM(d.AMOUNT),3) AMOUNT_SUM
                      FROM dbo.PUR_PREPAY_D d WHERE d.PREPAY_TYPE=@Type AND d.PREPAY_NO=@No
                     GROUP BY d.PREPAY_TYPE, d.PREPAY_NO) s
                  ON s.PREPAY_TYPE=m.PREPAY_TYPE AND s.PREPAY_NO=m.PREPAY_NO
                WHERE m.PREPAY_TYPE=@Type AND m.PREPAY_NO=@No;
                """, ("@Type", Type), ("@No", No));
            var byBaseline = await ReadAmountAsync(connection, transaction, token);
            Assert.Equal(byBaseline, byEffect);
        }
        finally
        {
            await transaction.RollbackAsync(token);
        }
    }

    [Fact]
    public async Task 预付帐款单_引用三件套与预付额度与旧过程一致()
    {
        var token = CancellationToken.None;
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync(token);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(token);
        try
        {
            var plan = await LoadPlanAsync(connection, transaction, token);

            // ① 引用三件套：填了采购单号但缺单别/序号 ⇒ 拒绝并回报序号
            await SeedAsync(connection, transaction, token, detail: DetailIncomplete);
            var incomplete = await Assert.ThrowsAsync<EffectValidationException>(() =>
                Executor.ValidateAsync(connection, transaction, plan, "SAVE", token, [Type, No]));
            Assert.StartsWith("以下序号项已填采购单号，但未填采购单别或采购序号", Normalize(incomplete.Message));
            Assert.Contains("2", Normalize(incomplete.Message));

            // ② 只填单别不填序号 ⇒ 同样拒绝
            await SeedAsync(connection, transaction, token, detail: DetailMissingSerial);
            var missingSerial = await Assert.ThrowsAsync<EffectValidationException>(() =>
                Executor.ValidateAsync(connection, transaction, plan, "SAVE", token, [Type, No]));
            Assert.StartsWith("以下序号项已填采购单号，但未填采购单别或采购序号", Normalize(missingSerial.Message));

            // ③ 门开 + 预付金额超出采购行未结金额（合计 7 > 上限 10 - 已发生 4）
            await SeedAsync(connection, transaction, token, detail: DetailExceeding);
            var exceed = await Assert.ThrowsAsync<EffectValidationException>(() =>
                Executor.ValidateAsync(connection, transaction, plan, "SAVE", token, [Type, No]));
            var baselineExceed = await RunBaselineCheckAsync(connection, transaction, token);
            Assert.False(baselineExceed.Success);
            Assert.Equal(Normalize(baselineExceed.Message), Normalize(exceed.Message));
            Assert.Contains("以下项预付金额超出采购金额", Normalize(exceed.Message));

            // ④ 门关：同一份超额数据不再被拦
            await SeedAsync(connection, transaction, token, detail: DetailExceeding);
            await SetGateAsync(connection, transaction, token, 0);
            await Executor.ValidateAsync(connection, transaction, plan, "SAVE", token, [Type, No]);

            // ⑤ 门开 + 额度内（合计 6 ≤ 6）放行
            await SeedAsync(connection, transaction, token, detail: DetailWithin);
            await SetGateAsync(connection, transaction, token, 1);
            await Executor.ValidateAsync(connection, transaction, plan, "SAVE", token, [Type, No]);
        }
        finally
        {
            await transaction.RollbackAsync(token);
        }
    }

    /// <summary>旧过程 `P_PUR_PREPAY_CHECK` 的语句链（游标逐行拼文案）。</summary>
    private static async Task<(bool Success, string Message)> RunBaselineCheckAsync(
        SqlConnection connection, SqlTransaction transaction, CancellationToken token)
    {
        const string batch = """
            declare @success int = 1, @msg varchar(8000) = '', @serial_no int, @amount float, @my_amount float, @finished_amount float;
            declare cur_tmp cursor for
                select i.SERIAL_NO, i.AMOUNT, o.AMOUNT, o.FINISHED_AMOUNT
                  from (select PURCHASE_TYPE, PURCHASE_NO, PURCHASE_SERIAL_NO, max(SERIAL_NO) SERIAL_NO, sum(d.AMOUNT) AMOUNT
                          from PUR_PREPAY_D d where PREPAY_TYPE=@Type and PREPAY_NO=@No
                         group by PURCHASE_TYPE, PURCHASE_NO, PURCHASE_SERIAL_NO) i, PUR_PURCHASE_D o
                 where i.PURCHASE_TYPE=o.PURCHASE_TYPE and i.PURCHASE_NO=o.PURCHASE_NO
                   and i.PURCHASE_SERIAL_NO=o.SERIAL_NO and i.AMOUNT>o.AMOUNT-o.FINISHED_AMOUNT;
            open cur_tmp
            fetch next from cur_tmp into @serial_no, @my_amount, @amount, @finished_amount
            while @@FETCH_STATUS = 0
            begin
                select @success = 0, @msg = @msg + cast(@serial_no as varchar) + '    ' + cast(@amount as varchar) + '    '
                     + cast(@finished_amount as varchar) + '    ' + cast(@my_amount as varchar) + char(13)
                fetch next from cur_tmp into @serial_no, @my_amount, @amount, @finished_amount
            end
            close cur_tmp
            deallocate cur_tmp
            if @success = 0
                select @msg = '以下项预付金额超出采购金额' + char(13) + '序号  采购金额  已收金额  单据金额' + char(13) + @msg
            select @success as Ok, @msg as Msg;
            """;
        await using var command = new SqlCommand(batch, connection, transaction);
        command.Parameters.Add("@Type", SqlDbType.NVarChar, 20).Value = Type;
        command.Parameters.Add("@No", SqlDbType.NVarChar, 40).Value = No;
        await using var reader = await command.ExecuteReaderAsync(token);
        Assert.True(await reader.ReadAsync(token));
        return (Convert.ToInt32(reader.GetValue(0)) == 1, reader.IsDBNull(1) ? string.Empty : reader.GetString(1));
    }

    private static string Normalize(string value) => Regex.Replace(value, @"\s+", " ").Trim();

    private static async Task SetGateAsync(
        SqlConnection connection, SqlTransaction transaction, CancellationToken token, int value)
        => await ExecuteAsync(connection, transaction, token,
            "UPDATE dbo.MODULES SET ERROR_NO_SAVE=@Value WHERE M_IDX=170203;", ("@Value", value));

    /// <summary>明细造数脚本：两行明细（序号 1、2），采购引用按用途不同。</summary>
    private const string DetailIncomplete = """
        (@Type, @No, 1, NULL, NULL, NULL, 1.1115),
        (@Type, @No, 2, NULL, N'PO-X', NULL, 2.2215)
        """;
    private const string DetailMissingSerial = """
        (@Type, @No, 1, NULL, NULL, NULL, 1.1115),
        (@Type, @No, 2, @PurType, N'PO-X', NULL, 2.2215)
        """;
    private const string DetailExceeding = """
        (@Type, @No, 1, @PurType, @PurNo, 1, 5),
        (@Type, @No, 2, @PurType, @PurNo, 1, 2)
        """;
    private const string DetailWithin = """
        (@Type, @No, 1, @PurType, @PurNo, 1, 4),
        (@Type, @No, 2, @PurType, @PurNo, 1, 2)
        """;

    private static async Task SeedAsync(
        SqlConnection connection, SqlTransaction transaction, CancellationToken token,
        string detail = """
            (@Type, @No, 1, NULL, NULL, NULL, 1.1115),
            (@Type, @No, 2, NULL, NULL, NULL, 2.2215)
            """)
    {
        await ExecuteAsync(connection, transaction, token, """
            DELETE FROM dbo.PUR_PREPAY_D WHERE PREPAY_TYPE=@Type;
            DELETE FROM dbo.PUR_PREPAY_M WHERE PREPAY_TYPE=@Type;
            DELETE FROM dbo.PUR_PURCHASE_D WHERE PURCHASE_TYPE=@PurType;
            DELETE FROM dbo.PUR_PURCHASE_M WHERE PURCHASE_TYPE=@PurType;
            DELETE FROM dbo.SUPPLIER WHERE SUPPLIER_ID=@Supplier;
            INSERT INTO dbo.SUPPLIER (SUPPLIER_ID, BUSINESS_TAG) VALUES (@Supplier, 0);
            INSERT INTO dbo.PUR_PURCHASE_M (PURCHASE_TYPE, PURCHASE_NO) VALUES (@PurType, @PurNo);
            INSERT INTO dbo.PUR_PURCHASE_D (PURCHASE_TYPE, PURCHASE_NO, SERIAL_NO, QTY, AMOUNT, FINISHED_AMOUNT)
            VALUES (@PurType, @PurNo, 1, 10, 10, 4);
            INSERT INTO dbo.PUR_PREPAY_M (PREPAY_TYPE, PREPAY_NO, SUPPLIER_ID, AMOUNT) VALUES (@Type, @No, @Supplier, 999);
            """,
            ("@Type", Type), ("@No", No), ("@PurType", PurType), ("@PurNo", PurNo), ("@Supplier", Supplier));
        await ExecuteAsync(connection, transaction, token, $"""
            INSERT INTO dbo.PUR_PREPAY_D (PREPAY_TYPE, PREPAY_NO, SERIAL_NO, PURCHASE_TYPE, PURCHASE_NO, PURCHASE_SERIAL_NO, AMOUNT)
            VALUES {detail};
            """,
            ("@Type", Type), ("@No", No), ("@PurType", PurType), ("@PurNo", PurNo));
    }

    private static async Task<double> ReadAmountAsync(
        SqlConnection connection, SqlTransaction transaction, CancellationToken token)
    {
        await using var command = new SqlCommand(
            "SELECT ISNULL(AMOUNT,0) FROM dbo.PUR_PREPAY_M WHERE PREPAY_TYPE=@Type AND PREPAY_NO=@No;",
            connection, transaction);
        command.Parameters.Add("@Type", SqlDbType.NVarChar, 20).Value = Type;
        command.Parameters.Add("@No", SqlDbType.NVarChar, 40).Value = No;
        await using var reader = await command.ExecuteReaderAsync(token);
        Assert.True(await reader.ReadAsync(token));
        return Convert.ToDouble(reader.GetValue(0));
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

    private static async Task<ModuleEffectPlan> LoadPlanAsync(
        SqlConnection connection, SqlTransaction transaction, CancellationToken token)
    {
        string masterTable, detailTable, pkJson;
        var rules = new List<EffectValidationPlan>();
        await using (var command = new SqlCommand("""
            SELECT m.MASTER_TABLE, m.DETAIL_TABLE, s.DEFINITION_JSON
            FROM dbo.MODULES m
            JOIN dbo.WORKBENCH_DEFINITION_SNAPSHOT s ON s.M_IDX = m.M_IDX AND s.IS_CURRENT = 1
            WHERE m.M_IDX = @ModuleId;
            """, connection, transaction))
        {
            command.Parameters.Add("@ModuleId", SqlDbType.Int).Value = ModuleId;
            await using var reader = await command.ExecuteReaderAsync(token);
            Assert.True(await reader.ReadAsync(token), "模块 170203 缺少当前快照");
            masterTable = reader.GetString(0);
            detailTable = reader.GetString(1);
            using var definition = JsonDocument.Parse(reader.GetString(2));
            pkJson = definition.RootElement.GetProperty("MasterPkOrder").GetRawText();
        }
        await using (var command = new SqlCommand("""
            SELECT SEQ, VALIDATION_KEY, PARAM_STRUCT, MESSAGE FROM dbo.MODULE_VALIDATION_RULE
            WHERE M_IDX=@ModuleId AND STAGE=N'SAVE' AND ENABLED=1
              AND VALIDATION_KEY IN (N'line-require', N'qty-not-exceed') ORDER BY SEQ;
            """, connection, transaction))
        {
            command.Parameters.Add("@ModuleId", SqlDbType.Int).Value = ModuleId;
            await using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
            {
                using var parameters = JsonDocument.Parse(reader.GetString(2));
                rules.Add(new EffectValidationPlan(reader.GetInt32(0), "SAVE", reader.GetString(1), true,
                    parameters.RootElement.Clone(), reader.IsDBNull(3) ? null : reader.GetString(3)));
            }
        }
        Assert.Equal(2, rules.Count);
        var pkOrder = JsonSerializer.Deserialize<List<string>>(pkJson)!;
        return new ModuleEffectPlan(ModuleId, masterTable, detailTable, "live-pur-prepay", pkOrder,
            Array.Empty<EffectActionPlan>(), rules);
    }

    private static JsonElement? Parse(string? json)
        => string.IsNullOrWhiteSpace(json) ? null : JsonDocument.Parse(json).RootElement.Clone();

    private static async Task ExecuteAsync(
        SqlConnection connection, SqlTransaction transaction, CancellationToken token, string sql,
        params (string Name, object? Value)[] parameters)
    {
        await using var command = new SqlCommand(sql, connection, transaction);
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(token);
    }
}
