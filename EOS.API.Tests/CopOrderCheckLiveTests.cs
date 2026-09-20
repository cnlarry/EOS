using System.Data;
using System.Text.Json;
using System.Text.RegularExpressions;
using EOS.API.Data.Effects;
using Microsoft.Data.SqlClient;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 客户订单（1405，原 `cop-order` C#）入校验目录后的真库验证：
/// 保存期规则 `custom-validation` → 注册实现 `cop-order-check` 的八条表达式级跨表判据
/// （交易天数 / 最低订单额 / 信用余额 / 产品交易天数 / 计价有效期 / 最小生产量 / 订单号重复 / 预交日期）。
/// 用例逐条造数触发，断言"命中 + 文案"，并核对该规则在"全部合规"时放行；
/// 同时验证 `custom-validation` 的闭集：未注册 handler 在发布期即被拒（用注册表本身断言）。
/// 造数用 `ADR12CO` 前缀，事务结束回滚。
/// </summary>
[Collection("live-database")]
public sealed class CopOrderCheckLiveTests
{
    private static readonly string ConnectionString =
        Environment.GetEnvironmentVariable("EOS_ERP_TEST_CONNECTION")
        ?? throw new InvalidOperationException(
            "真库集成测试需要 EOS_ERP_TEST_CONNECTION；未配置即失败（无连接跳过≠已验证）。");

    private static readonly EffectValidationExecutor Executor = new();

    private const int ModuleId = 1405;
    private const string Type = "ADR12CO";
    private const string No = "ADR12CO001";
    private const string Client = "ADR12COCL";
    private const string Pro1 = "ADR12COPRO1";
    private const string Pro2 = "ADR12COPRO2";

    [Fact]
    public async Task 客户订单_八条判据按序命中且合规放行()
    {
        var token = CancellationToken.None;
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync(token);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(token);
        try
        {
            var plan = await LoadPlanAsync(connection, transaction, token);

            // 基线：全部合规 ⇒ 放行
            await SeedAsync(connection, transaction, token);
            await Executor.ValidateAsync(connection, transaction, plan, "SAVE", token, [Type, No]);

            // ① 客户交易天数：CLIENT_DAYS=1 且最后交易日早于订单日 10 天
            await SeedAsync(connection, transaction, token, clientDays: 1, lastTradeDaysAgo: 10);
            var tradeDays = await Assert.ThrowsAsync<EffectValidationException>(() =>
                Executor.ValidateAsync(connection, transaction, plan, "SAVE", token, [Type, No]));
            Assert.Equal("已超过客户交易天数", tradeDays.Message);

            // ② 最低订单金额：最低订单额 100 × 客户币别汇率 > 订单价税合计 × 订单币别汇率
            await SeedAsync(connection, transaction, token, minOrderAmount: 5000);
            var minOrder = await Assert.ThrowsAsync<EffectValidationException>(() =>
                Executor.ValidateAsync(connection, transaction, plan, "SAVE", token, [Type, No]));
            Assert.StartsWith("总金额小于客户最低订单额:", minOrder.Message);

            // ③ 客户信用余额：信用额度不足
            await SeedAsync(connection, transaction, token, creditLimit: 1);
            var credit = await Assert.ThrowsAsync<EffectValidationException>(() =>
                Executor.ValidateAsync(connection, transaction, plan, "SAVE", token, [Type, No]));
            Assert.StartsWith("客户信用余额不足：", credit.Message);

            // ④ 产品交易天数：产品最后交易日早于订单日且 PRODUCT_DAYS 很小
            await SeedAsync(connection, transaction, token, clientDays: 999, productDays: 1,
                lastTradeDaysAgo: 0, productTradeDaysAgo: 10);
            var productDays = await Assert.ThrowsAsync<EffectValidationException>(() =>
                Executor.ValidateAsync(connection, transaction, plan, "SAVE", token, [Type, No]));
            Assert.StartsWith("以下产品编号已超出产品交易天数限制\r\n", productDays.Message);

            // ⑤ 产品计价有效期：客户计价行的生效日早于订单日期
            await SeedAsync(connection, transaction, token, priceInEffectDaysAgo: 10);
            var priceExpired = await Assert.ThrowsAsync<EffectValidationException>(() =>
                Executor.ValidateAsync(connection, transaction, plan, "SAVE", token, [Type, No]));
            Assert.StartsWith("以下产品计价已过有效期", priceExpired.Message);

            // ⑥ 最小生产数量：明细数量低于产品最小生产量
            await SeedAsync(connection, transaction, token, qty: 1, minProduceQty: 5);
            var minProduce = await Assert.ThrowsAsync<EffectValidationException>(() =>
                Executor.ValidateAsync(connection, transaction, plan, "SAVE", token, [Type, No]));
            Assert.StartsWith("以下产品编号订单量低于最小生产要求数量", minProduce.Message);

            // ⑦ 客户订单号重复（另一张单占用了同一客户订单号）
            await SeedAsync(connection, transaction, token, duplicateClientOrderNo: true);
            var duplicate = await Assert.ThrowsAsync<EffectValidationException>(() =>
                Executor.ValidateAsync(connection, transaction, plan, "SAVE", token, [Type, No]));
            Assert.Equal("客户订单号重复。", duplicate.Message);

            // ⑧ 预交日期早于订单日期
            await SeedAsync(connection, transaction, token, preSendDaysBeforeOrder: 3);
            var preSend = await Assert.ThrowsAsync<EffectValidationException>(() =>
                Executor.ValidateAsync(connection, transaction, plan, "SAVE", token, [Type, No]));
            Assert.StartsWith("以下序号项预交日期小于订单日期", preSend.Message);
        }
        finally
        {
            await transaction.RollbackAsync(token);
        }
    }

    private static async Task<ModuleEffectPlan> LoadPlanAsync(
        SqlConnection connection, SqlTransaction transaction, CancellationToken token)
    {
        string masterTable, detailTable, pkJson, paramStruct;
        await using (var command = new SqlCommand("""
            SELECT m.MASTER_TABLE, m.DETAIL_TABLE, s.DEFINITION_JSON, r.PARAM_STRUCT
            FROM dbo.MODULES m
            JOIN dbo.WORKBENCH_DEFINITION_SNAPSHOT s ON s.MODULE_ID = m.M_IDX AND s.IS_CURRENT = 1
            JOIN dbo.MODULE_VALIDATION_RULE r ON r.MODULE_ID = m.M_IDX AND r.STAGE = N'SAVE'
                 AND r.VALIDATION_KEY = N'custom-validation'
            WHERE m.M_IDX = @ModuleId;
            """, connection, transaction))
        {
            command.Parameters.Add("@ModuleId", SqlDbType.Int).Value = ModuleId;
            await using var reader = await command.ExecuteReaderAsync(token);
            Assert.True(await reader.ReadAsync(token), "模块 1405 缺少当前快照或 SAVE 期 custom-validation 配置");
            masterTable = reader.GetString(0);
            detailTable = reader.GetString(1);
            using var definition = JsonDocument.Parse(reader.GetString(2));
            pkJson = definition.RootElement.GetProperty("MasterPkOrder").GetRawText();
            paramStruct = reader.GetString(3);
        }
        var pkOrder = JsonSerializer.Deserialize<List<string>>(pkJson)!;
        using var parameters = JsonDocument.Parse(paramStruct);
        return new ModuleEffectPlan(ModuleId, masterTable, detailTable, "live-cop-order", pkOrder,
            Array.Empty<EffectActionPlan>(),
            [new EffectValidationPlan(2, "SAVE", "custom-validation", true, parameters.RootElement.Clone(), null)]);
    }

    private static async Task SeedAsync(SqlConnection connection, SqlTransaction transaction, CancellationToken token,
        int clientDays = 999, int productDays = 999, int lastTradeDaysAgo = 0, int productTradeDaysAgo = 0,
        double minOrderAmount = 0, double creditLimit = 1000, double qty = 10, double minProduceQty = 0,
        int priceInEffectDaysAgo = -1, bool duplicateClientOrderNo = false, int preSendDaysBeforeOrder = -1)
    {
        await ExecuteAsync(connection, transaction, token, """
            DELETE FROM dbo.COP_ORDER_D WHERE ORDER_TYPE=@Type;
            DELETE FROM dbo.COP_ORDER_M WHERE ORDER_TYPE=@Type OR ORDER_NO LIKE 'ADR12CO%';
            DELETE FROM dbo.CLIENT_PRICE_D WHERE CLIENT_ID=@Client;
            DELETE FROM dbo.PRODUCT WHERE PRO_NO IN (@Pro1, @Pro2);
            DELETE FROM dbo.CLIENT WHERE CLIENT_ID=@Client;
            UPDATE dbo.SYSSS
            SET PARAM_VALUE = CONVERT(nvarchar(4000), CASE PARAM_KEY WHEN N'CLIENT_DAYS' THEN @ClientDays ELSE @ProductDays END)
            WHERE OWNER_MODULE=110111 AND PARAM_KEY IN (N'CLIENT_DAYS', N'PRODUCT_DAYS');
            """,
            ("@Type", Type), ("@Client", Client), ("@Pro1", Pro1), ("@Pro2", Pro2),
            ("@ClientDays", clientDays), ("@ProductDays", productDays));
        await ExecuteAsync(connection, transaction, token, """
            INSERT INTO dbo.CLIENT (CLIENT_ID, BUSINESS_TAG, LAST_TRADE_DATE, MIN_ORDER_AMOUNT, CREDIT_LIMIT_NUM)
            VALUES (@Client, 0, DATEADD(day, -@LastTradeDaysAgo, '2026-03-01'), @MinOrderAmount, @CreditLimit);
            INSERT INTO dbo.PRODUCT (PRO_NO, LAST_TRADE_DATE, MIN_PRODUCE_QTY)
            VALUES (@Pro1, DATEADD(day, -@ProductTradeDaysAgo, '2026-03-01'), @MinProduceQty), (@Pro2, NULL, 0);
            INSERT INTO dbo.COP_ORDER_M (ORDER_TYPE, ORDER_NO, ORDER_DATE, CLIENT_ID, AMOUNT_TAX, CURR_RATE, CLIENT_ORDER_NO)
            VALUES (@Type, @No, '2026-03-01', @Client, 1000, 1, @ClientOrderNo);
            INSERT INTO dbo.COP_ORDER_D (ORDER_TYPE, ORDER_NO, SERIAL_NO, PRO_NO, QTY, PRE_SEND_DATE)
            VALUES (@Type, @No, 1, @Pro1, @Qty, @PreSendDate);
            """,
            ("@Type", Type), ("@No", No), ("@Client", Client), ("@Pro1", Pro1), ("@Pro2", Pro2),
            ("@LastTradeDaysAgo", lastTradeDaysAgo), ("@MinOrderAmount", minOrderAmount),
            ("@CreditLimit", creditLimit), ("@ProductTradeDaysAgo", productTradeDaysAgo),
            ("@MinProduceQty", minProduceQty), ("@Qty", qty),
            ("@ClientOrderNo", duplicateClientOrderNo ? "ADR12CO-DUP" : "ADR12CO-OK"),
            ("@PreSendDate", preSendDaysBeforeOrder < 0 ? "2026-03-05" : "2026-02-28"));
        if (duplicateClientOrderNo)
        {
            await ExecuteAsync(connection, transaction, token, """
                INSERT INTO dbo.COP_ORDER_M (ORDER_TYPE, ORDER_NO, ORDER_DATE, CLIENT_ID, AMOUNT_TAX, CURR_RATE, CLIENT_ORDER_NO)
                VALUES (@Type, N'ADR12CO002', '2026-03-01', @Client, 10, 1, N'ADR12CO-DUP');
                """, ("@Type", Type), ("@Client", Client));
        }
        if (priceInEffectDaysAgo >= 0)
        {
            await ExecuteAsync(connection, transaction, token, """
                INSERT INTO dbo.CLIENT_PRICE_D (CLIENT_ID, PRO_NO, UNIT_ID, TAX_ID, CURR_ID, REBATE, IN_EFFECT_DATE)
                VALUES (@Client, @Pro1, N'ADR12COU', N'ADR12COT', N'ADR12COC', 0, DATEADD(day, -@DaysAgo, '2026-03-01'));
                """, ("@Client", Client), ("@Pro1", Pro1), ("@DaysAgo", priceInEffectDaysAgo));
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
