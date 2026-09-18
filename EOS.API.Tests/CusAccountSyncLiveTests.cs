using System.Data;
using System.Text.Json;
using System.Text.RegularExpressions;
using EOS.API.Data.Effects;
using EOS.API.Data.Effects.ServiceEffectHandlers;
using Microsoft.Data.SqlClient;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 海关对帐单（3014，原 `cus-account` C#）入效果目录后的真库验证，覆盖两段：
///   ① 写链 `cus-account-sync`（明细单重/毛重取产品档案并换算、对帐数量补零、主表汇总）——
///      把旧过程 `P_CUS_ACCOUNT_After_Save` 的三条语句内联为基准，比较两表最终状态；
///   ② 受门控的不超量规则 `qty-not-exceed`（对帐不超送/退货单数量）——把旧过程
///      `P_CUS_ACCOUNT_CHECK` 的语句链内联为基准，比较"拒绝与否 + 文案"（按空白归一，与对拍脚本同口径），
///      并覆盖门控关（`ERROR_NO_SAVE=0` 时不校验）与"同引用键多行必须按组合并求和"两个易错点。
/// 造数用 `ADR12CA` 前缀，事务结束回滚。
/// </summary>
[Collection("live-database")]
public sealed class CusAccountSyncLiveTests
{
    private static readonly string ConnectionString =
        Environment.GetEnvironmentVariable("EOS_ERP_TEST_CONNECTION")
        ?? throw new InvalidOperationException(
            "真库集成测试需要 EOS_ERP_TEST_CONNECTION；未配置即失败（无连接跳过≠已验证）。");

    private static readonly EffectValidationExecutor Executor = new();

    private const int ModuleId = 3014;
    private const string Type = "ADR12CA";
    private const string No = "ADR12CA001";
    private const string Client = "ADR12CACL";
    private const string P1 = "ADR12CAP1";   // 单重 2、毛重 3
    private const string P2 = "ADR12CAP2";   // 单重为空、毛重 4（验证不做空值兜底）
    private const string SendNo = "ADR12CAS1";
    private const string ReturnNo = "ADR12CAR1";

    [Fact]
    public async Task 海关对帐单_明细单重与主表汇总与旧过程一致()
    {
        var token = CancellationToken.None;
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync(token);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(token);
        try
        {
            await SeedWriteAsync(connection, transaction, token);
            var action = await LoadActionAsync(connection, transaction, token);
            Assert.Equal("cus-account-sync", action.EffectKey);

            // ① 目录效果
            await new CusAccountSyncHandler().ExecuteAsync(
                new ServiceEffectContext(connection, transaction,
                    new ModuleEffectPlan(ModuleId, "CUS_ACCOUNT_M", "CUS_ACCOUNT_D", "live-cus-account",
                        ["ACCOUNT_TYPE", "ACCOUNT_NO"], [action], []),
                    action, EffectEvent.Save, No, [Type, No], "live-test"), token);
            var byEffect = await ReadWriteAsync(connection, transaction, token);

            // 明细单重/毛重取产品档案；对帐数量为零的补为对帐数量（非零不动）；主表按明细汇总
            Assert.Equal((2, 20, 3, 30, 20), byEffect.Details[1]);
            Assert.Equal((null, null, 4, 12, 7), byEffect.Details[2]);
            Assert.Equal(
                (150.75, 19.6, 170.35, 171.6, 13, 27, 42, 216),
                byEffect.Master);

            // ② 回到同一初始态跑旧过程的三条语句
            await SeedWriteAsync(connection, transaction, token);
            await ExecuteAsync(connection, transaction, token, """
                UPDATE d SET d.SUTTLE=p.SUTTLE, d.CUS_QTY=d.QTY*p.SUTTLE, d.GROSS_WEIGHT=p.GROSS_WEIGHT,
                       d.CUS_GROSS_QTY=d.QTY*p.GROSS_WEIGHT
                FROM dbo.CUS_ACCOUNT_D d INNER JOIN dbo.PRODUCT p ON p.PRO_NO=d.PRO_NO
                WHERE d.ACCOUNT_TYPE=@Type AND d.ACCOUNT_NO=@No;
                UPDATE dbo.CUS_ACCOUNT_D SET ACCOUNT_QTY=CUS_QTY
                WHERE ACCOUNT_TYPE=@Type AND ACCOUNT_NO=@No AND ISNULL(ACCOUNT_QTY,0)=0;
                UPDATE m SET m.AMOUNT=s.AMOUNT_SUM, m.TAX_SUM=s.TAX_SUM_SUM, m.AMOUNT_TAX=s.AMOUNT_TAX_SUM,
                       m.SUM_AMOUNT=s.AMOUNT_TAX_SUM+m.OTHER_PRICE, m.QTY_TOTAL=s.QTY_SUM,
                       m.CUS_QTY=s.ACCOUNT_QTY_SUM, m.CUS_GROSS_QTY=s.CUS_GROSS_QTY_SUM,
                       m.PROCESS_AMOUNT=s.ACCOUNT_QTY_SUM*m.PROCESS_PRICE
                FROM dbo.CUS_ACCOUNT_M m INNER JOIN (SELECT d.ACCOUNT_TYPE, d.ACCOUNT_NO,
                            ROUND(SUM(d.AMOUNT),2) AMOUNT_SUM, ROUND(SUM(d.TAX_SUM),2) TAX_SUM_SUM,
                            ROUND(SUM(d.AMOUNT_TAX),2) AMOUNT_TAX_SUM, ROUND(SUM(d.QTY),2) QTY_SUM,
                            ROUND(SUM(d.ACCOUNT_QTY),2) ACCOUNT_QTY_SUM, ROUND(SUM(d.CUS_GROSS_QTY),2) CUS_GROSS_QTY_SUM
                        FROM dbo.CUS_ACCOUNT_D d WHERE d.ACCOUNT_TYPE=@Type AND d.ACCOUNT_NO=@No
                        GROUP BY d.ACCOUNT_TYPE, d.ACCOUNT_NO) s
                  ON s.ACCOUNT_TYPE=m.ACCOUNT_TYPE AND s.ACCOUNT_NO=m.ACCOUNT_NO
                WHERE m.ACCOUNT_TYPE=@Type AND m.ACCOUNT_NO=@No;
                """, ("@Type", Type), ("@No", No));
            var byLegacy = await ReadWriteAsync(connection, transaction, token);

            Assert.Equal(byLegacy.Master, byEffect.Master);
            Assert.Equal(byLegacy.Details, byEffect.Details);
        }
        finally
        {
            await transaction.RollbackAsync(token);
        }
    }

    [Fact]
    public async Task 海关对帐单_对帐不超送退货单数量_与旧过程一致()
    {
        var token = CancellationToken.None;
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync(token);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(token);
        try
        {
            var plan = await LoadValidationPlanAsync(connection, transaction, token);

            // ① 门开 + 送货侧超量（同一送货行的两行明细合计 7，已对帐 4，上限 10 ⇒ 4+7>10）
            await SeedCheckAsync(connection, transaction, token, "send", over: true);
            await SetGateAsync(connection, transaction, token, 1);
            var sendError = await Assert.ThrowsAsync<EffectValidationException>(() =>
                Executor.ValidateAsync(connection, transaction, plan, "SAVE", token, [Type, No]));
            var legacySend = await RunLegacyCheckAsync(connection, transaction, token);

            Assert.False(legacySend.Success);
            Assert.Equal(Normalize(legacySend.Message), Normalize(sendError.Message));
            Assert.StartsWith("以下对帐已超出送货单数量 送货单号 送货数量 已对帐数量 单据数量", Normalize(sendError.Message));
            Assert.Contains($"{SendNo} 10 4 7", Normalize(sendError.Message));

            // ② 门开 + 退货侧超量（已对帐 1 + 本单 5 > 上限 5）
            await SeedCheckAsync(connection, transaction, token, "return", over: true);
            await SetGateAsync(connection, transaction, token, 1);
            var returnError = await Assert.ThrowsAsync<EffectValidationException>(() =>
                Executor.ValidateAsync(connection, transaction, plan, "SAVE", token, [Type, No]));
            var legacyReturn = await RunLegacyCheckAsync(connection, transaction, token);

            Assert.False(legacyReturn.Success);
            Assert.Equal(Normalize(legacyReturn.Message), Normalize(returnError.Message));
            Assert.Contains("以下对帐已超出退货单数量", Normalize(returnError.Message));

            // ③ 门关：门控关掉后同一份超量数据不再被拦
            await SeedCheckAsync(connection, transaction, token, "send", over: true);
            await SetGateAsync(connection, transaction, token, 0);
            await Executor.ValidateAsync(connection, transaction, plan, "SAVE", token, [Type, No]);

            // ④ 门开 + 额度内（合计 6 ≤ 10）放行
            await SeedCheckAsync(connection, transaction, token, "send", over: false);
            await SetGateAsync(connection, transaction, token, 1);
            await Executor.ValidateAsync(connection, transaction, plan, "SAVE", token, [Type, No]);
        }
        finally
        {
            await transaction.RollbackAsync(token);
        }
    }

    /// <summary>旧过程 `P_CUS_ACCOUNT_CHECK` 的语句链（游标逐行拼文案），返回"是否通过 + 文案"。</summary>
    private static async Task<(bool Success, string Message)> RunLegacyCheckAsync(
        SqlConnection connection, SqlTransaction transaction, CancellationToken token)
    {
        const string batch = """
            declare @success int = 1, @msg varchar(8000) = '', @s_r_no nchar(20), @qty float, @finished_qty float, @my_qty float;
            declare cur_tmp cursor for
                select od.SEND_NO, od.QTY, od.FINISHED_QTY, sd.QTY
                  from COP_SEND_D od, (select S_R_TYPE, S_R_NO, S_R_SERIAL_NO, sum(QTY) QTY
                                         from CUS_ACCOUNT_D where ACCOUNT_TYPE=@Type and ACCOUNT_NO=@No
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
                                               from CUS_ACCOUNT_D where ACCOUNT_TYPE=@Type and ACCOUNT_NO=@No
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
        var ok = Convert.ToInt32(reader.GetValue(0)) == 1;
        var message = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
        return (ok, message);
    }

    /// <summary>与对拍脚本同口径的文案归一：把连续空白折叠成一个空格。</summary>
    private static string Normalize(string value) => Regex.Replace(value, @"\s+", " ").Trim();

    private static async Task SetGateAsync(
        SqlConnection connection, SqlTransaction transaction, CancellationToken token, int value)
        => await ExecuteAsync(connection, transaction,
            "UPDATE dbo.MODULES SET ERROR_NO_SAVE=@Value WHERE M_IDX=@ModuleId;",
            token, ("@Value", value), ("@ModuleId", ModuleId));

    private static async Task SeedWriteAsync(
        SqlConnection connection, SqlTransaction transaction, CancellationToken token)
        => await ExecuteAsync(connection, transaction, token, """
            DELETE FROM dbo.CUS_ACCOUNT_D WHERE ACCOUNT_TYPE=@Type;
            DELETE FROM dbo.CUS_ACCOUNT_M WHERE ACCOUNT_TYPE=@Type;
            DELETE FROM dbo.PRODUCT WHERE PRO_NO IN (@P1, @P2);
            INSERT INTO dbo.PRODUCT (PRO_NO, SUTTLE, GROSS_WEIGHT) VALUES (@P1, 2, 3), (@P2, NULL, 4);
            -- 主表先放垃圾值，验证汇总确实覆盖
            INSERT INTO dbo.CUS_ACCOUNT_M (ACCOUNT_TYPE, ACCOUNT_NO, CLIENT_ID, OTHER_PRICE, PROCESS_PRICE,
                                           AMOUNT, TAX_SUM, AMOUNT_TAX, SUM_AMOUNT, QTY_TOTAL, CUS_QTY, CUS_GROSS_QTY, PROCESS_AMOUNT)
            VALUES (@Type, @No, @Client, 1.25, 8, 999, 999, 999, 999, 999, 999, 999, 999);
            INSERT INTO dbo.CUS_ACCOUNT_D (ACCOUNT_TYPE, ACCOUNT_NO, SERIAL_NO, PRO_NO, QTY, SUTTLE, CUS_QTY,
                                           GROSS_WEIGHT, CUS_GROSS_QTY, ACCOUNT_QTY, AMOUNT, TAX_SUM, AMOUNT_TAX)
            VALUES (@Type, @No, 1, @P1, 10, 0, 0, 0, 0, 0, 100.5, 13.07, 113.57),
                   (@Type, @No, 2, @P2, 3, 0, 0, 0, 0, 7, 50.25, 6.53, 56.78);
            """,
            ("@Type", Type), ("@No", No), ("@Client", Client), ("@P1", P1), ("@P2", P2));

    private static async Task SeedCheckAsync(
        SqlConnection connection, SqlTransaction transaction, CancellationToken token, string side, bool over)
    {
        await ExecuteAsync(connection, transaction, token, """
            DELETE FROM dbo.CUS_ACCOUNT_D WHERE ACCOUNT_TYPE=@Type;
            DELETE FROM dbo.CUS_ACCOUNT_M WHERE ACCOUNT_TYPE=@Type;
            DELETE FROM dbo.COP_SEND_D WHERE SEND_TYPE=@Type;
            DELETE FROM dbo.COP_RETURN_D WHERE RETURN_TYPE=@Type;
            DELETE FROM dbo.PRODUCT WHERE PRO_NO IN (@P1, @P2);
            DELETE FROM dbo.CLIENT WHERE CLIENT_ID=@Client;
            INSERT INTO dbo.CLIENT (CLIENT_ID, BUSINESS_TAG) VALUES (@Client, 0);
            INSERT INTO dbo.PRODUCT (PRO_NO, SUTTLE, GROSS_WEIGHT) VALUES (@P1, 2, 3), (@P2, NULL, 4);
            INSERT INTO dbo.CUS_ACCOUNT_M (ACCOUNT_TYPE, ACCOUNT_NO, CLIENT_ID) VALUES (@Type, @No, @Client);
            INSERT INTO dbo.COP_SEND_D (SEND_TYPE, SEND_NO, SERIAL_NO, QTY, FINISHED_QTY) VALUES (@Type, @SendNo, 1, 10, 4);
            INSERT INTO dbo.COP_RETURN_D (RETURN_TYPE, RETURN_NO, SERIAL_NO, QTY, FINISHED_QTY) VALUES (@Type, @ReturnNo, 1, 5, 1);
            """,
            ("@Type", Type), ("@No", No), ("@Client", Client), ("@P1", P1), ("@P2", P2),
            ("@SendNo", SendNo), ("@ReturnNo", ReturnNo));
        await FillDetailsAsync(connection, transaction, token, side, over);
    }

    /// <summary>按侧别补明细：超量时同一引用键分两行（合计越界，单行都不越界），否则合计在额度内。</summary>
    private static async Task FillDetailsAsync(
        SqlConnection connection, SqlTransaction transaction, CancellationToken token, string side, bool over)
    {
        var first = over ? 3 : 2;
        var second = over ? 4 : 4;
        var reference = side == "send" ? SendNo : ReturnNo;
        await ExecuteAsync(connection, transaction, token, """
            INSERT INTO dbo.CUS_ACCOUNT_D (ACCOUNT_TYPE, ACCOUNT_NO, SERIAL_NO, PRO_NO, S_R_TYPE, S_R_NO, S_R_SERIAL_NO, QTY)
            VALUES (@Type, @No, 1, @P1, @Type, @Ref, 1, @First),
                   (@Type, @No, 2, @P1, @Type, @Ref, 1, @Second);
            """,
            ("@Type", Type), ("@No", No), ("@P1", P1), ("@Ref", reference),
            ("@First", first), ("@Second", second));
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

    private static async Task<ModuleEffectPlan> LoadValidationPlanAsync(
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
            Assert.True(await reader.ReadAsync(token), $"模块 {ModuleId} 缺少当前快照或 SAVE 期 qty-not-exceed 配置");
            masterTable = reader.GetString(0);
            detailTable = reader.GetString(1);
            using var definition = JsonDocument.Parse(reader.GetString(2));
            pkJson = definition.RootElement.GetProperty("MasterPkOrder").GetRawText();
            paramStruct = reader.GetString(3);
        }
        var pkOrder = JsonSerializer.Deserialize<List<string>>(pkJson)!;
        using var parameters = JsonDocument.Parse(paramStruct);
        return new ModuleEffectPlan(ModuleId, masterTable, detailTable, $"live-cus-account-{ModuleId}", pkOrder,
            Array.Empty<EffectActionPlan>(),
            [new EffectValidationPlan(1, "SAVE", "qty-not-exceed", true, parameters.RootElement.Clone(), null)]);
    }

    private static JsonElement? Parse(string? json)
        => string.IsNullOrWhiteSpace(json) ? null : JsonDocument.Parse(json).RootElement.Clone();

    private static async Task<((double Amount, double TaxSum, double AmountTax, double SumAmount, double QtyTotal,
        double CusQty, double CusGrossQty, double ProcessAmount) Master, Dictionary<int, (double? Suttle, double? CusQty,
        double? GrossWeight, double? CusGrossQty, double? AccountQty)> Details)> ReadWriteAsync(
        SqlConnection connection, SqlTransaction transaction, CancellationToken token)
    {
        double amount, taxSum, amountTax, sumAmount, qtyTotal, cusQty, cusGrossQty, processAmount;
        await using (var master = new SqlCommand("""
            SELECT ISNULL(AMOUNT,0), ISNULL(TAX_SUM,0), ISNULL(AMOUNT_TAX,0), ISNULL(SUM_AMOUNT,0),
                   ISNULL(QTY_TOTAL,0), ISNULL(CUS_QTY,0), ISNULL(CUS_GROSS_QTY,0), ISNULL(PROCESS_AMOUNT,0)
            FROM dbo.CUS_ACCOUNT_M WHERE ACCOUNT_TYPE=@Type AND ACCOUNT_NO=@No;
            """, connection, transaction))
        {
            master.Parameters.Add("@Type", SqlDbType.NVarChar, 20).Value = Type;
            master.Parameters.Add("@No", SqlDbType.NVarChar, 40).Value = No;
            await using var reader = await master.ExecuteReaderAsync(token);
            Assert.True(await reader.ReadAsync(token));
            amount = Convert.ToDouble(reader.GetValue(0));
            taxSum = Convert.ToDouble(reader.GetValue(1));
            amountTax = Convert.ToDouble(reader.GetValue(2));
            sumAmount = Convert.ToDouble(reader.GetValue(3));
            qtyTotal = Convert.ToDouble(reader.GetValue(4));
            cusQty = Convert.ToDouble(reader.GetValue(5));
            cusGrossQty = Convert.ToDouble(reader.GetValue(6));
            processAmount = Convert.ToDouble(reader.GetValue(7));
        }
        var details = new Dictionary<int, (double?, double?, double?, double?, double?)>();
        await using (var detail = new SqlCommand("""
            SELECT SERIAL_NO, SUTTLE, CUS_QTY, GROSS_WEIGHT, CUS_GROSS_QTY, ACCOUNT_QTY
            FROM dbo.CUS_ACCOUNT_D WHERE ACCOUNT_TYPE=@Type AND ACCOUNT_NO=@No ORDER BY SERIAL_NO;
            """, connection, transaction))
        {
            detail.Parameters.Add("@Type", SqlDbType.NVarChar, 20).Value = Type;
            detail.Parameters.Add("@No", SqlDbType.NVarChar, 40).Value = No;
            await using var reader = await detail.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
                details[Convert.ToInt32(reader.GetValue(0))] = (
                    Nullable(reader, 1), Nullable(reader, 2), Nullable(reader, 3), Nullable(reader, 4), Nullable(reader, 5));
        }
        return ((amount, taxSum, amountTax, sumAmount, qtyTotal, cusQty, cusGrossQty, processAmount), details);
    }

    private static double? Nullable(SqlDataReader reader, int index)
        => reader.IsDBNull(index) ? null : Convert.ToDouble(reader.GetValue(index));

    private static async Task ExecuteAsync(
        SqlConnection connection, SqlTransaction transaction, CancellationToken token, string sql,
        params (string Name, object? Value)[] parameters)
    {
        await using var command = new SqlCommand(sql, connection, transaction);
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(token);
    }

    private static Task ExecuteAsync(
        SqlConnection connection, SqlTransaction transaction, string sql, CancellationToken token,
        params (string Name, object? Value)[] parameters)
        => ExecuteAsync(connection, transaction, token, sql, parameters);
}
