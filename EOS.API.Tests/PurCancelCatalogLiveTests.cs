using System.Data;
using System.Text.Json;
using EOS.API.Data.Effects;
using Microsoft.Data.SqlClient;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 采购退料单族（1608/1612，原 `pur-cancel` C#）入目录后的真库验证：
/// 直接装载 SAVE 期规则并调用执行器，断言三类判据的实际行为——
/// ① 退料合计不超收料合计（数量量纲）；② 备品量纲独立生效（同 check 两量纲 OR 合并）；
/// ③ 批管品必填批号（line-require）。
/// 同时把**旧 C# 的聚合查询内联为基准**，逐例比较"被判违规的明细序号集合"两侧一致。
/// 造数用 `ADR12PC` 前缀的合成采购键，事务结束回滚。
/// </summary>
[Trait("Category", "Integration")]
[Collection("live-database")]
public sealed class PurCancelCatalogLiveTests
{
    private static readonly string ConnectionString =
        Environment.GetEnvironmentVariable("MSSQL_ERP_CONN")
        ?? throw new InvalidOperationException(
            "真库集成测试需要 MSSQL_ERP_CONN；未配置即失败（无连接跳过≠已验证）。");

    private static readonly EffectValidationExecutor Executor = new();

    private const int ModuleId = 1608;
    private const string CancelType = "ADR12PC";
    private const string CancelNo = "ADR12PC001";
    private const string PurchaseNo = "ADR12PC001";
    private const string BatchProduct = "ADR12PCBATCH";
    private const string PlainProduct = "ADR12PCNORM";

    [Fact]
    public async Task 退料不超收料_数量与备品两量纲_以及批管品必填批号()
    {
        var token = CancellationToken.None;
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync(token);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(token);
        try
        {
            await SeedAsync(connection, transaction, token);
            var plan = await LoadPlanAsync(connection, transaction, "qty-not-exceed", token);

            // 合规：退料 8 ≤ 收料 10，备品 1 ≤ 2
            await SetCancelAsync(connection, transaction, token, qty: 8, spare: 1);
            Assert.Null(await ValidateAsync(connection, transaction, plan, token));
            Assert.Empty(await BaselineViolationsAsync(connection, transaction, token));

            // 数量越界：退料 12 > 收料 10（备品仍在额度内）——两量纲 OR 合并，命中即回报 7 列诊断
            await SetCancelAsync(connection, transaction, token, qty: 12, spare: 1);
            var exceeded = await ValidateAsync(connection, transaction, plan, token);
            Assert.NotNull(exceeded);
            Assert.Contains("以下序号项退料数量大于收料", exceeded);
            Assert.Contains("1    10    10    12    5    2    1", exceeded);
            Assert.Equal(["1"], await BaselineViolationsAsync(connection, transaction, token));
            Assert.Equal(["1"], SerialsOf(exceeded));

            // 仅备品越界：退料 8 ≤ 10，备品 3 > 2 ⇒ 第二量纲独立生效
            await SetCancelAsync(connection, transaction, token, qty: 8, spare: 3);
            var spareExceeded = await ValidateAsync(connection, transaction, plan, token);
            Assert.NotNull(spareExceeded);
            Assert.Contains("以下序号项退料数量大于收料", spareExceeded);
            Assert.Equal(["1"], await BaselineViolationsAsync(connection, transaction, token));
            Assert.Equal(["1"], SerialsOf(spareExceeded));

            // 批管品必填批号：批号为空且产品 MANAGE_BATCH=1 ⇒ 拒绝（与旧 C# 的文案逐字一致）
            var batchPlan = await LoadPlanAsync(connection, transaction, "line-require", token);
            await SetCancelAsync(connection, transaction, token, qty: 1, spare: 0, product: BatchProduct, batchNo: "");
            Assert.Equal("以下序号项需要输入批号 \r\n1", await ValidateAsync(connection, transaction, batchPlan, token));

            // 非批管品不触发；批号已填也不触发
            await SetCancelAsync(connection, transaction, token, qty: 1, spare: 0, product: PlainProduct, batchNo: "");
            Assert.Null(await ValidateAsync(connection, transaction, batchPlan, token));
            await SetCancelAsync(connection, transaction, token, qty: 1, spare: 0, product: BatchProduct, batchNo: "B240101");
            Assert.Null(await ValidateAsync(connection, transaction, plan, token));
        }
        finally
        {
            await transaction.RollbackAsync(token);
        }
    }

    /// <summary>从诊断行文本里取第一列（明细序号）。</summary>
    private static List<string> SerialsOf(string message)
        => message.Split('\n')
            .Skip(2)
            .Select(line => line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty)
            .Where(item => item.Length > 0)
            .ToList();

    private static async Task<string?> ValidateAsync(
        SqlConnection connection, SqlTransaction transaction, ModuleEffectPlan plan, CancellationToken token)
    {
        try
        {
            await Executor.ValidateAsync(connection, transaction, plan, "SAVE", token, [CancelType, CancelNo]);
            return null;
        }
        catch (EffectValidationException error)
        {
            return error.Message;
        }
    }

    /// <summary>
    /// 旧 C# 判据内联基准：按本单明细行的 (采购类型,采购单号,采购行) 关联三表并集，
    /// 回报"退料合计 > 收料合计（数量或备品）"的明细序号集合。
    /// </summary>
    private static async Task<List<string>> BaselineViolationsAsync(
        SqlConnection connection, SqlTransaction transaction, CancellationToken token)
    {
        const string sql = """
            SELECT CONVERT(nvarchar(20), d.SERIAL_NO)
            FROM dbo.PUR_CANCEL_D d
            INNER JOIN (
                SELECT PURCHASE_TYPE, PURCHASE_NO, SERIAL_NO AS PURCHASE_SERIAL_NO, QTY, SPARE_QTY, 'P' SRC FROM dbo.PUR_PURCHASE_D
                UNION ALL
                SELECT PURCHASE_TYPE, PURCHASE_NO, PURCHASE_SERIAL_NO, QTY, SPARE_QTY, 'R' SRC FROM dbo.PUR_RECEIVE_D
                UNION ALL
                SELECT PURCHASE_TYPE, PURCHASE_NO, PURCHASE_SERIAL_NO, QTY, SPARE_QTY, 'C' SRC FROM dbo.PUR_CANCEL_D
            ) p ON p.PURCHASE_TYPE=d.PURCHASE_TYPE AND p.PURCHASE_NO=d.PURCHASE_NO AND p.PURCHASE_SERIAL_NO=d.PURCHASE_SERIAL_NO
            WHERE d.CANCEL_TYPE=@Type AND d.CANCEL_NO=@No
            GROUP BY d.SERIAL_NO
            HAVING SUM(CASE WHEN p.SRC='C' THEN p.QTY END) > ISNULL(SUM(CASE WHEN p.SRC='R' THEN p.QTY END),0)
                OR SUM(CASE WHEN p.SRC='C' THEN p.SPARE_QTY END) > ISNULL(SUM(CASE WHEN p.SRC='R' THEN p.SPARE_QTY END),0);
            """;
        await using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.Add("@Type", SqlDbType.NVarChar, 10).Value = CancelType;
        command.Parameters.Add("@No", SqlDbType.NVarChar, 20).Value = CancelNo;
        var result = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token)) result.Add(reader.GetString(0).Trim());
        return result;
    }

    private static async Task<ModuleEffectPlan> LoadPlanAsync(
        SqlConnection connection, SqlTransaction transaction, string validationKey, CancellationToken token)
    {
        string masterTable, detailTable, pkJson, paramStruct;
        await using (var command = new SqlCommand("""
            SELECT m.MASTER_TABLE, m.DETAIL_TABLE, s.[DEFINITION_JSON], r.PARAM_STRUCT
            FROM dbo.MODULES m
            JOIN dbo.WORKBENCH_DEFINITION_SNAPSHOT s ON s.M_IDX = m.M_IDX AND s.IS_CURRENT = 1
            JOIN dbo.MODULE_VALIDATION_RULE r ON r.M_IDX = m.M_IDX AND r.STAGE = N'SAVE' AND r.VALIDATION_KEY = @Key
            WHERE m.M_IDX = @ModuleId;
            """, connection, transaction))
        {
            command.Parameters.Add("@ModuleId", SqlDbType.Int).Value = ModuleId;
            command.Parameters.Add("@Key", SqlDbType.NVarChar, 50).Value = validationKey;
            await using var reader = await command.ExecuteReaderAsync(token);
            Assert.True(await reader.ReadAsync(token), $"模块 {ModuleId} 缺少当前快照或 SAVE 期 {validationKey} 配置");
            masterTable = reader.GetString(0);
            detailTable = reader.IsDBNull(1) ? null! : reader.GetString(1);
            using var definition = JsonDocument.Parse(reader.GetString(2));
            pkJson = definition.RootElement.GetProperty("MasterPkOrder").GetRawText();
            paramStruct = reader.GetString(3);
        }
        var pkOrder = JsonSerializer.Deserialize<List<string>>(pkJson)!;
        using var parameters = JsonDocument.Parse(paramStruct);
        return new ModuleEffectPlan(ModuleId, masterTable, detailTable, $"live-pur-cancel-{validationKey}", pkOrder,
            Array.Empty<EffectActionPlan>(),
            [new EffectValidationPlan(1, "SAVE", validationKey, true, parameters.RootElement.Clone(), null)]);
    }

    private static async Task SeedAsync(SqlConnection connection, SqlTransaction transaction, CancellationToken token)
    {
        await ExecuteAsync(connection, transaction, """
            DELETE FROM dbo.PUR_CANCEL_D WHERE CANCEL_TYPE=@Type AND CANCEL_NO=@No;
            DELETE FROM dbo.PUR_RECEIVE_D WHERE PURCHASE_TYPE=@Type AND PURCHASE_NO=@No;
            DELETE FROM dbo.PUR_PURCHASE_D WHERE PURCHASE_TYPE=@Type AND PURCHASE_NO=@No;
            DELETE FROM dbo.PRODUCT WHERE PRO_NO IN (@Batch, @Plain);
            INSERT INTO dbo.PRODUCT (PRO_NO, PRO_NAME, PRO_SPEC, MANAGE_BATCH) VALUES (@Batch, N'ADR12 批管料', N'规格', 1);
            INSERT INTO dbo.PRODUCT (PRO_NO, PRO_NAME, PRO_SPEC, MANAGE_BATCH) VALUES (@Plain, N'ADR12 普通料', N'规格', 0);
            INSERT INTO dbo.PUR_PURCHASE_D (PURCHASE_TYPE, PURCHASE_NO, SERIAL_NO, PRO_NO, QTY, SPARE_QTY)
                VALUES (@Type, @No, 1, @Batch, 10, 5);
            INSERT INTO dbo.PUR_RECEIVE_D (RECEIVE_TYPE, RECEIVE_NO, SERIAL_NO, PURCHASE_TYPE, PURCHASE_NO, PURCHASE_SERIAL_NO, QTY, SPARE_QTY)
                VALUES (@Type, @No, 1, @Type, @No, 1, 10, 2);
            """, token,
            ("@Type", CancelType), ("@No", PurchaseNo), ("@Batch", BatchProduct), ("@Plain", PlainProduct));
    }

    /// <summary>写入（或改写）本单唯一明细行：退料数量/备品、料号与批号。</summary>
    private static async Task SetCancelAsync(
        SqlConnection connection, SqlTransaction transaction, CancellationToken token,
        double qty, double spare, string? product = null, string? batchNo = null)
        => await ExecuteAsync(connection, transaction, """
            DELETE FROM dbo.PUR_CANCEL_D WHERE CANCEL_TYPE=@Type AND CANCEL_NO=@No;
            INSERT INTO dbo.PUR_CANCEL_D (CANCEL_TYPE, CANCEL_NO, SERIAL_NO, PURCHASE_TYPE, PURCHASE_NO, PURCHASE_SERIAL_NO, PRO_NO, QTY, SPARE_QTY, BATCH_NO)
                VALUES (@Type, @No, 1, @Type, @No, 1, @Product, @Qty, @Spare, @BatchNo);
            """, token,
            ("@Type", CancelType), ("@No", CancelNo), ("@Product", product ?? BatchProduct),
            ("@Qty", qty), ("@Spare", spare), ("@BatchNo", batchNo ?? "B240101"));

    private static async Task ExecuteAsync(
        SqlConnection connection, SqlTransaction transaction, string sql, CancellationToken token,
        params (string Name, object? Value)[] parameters)
    {
        await using var command = new SqlCommand(sql, connection, transaction);
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(token);
    }
}
