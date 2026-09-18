using System.Data;
using System.Text.Json;
using System.Text.RegularExpressions;
using EOS.API.Data.Effects;
using EOS.API.Data.Effects.ServiceEffectHandlers;
using Microsoft.Data.SqlClient;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 应收对帐单（170101，原 `cop-account` C#）与预收帐款单（170103，原 `cop-prepay` C#）入效果目录后的真库验证：
///   ① 170101 写段 `cop-account-rollup`：明细金额/税额/价税合计/数量合计回写主表，含税总额＝价税合计+其它费用
///      ——把旧过程 `P_COP_ACCOUNT_After_Save` 的汇总语句内联为基准，比较主表最终状态；
///   ② 170101 门控段 `qty-not-exceed`：对帐不超送/退货单数量（`thisQty.agg=SUM` 分组求和）——按旧过程
///      `P_COP_ACCOUNT_CHECK` 的语句链比对"拒绝与否 + 文案"，并覆盖门控关与额度内放行；
///   ③ 170103 `cop-prepay-rollup`：主表金额＝明细金额合计（舍入三位）——同样与旧过程语句对拍。
/// 造数用 `ADR12CA`/`ADR12CP` 前缀，事务结束回滚。
/// </summary>
[Collection("live-database")]
public sealed class CopAccountCatalogLiveTests
{
    private static readonly string ConnectionString =
        Environment.GetEnvironmentVariable("EOS_ERP_TEST_CONNECTION")
        ?? throw new InvalidOperationException(
            "真库集成测试需要 EOS_ERP_TEST_CONNECTION；未配置即失败（无连接跳过≠已验证）。");

    private static readonly EffectValidationExecutor Executor = new();

    private const string Type = "ADR12CA";
    private const string No = "ADR12CA001";
    private const string Client = "ADR12CACL";
    private const string SendNo = "ADR12CAS1";
    private const string ReturnNo = "ADR12CAR1";
    private const string PrepayType = "ADR12CP";
    private const string PrepayNo = "ADR12CP001";

    [Fact]
    public async Task 应收对帐单_主表金额汇总与旧过程一致()
    {
        var token = CancellationToken.None;
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync(token);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(token);
        try
        {
            await SeedAccountAsync(connection, transaction, token);
            var action = await LoadActionAsync(connection, transaction, token, 170101);
            Assert.Equal("detail-rollup", action.EffectKey);
            var plan = new ModuleEffectPlan(170101, "COP_ACCOUNT_M", "COP_ACCOUNT_D", "live-cop-account",
                ["ACCOUNT_TYPE", "ACCOUNT_NO"], [action], []);

            await new DetailRollupHandler().ExecuteAsync(
                new ServiceEffectContext(connection, transaction, plan, action, EffectEvent.Save,
                    No, [Type, No], "live-test"), token);
            var byEffect = await ReadAccountAsync(connection, transaction, token);

            // 明细：AMOUNT 100.5+50.25、TAX_SUM 13.07+6.53、AMOUNT_TAX 113.57+56.78、QTY 10+3
            Assert.Equal((150.75, 19.6, 170.35, 171.6, 13), byEffect);

            await SeedAccountAsync(connection, transaction, token);
            await ExecuteAsync(connection, transaction, token, """
                UPDATE m SET m.AMOUNT=s.AMOUNT_SUM, m.TAX_SUM=s.TAX_SUM_SUM, m.AMOUNT_TAX=s.AMOUNT_TAX_SUM,
                       m.SUM_AMOUNT=s.AMOUNT_TAX_SUM+m.OTHER_PRICE, m.QTY_TOTAL=s.QTY_SUM
                FROM dbo.COP_ACCOUNT_M m INNER JOIN (SELECT d.ACCOUNT_TYPE, d.ACCOUNT_NO,
                            ROUND(SUM(d.AMOUNT_TAX),2) AMOUNT_TAX_SUM, ROUND(SUM(d.AMOUNT),2) AMOUNT_SUM,
                            ROUND(SUM(d.TAX_SUM),2) TAX_SUM_SUM, ROUND(SUM(d.QTY),2) QTY_SUM
                        FROM dbo.COP_ACCOUNT_D d WHERE d.ACCOUNT_TYPE=@Type AND d.ACCOUNT_NO=@No
                        GROUP BY d.ACCOUNT_TYPE, d.ACCOUNT_NO) s
                  ON s.ACCOUNT_TYPE=m.ACCOUNT_TYPE AND s.ACCOUNT_NO=m.ACCOUNT_NO
                WHERE m.ACCOUNT_TYPE=@Type AND m.ACCOUNT_NO=@No;
                """, ("@Type", Type), ("@No", No));
            var byLegacy = await ReadAccountAsync(connection, transaction, token);

            Assert.Equal(byLegacy, byEffect);
        }
        finally
        {
            await transaction.RollbackAsync(token);
        }
    }

    [Fact]
    public async Task 应收对帐单_对帐不超送退货数量与旧过程一致()
    {
        var token = CancellationToken.None;
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync(token);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(token);
        try
        {
            var plan = await LoadCheckPlanAsync(connection, transaction, token);

            // ① 门开 + 送货侧超量（同一送货行两行明细合计 7，已对帐 4，上限 10 ⇒ 4+7>10）
            await SeedAccountCheckAsync(connection, transaction, token, "send", over: true);
            var sendError = await Assert.ThrowsAsync<EffectValidationException>(() =>
                Executor.ValidateAsync(connection, transaction, plan, "SAVE", token, [Type, No]));
            var legacySend = await RunLegacyCheckAsync(connection, transaction, token);
            Assert.False(legacySend.Success);
            Assert.Equal(Normalize(legacySend.Message), Normalize(sendError.Message));
            Assert.Contains($"{SendNo} 10 4 7", Normalize(sendError.Message));

            // ② 门关：同一份超量数据不再被拦
            await SetGateAsync(connection, transaction, token, 0);
            await Executor.ValidateAsync(connection, transaction, plan, "SAVE", token, [Type, No]);

            // ③ 门开 + 退货侧超量 + 文案对拍
            await SeedAccountCheckAsync(connection, transaction, token, "return", over: true);
            await SetGateAsync(connection, transaction, token, 1);
            var returnError = await Assert.ThrowsAsync<EffectValidationException>(() =>
                Executor.ValidateAsync(connection, transaction, plan, "SAVE", token, [Type, No]));
            var legacyReturn = await RunLegacyCheckAsync(connection, transaction, token);
            Assert.False(legacyReturn.Success);
            Assert.Equal(Normalize(legacyReturn.Message), Normalize(returnError.Message));

            // ④ 额度内放行（合计 6 ≤ 10）
            await SeedAccountCheckAsync(connection, transaction, token, "send", over: false);
            await Executor.ValidateAsync(connection, transaction, plan, "SAVE", token, [Type, No]);
        }
        finally
        {
            await transaction.RollbackAsync(token);
        }
    }

    [Fact]
    public async Task 预收帐款单_金额汇总与旧过程一致()
    {
        var token = CancellationToken.None;
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync(token);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(token);
        try
        {
            await SeedPrepayAsync(connection, transaction, token);
            var action = await LoadActionAsync(connection, transaction, token, 170103);
            Assert.Equal("detail-rollup", action.EffectKey);
            var plan = new ModuleEffectPlan(170103, "COP_PREPAY_M", "COP_PREPAY_D", "live-cop-prepay",
                ["PREPAY_TYPE", "PREPAY_NO"], [action], []);

            await new DetailRollupHandler().ExecuteAsync(
                new ServiceEffectContext(connection, transaction, plan, action, EffectEvent.Save,
                    PrepayNo, [PrepayType, PrepayNo], "live-test"), token);
            var byEffect = await ReadPrepayAsync(connection, transaction, token);
            Assert.Equal(3.333, byEffect);

            await SeedPrepayAsync(connection, transaction, token);
            await ExecuteAsync(connection, transaction, token, """
                UPDATE m SET m.AMOUNT=s.AMOUNT_SUM FROM dbo.COP_PREPAY_M m INNER JOIN (
                    SELECT d.PREPAY_TYPE, d.PREPAY_NO, ROUND(SUM(d.AMOUNT),3) AMOUNT_SUM
                      FROM dbo.COP_PREPAY_D d WHERE d.PREPAY_TYPE=@Type AND d.PREPAY_NO=@No
                     GROUP BY d.PREPAY_TYPE, d.PREPAY_NO) s
                  ON s.PREPAY_TYPE=m.PREPAY_TYPE AND s.PREPAY_NO=m.PREPAY_NO
                WHERE m.PREPAY_TYPE=@Type AND m.PREPAY_NO=@No;
                """, ("@Type", PrepayType), ("@No", PrepayNo));
            var byLegacy = await ReadPrepayAsync(connection, transaction, token);
            Assert.Equal(byLegacy, byEffect);
        }
        finally
        {
            await transaction.RollbackAsync(token);
        }
    }

    /// <summary>旧过程 `P_COP_ACCOUNT_CHECK` 的语句链（游标逐行拼文案）。</summary>
    private static async Task<(bool Success, string Message)> RunLegacyCheckAsync(
        SqlConnection connection, SqlTransaction transaction, CancellationToken token)
    {
        const string batch = """
            declare @success int = 1, @msg varchar(8000) = '', @s_r_no nchar(20), @qty float, @finished_qty float, @my_qty float;
            declare cur_tmp cursor for
                select od.SEND_NO, od.QTY, od.FINISHED_QTY, sd.QTY
                  from COP_SEND_D od, (select S_R_TYPE, S_R_NO, S_R_SERIAL_NO, sum(QTY) QTY
                                         from COP_ACCOUNT_D where ACCOUNT_TYPE=@Type and ACCOUNT_NO=@No
                                        group by S_R_TYPE, S_R_NO, S_R_SERIAL_NO) sd
                 where od.SEND_TYPE=sd.S_R_TYPE and od.SEND_NO=sd.S_R_NO and od.SERIAL_NO=sd.S_R_SERIAL_NO
                   and od.FINISHED_QTY+sd.QTY>od.QTY;
            open cur_tmp
            fetch next from cur_tmp into @s_r_no, @qty, @finished_qty, @my_qty
            while @@FETCH_STATUS = 0
            begin
                select @success = 0, @msg = @msg + rtrim(@s_r_no) + '    ' + cast(@qty as varchar) + '    '
                     + cast(@finished_qty as varchar) + '    ' + cast(@my_qty as varchar) + char(13)
                fetch next from cur_tmp into @s_r_no, @qty, @finished_qty, @my_qty
            end
            close cur_tmp
            deallocate cur_tmp
            if @success = 0
                select @msg = '以下对帐已超出送货单数量' + char(13) + ' 送货单号  送货数量  已对帐数量  单据数量' + char(13) + @msg
            if @success = 1
            begin
                declare cur_ret cursor for
                    select od.RETURN_NO, od.QTY, od.FINISHED_QTY, sd.QTY
                      from COP_RETURN_D od, (select S_R_TYPE, S_R_NO, S_R_SERIAL_NO, sum(QTY) QTY
                                               from COP_ACCOUNT_D where ACCOUNT_TYPE=@Type and ACCOUNT_NO=@No
                                              group by S_R_TYPE, S_R_NO, S_R_SERIAL_NO) sd
                     where od.RETURN_TYPE=sd.S_R_TYPE and od.RETURN_NO=sd.S_R_NO and od.SERIAL_NO=sd.S_R_SERIAL_NO
                       and od.FINISHED_QTY+sd.QTY>od.QTY;
                open cur_ret
                fetch next from cur_ret into @s_r_no, @qty, @finished_qty, @my_qty
                while @@FETCH_STATUS = 0
                begin
                    select @success = 0, @msg = @msg + rtrim(@s_r_no) + '    ' + cast(@qty as varchar) + '    '
                         + cast(@finished_qty as varchar) + '    ' + cast(@my_qty as varchar) + char(13)
                    fetch next from cur_ret into @s_r_no, @qty, @finished_qty, @my_qty
                end
                close cur_ret
                deallocate cur_ret
                if @success = 0
                    select @msg = '以下对帐已超出退货单数量' + char(13) + ' 退货单号  退货数量  已对帐数量  单据数量' + char(13) + @msg
            end
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
            "UPDATE dbo.MODULES SET ERROR_NO_SAVE=@Value WHERE M_IDX=170101;", ("@Value", value));

    private static async Task SeedAccountAsync(
        SqlConnection connection, SqlTransaction transaction, CancellationToken token)
        => await ExecuteAsync(connection, transaction, token, """
            DELETE FROM dbo.COP_ACCOUNT_D WHERE ACCOUNT_TYPE=@Type;
            DELETE FROM dbo.COP_ACCOUNT_M WHERE ACCOUNT_TYPE=@Type;
            INSERT INTO dbo.COP_ACCOUNT_M (ACCOUNT_TYPE, ACCOUNT_NO, OTHER_PRICE, AMOUNT, TAX_SUM, AMOUNT_TAX, SUM_AMOUNT, QTY_TOTAL)
            VALUES (@Type, @No, 1.25, 999, 999, 999, 999, 999);
            INSERT INTO dbo.COP_ACCOUNT_D (ACCOUNT_TYPE, ACCOUNT_NO, SERIAL_NO, QTY, AMOUNT, TAX_SUM, AMOUNT_TAX)
            VALUES (@Type, @No, 1, 10, 100.5, 13.07, 113.57), (@Type, @No, 2, 3, 50.25, 6.53, 56.78);
            """, ("@Type", Type), ("@No", No));

    private static async Task SeedAccountCheckAsync(
        SqlConnection connection, SqlTransaction transaction, CancellationToken token, string side, bool over)
    {
        var reference = side == "send" ? SendNo : ReturnNo;
        var first = over ? 3 : 2;
        await ExecuteAsync(connection, transaction, token, """
            DELETE FROM dbo.COP_ACCOUNT_D WHERE ACCOUNT_TYPE=@Type;
            DELETE FROM dbo.COP_ACCOUNT_M WHERE ACCOUNT_TYPE=@Type;
            DELETE FROM dbo.COP_SEND_D WHERE SEND_TYPE=@Type;
            DELETE FROM dbo.COP_RETURN_D WHERE RETURN_TYPE=@Type;
            DELETE FROM dbo.CLIENT WHERE CLIENT_ID=@Client;
            INSERT INTO dbo.CLIENT (CLIENT_ID, BUSINESS_TAG) VALUES (@Client, 0);
            INSERT INTO dbo.COP_ACCOUNT_M (ACCOUNT_TYPE, ACCOUNT_NO, CLIENT_ID) VALUES (@Type, @No, @Client);
            INSERT INTO dbo.COP_SEND_D (SEND_TYPE, SEND_NO, SERIAL_NO, QTY, FINISHED_QTY) VALUES (@Type, @SendNo, 1, 10, 4);
            INSERT INTO dbo.COP_RETURN_D (RETURN_TYPE, RETURN_NO, SERIAL_NO, QTY, FINISHED_QTY) VALUES (@Type, @ReturnNo, 1, 5, 1);
            INSERT INTO dbo.COP_ACCOUNT_D (ACCOUNT_TYPE, ACCOUNT_NO, SERIAL_NO, S_R_TYPE, S_R_NO, S_R_SERIAL_NO, QTY)
            VALUES (@Type, @No, 1, @Type, @Ref, 1, @First), (@Type, @No, 2, @Type, @Ref, 1, 4);
            """,
            ("@Type", Type), ("@No", No), ("@Client", Client), ("@SendNo", SendNo), ("@ReturnNo", ReturnNo),
            ("@Ref", reference), ("@First", first));
    }

    private static async Task SeedPrepayAsync(
        SqlConnection connection, SqlTransaction transaction, CancellationToken token)
        => await ExecuteAsync(connection, transaction, token, """
            DELETE FROM dbo.COP_PREPAY_D WHERE PREPAY_TYPE=@Type;
            DELETE FROM dbo.COP_PREPAY_M WHERE PREPAY_TYPE=@Type;
            INSERT INTO dbo.COP_PREPAY_M (PREPAY_TYPE, PREPAY_NO, AMOUNT) VALUES (@Type, @No, 999);
            INSERT INTO dbo.COP_PREPAY_D (PREPAY_TYPE, PREPAY_NO, SERIAL_NO, AMOUNT)
            VALUES (@Type, @No, 1, 1.1115), (@Type, @No, 2, 2.2215);
            """, ("@Type", PrepayType), ("@No", PrepayNo));

    private static async Task<(double Amount, double TaxSum, double AmountTax, double SumAmount, double QtyTotal)>
        ReadAccountAsync(SqlConnection connection, SqlTransaction transaction, CancellationToken token)
    {
        await using var command = new SqlCommand("""
            SELECT ISNULL(AMOUNT,0), ISNULL(TAX_SUM,0), ISNULL(AMOUNT_TAX,0), ISNULL(SUM_AMOUNT,0), ISNULL(QTY_TOTAL,0)
            FROM dbo.COP_ACCOUNT_M WHERE ACCOUNT_TYPE=@Type AND ACCOUNT_NO=@No;
            """, connection, transaction);
        command.Parameters.Add("@Type", SqlDbType.NVarChar, 20).Value = Type;
        command.Parameters.Add("@No", SqlDbType.NVarChar, 40).Value = No;
        await using var reader = await command.ExecuteReaderAsync(token);
        Assert.True(await reader.ReadAsync(token));
        return (Convert.ToDouble(reader.GetValue(0)), Convert.ToDouble(reader.GetValue(1)),
                Convert.ToDouble(reader.GetValue(2)), Convert.ToDouble(reader.GetValue(3)),
                Convert.ToDouble(reader.GetValue(4)));
    }

    private static async Task<double> ReadPrepayAsync(
        SqlConnection connection, SqlTransaction transaction, CancellationToken token)
    {
        await using var command = new SqlCommand(
            "SELECT ISNULL(AMOUNT,0) FROM dbo.COP_PREPAY_M WHERE PREPAY_TYPE=@Type AND PREPAY_NO=@No;",
            connection, transaction);
        command.Parameters.Add("@Type", SqlDbType.NVarChar, 20).Value = PrepayType;
        command.Parameters.Add("@No", SqlDbType.NVarChar, 40).Value = PrepayNo;
        await using var reader = await command.ExecuteReaderAsync(token);
        Assert.True(await reader.ReadAsync(token));
        return Convert.ToDouble(reader.GetValue(0));
    }

    private static async Task<EffectActionPlan> LoadActionAsync(
        SqlConnection connection, SqlTransaction transaction, CancellationToken token, int moduleId)
    {
        await using var command = new SqlCommand("""
            SELECT SEQ, EVENT_CODE, EFFECT_KEY, EFFECT_NAME, ENABLED, FAIL_MODE,
                   CONDITION_STRUCT, PARAM_STRUCT, REVERSE_STRUCT
            FROM dbo.MODULE_BUSINESS_ACTION
            WHERE MODULE_ID=@ModuleId AND EVENT_CODE=N'SAVE' AND SEQ=1;
            """, connection, transaction);
        command.Parameters.Add("@ModuleId", SqlDbType.Int).Value = moduleId;
        await using var reader = await command.ExecuteReaderAsync(token);
        Assert.True(await reader.ReadAsync(token), $"模块 {moduleId} 缺少 SAVE 期动作");
        return new EffectActionPlan(
            reader.GetInt32(0), reader.GetString(1), reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetBoolean(4), reader.GetString(5),
            Parse(reader.IsDBNull(6) ? null : reader.GetString(6)),
            Parse(reader.IsDBNull(7) ? null : reader.GetString(7)),
            Parse(reader.IsDBNull(8) ? null : reader.GetString(8)),
            Array.Empty<EffectOpPlan>());
    }

    private static async Task<ModuleEffectPlan> LoadCheckPlanAsync(
        SqlConnection connection, SqlTransaction transaction, CancellationToken token)
    {
        string masterTable, detailTable, pkJson, paramStruct;
        await using (var command = new SqlCommand("""
            SELECT m.MASTER_TABLE, m.DETAIL_TABLE, s.DEFINITION_JSON, r.PARAM_STRUCT
            FROM dbo.MODULES m
            JOIN dbo.WORKBENCH_DEFINITION_SNAPSHOT s ON s.MODULE_ID = m.M_IDX AND s.IS_CURRENT = 1
            JOIN dbo.MODULE_VALIDATION_RULE r ON r.MODULE_ID = m.M_IDX AND r.STAGE = N'SAVE'
                 AND r.VALIDATION_KEY = N'qty-not-exceed'
            WHERE m.M_IDX = 170101;
            """, connection, transaction))
        {
            await using var reader = await command.ExecuteReaderAsync(token);
            Assert.True(await reader.ReadAsync(token), "模块 170101 缺少当前快照或 SAVE 期 qty-not-exceed 配置");
            masterTable = reader.GetString(0);
            detailTable = reader.GetString(1);
            using var definition = JsonDocument.Parse(reader.GetString(2));
            pkJson = definition.RootElement.GetProperty("MasterPkOrder").GetRawText();
            paramStruct = reader.GetString(3);
        }
        var pkOrder = JsonSerializer.Deserialize<List<string>>(pkJson)!;
        using var parameters = JsonDocument.Parse(paramStruct);
        return new ModuleEffectPlan(170101, masterTable, detailTable, "live-cop-account-check", pkOrder,
            Array.Empty<EffectActionPlan>(),
            [new EffectValidationPlan(3, "SAVE", "qty-not-exceed", true, parameters.RootElement.Clone(), null)]);
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
