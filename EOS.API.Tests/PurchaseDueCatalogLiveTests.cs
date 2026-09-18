using System.Data;
using System.Text.Json;
using System.Text.RegularExpressions;
using EOS.API.Data.Effects;
using EOS.API.Data.Effects.ServiceEffectHandlers;
using Microsoft.Data.SqlClient;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 应付对帐单（170201，原 `purchase-due` C#）入效果目录后的真库验证：
///   ① 写段 `purchase-due-rollup`：明细金额/税额/价税合计/数量合计回写主表，含税总额＝价税合计+其它费用
///      ——把旧过程 `P_PUR_DUE_After_Save` 的汇总语句内联为基准，比较主表最终状态；
///   ② 门控段 `qty-not-exceed`：对帐不超收料/退料单数量——按旧过程 `P_PUR_DUE_CHECK` 的语句链比对
///      "拒绝与否 + 文案"，并覆盖门控关、额度内放行与"同引用键多行必须按组合并求和"。
/// 造数用 `ADR12PD` 前缀，事务结束回滚。
/// </summary>
[Collection("live-database")]
public sealed class PurchaseDueCatalogLiveTests
{
    private static readonly string ConnectionString =
        Environment.GetEnvironmentVariable("EOS_ERP_TEST_CONNECTION")
        ?? throw new InvalidOperationException(
            "真库集成测试需要 EOS_ERP_TEST_CONNECTION；未配置即失败（无连接跳过≠已验证）。");

    private static readonly EffectValidationExecutor Executor = new();

    private const int ModuleId = 170201;
    private const string Type = "ADR12PD";
    private const string No = "ADR12PD001";
    private const string ReceiveNo = "ADR12PDR1";
    private const string CancelNo = "ADR12PDC1";

    [Fact]
    public async Task 应付对帐单_主表金额汇总与旧过程一致()
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
            var plan = new ModuleEffectPlan(ModuleId, "PUR_DUE_M", "PUR_DUE_D", "live-purchase-due",
                ["DUE_TYPE", "DUE_NO"], [action], []);

            await new DetailRollupHandler().ExecuteAsync(
                new ServiceEffectContext(connection, transaction, plan, action, EffectEvent.Save,
                    No, [Type, No], "live-test"), token);
            var byEffect = await ReadMasterAsync(connection, transaction, token);
            Assert.Equal((150.75, 19.6, 170.35, 171.6, 13), byEffect);

            await SeedAsync(connection, transaction, token);
            await ExecuteAsync(connection, transaction, token, """
                UPDATE m SET m.AMOUNT=s.AMOUNT_SUM, m.TAX_SUM=s.TAX_SUM_SUM, m.AMOUNT_TAX=s.AMOUNT_TAX_SUM,
                       m.SUM_AMOUNT=s.AMOUNT_TAX_SUM+m.OTHER_PRICE, m.QTY_TOTAL=s.QTY_SUM
                FROM dbo.PUR_DUE_M m INNER JOIN (SELECT d.DUE_TYPE, d.DUE_NO,
                            ROUND(SUM(d.AMOUNT_TAX),2) AMOUNT_TAX_SUM, ROUND(SUM(d.AMOUNT),2) AMOUNT_SUM,
                            ROUND(SUM(d.TAX_SUM),2) TAX_SUM_SUM, ROUND(SUM(d.QTY),2) QTY_SUM
                        FROM dbo.PUR_DUE_D d WHERE d.DUE_TYPE=@Type AND d.DUE_NO=@No
                        GROUP BY d.DUE_TYPE, d.DUE_NO) s
                  ON s.DUE_TYPE=m.DUE_TYPE AND s.DUE_NO=m.DUE_NO
                WHERE m.DUE_TYPE=@Type AND m.DUE_NO=@No;
                """, ("@Type", Type), ("@No", No));
            var byLegacy = await ReadMasterAsync(connection, transaction, token);
            Assert.Equal(byLegacy, byEffect);
        }
        finally
        {
            await transaction.RollbackAsync(token);
        }
    }

    [Fact]
    public async Task 应付对帐单_对帐不超收退料数量与旧过程一致()
    {
        var token = CancellationToken.None;
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync(token);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(token);
        try
        {
            var plan = await LoadCheckPlanAsync(connection, transaction, token);

            // ① 门开 + 收料侧超量（同一收料行两行明细合计 7，已对帐 4，上限 10）
            await SeedCheckAsync(connection, transaction, token, "receive", over: true);
            var receiveError = await Assert.ThrowsAsync<EffectValidationException>(() =>
                Executor.ValidateAsync(connection, transaction, plan, "SAVE", token, [Type, No]));
            var legacyReceive = await RunLegacyCheckAsync(connection, transaction, token);
            Assert.False(legacyReceive.Success);
            Assert.Equal(Normalize(legacyReceive.Message), Normalize(receiveError.Message));
            Assert.Contains($"{ReceiveNo} 10 4 7", Normalize(receiveError.Message));

            // ② 门关：同一份超量数据不再被拦
            await SetGateAsync(connection, transaction, token, 0);
            await Executor.ValidateAsync(connection, transaction, plan, "SAVE", token, [Type, No]);

            // ③ 门开 + 退料侧超量 + 文案对拍
            await SeedCheckAsync(connection, transaction, token, "cancel", over: true);
            await SetGateAsync(connection, transaction, token, 1);
            var cancelError = await Assert.ThrowsAsync<EffectValidationException>(() =>
                Executor.ValidateAsync(connection, transaction, plan, "SAVE", token, [Type, No]));
            var legacyCancel = await RunLegacyCheckAsync(connection, transaction, token);
            Assert.False(legacyCancel.Success);
            Assert.StartsWith("以下对帐已超出退料单数量", Normalize(cancelError.Message));
            Assert.Equal(Normalize(legacyCancel.Message), Normalize(cancelError.Message));

            // ④ 额度内放行（合计 6 ≤ 10）
            await SeedCheckAsync(connection, transaction, token, "receive", over: false);
            await Executor.ValidateAsync(connection, transaction, plan, "SAVE", token, [Type, No]);
        }
        finally
        {
            await transaction.RollbackAsync(token);
        }
    }

    /// <summary>旧过程 `P_PUR_DUE_CHECK` 的语句链（游标逐行拼文案）。</summary>
    private static async Task<(bool Success, string Message)> RunLegacyCheckAsync(
        SqlConnection connection, SqlTransaction transaction, CancellationToken token)
    {
        const string batch = """
            declare @success int = 1, @msg varchar(8000) = '', @r_c_no nchar(20), @qty float, @finished_qty float, @my_qty float;
            declare cur_tmp cursor for
                select od.RECEIVE_NO, od.QTY, od.FINISHED_QTY, sd.QTY
                  from PUR_RECEIVE_D od, (select R_C_TYPE, R_C_NO, R_C_SERIAL_NO, sum(QTY) QTY
                                            from PUR_DUE_D where DUE_TYPE=@Type and DUE_NO=@No
                                           group by R_C_TYPE, R_C_NO, R_C_SERIAL_NO) sd
                 where od.RECEIVE_TYPE=sd.R_C_TYPE and od.RECEIVE_NO=sd.R_C_NO and od.SERIAL_NO=sd.R_C_SERIAL_NO
                   and od.FINISHED_QTY+sd.QTY>od.QTY;
            open cur_tmp
            fetch next from cur_tmp into @r_c_no, @qty, @finished_qty, @my_qty
            while @@FETCH_STATUS = 0
            begin
                select @success = 0, @msg = @msg + rtrim(@r_c_no) + '    ' + cast(@qty as varchar) + '    '
                     + cast(@finished_qty as varchar) + '    ' + cast(@my_qty as varchar) + char(13)
                fetch next from cur_tmp into @r_c_no, @qty, @finished_qty, @my_qty
            end
            close cur_tmp
            deallocate cur_tmp
            if @success = 0
                select @msg = '以下对帐已超出收料单数量' + char(13) + ' 收料单号  收料数量  已对帐数量  单据数量' + char(13) + @msg
            if @success = 1
            begin
                declare cur_cancel cursor for
                    select od.CANCEL_NO, od.QTY, od.FINISHED_QTY, sd.QTY
                      from PUR_CANCEL_D od, (select R_C_TYPE, R_C_NO, R_C_SERIAL_NO, sum(QTY) QTY
                                               from PUR_DUE_D where DUE_TYPE=@Type and DUE_NO=@No
                                              group by R_C_TYPE, R_C_NO, R_C_SERIAL_NO) sd
                     where od.CANCEL_TYPE=sd.R_C_TYPE and od.CANCEL_NO=sd.R_C_NO and od.SERIAL_NO=sd.R_C_SERIAL_NO
                       and od.FINISHED_QTY+sd.QTY>od.QTY;
                open cur_cancel
                fetch next from cur_cancel into @r_c_no, @qty, @finished_qty, @my_qty
                while @@FETCH_STATUS = 0
                begin
                    select @success = 0, @msg = @msg + rtrim(@r_c_no) + '    ' + cast(@qty as varchar) + '    '
                         + cast(@finished_qty as varchar) + '    ' + cast(@my_qty as varchar) + char(13)
                    fetch next from cur_cancel into @r_c_no, @qty, @finished_qty, @my_qty
                end
                close cur_cancel
                deallocate cur_cancel
                if @success = 0
                    select @msg = '以下对帐已超出退料单数量' + char(13) + ' 退料单号  退料数量  已对帐数量  单据数量' + char(13) + @msg
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
            "UPDATE dbo.MODULES SET ERROR_NO_SAVE=@Value WHERE M_IDX=170201;", ("@Value", value));

    private static async Task SeedAsync(
        SqlConnection connection, SqlTransaction transaction, CancellationToken token)
        => await ExecuteAsync(connection, transaction, token, """
            DELETE FROM dbo.PUR_DUE_D WHERE DUE_TYPE=@Type;
            DELETE FROM dbo.PUR_DUE_M WHERE DUE_TYPE=@Type;
            INSERT INTO dbo.PUR_DUE_M (DUE_TYPE, DUE_NO, OTHER_PRICE, AMOUNT, TAX_SUM, AMOUNT_TAX, SUM_AMOUNT, QTY_TOTAL)
            VALUES (@Type, @No, 1.25, 999, 999, 999, 999, 999);
            INSERT INTO dbo.PUR_DUE_D (DUE_TYPE, DUE_NO, SERIAL_NO, QTY, AMOUNT, TAX_SUM, AMOUNT_TAX)
            VALUES (@Type, @No, 1, 10, 100.5, 13.07, 113.57), (@Type, @No, 2, 3, 50.25, 6.53, 56.78);
            """, ("@Type", Type), ("@No", No));

    private static async Task SeedCheckAsync(
        SqlConnection connection, SqlTransaction transaction, CancellationToken token, string side, bool over)
    {
        var reference = side == "receive" ? ReceiveNo : CancelNo;
        var first = over ? 3 : 2;
        await ExecuteAsync(connection, transaction, token, """
            DELETE FROM dbo.PUR_DUE_D WHERE DUE_TYPE=@Type;
            DELETE FROM dbo.PUR_DUE_M WHERE DUE_TYPE=@Type;
            DELETE FROM dbo.PUR_RECEIVE_D WHERE RECEIVE_TYPE=@Type;
            DELETE FROM dbo.PUR_CANCEL_D WHERE CANCEL_TYPE=@Type;
            INSERT INTO dbo.PUR_DUE_M (DUE_TYPE, DUE_NO) VALUES (@Type, @No);
            INSERT INTO dbo.PUR_RECEIVE_D (RECEIVE_TYPE, RECEIVE_NO, SERIAL_NO, QTY, FINISHED_QTY) VALUES (@Type, @ReceiveNo, 1, 10, 4);
            INSERT INTO dbo.PUR_CANCEL_D (CANCEL_TYPE, CANCEL_NO, SERIAL_NO, QTY, FINISHED_QTY) VALUES (@Type, @CancelNo, 1, 5, 1);
            INSERT INTO dbo.PUR_DUE_D (DUE_TYPE, DUE_NO, SERIAL_NO, R_C_TYPE, R_C_NO, R_C_SERIAL_NO, QTY)
            VALUES (@Type, @No, 1, @Type, @Ref, 1, @First), (@Type, @No, 2, @Type, @Ref, 1, 4);
            """,
            ("@Type", Type), ("@No", No), ("@ReceiveNo", ReceiveNo), ("@CancelNo", CancelNo),
            ("@Ref", reference), ("@First", first));
    }

    private static async Task<(double Amount, double TaxSum, double AmountTax, double SumAmount, double QtyTotal)>
        ReadMasterAsync(SqlConnection connection, SqlTransaction transaction, CancellationToken token)
    {
        await using var command = new SqlCommand("""
            SELECT ISNULL(AMOUNT,0), ISNULL(TAX_SUM,0), ISNULL(AMOUNT_TAX,0), ISNULL(SUM_AMOUNT,0), ISNULL(QTY_TOTAL,0)
            FROM dbo.PUR_DUE_M WHERE DUE_TYPE=@Type AND DUE_NO=@No;
            """, connection, transaction);
        command.Parameters.Add("@Type", SqlDbType.NVarChar, 20).Value = Type;
        command.Parameters.Add("@No", SqlDbType.NVarChar, 40).Value = No;
        await using var reader = await command.ExecuteReaderAsync(token);
        Assert.True(await reader.ReadAsync(token));
        return (Convert.ToDouble(reader.GetValue(0)), Convert.ToDouble(reader.GetValue(1)),
                Convert.ToDouble(reader.GetValue(2)), Convert.ToDouble(reader.GetValue(3)),
                Convert.ToDouble(reader.GetValue(4)));
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
            WHERE m.M_IDX = @ModuleId;
            """, connection, transaction))
        {
            command.Parameters.Add("@ModuleId", SqlDbType.Int).Value = ModuleId;
            await using var reader = await command.ExecuteReaderAsync(token);
            Assert.True(await reader.ReadAsync(token), "模块 170201 缺少当前快照或 SAVE 期 qty-not-exceed 配置");
            masterTable = reader.GetString(0);
            detailTable = reader.GetString(1);
            using var definition = JsonDocument.Parse(reader.GetString(2));
            pkJson = definition.RootElement.GetProperty("MasterPkOrder").GetRawText();
            paramStruct = reader.GetString(3);
        }
        var pkOrder = JsonSerializer.Deserialize<List<string>>(pkJson)!;
        using var parameters = JsonDocument.Parse(paramStruct);
        return new ModuleEffectPlan(ModuleId, masterTable, detailTable, "live-purchase-due-check", pkOrder,
            Array.Empty<EffectActionPlan>(),
            [new EffectValidationPlan(1, "SAVE", "qty-not-exceed", true, parameters.RootElement.Clone(), null)]);
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
