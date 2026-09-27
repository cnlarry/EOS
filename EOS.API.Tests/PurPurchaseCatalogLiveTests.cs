using System.Data;
using System.Text.Json;
using EOS.API.Data.Effects;
using EOS.API.Data.Effects.ServiceEffectHandlers;
using Microsoft.Data.SqlClient;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 采购单（1606，原 `pur-purchase` C#）入校验/效果目录后的真库验证：
///   ① 校验段 `custom-validation` → `pur-purchase-check`：产品计价已过有效期、预交日期早于采购日期；
///   ② 写段 `pur-purchase-sync`：待购表汇总同步整链（补明细 → 币别税率带出 → 厂商计价回填单价 →
///      应购数量清零回填 → 金额按税种 I/O/N 重算 → 主表金额按汇率折算汇总 → 数量逐行分配 → 单号串联）。
/// 写段用"旧 C# 的语句链内联为基准"做对拍，并断言关键列值。造数用 `ADR12PU` 前缀，事务结束回滚。
/// </summary>
[Collection("live-database")]
public sealed class PurPurchaseCatalogLiveTests
{
    private static readonly string ConnectionString =
        Environment.GetEnvironmentVariable("MSSQL_ERP_CONN")
        ?? throw new InvalidOperationException(
            "真库集成测试需要 MSSQL_ERP_CONN；未配置即失败（无连接跳过≠已验证）。");

    private static readonly EffectValidationExecutor Executor = new();

    private const int ModuleId = 1606;
    private const string Type = "ADR12PU";
    private const string No = "ADR12PU001";
    private const string Supplier = "ADR12PUSUP";
    private const string Pro1 = "ADR12PUPRO1";
    private const string Pro2 = "ADR12PUPRO2";
    private const string Unit = "ADR12PUU";
    private const string Curr = "ADR12PUC";
    private const string TaxId = "ADR12PUT";

    [Fact]
    public async Task 采购单_校验判据与待购表同步()
    {
        var token = CancellationToken.None;
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync(token);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(token);
        try
        {
            // ① 校验：产品计价已过有效期
            var plan = await LoadCheckPlanAsync(connection, transaction, token);
            await SeedAsync(connection, transaction, token, priceInEffectDaysAgo: 10);
            var priceExpired = await Assert.ThrowsAsync<EffectValidationException>(() =>
                Executor.ValidateAsync(connection, transaction, plan, "SAVE", token, [Type, No]));
            Assert.StartsWith("以下产品计价已过有效期\r\n", priceExpired.Message);

            // ② 校验：预交日期早于采购日期（用未过期的计价行，避免先命中计价判据）
            await SeedAsync(connection, transaction, token, priceInEffectDaysAgo: -1, planDeliveryDaysBefore: 5);
            var delivery = await Assert.ThrowsAsync<EffectValidationException>(() =>
                Executor.ValidateAsync(connection, transaction, plan, "SAVE", token, [Type, No]));
            Assert.StartsWith("以下序号项预交日期小于采购单日期", delivery.Message);

            // ③ 校验通过
            await SeedAsync(connection, transaction, token, priceInEffectDaysAgo: -1);
            await Executor.ValidateAsync(connection, transaction, plan, "SAVE", token, [Type, No]);

            // ④ 写段：目录效果 与 旧语句链 对拍
            await SeedAsync(connection, transaction, token, priceInEffectDaysAgo: -1);
            var action = await LoadActionAsync(connection, transaction, token);
            Assert.Equal("pur-purchase-sync", action.EffectKey);
            var effectPlan = new ModuleEffectPlan(ModuleId, "PUR_PURCHASE_M", "PUR_PURCHASE_D", "live-pur-purchase",
                ["PURCHASE_TYPE", "PURCHASE_NO"], [action], []);
            await new PurPurchaseSyncHandler().ExecuteAsync(
                new ServiceEffectContext(connection, transaction, effectPlan, action, EffectEvent.Save,
                    No, [Type, No], "live-test"), token);
            var byEffect = await ReadAsync(connection, transaction, token);

            await SeedAsync(connection, transaction, token, priceInEffectDaysAgo: -1);
            await RunBaselineAsync(connection, transaction, token);
            var byBaseline = await ReadAsync(connection, transaction, token);

            Assert.Equal(byBaseline.Names, byEffect.Names);
            Assert.Equal(byBaseline.Details, byEffect.Details);
            Assert.Equal(byBaseline.MoreQty, byEffect.MoreQty);

            // 关键结论：补行（P2 待购行 → 明细序号 3、数量取待购合计）、单价回填、应购回填、数量分配、单号串联
            // 补行：待购表里尚未成行的 P2 追加为明细序号 2（数量取待购合计 2、单价取厂商计价 30）
            Assert.Contains(byEffect.Details, row => row.StartsWith("2|" + Pro2 + "|2|30|2|60|66|6|O", StringComparison.Ordinal));
            Assert.Equal(("PO-1,PO-2", "PR1,PR2"), byEffect.Names);
            Assert.Equal([3d, 1d, 2d], byEffect.MoreQty);
        }
        finally
        {
            await transaction.RollbackAsync(token);
        }
    }

    /// <summary>旧 C# `PurPurchaseAfterSaveAsync` 的写段语句链（补行 → 带出 → 回填 → 清零回填 → 重算 → 汇总 → 分配 → 单号）。</summary>
    private static Task RunBaselineAsync(SqlConnection connection, SqlTransaction transaction, CancellationToken token)
    {
        const string batch = """
            DECLARE @Type NVARCHAR(20) = N'ADR12PU', @No NVARCHAR(40) = N'ADR12PU001';
            DECLARE @maxSerial INT = (SELECT ISNULL(MAX(SERIAL_NO),0) FROM dbo.PUR_PURCHASE_D WHERE PURCHASE_TYPE=@Type AND PURCHASE_NO=@No);
            DECLARE @proNo NVARCHAR(60), @qty FLOAT;
            DECLARE cur CURSOR FOR
                SELECT DISTINCT LTRIM(RTRIM(m.PRO_NO)) FROM dbo.PUR_PURCHASE_MORE m
                 WHERE m.PURCHASE_TYPE=@Type AND m.PURCHASE_NO=@No
                   AND NOT EXISTS (SELECT 1 FROM dbo.PUR_PURCHASE_D d
                                   WHERE d.PURCHASE_TYPE=m.PURCHASE_TYPE AND d.PURCHASE_NO=m.PURCHASE_NO AND d.PRO_NO=m.PRO_NO);
            OPEN cur
            FETCH NEXT FROM cur INTO @proNo
            WHILE @@FETCH_STATUS = 0
            BEGIN
                SET @maxSerial = @maxSerial + 1;
                SET @qty = (SELECT SUM(REQUIRE_QTY) FROM dbo.PUR_PURCHASE_MORE WHERE PURCHASE_TYPE=@Type AND PURCHASE_NO=@No AND PRO_NO=@proNo);
                INSERT INTO dbo.PUR_PURCHASE_D (PURCHASE_TYPE, PURCHASE_NO, SERIAL_NO, PRO_NO, DEPOT_ID, QTY, RECEIVE_QTY, UNIT_ID)
                SELECT @Type, @No, @maxSerial, p.PRO_NO, p.DEPOT_ID, @qty, 0, p.UNIT_ID FROM dbo.PRODUCT p WHERE p.PRO_NO=@proNo;
                FETCH NEXT FROM cur INTO @proNo
            END
            CLOSE cur
            DEALLOCATE cur;

            UPDATE d SET d.CURR_ID=m.CURR_ID, d.CURR_RATE=m.CURR_RATE, d.TAX_TYPE=m.TAX_TYPE,
                   d.TAX_RATE=m.TAX_RATE, d.TAX_ID=m.TAX_ID
              FROM dbo.PUR_PURCHASE_D d INNER JOIN dbo.PUR_PURCHASE_M m
                ON m.PURCHASE_TYPE=d.PURCHASE_TYPE AND m.PURCHASE_NO=d.PURCHASE_NO
             WHERE d.PURCHASE_TYPE=@Type AND d.PURCHASE_NO=@No;

            UPDATE d SET d.PRICE=p.PRICE, d.TAX_RATE=p.TAX_RATE, d.CURR_RATE=p.CURR_RATE, d.REBATE=p.REBATE
              FROM dbo.PUR_PURCHASE_D d
              INNER JOIN dbo.PUR_PURCHASE_M m ON m.PURCHASE_TYPE=d.PURCHASE_TYPE AND m.PURCHASE_NO=d.PURCHASE_NO
              INNER JOIN dbo.SUPPLIER_PRICE_D p
                ON p.SUPPLIER_ID=m.SUPPLIER_ID AND p.PRO_NO=d.PRO_NO AND p.UNIT_ID=d.UNIT_ID
               AND p.CURR_ID=d.CURR_ID AND p.TAX_ID=d.TAX_ID AND p.TAX_TYPE=d.TAX_TYPE
             WHERE d.PURCHASE_TYPE=@Type AND d.PURCHASE_NO=@No;

            UPDATE dbo.PUR_PURCHASE_D SET REQUIRE_QTY=0 WHERE PURCHASE_TYPE=@Type AND PURCHASE_NO=@No;
            UPDATE d SET d.REQUIRE_QTY=s.REQUIRE_QTY FROM dbo.PUR_PURCHASE_D d
              INNER JOIN (SELECT PURCHASE_TYPE, PURCHASE_NO, PRO_NO, SUM(REQUIRE_QTY) REQUIRE_QTY
                            FROM dbo.PUR_PURCHASE_MORE WHERE PURCHASE_TYPE=@Type AND PURCHASE_NO=@No
                           GROUP BY PURCHASE_TYPE, PURCHASE_NO, PRO_NO) s
                ON d.PURCHASE_TYPE=s.PURCHASE_TYPE AND d.PURCHASE_NO=s.PURCHASE_NO AND d.PRO_NO=s.PRO_NO
             WHERE d.PURCHASE_TYPE=@Type AND d.PURCHASE_NO=@No;

            UPDATE dbo.PUR_PURCHASE_D SET
                AMOUNT=CASE TAX_TYPE WHEN 'I' THEN ROUND((QTY*PRICE*ISNULL(REBATE,100)/100)/(1+ISNULL(TAX_RATE,0)/100),2)
                                     ELSE ROUND(QTY*PRICE*ISNULL(REBATE,100)/100,2) END,
                AMOUNT_TAX=CASE TAX_TYPE WHEN 'O' THEN ROUND(QTY*PRICE*ISNULL(REBATE,100)/100*(1+ISNULL(TAX_RATE,0)/100),2)
                                         ELSE ROUND(QTY*PRICE*ISNULL(REBATE,100)/100,2) END,
                TAX_SUM=CASE TAX_TYPE WHEN 'N' THEN 0
                        WHEN 'O' THEN ROUND(QTY*PRICE*ISNULL(REBATE,100)/100*ISNULL(TAX_RATE,0)/100,2)
                        WHEN 'I' THEN ROUND(QTY*PRICE*ISNULL(REBATE,100)/100*ISNULL(TAX_RATE,0)/100/(1+ISNULL(TAX_RATE,0)/100),2)
                        ELSE 0 END
             WHERE PURCHASE_TYPE=@Type AND PURCHASE_NO=@No;

            UPDATE m SET m.AMOUNT=ROUND(d.AMOUNT/m.CURR_RATE,2), m.AMOUNT_TAX=ROUND(d.AMOUNT_TAX/m.CURR_RATE,2),
                   m.TAX_SUM=ROUND(d.TAX_SUM/m.CURR_RATE,2)
              FROM dbo.PUR_PURCHASE_M m
              INNER JOIN (SELECT PURCHASE_TYPE, PURCHASE_NO, SUM(AMOUNT*CURR_RATE) AMOUNT,
                                 SUM(AMOUNT_TAX*CURR_RATE) AMOUNT_TAX, SUM(TAX_SUM*CURR_RATE) TAX_SUM
                            FROM dbo.PUR_PURCHASE_D WHERE PURCHASE_TYPE=@Type AND PURCHASE_NO=@No
                           GROUP BY PURCHASE_TYPE, PURCHASE_NO) d
                ON m.PURCHASE_TYPE=d.PURCHASE_TYPE AND m.PURCHASE_NO=d.PURCHASE_NO
             WHERE m.PURCHASE_TYPE=@Type AND m.PURCHASE_NO=@No;

            UPDATE dbo.PUR_PURCHASE_MORE SET QTY=0 WHERE PURCHASE_TYPE=@Type AND PURCHASE_NO=@No;
            DECLARE @currentPro NVARCHAR(60) = NULL, @remaining FLOAT = 0, @serial INT, @requireQty FLOAT, @detailQty FLOAT;
            DECLARE cur2 CURSOR FOR
                SELECT SERIAL_NO, LTRIM(RTRIM(PRO_NO)), REQUIRE_QTY FROM dbo.PUR_PURCHASE_MORE
                 WHERE PURCHASE_TYPE=@Type AND PURCHASE_NO=@No ORDER BY PRO_NO, SERIAL_NO;
            OPEN cur2
            FETCH NEXT FROM cur2 INTO @serial, @proNo, @requireQty
            WHILE @@FETCH_STATUS = 0
            BEGIN
                IF @currentPro IS NULL OR @currentPro <> @proNo
                BEGIN
                    SET @currentPro = @proNo;
                    SET @detailQty = (SELECT QTY FROM dbo.PUR_PURCHASE_D WHERE PURCHASE_TYPE=@Type AND PURCHASE_NO=@No AND PRO_NO=@proNo);
                    SET @remaining = ISNULL(@detailQty, 0);
                END
                IF @remaining > @requireQty
                BEGIN
                    UPDATE dbo.PUR_PURCHASE_MORE SET QTY=@requireQty WHERE PURCHASE_TYPE=@Type AND PURCHASE_NO=@No AND SERIAL_NO=@serial;
                    SET @remaining = @remaining - @requireQty;
                END
                ELSE IF @remaining > 0
                BEGIN
                    UPDATE dbo.PUR_PURCHASE_MORE SET QTY=@remaining WHERE PURCHASE_TYPE=@Type AND PURCHASE_NO=@No AND SERIAL_NO=@serial;
                    SET @remaining = @remaining - @requireQty;
                END
                FETCH NEXT FROM cur2 INTO @serial, @proNo, @requireQty
            END
            CLOSE cur2
            DEALLOCATE cur2;

            DECLARE @orderNo NVARCHAR(MAX) = STUFF((SELECT DISTINCT ',' + LTRIM(RTRIM(ORDER_NO))
                                                      FROM dbo.PUR_PURCHASE_MORE
                                                     WHERE PURCHASE_TYPE=@Type AND PURCHASE_NO=@No AND ISNULL(ORDER_NO,'')<>''
                                                     ORDER BY ',' + LTRIM(RTRIM(ORDER_NO)) FOR XML PATH('')), 1, 1, '');
            IF ISNULL(@orderNo,'') <> ''
                UPDATE dbo.PUR_PURCHASE_M SET ORDER_NO=@orderNo WHERE PURCHASE_TYPE=@Type AND PURCHASE_NO=@No;
            DECLARE @produceNo NVARCHAR(MAX) = STUFF((SELECT DISTINCT ',' + LTRIM(RTRIM(PRODUCE_NO))
                                                        FROM dbo.PUR_PURCHASE_MORE
                                                       WHERE PURCHASE_TYPE=@Type AND PURCHASE_NO=@No AND ISNULL(PRODUCE_NO,'')<>''
                                                       ORDER BY ',' + LTRIM(RTRIM(PRODUCE_NO)) FOR XML PATH('')), 1, 1, '');
            IF ISNULL(@produceNo,'') <> ''
                UPDATE dbo.PUR_PURCHASE_M SET PRODUCE_NO=@produceNo WHERE PURCHASE_TYPE=@Type AND PURCHASE_NO=@No;
            """;
        return ExecuteAsync(connection, transaction, token, batch);
    }

    private static async Task<ModuleEffectPlan> LoadCheckPlanAsync(
        SqlConnection connection, SqlTransaction transaction, CancellationToken token)
    {
        string masterTable, detailTable, pkJson, paramStruct;
        await using (var command = new SqlCommand("""
            SELECT m.MASTER_TABLE, m.DETAIL_TABLE, s.DEFINITION_JSON, r.PARAM_STRUCT
            FROM dbo.MODULES m
            JOIN dbo.WORKBENCH_DEFINITION_SNAPSHOT s ON s.M_IDX = m.M_IDX AND s.IS_CURRENT = 1
            JOIN dbo.MODULE_VALIDATION_RULE r ON r.M_IDX = m.M_IDX AND r.STAGE = N'SAVE'
                 AND r.VALIDATION_KEY = N'custom-validation'
            WHERE m.M_IDX = @ModuleId;
            """, connection, transaction))
        {
            command.Parameters.Add("@ModuleId", SqlDbType.Int).Value = ModuleId;
            await using var reader = await command.ExecuteReaderAsync(token);
            Assert.True(await reader.ReadAsync(token), "模块 1606 缺少当前快照或 SAVE 期 custom-validation 配置");
            masterTable = reader.GetString(0);
            detailTable = reader.GetString(1);
            using var definition = JsonDocument.Parse(reader.GetString(2));
            pkJson = definition.RootElement.GetProperty("MasterPkOrder").GetRawText();
            paramStruct = reader.GetString(3);
        }
        var pkOrder = JsonSerializer.Deserialize<List<string>>(pkJson)!;
        using var parameters = JsonDocument.Parse(paramStruct);
        return new ModuleEffectPlan(ModuleId, masterTable, detailTable, "live-pur-purchase", pkOrder,
            Array.Empty<EffectActionPlan>(),
            [new EffectValidationPlan(3, "SAVE", "custom-validation", true, parameters.RootElement.Clone(), null)]);
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
        static JsonElement? Parse(string? json)
            => string.IsNullOrWhiteSpace(json) ? null : JsonDocument.Parse(json).RootElement.Clone();
        return new EffectActionPlan(
            reader.GetInt32(0), reader.GetString(1), reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetBoolean(4), reader.GetString(5),
            Parse(reader.IsDBNull(6) ? null : reader.GetString(6)),
            Parse(reader.IsDBNull(7) ? null : reader.GetString(7)),
            Parse(reader.IsDBNull(8) ? null : reader.GetString(8)),
            Array.Empty<EffectOpPlan>());
    }

    private static async Task<((string OrderNo, string ProduceNo) Names, List<string> Details, List<double> MoreQty)>
        ReadAsync(SqlConnection connection, SqlTransaction transaction, CancellationToken token)
    {
        string orderNo, produceNo;
        await using (var command = new SqlCommand(
            "SELECT ISNULL(ORDER_NO,''), ISNULL(PRODUCE_NO,'') FROM dbo.PUR_PURCHASE_M WHERE PURCHASE_TYPE=@Type AND PURCHASE_NO=@No;",
            connection, transaction))
        {
            command.Parameters.Add("@Type", SqlDbType.NVarChar, 20).Value = Type;
            command.Parameters.Add("@No", SqlDbType.NVarChar, 40).Value = No;
            await using var reader = await command.ExecuteReaderAsync(token);
            Assert.True(await reader.ReadAsync(token));
            orderNo = reader.GetString(0);
            produceNo = reader.GetString(1);
        }
        var details = new List<string>();
        await using (var command = new SqlCommand("""
            SELECT SERIAL_NO, LTRIM(RTRIM(PRO_NO)), ISNULL(QTY,0), ISNULL(PRICE,0), ISNULL(REQUIRE_QTY,0),
                   ISNULL(AMOUNT,0), ISNULL(AMOUNT_TAX,0), ISNULL(TAX_SUM,0), ISNULL(TAX_TYPE,'')
            FROM dbo.PUR_PURCHASE_D WHERE PURCHASE_TYPE=@Type AND PURCHASE_NO=@No ORDER BY SERIAL_NO;
            """, connection, transaction))
        {
            command.Parameters.Add("@Type", SqlDbType.NVarChar, 20).Value = Type;
            command.Parameters.Add("@No", SqlDbType.NVarChar, 40).Value = No;
            await using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
                details.Add(string.Join('|', Enumerable.Range(0, reader.FieldCount)
                    .Select(i => reader.IsDBNull(i) ? string.Empty
                        : Convert.ToString(reader.GetValue(i), System.Globalization.CultureInfo.InvariantCulture)?.Trim())));
        }
        var moreQty = new List<double>();
        await using (var command = new SqlCommand(
            "SELECT ISNULL(QTY,0) FROM dbo.PUR_PURCHASE_MORE WHERE PURCHASE_TYPE=@Type AND PURCHASE_NO=@No ORDER BY SERIAL_NO;",
            connection, transaction))
        {
            command.Parameters.Add("@Type", SqlDbType.NVarChar, 20).Value = Type;
            command.Parameters.Add("@No", SqlDbType.NVarChar, 40).Value = No;
            await using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token)) moreQty.Add(Convert.ToDouble(reader.GetValue(0)));
        }
        return ((orderNo, produceNo), details, moreQty);
    }

    private static async Task SeedAsync(SqlConnection connection, SqlTransaction transaction, CancellationToken token,
        int priceInEffectDaysAgo, int planDeliveryDaysBefore = -1)
    {
        await ExecuteAsync(connection, transaction, token, """
            DELETE FROM dbo.PUR_PURCHASE_D WHERE PURCHASE_TYPE=@Type;
            DELETE FROM dbo.PUR_PURCHASE_MORE WHERE PURCHASE_TYPE=@Type;
            DELETE FROM dbo.PUR_PURCHASE_M WHERE PURCHASE_TYPE=@Type;
            DELETE FROM dbo.SUPPLIER_PRICE_D WHERE SUPPLIER_ID=@Supplier;
            DELETE FROM dbo.PRODUCT WHERE PRO_NO IN (@Pro1, @Pro2);
            DELETE FROM dbo.SUPPLIER WHERE SUPPLIER_ID=@Supplier;
            INSERT INTO dbo.SUPPLIER (SUPPLIER_ID, BUSINESS_TAG) VALUES (@Supplier, 0);
            INSERT INTO dbo.PRODUCT (PRO_NO, DEPOT_ID, UNIT_ID) VALUES (@Pro1, N'ADR12PUDP', @Unit), (@Pro2, N'ADR12PUDP', @Unit);
            INSERT INTO dbo.PUR_PURCHASE_M (PURCHASE_TYPE, PURCHASE_NO, PURCHASE_DATE, SUPPLIER_ID, CURR_ID, CURR_RATE,
                                            TAX_TYPE, TAX_RATE, TAX_ID, ORDER_NO, PRODUCE_NO)
            VALUES (@Type, @No, '2026-03-10', @Supplier, @Curr, 1, N'O', 10, @TaxId, N'OLD-O', N'OLD-P');
            INSERT INTO dbo.PUR_PURCHASE_D (PURCHASE_TYPE, PURCHASE_NO, SERIAL_NO, PRO_NO, QTY, RECEIVE_QTY, UNIT_ID,
                                           CURR_ID, CURR_RATE, TAX_TYPE, TAX_RATE, TAX_ID)
            VALUES (@Type, @No, 1, @Pro1, 5, 0, @Unit, @Curr, 1, N'O', 10, @TaxId);
            INSERT INTO dbo.PUR_PURCHASE_MORE (PURCHASE_TYPE, PURCHASE_NO, SERIAL_NO, PRO_NO, QTY, REQUIRE_QTY, ORDER_NO, PRODUCE_NO)
            VALUES (@Type, @No, 1, @Pro1, 77, 3, N'PO-1', N'PR1'),
                   (@Type, @No, 2, @Pro1, 77, 1, N'PO-1', N'PR2'),
                   (@Type, @No, 3, @Pro2, 77, 2, N'PO-2', N'PR1');
            """,
            ("@Type", Type), ("@No", No), ("@Supplier", Supplier), ("@Pro1", Pro1), ("@Pro2", Pro2),
            ("@Unit", Unit), ("@Curr", Curr), ("@TaxId", TaxId));
        if (priceInEffectDaysAgo >= 0)
        {
            await ExecuteAsync(connection, transaction, token, """
                INSERT INTO dbo.SUPPLIER_PRICE_D (SUPPLIER_ID, PRO_NO, UNIT_ID, CURR_ID, TAX_ID, TAX_TYPE,
                                                  PRICE, TAX_RATE, CURR_RATE, REBATE, IN_EFFECT_DATE)
                VALUES (@Supplier, @Pro1, @Unit, @Curr, @TaxId, N'O', 20, 10, 1, 100, DATEADD(day, -@DaysAgo, '2026-03-10'));
                """, ("@Supplier", Supplier), ("@Pro1", Pro1), ("@Unit", Unit), ("@Curr", Curr),
                ("@TaxId", TaxId), ("@DaysAgo", priceInEffectDaysAgo));
        }
        else
        {
            await ExecuteAsync(connection, transaction, token, """
                INSERT INTO dbo.SUPPLIER_PRICE_D (SUPPLIER_ID, PRO_NO, UNIT_ID, CURR_ID, TAX_ID, TAX_TYPE,
                                                  PRICE, TAX_RATE, CURR_RATE, REBATE, IN_EFFECT_DATE)
                VALUES (@Supplier, @Pro1, @Unit, @Curr, @TaxId, N'O', 20, 10, 1, 100, '2026-04-01'),
                       (@Supplier, @Pro2, @Unit, @Curr, @TaxId, N'O', 30, 10, 1, 100, '2026-04-01');
                """, ("@Supplier", Supplier), ("@Pro1", Pro1), ("@Pro2", Pro2), ("@Unit", Unit), ("@Curr", Curr),
                ("@TaxId", TaxId));
        }
        if (planDeliveryDaysBefore >= 0)
        {
            await ExecuteAsync(connection, transaction, token, """
                UPDATE dbo.PUR_PURCHASE_D SET PLAN_DELIVERY_DATE=DATEADD(day, -@Days, '2026-03-10')
                 WHERE PURCHASE_TYPE=@Type AND PURCHASE_NO=@No;
                """, ("@Type", Type), ("@No", No), ("@Days", planDeliveryDaysBefore));
        }
    }

    private static async Task ExecuteAsync(
        SqlConnection connection, SqlTransaction transaction, CancellationToken token, string sql,
        params (string Name, object? Value)[] parameters)
    {
        await using var command = new SqlCommand(sql, connection, transaction);
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(token);
    }
}
