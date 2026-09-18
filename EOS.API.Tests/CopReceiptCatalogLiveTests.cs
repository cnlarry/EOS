using System.Data;
using System.Text.Json;
using System.Text.RegularExpressions;
using EOS.API.Data.Effects;
using EOS.API.Data.Effects.ServiceEffectHandlers;
using Microsoft.Data.SqlClient;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 收款单（170102，原 `cop-receipt` C#）入效果目录后的真库验证：
/// SAVE 期 `cop-receipt-offset` 与付款单是同一段旧逻辑的拷贝——① 预收冲抵汇总（PREPAY_SUM/RECEIVE_SUM/最后更新日期）；
/// ② 实收为负即拒绝（无门控）；③ 受模块门控的"实收不超应收-折扣-预收冲帐（+0.1）"（写后恒不成立，属旧实现的死分支，
/// 用例只断言"不触发"）；④ 受门控的"对帐单已收款不超应收款"（四列诊断，列间 7/10/10 空格）。
/// 用例把旧过程 `P_COP_RECEIPT_After_Save`/`P_COP_RECEIPT_CHECK` 的语句链内联为基准，比较主表状态与拒绝文案，
/// 并覆盖门控关。造数用 `ADR12CR` 前缀，事务结束回滚。
/// </summary>
[Collection("live-database")]
public sealed class CopReceiptCatalogLiveTests
{
    private static readonly string ConnectionString =
        Environment.GetEnvironmentVariable("EOS_ERP_TEST_CONNECTION")
        ?? throw new InvalidOperationException(
            "真库集成测试需要 EOS_ERP_TEST_CONNECTION；未配置即失败（无连接跳过≠已验证）。");

    private const int ModuleId = 170102;
    private const string Type = "ADR12CR";
    private const string No = "ADR12CR001";
    private const string DueType = "ADR12CR";
    private const string DueNo = "ADR12CRD1";

    [Fact]
    public async Task 收款单_预收冲抵与实收校验与旧过程一致()
    {
        var token = CancellationToken.None;
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync(token);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(token);
        try
        {
            var action = await LoadActionAsync(connection, transaction, token);
            Assert.Equal("cop-receipt-offset", action.EffectKey);
            var plan = new ModuleEffectPlan(ModuleId, "COP_RECEIPT_M", "COP_RECEIPT_D", "live-cop-receipt",
                ["RECEIPT_TYPE", "RECEIPT_NO"], [action], []);

            // ① 正常：应收 100、折扣 5、预收冲抵 20 ⇒ 实收 75
            await SeedAsync(connection, transaction, token, amountTax: 100, rebate: 5, prepay: 20, receive: null);
            await HandlerAsync(connection, transaction, plan, action, token);
            var byEffect = await ReadAsync(connection, transaction, token);
            Assert.Equal((20, 75), byEffect);

            await SeedAsync(connection, transaction, token, amountTax: 100, rebate: 5, prepay: 20, receive: null);
            await RunLegacyWriteAsync(connection, transaction, token);
            var byLegacy = await ReadAsync(connection, transaction, token);
            Assert.Equal(byLegacy, byEffect);

            // ② 实收为负 ⇒ 拒绝且文案逐字（无门控）
            await SeedAsync(connection, transaction, token, amountTax: 10, rebate: 0, prepay: 20, receive: null);
            var negative = await Assert.ThrowsAsync<EffectValidationException>(() =>
                HandlerAsync(connection, transaction, plan, action, token));
            Assert.Equal("实收金额不能为负数", negative.Message);

            // ③ 门开：对帐单已收款 + 本次收款超应收款 ⇒ 拒绝，四列文案与旧过程一致
            await SeedAsync(connection, transaction, token, amountTax: 100, rebate: 0, prepay: 0, receive: 60,
                dueSum: 100, dueReceive: 50);
            await SetGateAsync(connection, transaction, token, 1);
            var dueError = await Assert.ThrowsAsync<EffectValidationException>(() =>
                HandlerAsync(connection, transaction, plan, action, token));
            var legacyDue = await RunLegacyCheckAsync(connection, transaction, token);
            Assert.False(legacyDue.Success);
            Assert.Equal(Normalize(legacyDue.Message), Normalize(dueError.Message));
            Assert.Contains("以下会出现对帐单已收款大于应收款", Normalize(dueError.Message));

            // ④ 门关：同一份超额数据不再被拦（写仍然生效）
            await SeedAsync(connection, transaction, token, amountTax: 100, rebate: 0, prepay: 0, receive: 60,
                dueSum: 100, dueReceive: 50);
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
        => new CopReceiptOffsetHandler().ExecuteAsync(
            new ServiceEffectContext(connection, transaction, plan, action, EffectEvent.Save,
                No, [Type, No], "live-test"), token);

    /// <summary>旧过程 `P_COP_RECEIPT_After_Save` 的写段（汇总预收 + 实收 + 刷新日期）。</summary>
    private static Task RunLegacyWriteAsync(SqlConnection connection, SqlTransaction transaction, CancellationToken token)
        => ExecuteAsync(connection, transaction, token, """
            DECLARE @sum_prepay DECIMAL(18,2) =
                (SELECT SUM(PREPAY_AMOUNT) FROM dbo.COP_RECEIPT_PREPAY WHERE RECEIPT_TYPE=@Type AND RECEIPT_NO=@No);
            UPDATE dbo.COP_RECEIPT_M SET PREPAY_SUM=@sum_prepay, RECEIVE_SUM=AMOUNT_TAX-REBATE_SUM-@sum_prepay,
                   LAST_UPDATE_DATE=GETDATE()
             WHERE RECEIPT_TYPE=@Type AND RECEIPT_NO=@No;
            """, ("@Type", Type), ("@No", No));

    /// <summary>旧过程 `P_COP_RECEIPT_CHECK` 的语句链（游标逐行拼文案）。</summary>
    private static async Task<(bool Success, string Message)> RunLegacyCheckAsync(
        SqlConnection connection, SqlTransaction transaction, CancellationToken token)
    {
        const string batch = """
            declare @success int = 1, @msg varchar(8000) = '';
            declare @account_type nchar(10), @account_no nchar(20), @sum_amount float, @receive_amount float, @my_receive_amount float;
            if exists(select * from COP_RECEIPT_M where RECEIPT_TYPE=@Type and RECEIPT_NO=@No and RECEIVE_SUM>AMOUNT_TAX-REBATE_SUM-PREPAY_SUM+0.1) begin
                select @success=0, @msg = '实收金额 不能大于 应收金额-现金折扣-预收冲帐' + char(13)
            end
            if @success = 1
            begin
                declare cur_tmp cursor for
                    select od.ACCOUNT_TYPE, od.ACCOUNT_NO, od.SUM_AMOUNT, od.RECEIVE_AMOUNT, sd.RECEIVE_AMOUNT
                      from COP_ACCOUNT_M od, (select ACCOUNT_TYPE, ACCOUNT_NO, sum(RECEIVE_AMOUNT) RECEIVE_AMOUNT
                                                from COP_RECEIPT_D where RECEIPT_TYPE=@Type and RECEIPT_NO=@No
                                               group by ACCOUNT_TYPE, ACCOUNT_NO) sd
                     where od.ACCOUNT_TYPE=sd.ACCOUNT_TYPE and od.ACCOUNT_NO=sd.ACCOUNT_NO
                       and (od.RECEIVE_AMOUNT + sd.RECEIVE_AMOUNT > od.SUM_AMOUNT);
                open cur_tmp
                fetch next from cur_tmp into @account_type, @account_no, @sum_amount, @receive_amount, @my_receive_amount
                select @msg = ''
                while @@FETCH_STATUS = 0
                begin
                    select @success = 0, @msg = @msg + rtrim(@account_no) + '       ' + cast(@sum_amount as varchar)
                         + '          ' + cast(@receive_amount as varchar) + '          ' + cast(@my_receive_amount as varchar) + char(13)
                    fetch next from cur_tmp into @account_type, @account_no, @sum_amount, @receive_amount, @my_receive_amount
                end
                close cur_tmp
                deallocate cur_tmp
                if @success = 0
                    select @msg = '以下会出现对帐单已收款大于应收款' + char(13) + '对帐单号     应收款       已收款       本次收款' + char(13) + @msg
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
            "UPDATE dbo.MODULES SET ERROR_NO_SAVE=@Value WHERE M_IDX=170102;", ("@Value", value));

    private static async Task SeedAsync(SqlConnection connection, SqlTransaction transaction, CancellationToken token,
        double amountTax, double rebate, double prepay, double? receive, double dueSum = 0, double dueReceive = 0)
    {
        await ExecuteAsync(connection, transaction, token, """
            DELETE FROM dbo.COP_RECEIPT_D WHERE RECEIPT_TYPE=@Type;
            DELETE FROM dbo.COP_RECEIPT_PREPAY WHERE RECEIPT_TYPE=@Type;
            DELETE FROM dbo.COP_RECEIPT_M WHERE RECEIPT_TYPE=@Type;
            DELETE FROM dbo.COP_ACCOUNT_M WHERE ACCOUNT_TYPE=@DueType;
            INSERT INTO dbo.COP_ACCOUNT_M (ACCOUNT_TYPE, ACCOUNT_NO, SUM_AMOUNT, RECEIVE_AMOUNT) VALUES (@DueType, @DueNo, @DueSum, @DueReceive);
            INSERT INTO dbo.COP_RECEIPT_M (RECEIPT_TYPE, RECEIPT_NO, AMOUNT_TAX, REBATE_SUM) VALUES (@Type, @No, @AmountTax, @Rebate);
            INSERT INTO dbo.COP_RECEIPT_PREPAY (RECEIPT_TYPE, RECEIPT_NO, SERIAL_NO, PREPAY_AMOUNT) VALUES (@Type, @No, 1, @Prepay);
            """,
            ("@Type", Type), ("@No", No), ("@DueType", DueType), ("@DueNo", DueNo),
            ("@AmountTax", amountTax), ("@Rebate", rebate), ("@Prepay", prepay),
            ("@DueSum", dueSum), ("@DueReceive", dueReceive));
        if (receive is null) return;
        await ExecuteAsync(connection, transaction, token, """
            INSERT INTO dbo.COP_RECEIPT_D (RECEIPT_TYPE, RECEIPT_NO, SERIAL_NO, ACCOUNT_TYPE, ACCOUNT_NO, RECEIVE_AMOUNT)
            VALUES (@Type, @No, 1, @DueType, @DueNo, @Receive);
            """, ("@Type", Type), ("@No", No), ("@DueType", DueType), ("@DueNo", DueNo), ("@Receive", receive.Value));
    }

    private static async Task<(double PrepaySum, double ReceiveSum)> ReadAsync(
        SqlConnection connection, SqlTransaction transaction, CancellationToken token)
    {
        await using var command = new SqlCommand("""
            SELECT ISNULL(PREPAY_SUM,0), ISNULL(RECEIVE_SUM,0) FROM dbo.COP_RECEIPT_M WHERE RECEIPT_TYPE=@Type AND RECEIPT_NO=@No;
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
            WHERE MODULE_ID=@ModuleId AND EVENT_CODE=N'SAVE' AND SEQ=1;
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
