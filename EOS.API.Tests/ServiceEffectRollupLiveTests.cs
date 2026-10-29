using System.Data;
using System.Text.Json;
using EOS.API.Data.Effects;
using EOS.API.Data.Effects.ServiceEffectHandlers;
using Microsoft.Data.SqlClient;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 跨单据回写的金额汇总真库验收：变更单批核（`order-change-apply`）与回送/退货回执核价
/// （`callback-reprice`）都把**目标单据**主表金额按「明细同名列的裸 `SUM`」重算，主表汇率不参与。
///
/// <para>
/// 这两条链此前只有实现、没有真库用例。判据取"主表汇率故意置 2"：折算算式会得到 75/2=37.5，
/// 裸 `SUM` 仍是 75——改回折算即红。两侧都按库里当前生效的 `MODULE_BUSINESS_ACTION` 配置驱动，
/// 配置一漂移用例同样会红（不是把参数写死在测试里）。
/// </para>
///
/// <para>夹具全部自造（ZZ 前缀），两个用例都在事务内执行并回滚；不借真实单据取样值。</para>
/// </summary>
[Trait("Category", "Integration")]
[Collection("live-database")]
public sealed class ServiceEffectRollupLiveTests
{
    private static readonly string ConnectionString =
        Environment.GetEnvironmentVariable("MSSQL_ERP_CONN")
        ?? throw new InvalidOperationException(
            "真库集成测试需要 MSSQL_ERP_CONN；未配置即失败（无连接跳过≠已验证）。");

    // ---- 变更单批核：销售订单变更（1418）→ 原销售订单（COP_ORDER_M/D） ----

    private const int ChangeModuleId = 1418;
    private const string OrderType = "ZZOCM";
    private const string OrderNo = "ZZOCM0001";
    private const string ChangeType = "ZCHG";
    private const string ChangeNo = "ZCHG0001";

    [Fact]
    public async Task OrderChangeApply_WritesBareSum_WhenTargetMasterRateDiffers()
    {
        var token = CancellationToken.None;
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync(token);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(token);
        try
        {
            await SeedOrderSignalAsync(connection, transaction, token);
            var action = await LoadActionAsync(connection, transaction, ChangeModuleId, "order-change-apply", token);

            var affected = await new OrderChangeApplyHandler().ExecuteAsync(
                new ServiceEffectContext(connection, transaction,
                    new ModuleEffectPlan(ChangeModuleId, "COP_ORDER_CHANGE_M", "COP_ORDER_CHANGE_D",
                        "live-order-change", ["CHANGE_ORDER_TYPE", "CHANGE_ORDER_NO"], [action], []),
                    action, EffectEvent.ApproveEffect, ChangeType + "," + ChangeNo,
                    [ChangeType, ChangeNo], "live-test"), token);

            Assert.True(affected >= 2, $"变更单批核未改动目标单据（affected={affected}）。");

            // 明细：QTY/PRICE 取变更单，金额按目标行自己的税种重算（TAX_TYPE='N' ⇒ 金额=含税金额、税额 0）
            Assert.Equal((6m, 10m, 60m, 60m, 0m), await ReadOrderDetailAsync(connection, transaction, 1, token));
            Assert.Equal((2m, 7.5m, 15m, 15m, 0m), await ReadOrderDetailAsync(connection, transaction, 2, token));

            // 主表：裸 SUM = 75；按主表汇率 2 折算会得 37.5
            var master = await ReadOrderMasterAsync(connection, transaction, token);
            Assert.Equal(2m, master.Rate);
            Assert.Equal(75m, master.Amount);
            Assert.Equal(75m, master.AmountTax);
            Assert.Equal(0m, master.TaxSum);
        }
        finally
        {
            await transaction.RollbackAsync(token);
        }
    }

    // ---- 回执核价：送货单回执（1413）→ 送货单（COP_SEND_M/D） ----

    private const int CallbackModuleId = 1413;
    private const string SendType = "ZZCBM";
    private const string SendNo = "ZZCBM0001";
    private const string CallbackType = "ZZCB";
    private const string CallbackNo = "ZZCB0001";

    [Fact]
    public async Task CallbackReprice_WritesBareSum_AndMarksLines_WhenTargetMasterRateDiffers()
    {
        var token = CancellationToken.None;
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync(token);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(token);
        try
        {
            await SeedCallbackAsync(connection, transaction, token);
            var action = await LoadActionAsync(connection, transaction, CallbackModuleId, "callback-reprice", token);
            var context = new ServiceEffectContext(connection, transaction,
                new ModuleEffectPlan(CallbackModuleId, "COP_CALLBACK_M", "COP_CALLBACK_D",
                    "live-callback", ["CALLBACK_TYPE", "CALLBACK_NO"], [action], []),
                action, EffectEvent.ApproveEffect, CallbackType + "," + CallbackNo,
                [CallbackType, CallbackNo], "live-test");

            var affected = await new CallbackRepriceHandler().ExecuteAsync(context, token);
            Assert.True(affected >= 1, $"回执核价未改动目标送货单（affected={affected}）。");

            // 明细：单价取回执价 12（原 10），金额按数量重算；回执标记写回目标行
            Assert.Equal((4m, 12m, 48m, 48m, 0m, CallbackType, CallbackNo, 1), 
                await ReadSendDetailAsync(connection, transaction, token));

            // 主表：裸 SUM = 48；按主表汇率 2 折算会得 24
            var master = await ReadSendMasterAsync(connection, transaction, token);
            Assert.Equal(2m, master.Rate);
            Assert.Equal(48m, master.Amount);

            // 同一条回执再批一次必须被"已有回执"守卫拦下（目标行已被标记）
            var again = await Assert.ThrowsAsync<EffectValidationException>(
                () => new CallbackRepriceHandler().ExecuteAsync(context, token));
            Assert.Contains("已有回执", again.Message);
        }
        finally
        {
            await transaction.RollbackAsync(token);
        }
    }

    private static async Task SeedOrderSignalAsync(
        SqlConnection connection, SqlTransaction transaction, CancellationToken token)
    {
        await ExecuteAsync(connection, transaction, token, """
            DELETE FROM dbo.COP_ORDER_D WHERE ORDER_TYPE=@OrderType AND ORDER_NO=@OrderNo;
            DELETE FROM dbo.COP_ORDER_M WHERE ORDER_TYPE=@OrderType AND ORDER_NO=@OrderNo;
            DELETE FROM dbo.COP_ORDER_CHANGE_D WHERE CHANGE_ORDER_TYPE=@ChangeType AND CHANGE_ORDER_NO=@ChangeNo;
            DELETE FROM dbo.COP_ORDER_CHANGE_M WHERE CHANGE_ORDER_TYPE=@ChangeType AND CHANGE_ORDER_NO=@ChangeNo;

            INSERT INTO dbo.COP_ORDER_M (ORDER_TYPE, ORDER_NO, CURR_RATE, AMOUNT, AMOUNT_TAX, TAX_SUM)
            VALUES (@OrderType, @OrderNo, 2, 999, 999, 999);

            INSERT INTO dbo.COP_ORDER_D
                (ORDER_TYPE, ORDER_NO, SERIAL_NO, PRO_NO, QTY, PRICE, REBATE, TAX_TYPE, TAX_RATE, CURR_RATE,
                 AMOUNT, AMOUNT_TAX, TAX_SUM)
            VALUES (@OrderType, @OrderNo, 1, N'ZZPRO01', 1, 1, 100, 'N', 0, 2, 1, 1, 0),
                   (@OrderType, @OrderNo, 2, N'ZZPRO02', 1, 1, 100, 'N', 0, 2, 1, 1, 0);

            INSERT INTO dbo.COP_ORDER_CHANGE_M (CHANGE_ORDER_TYPE, CHANGE_ORDER_NO, ORDER_TYPE, ORDER_NO)
            VALUES (@ChangeType, @ChangeNo, @OrderType, @OrderNo);

            INSERT INTO dbo.COP_ORDER_CHANGE_D
                (CHANGE_ORDER_TYPE, CHANGE_ORDER_NO, SERIAL_NO, ORDER_TYPE, ORDER_NO, ORDER_SERIAL_NO,
                 PRO_NO, QTY, PRICE)
            VALUES (@ChangeType, @ChangeNo, 1, @OrderType, @OrderNo, 1, N'ZZPRO01', 6, 10),
                   (@ChangeType, @ChangeNo, 2, @OrderType, @OrderNo, 2, N'ZZPRO02', 2, 7.5);
            """,
            ("@OrderType", OrderType), ("@OrderNo", OrderNo),
            ("@ChangeType", ChangeType), ("@ChangeNo", ChangeNo));
    }

    private static async Task SeedCallbackAsync(
        SqlConnection connection, SqlTransaction transaction, CancellationToken token)
    {
        await ExecuteAsync(connection, transaction, token, """
            DELETE FROM dbo.COP_SEND_D WHERE SEND_TYPE=@SendType AND SEND_NO=@SendNo;
            DELETE FROM dbo.COP_SEND_M WHERE SEND_TYPE=@SendType AND SEND_NO=@SendNo;
            DELETE FROM dbo.COP_CALLBACK_D WHERE CALLBACK_TYPE=@CallbackType AND CALLBACK_NO=@CallbackNo;
            DELETE FROM dbo.COP_CALLBACK_M WHERE CALLBACK_TYPE=@CallbackType AND CALLBACK_NO=@CallbackNo;

            INSERT INTO dbo.COP_SEND_M (SEND_TYPE, SEND_NO, CURR_RATE, AMOUNT, AMOUNT_TAX, TAX_SUM)
            VALUES (@SendType, @SendNo, 2, 999, 999, 999);

            INSERT INTO dbo.COP_SEND_D
                (SEND_TYPE, SEND_NO, SERIAL_NO, PRO_NO, QTY, PRICE, REBATE, TAX_TYPE, TAX_RATE, CURR_ID, TAX_ID,
                 CALLBACK_NO, AMOUNT, AMOUNT_TAX, TAX_SUM)
            VALUES (@SendType, @SendNo, 1, N'ZZPRO03', 4, 10, 100, 'N', 0, 'RMB', 'T01', '', 40, 40, 0);

            INSERT INTO dbo.COP_CALLBACK_M (CALLBACK_TYPE, CALLBACK_NO) VALUES (@CallbackType, @CallbackNo);

            INSERT INTO dbo.COP_CALLBACK_D
                (CALLBACK_TYPE, CALLBACK_NO, SERIAL_NO, S_R_TYPE, S_R_NO, S_R_SERIAL_NO,
                 PRO_NO, QTY, PRICE, TAX_TYPE, TAX_RATE, CURR_ID, TAX_ID, CLIENT_ACCEPT_NO)
            VALUES (@CallbackType, @CallbackNo, 1, @SendType, @SendNo, 1,
                    N'ZZPRO03', 4, 12, 'N', 0, 'RMB', 'T01', N'ZZACC001');
            """,
            ("@SendType", SendType), ("@SendNo", SendNo),
            ("@CallbackType", CallbackType), ("@CallbackNo", CallbackNo));
    }

    private static async Task<(decimal Qty, decimal Price, decimal Amount, decimal AmountTax, decimal TaxSum)> ReadOrderDetailAsync(
        SqlConnection connection, SqlTransaction transaction, int serialNo, CancellationToken token)
    {
        await using var command = new SqlCommand("""
            SELECT COUNT_BIG(1),
                   ISNULL(SUM(QTY),0), ISNULL(SUM(PRICE),0), ISNULL(SUM(AMOUNT),0),
                   ISNULL(SUM(AMOUNT_TAX),0), ISNULL(SUM(TAX_SUM),0)
            FROM dbo.COP_ORDER_D WHERE ORDER_TYPE=@Type AND ORDER_NO=@No AND SERIAL_NO=@Serial;
            """, connection, transaction);
        command.Parameters.Add("@Type", SqlDbType.NChar, 10).Value = OrderType;
        command.Parameters.Add("@No", SqlDbType.NChar, 20).Value = OrderNo;
        command.Parameters.Add("@Serial", SqlDbType.SmallInt).Value = serialNo;
        await using var reader = await command.ExecuteReaderAsync(token);
        Assert.True(await reader.ReadAsync(token));
        Assert.Equal(1L, Convert.ToInt64(reader.GetValue(0)));
        return (Convert.ToDecimal(reader.GetValue(1)), Convert.ToDecimal(reader.GetValue(2)),
            Convert.ToDecimal(reader.GetValue(3)), Convert.ToDecimal(reader.GetValue(4)),
            Convert.ToDecimal(reader.GetValue(5)));
    }

    private static async Task<(decimal Rate, decimal Amount, decimal AmountTax, decimal TaxSum)> ReadOrderMasterAsync(
        SqlConnection connection, SqlTransaction transaction, CancellationToken token)
    {
        await using var command = new SqlCommand("""
            SELECT ISNULL(CURR_RATE,0), ISNULL(AMOUNT,0), ISNULL(AMOUNT_TAX,0), ISNULL(TAX_SUM,0)
            FROM dbo.COP_ORDER_M WHERE ORDER_TYPE=@Type AND ORDER_NO=@No;
            """, connection, transaction);
        command.Parameters.Add("@Type", SqlDbType.NChar, 10).Value = OrderType;
        command.Parameters.Add("@No", SqlDbType.NChar, 20).Value = OrderNo;
        await using var reader = await command.ExecuteReaderAsync(token);
        Assert.True(await reader.ReadAsync(token));
        return (Convert.ToDecimal(reader.GetValue(0)), Convert.ToDecimal(reader.GetValue(1)),
            Convert.ToDecimal(reader.GetValue(2)), Convert.ToDecimal(reader.GetValue(3)));
    }

    private static async Task<(decimal Qty, decimal Price, decimal Amount, decimal AmountTax, decimal TaxSum,
        string CallbackType, string CallbackNo, int CallbackSerial)> ReadSendDetailAsync(
        SqlConnection connection, SqlTransaction transaction, CancellationToken token)
    {
        await using var command = new SqlCommand("""
            SELECT ISNULL(QTY,0), ISNULL(PRICE,0), ISNULL(AMOUNT,0), ISNULL(AMOUNT_TAX,0), ISNULL(TAX_SUM,0),
                   ISNULL(CALLBACK_TYPE,''), ISNULL(CALLBACK_NO,''), ISNULL(CALLBACK_SERIAL_NO,0)
            FROM dbo.COP_SEND_D WHERE SEND_TYPE=@Type AND SEND_NO=@No AND SERIAL_NO=1;
            """, connection, transaction);
        command.Parameters.Add("@Type", SqlDbType.NChar, 10).Value = SendType;
        command.Parameters.Add("@No", SqlDbType.NChar, 20).Value = SendNo;
        await using var reader = await command.ExecuteReaderAsync(token);
        Assert.True(await reader.ReadAsync(token));
        return (Convert.ToDecimal(reader.GetValue(0)), Convert.ToDecimal(reader.GetValue(1)),
            Convert.ToDecimal(reader.GetValue(2)), Convert.ToDecimal(reader.GetValue(3)),
            Convert.ToDecimal(reader.GetValue(4)), Convert.ToString(reader.GetValue(5))!.Trim(),
            Convert.ToString(reader.GetValue(6))!.Trim(), Convert.ToInt32(reader.GetValue(7)));
    }

    private static async Task<(decimal Rate, decimal Amount)> ReadSendMasterAsync(
        SqlConnection connection, SqlTransaction transaction, CancellationToken token)
    {
        await using var command = new SqlCommand("""
            SELECT ISNULL(CURR_RATE,0), ISNULL(AMOUNT,0)
            FROM dbo.COP_SEND_M WHERE SEND_TYPE=@Type AND SEND_NO=@No;
            """, connection, transaction);
        command.Parameters.Add("@Type", SqlDbType.NChar, 10).Value = SendType;
        command.Parameters.Add("@No", SqlDbType.NChar, 20).Value = SendNo;
        await using var reader = await command.ExecuteReaderAsync(token);
        Assert.True(await reader.ReadAsync(token));
        return (Convert.ToDecimal(reader.GetValue(0)), Convert.ToDecimal(reader.GetValue(1)));
    }

    /// <summary>按库里当前生效的配置加载动作：参数漂移时用例跟着红，而不是照测试里的死参数通过。</summary>
    private static async Task<EffectActionPlan> LoadActionAsync(
        SqlConnection connection, SqlTransaction transaction, int moduleId, string effectKey, CancellationToken token)
    {
        await using var command = new SqlCommand("""
            SELECT SEQ, EVENT_CODE, EFFECT_KEY, EFFECT_NAME, ENABLED, FAIL_MODE,
                   CONDITION_STRUCT, PARAM_STRUCT, REVERSE_STRUCT
            FROM dbo.MODULE_BUSINESS_ACTION
            WHERE M_IDX=@ModuleId AND EFFECT_KEY=@EffectKey AND ENABLED=1;
            """, connection, transaction);
        command.Parameters.Add("@ModuleId", SqlDbType.Int).Value = moduleId;
        command.Parameters.Add("@EffectKey", SqlDbType.NVarChar, 100).Value = effectKey;
        await using var reader = await command.ExecuteReaderAsync(token);
        Assert.True(await reader.ReadAsync(token), $"模块 {moduleId} 缺少启用的效果 {effectKey}。");
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
        SqlConnection connection, SqlTransaction transaction, CancellationToken token,
        string sql, params (string Name, object? Value)[] parameters)
    {
        await using var command = new SqlCommand(sql, connection, transaction);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        await command.ExecuteNonQueryAsync(token);
    }
}
