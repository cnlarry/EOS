using System.Data;
using System.Text.Json;
using System.Text.RegularExpressions;
using EOS.API.Data.Effects;
using EOS.API.Data.Effects.ServiceEffectHandlers;
using Microsoft.Data.SqlClient;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 付款单（170202，原 `pur-pay` C#）入效果目录后的真库验证：
/// SAVE 期 `pur-pay-offset` 是**先写后校验**的有序链——① 预pay冲抵汇总（PREPAY_SUM/PAYOUT_SUM/最后更新日期）；
/// ② 实付为负即拒绝（无门控）；③ 受模块门控的"实付不超应付-折扣-预付冲帐"；④ 受门控的"对帐单已付款不超应付款"
/// （四列诊断，列间 7/10/10 空格）。用例把旧过程 `P_PUR_PAY_After_Save`/`P_PUR_PAY_CHECK` 的语句链内联为基准，
/// 比较主表最终状态与"拒绝与否 + 文案"，并覆盖门控关。造数用 `ADR12PY` 前缀，事务结束回滚。
/// </summary>
[Collection("live-database")]
public sealed class PurPayCatalogLiveTests
{
    private static readonly string ConnectionString =
        Environment.GetEnvironmentVariable("EOS_ERP_TEST_CONNECTION")
        ?? throw new InvalidOperationException(
            "真库集成测试需要 EOS_ERP_TEST_CONNECTION；未配置即失败（无连接跳过≠已验证）。");

    private const int ModuleId = 170202;
    private const string Type = "ADR12PY";
    private const string No = "ADR12PY001";
    private const string DueType = "ADR12PY";
    private const string DueNo = "ADR12PYD1";

    [Fact]
    public async Task 付款单_预付冲抵与实付校验与旧过程一致()
    {
        var token = CancellationToken.None;
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync(token);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(token);
        try
        {
            var action = await LoadActionAsync(connection, transaction, token);
            Assert.Equal("pur-pay-offset", action.EffectKey);
            var plan = new ModuleEffectPlan(ModuleId, "PUR_PAY_M", "PUR_PAY_D", "live-pur-pay",
                ["PAY_TYPE", "PAY_NO"], [action], []);

            // ① 正常：应付 100、折扣 5、预付冲抵 20 ⇒ 实付 75
            await SeedAsync(connection, transaction, token, amountTax: 100, rebate: 5, prepay: 20, payout: null);
            await HandlerAsync(connection, transaction, plan, action, token);
            var byEffect = await ReadAsync(connection, transaction, token);
            Assert.Equal((20, 75), byEffect);

            await SeedAsync(connection, transaction, token, amountTax: 100, rebate: 5, prepay: 20, payout: null);
            await RunLegacyWriteAsync(connection, transaction, token);
            var byLegacy = await ReadAsync(connection, transaction, token);
            Assert.Equal(byLegacy, byEffect);

            // ② 实付为负 ⇒ 拒绝且文案逐字（无门控）
            await SeedAsync(connection, transaction, token, amountTax: 10, rebate: 0, prepay: 20, payout: null);
            var negative = await Assert.ThrowsAsync<EffectValidationException>(() =>
                HandlerAsync(connection, transaction, plan, action, token));
            Assert.Equal("实付金额不能为负数", negative.Message);

            // ③ 门开：对帐单已付款 + 本次付款超应付款 ⇒ 拒绝，四列文案与旧过程一致
            await SeedAsync(connection, transaction, token, amountTax: 100, rebate: 0, prepay: 0, payout: 60,
                dueSum: 100, duePayout: 50);
            await SetGateAsync(connection, transaction, token, 1);
            var dueError = await Assert.ThrowsAsync<EffectValidationException>(() =>
                HandlerAsync(connection, transaction, plan, action, token));
            var legacyDue = await RunLegacyCheckAsync(connection, transaction, token);
            Assert.False(legacyDue.Success);
            Assert.Equal(Normalize(legacyDue.Message), Normalize(dueError.Message));
            Assert.Contains("以下会出现对帐单已付款大于应付款", Normalize(dueError.Message));

            // ④ 门关：同一份超额数据不再被拦（写仍然生效）
            await SeedAsync(connection, transaction, token, amountTax: 100, rebate: 0, prepay: 0, payout: 60,
                dueSum: 100, duePayout: 50);
            await SetGateAsync(connection, transaction, token, 0);
            await HandlerAsync(connection, transaction, plan, action, token);
            Assert.Equal((0, 100), await ReadAsync(connection, transaction, token));
        }
        finally
        {
            await transaction.RollbackAsync(token);
        }
    }

    private static Task HandlerAsync(SqlConnection connection, SqlTransaction transaction,
        ModuleEffectPlan plan, EffectActionPlan action, CancellationToken token)
        => new PurPayOffsetHandler().ExecuteAsync(
            new ServiceEffectContext(connection, transaction, plan, action, EffectEvent.Save,
                No, [Type, No], "live-test"), token);

    /// <summary>旧过程 `P_PUR_PAY_After_Save` 的写段（汇总预付 + 实付 + 刷新日期）。</summary>
    private static Task RunLegacyWriteAsync(SqlConnection connection, SqlTransaction transaction, CancellationToken token)
        => ExecuteAsync(connection, transaction, token, """
            DECLARE @sum_prepay DECIMAL(18,2) =
                (SELECT SUM(PREPAY_AMOUNT) FROM dbo.PUR_PAY_PREPAY WHERE PAY_TYPE=@Type AND PAY_NO=@No);
            UPDATE dbo.PUR_PAY_M SET PREPAY_SUM=@sum_prepay, PAYOUT_SUM=AMOUNT_TAX-REBATE_SUM-@sum_prepay,
                   LAST_UPDATE_DATE=GETDATE()
             WHERE PAY_TYPE=@Type AND PAY_NO=@No;
            """, ("@Type", Type), ("@No", No));

    /// <summary>旧过程 `P_PUR_PAY_CHECK` 的语句链（游标逐行拼文案）。</summary>
    private static async Task<(bool Success, string Message)> RunLegacyCheckAsync(
        SqlConnection connection, SqlTransaction transaction, CancellationToken token)
    {
        const string batch = """
            declare @success int = 1, @msg varchar(8000) = '';
            declare @due_type nchar(10), @due_no nchar(20), @sum_amount float, @payout_amount float, @my_payout_amount float;
            if exists(select * from PUR_PAY_M where PAY_TYPE=@Type and PAY_NO=@No and PAYOUT_SUM>AMOUNT_TAX-REBATE_SUM-PREPAY_SUM) begin
                select @success=0, @msg = '实付金额 不能大于 应付金额-现金折扣-预付冲帐' + char(13)
            end
            if @success = 1
            begin
                declare cur_tmp cursor for
                    select od.DUE_TYPE, od.DUE_NO, od.SUM_AMOUNT, od.PAYOUT_AMOUNT, sd.PAYOUT_AMOUNT
                      from PUR_DUE_M od, (select DUE_TYPE, DUE_NO, sum(PAYOUT_AMOUNT) PAYOUT_AMOUNT
                                            from PUR_PAY_D where PAY_TYPE=@Type and PAY_NO=@No
                                           group by DUE_TYPE, DUE_NO) sd
                     where od.DUE_TYPE=sd.DUE_TYPE and od.DUE_NO=sd.DUE_NO
                       and (od.PAYOUT_AMOUNT + sd.PAYOUT_AMOUNT > od.SUM_AMOUNT);
                open cur_tmp
                fetch next from cur_tmp into @due_type, @due_no, @sum_amount, @payout_amount, @my_payout_amount
                select @msg = ''
                while @@FETCH_STATUS = 0
                begin
                    select @success = 0, @msg = @msg + rtrim(@due_no) + '       ' + cast(@sum_amount as varchar)
                         + '          ' + cast(@payout_amount as varchar) + '          ' + cast(@my_payout_amount as varchar) + char(13)
                    fetch next from cur_tmp into @due_type, @due_no, @sum_amount, @payout_amount, @my_payout_amount
                end
                close cur_tmp
                deallocate cur_tmp
                if @success = 0
                    select @msg = '以下会出现对帐单已付款大于应付款' + char(13) + '对帐单号     应付款       已付款       本次付款' + char(13) + @msg
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
            "UPDATE dbo.MODULES SET ERROR_NO_SAVE=@Value WHERE M_IDX=170202;", ("@Value", value));

    private static async Task SeedAsync(SqlConnection connection, SqlTransaction transaction, CancellationToken token,
        double amountTax, double rebate, double prepay, double? payout, double dueSum = 0, double duePayout = 0)
    {
        await ExecuteAsync(connection, transaction, token, """
            DELETE FROM dbo.PUR_PAY_D WHERE PAY_TYPE=@Type;
            DELETE FROM dbo.PUR_PAY_PREPAY WHERE PAY_TYPE=@Type;
            DELETE FROM dbo.PUR_PAY_M WHERE PAY_TYPE=@Type;
            DELETE FROM dbo.PUR_DUE_M WHERE DUE_TYPE=@DueType;
            INSERT INTO dbo.PUR_DUE_M (DUE_TYPE, DUE_NO, SUM_AMOUNT, PAYOUT_AMOUNT) VALUES (@DueType, @DueNo, @DueSum, @DuePayout);
            INSERT INTO dbo.PUR_PAY_M (PAY_TYPE, PAY_NO, AMOUNT_TAX, REBATE_SUM) VALUES (@Type, @No, @AmountTax, @Rebate);
            INSERT INTO dbo.PUR_PAY_PREPAY (PAY_TYPE, PAY_NO, SERIAL_NO, PREPAY_AMOUNT) VALUES (@Type, @No, 1, @Prepay);
            """,
            ("@Type", Type), ("@No", No), ("@DueType", DueType), ("@DueNo", DueNo),
            ("@AmountTax", amountTax), ("@Rebate", rebate), ("@Prepay", prepay),
            ("@DueSum", dueSum), ("@DuePayout", duePayout));
        if (payout is null) return;
        await ExecuteAsync(connection, transaction, token, """
            INSERT INTO dbo.PUR_PAY_D (PAY_TYPE, PAY_NO, SERIAL_NO, DUE_TYPE, DUE_NO, PAYOUT_AMOUNT)
            VALUES (@Type, @No, 1, @DueType, @DueNo, @Payout);
            """, ("@Type", Type), ("@No", No), ("@DueType", DueType), ("@DueNo", DueNo), ("@Payout", payout.Value));
    }

    private static async Task<(double PrepaySum, double PayoutSum)> ReadAsync(
        SqlConnection connection, SqlTransaction transaction, CancellationToken token)
    {
        await using var command = new SqlCommand("""
            SELECT ISNULL(PREPAY_SUM,0), ISNULL(PAYOUT_SUM,0) FROM dbo.PUR_PAY_M WHERE PAY_TYPE=@Type AND PAY_NO=@No;
            """, connection, transaction);
        command.Parameters.Add("@Type", SqlDbType.NVarChar, 20).Value = Type;
        command.Parameters.Add("@No", SqlDbType.NVarChar, 40).Value = No;
        await using var reader = await command.ExecuteReaderAsync(token);
        Assert.True(await reader.ReadAsync(token));
        return (Convert.ToDouble(reader.GetValue(0)), Convert.ToDouble(reader.GetValue(1)));
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

    private static async Task ExecuteAsync(
        SqlConnection connection, SqlTransaction transaction, CancellationToken token, string sql,
        params (string Name, object? Value)[] parameters)
    {
        await using var command = new SqlCommand(sql, connection, transaction);
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(token);
    }
}
