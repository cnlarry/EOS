using System.Data;
using System.Text.Json;
using EOS.API.Data.Effects;
using EOS.API.Data.Effects.ServiceEffectHandlers;
using Microsoft.Data.SqlClient;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 送货单（1406，原 `cop-send` C#）入校验目录后的真库验证：
/// 保存期 `custom-validation` → 注册实现 `cop-send-check` 的五条判据
/// （批号必填 / 送货日期超 30 天 / 库别存在 / 出库数量不超库存 / 批号出库不超批号库存，后三条受 `SYSSS.SEND_TAG` 门控），
/// 以及 SAVE 期 `cop-send-mo-flag` 的包装标记写入（每品号客户订单号最大的一行）。
/// 造数用 `ADR12CS` 前缀，事务结束回滚。
/// </summary>
[Collection("live-database")]
public sealed class CopSendCatalogLiveTests
{
    private static readonly string ConnectionString =
        Environment.GetEnvironmentVariable("MSSQL_ERP_CONN")
        ?? throw new InvalidOperationException(
            "真库集成测试需要 MSSQL_ERP_CONN；未配置即失败（无连接跳过≠已验证）。");

    private static readonly EffectValidationExecutor Executor = new();

    private const int ModuleId = 1406;
    private const string Type = "ADR12CS";
    private const string No = "ADR12CS001";
    private const string Depot = "ADR12CSDP";
    private const string Pro = "ADR12CSPR1";

    [Fact]
    public async Task 送货单_五条判据与包装标记()
    {
        var token = CancellationToken.None;
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync(token);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(token);
        try
        {
            var plan = await LoadPlanAsync(connection, transaction, token, "custom-validation");

            // ① 批管品缺批号 ⇒ 拒绝并回报序号
            await SeedAsync(connection, transaction, token, manageBatch: true, batch: null,
                sendDaysBeforeCreate: 0, depotExists: true, stockQty: 100);
            var batchMissing = await Assert.ThrowsAsync<EffectValidationException>(() =>
                Executor.ValidateAsync(connection, transaction, plan, "SAVE", token, [Type, No]));
            Assert.StartsWith("以下序号项需要输入批号", batchMissing.Message);

            // ② 送货日期早于建立日期 30 天以上 ⇒ 拒绝
            await SeedAsync(connection, transaction, token, manageBatch: false, batch: null,
                sendDaysBeforeCreate: 40, depotExists: true, stockQty: 100);
            var dateTooOld = await Assert.ThrowsAsync<EffectValidationException>(() =>
                Executor.ValidateAsync(connection, transaction, plan, "SAVE", token, [Type, No]));
            Assert.Equal("送货日期不能小于建立日期30天", dateTooOld.Message);

            // ③ 库别不存在（受 SEND_TAG 门控）⇒ 拒绝
            await SeedAsync(connection, transaction, token, manageBatch: false, batch: null,
                sendDaysBeforeCreate: 0, depotExists: false, stockQty: 100);
            var depotMissing = await Assert.ThrowsAsync<EffectValidationException>(() =>
                Executor.ValidateAsync(connection, transaction, plan, "SAVE", token, [Type, No]));
            Assert.StartsWith("以下库别不存在\r\n序号----库别\r\n", depotMissing.Message);

            // ④ 库存不足（出库 10 > 库存 3）⇒ 拒绝
            await SeedAsync(connection, transaction, token, manageBatch: false, batch: null,
                sendDaysBeforeCreate: 0, depotExists: true, stockQty: 3);
            var stockNotEnough = await Assert.ThrowsAsync<EffectValidationException>(() =>
                Executor.ValidateAsync(connection, transaction, plan, "SAVE", token, [Type, No]));
            Assert.StartsWith("库存数量不足\r\n料号---------------库别/库位----出库数量----库存数量---不足数量\r\n", stockNotEnough.Message);

            // ⑤ 批号库存不足 ⇒ 拒绝
            await SeedAsync(connection, transaction, token, manageBatch: false, batch: "ADR12CSB1",
                sendDaysBeforeCreate: 0, depotExists: true, stockQty: 100, batchStockIn: 3);
            var batchStock = await Assert.ThrowsAsync<EffectValidationException>(() =>
                Executor.ValidateAsync(connection, transaction, plan, "SAVE", token, [Type, No]));
            Assert.StartsWith("批号库存数量不足", batchStock.Message);

            // ⑥ 门关（SEND_TAG=0）：库别/库存/批号库存三条不再拦
            await SeedAsync(connection, transaction, token, manageBatch: false, batch: null,
                sendDaysBeforeCreate: 0, depotExists: false, stockQty: 0, sendTag: 0);
            await Executor.ValidateAsync(connection, transaction, plan, "SAVE", token, [Type, No]);

            // ⑦ 全部合规 ⇒ 放行
            await SeedAsync(connection, transaction, token, manageBatch: false, batch: null,
                sendDaysBeforeCreate: 0, depotExists: true, stockQty: 100);
            await Executor.ValidateAsync(connection, transaction, plan, "SAVE", token, [Type, No]);

            // ⑧ 包装标记：两行明细（同品号不同客户订单号）⇒ 只标记客户订单号最大的那行
            await SeedAsync(connection, transaction, token, manageBatch: false, batch: null,
                sendDaysBeforeCreate: 0, depotExists: true, stockQty: 100, secondDetailClientOrderNo: "ADR12CS-O2");
            var action = await LoadActionAsync(connection, transaction, token);
            Assert.Equal("cop-send-mo-flag", action.EffectKey);
            var effectPlan = new ModuleEffectPlan(ModuleId, "COP_SEND_M", "COP_SEND_D", "live-cop-send",
                ["SEND_TYPE", "SEND_NO"], [action], []);
            await new CopSendMoFlagHandler().ExecuteAsync(
                new ServiceEffectContext(connection, transaction, effectPlan, action, EffectEvent.Save,
                    No, [Type, No], "live-test"), token);
            var flags = await ReadFlagsAsync(connection, transaction, token);
            Assert.Equal(["", "showbaozhuang"], flags);
        }
        finally
        {
            await transaction.RollbackAsync(token);
        }
    }

    private static async Task<ModuleEffectPlan> LoadPlanAsync(
        SqlConnection connection, SqlTransaction transaction, CancellationToken token, string validationKey)
    {
        string masterTable, detailTable, pkJson, paramStruct;
        await using (var command = new SqlCommand("""
            SELECT m.MASTER_TABLE, m.DETAIL_TABLE, s.DEFINITION_JSON, r.PARAM_STRUCT
            FROM dbo.MODULES m
            JOIN dbo.WORKBENCH_DEFINITION_SNAPSHOT s ON s.M_IDX = m.M_IDX AND s.IS_CURRENT = 1
            JOIN dbo.MODULE_VALIDATION_RULE r ON r.M_IDX = m.M_IDX AND r.STAGE = N'SAVE'
                 AND r.VALIDATION_KEY = @Key
            WHERE m.M_IDX = @ModuleId;
            """, connection, transaction))
        {
            command.Parameters.Add("@ModuleId", SqlDbType.Int).Value = ModuleId;
            command.Parameters.Add("@Key", SqlDbType.NVarChar, 50).Value = validationKey;
            await using var reader = await command.ExecuteReaderAsync(token);
            Assert.True(await reader.ReadAsync(token), $"模块 1406 缺少当前快照或 SAVE 期 {validationKey} 配置");
            masterTable = reader.GetString(0);
            detailTable = reader.GetString(1);
            using var definition = JsonDocument.Parse(reader.GetString(2));
            pkJson = definition.RootElement.GetProperty("MasterPkOrder").GetRawText();
            paramStruct = reader.GetString(3);
        }
        var pkOrder = JsonSerializer.Deserialize<List<string>>(pkJson)!;
        using var parameters = JsonDocument.Parse(paramStruct);
        return new ModuleEffectPlan(ModuleId, masterTable, detailTable, "live-cop-send", pkOrder,
            Array.Empty<EffectActionPlan>(),
            [new EffectValidationPlan(3, "SAVE", validationKey, true, parameters.RootElement.Clone(), null)]);
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

    private static async Task<List<string>> ReadFlagsAsync(
        SqlConnection connection, SqlTransaction transaction, CancellationToken token)
    {
        await using var command = new SqlCommand("""
            SELECT ISNULL(mo_no,'') FROM dbo.COP_SEND_D WHERE SEND_TYPE=@Type AND SEND_NO=@No ORDER BY SERIAL_NO;
            """, connection, transaction);
        command.Parameters.Add("@Type", SqlDbType.NVarChar, 20).Value = Type;
        command.Parameters.Add("@No", SqlDbType.NVarChar, 40).Value = No;
        var flags = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token)) flags.Add(reader.GetString(0).Trim());
        return flags;
    }

    private static async Task SeedAsync(SqlConnection connection, SqlTransaction transaction, CancellationToken token,
        bool manageBatch, string? batch, int sendDaysBeforeCreate, bool depotExists, double stockQty,
        double batchStockIn = 0, int sendTag = 1, string? secondDetailClientOrderNo = null)
    {
        await ExecuteAsync(connection, transaction, token, """
            DELETE FROM dbo.COP_SEND_D WHERE SEND_TYPE=@Type;
            DELETE FROM dbo.COP_SEND_M WHERE SEND_TYPE=@Type;
            DELETE FROM dbo.INV_BATCH_M WHERE BATCH_NO LIKE 'ADR12CSB%';
            DELETE FROM dbo.INV_PRO_DEPOT WHERE PRO_NO=@Pro;
            DELETE FROM dbo.DEPOT_LOCATION WHERE DEPOT_ID=@Depot;
            DELETE FROM dbo.DEPOT WHERE DEPOT_ID=@Depot;
            DELETE FROM dbo.PRODUCT WHERE PRO_NO=@Pro;
            UPDATE dbo.SYSSS SET PARAM_VALUE=CONVERT(nvarchar(4000), @SendTag)
                WHERE OWNER_MODULE=110111 AND PARAM_KEY=N'SEND_TAG';
            """, ("@Type", Type), ("@Pro", Pro), ("@Depot", Depot), ("@SendTag", sendTag));
        if (depotExists)
            await ExecuteAsync(connection, transaction, token, """
                INSERT INTO dbo.DEPOT (DEPOT_ID, DEPOT_NAME) VALUES (@Depot, N'ADR12CS仓库');
                INSERT INTO dbo.DEPOT_LOCATION (DEPOT_ID, LOCATION_NO, PARENT_NO, LOCATION_PATH, LOCATION_TYPE, LOCATION_NAME, STORAGE_TYPE, SEQ_NO, STATUS)
                VALUES (@Depot, N'-', NULL, N'/-', N'BIN', N'未指定位置（待归位）', NULL, 0, N'A');
                """, ("@Depot", Depot));
        await ExecuteAsync(connection, transaction, token, """
            INSERT INTO dbo.PRODUCT (PRO_NO, MANAGE_BATCH, UNIT_ID) VALUES (@Pro, @ManageBatch, N'ADR12CSU');
            """, ("@Pro", Pro), ("@ManageBatch", manageBatch ? 1 : 0));
        // 余额行的库别必须存在于仓库主档（并因此拥有哨兵位置行），
        // 因此"库别不存在"的用例本身不再造库存。
        if (depotExists)
            await ExecuteAsync(connection, transaction, token, """
                INSERT INTO dbo.INV_PRO_DEPOT (PRO_NO, DEPOT_ID, QTY) VALUES (@Pro, @Depot, @StockQty);
                """, ("@Pro", Pro), ("@Depot", Depot), ("@StockQty", stockQty));
        if (batchStockIn > 0)
        {
            await ExecuteAsync(connection, transaction, token, """
                INSERT INTO dbo.INV_BATCH_M (BATCH_NO, PRO_NO, IN_SUM, OUT_SUM) VALUES (N'ADR12CSB1', @Pro, @InSum, 0);
                """, ("@Pro", Pro), ("@InSum", batchStockIn));
        }
        await ExecuteAsync(connection, transaction, token, """
            INSERT INTO dbo.COP_SEND_M (SEND_TYPE, SEND_NO, SEND_DATE, CREATE_DATE)
            VALUES (@Type, @No, DATEADD(day, -@DaysBeforeCreate, '2026-03-01'), '2026-03-01');
            INSERT INTO dbo.COP_SEND_D (SEND_TYPE, SEND_NO, SERIAL_NO, PRO_NO, QTY, BATCH_NO, DEPOT_ID, UNIT_ID, CLIENT_ORDER_NO)
            VALUES (@Type, @No, 1, @Pro, 10, @Batch, @Depot, N'ADR12CSU', N'ADR12CS-O1');
            """,
            ("@Type", Type), ("@No", No), ("@Pro", Pro), ("@Depot", Depot),
            ("@DaysBeforeCreate", sendDaysBeforeCreate), ("@Batch", batch));
        if (secondDetailClientOrderNo is not null)
        {
            await ExecuteAsync(connection, transaction, token, """
                INSERT INTO dbo.COP_SEND_D (SEND_TYPE, SEND_NO, SERIAL_NO, PRO_NO, QTY, DEPOT_ID, UNIT_ID, CLIENT_ORDER_NO)
                VALUES (@Type, @No, 2, @Pro, 1, @Depot, N'ADR12CSU', @ClientOrderNo);
                """, ("@Type", Type), ("@No", No), ("@Pro", Pro), ("@Depot", Depot),
                ("@ClientOrderNo", secondDetailClientOrderNo));
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
