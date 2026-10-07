using System.Data;
using System.Text.Json;
using EOS.API.Data.Effects;
using Microsoft.Data.SqlClient;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 逐行（thisQty 无 agg=SUM）形态的数量校验必须把来源行限定在当前单据内：同一被引用行的
/// "他单超量"不得拦下本单。用例在同一事务内造"违规单 + 合规单"并回滚，零残留。
/// 需要 MSSQL_ERP_CONN（与本仓库其它真库测试一致的约定）。
/// </summary>
[Trait("Category", "Integration")]
public sealed class EffectValidationQtyScopeLiveTests
{
    private static readonly string ConnectionString =
        Environment.GetEnvironmentVariable("MSSQL_ERP_CONN")
        ?? throw new InvalidOperationException("真库集成测试需要 MSSQL_ERP_CONN；未配置即失败（无连接跳过≠已验证）。");

    private static readonly EffectValidationExecutor Executor = new();

    private const string ScopeType = "E2EQTY";
    private const string ViolatingNo = "E2EQTY1";
    private const string CompliantNo = "E2EQTY2";

    private static async Task<ModuleEffectPlan> LoadPlanAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        int moduleId,
        string stage,
        string validationKey,
        CancellationToken token)
    {
        string masterTable, detailTable, pkJson, paramStruct;
        await using (var command = new SqlCommand("""
            SELECT m.MASTER_TABLE, m.DETAIL_TABLE, s.[DEFINITION_JSON], r.PARAM_STRUCT
            FROM dbo.MODULES m
            JOIN dbo.WORKBENCH_DEFINITION_SNAPSHOT s ON s.M_IDX = m.M_IDX AND s.IS_CURRENT = 1
            JOIN dbo.MODULE_VALIDATION_RULE r ON r.M_IDX = m.M_IDX AND r.STAGE = @Stage AND r.VALIDATION_KEY = @Key
            WHERE m.M_IDX = @ModuleId;
            """, connection, transaction))
        {
            command.Parameters.Add("@ModuleId", SqlDbType.Int).Value = moduleId;
            command.Parameters.Add("@Stage", SqlDbType.NVarChar, 20).Value = stage;
            command.Parameters.Add("@Key", SqlDbType.NVarChar, 50).Value = validationKey;
            await using var reader = await command.ExecuteReaderAsync(token);
            Assert.True(await reader.ReadAsync(token), $"模块 {moduleId} 缺少当前快照或 {stage} 期 {validationKey} 配置");
            masterTable = reader.GetString(0);
            detailTable = reader.IsDBNull(1) ? null! : reader.GetString(1);
            using var definition = JsonDocument.Parse(reader.GetString(2));
            pkJson = definition.RootElement.GetProperty("MasterPkOrder").GetRawText();
            paramStruct = reader.GetString(3);
        }
        var pkOrder = JsonSerializer.Deserialize<List<string>>(pkJson)!;
        using var parameters = JsonDocument.Parse(paramStruct);
        return new ModuleEffectPlan(moduleId, masterTable, detailTable, $"live-qty-scope-{moduleId}", pkOrder,
            Array.Empty<EffectActionPlan>(),
            [new EffectValidationPlan(1, stage, validationKey, true, parameters.RootElement.Clone(), null)]);
    }

    private sealed record PurchaseLine(string Type, string No, string Serial, decimal Limit);

    private static async Task<PurchaseLine> PickPurchaseLineAsync(
        SqlConnection connection, SqlTransaction transaction, CancellationToken token)
    {
        // 需要一条"还留有余量"的采购行：合规单用它只收 1 个，违规单用它收到超量。
        await using var command = new SqlCommand("""
            SELECT TOP 1 LTRIM(RTRIM(d.PURCHASE_TYPE)), LTRIM(RTRIM(d.PURCHASE_NO)),
                   CONVERT(nvarchar(20), d.SERIAL_NO), ISNULL(d.QTY, 0)
            FROM dbo.PUR_PURCHASE_D d
            INNER JOIN dbo.PUR_PURCHASE_M m ON m.PURCHASE_TYPE = d.PURCHASE_TYPE AND m.PURCHASE_NO = d.PURCHASE_NO
            WHERE LTRIM(RTRIM(d.PURCHASE_TYPE)) <> ''
              AND ISNULL(d.QTY, 0) - ISNULL(d.RECEIVE_QTY, 0) > 10
              AND ISNULL(d.SPARE_QTY, 0) - ISNULL(d.RECEIVE_SPARE_QTY, 0) >= 0
            ORDER BY d.PURCHASE_NO;
            """, connection, transaction);
        await using var reader = await command.ExecuteReaderAsync(token);
        Assert.True(await reader.ReadAsync(token), "库内缺少可用于验证的采购行（需有余量）");
        return new PurchaseLine(reader.GetString(0), reader.GetString(1), reader.GetString(2),
            Convert.ToDecimal(reader.GetValue(3), System.Globalization.CultureInfo.InvariantCulture));
    }

    private static async Task SeedDocumentAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string receiveNo,
        PurchaseLine line,
        decimal quantity,
        CancellationToken token)
    {
        await using (var master = new SqlCommand("""
            IF NOT EXISTS (SELECT 1 FROM dbo.PUR_RECEIVE_M WHERE RECEIVE_TYPE=@T AND RECEIVE_NO=@N)
                INSERT INTO dbo.PUR_RECEIVE_M (RECEIVE_TYPE, RECEIVE_NO, RECEIVE_DATE, CREATE_PERSON, LAST_UPDATE_BY)
                VALUES (@T, @N, '2026-09-17', 'E2E', 'E2E');
            """, connection, transaction))
        {
            master.Parameters.Add("@T", SqlDbType.NVarChar, 10).Value = ScopeType;
            master.Parameters.Add("@N", SqlDbType.NVarChar, 20).Value = receiveNo;
            await master.ExecuteNonQueryAsync(token);
        }
        await using (var detail = new SqlCommand("""
            INSERT INTO dbo.PUR_RECEIVE_D
                (RECEIVE_TYPE, RECEIVE_NO, SERIAL_NO, PURCHASE_TYPE, PURCHASE_NO, PURCHASE_SERIAL_NO, PRO_NO, DEPOT_ID, QTY, SPARE_QTY)
            SELECT @RT, @RN,
                   (SELECT ISNULL(MAX(SERIAL_NO),0) + 1 FROM dbo.PUR_RECEIVE_D WHERE RECEIVE_TYPE=@RT AND RECEIVE_NO=@RN),
                   d.PURCHASE_TYPE, d.PURCHASE_NO, d.SERIAL_NO, d.PRO_NO, d.DEPOT_ID, @Qty, 0
            FROM dbo.PUR_PURCHASE_D d
            WHERE d.PURCHASE_TYPE=@PT AND d.PURCHASE_NO=@PN AND CONVERT(nvarchar(20), d.SERIAL_NO)=@S;
            """, connection, transaction))
        {
            detail.Parameters.Add("@RT", SqlDbType.NVarChar, 10).Value = ScopeType;
            detail.Parameters.Add("@RN", SqlDbType.NVarChar, 20).Value = receiveNo;
            detail.Parameters.Add("@Qty", SqlDbType.Decimal).Value = quantity;
            detail.Parameters.Add("@PT", SqlDbType.NVarChar, 10).Value = line.Type;
            detail.Parameters.Add("@PN", SqlDbType.NVarChar, 20).Value = line.No;
            detail.Parameters.Add("@S", SqlDbType.NVarChar, 20).Value = line.Serial;
            Assert.Equal(1, await detail.ExecuteNonQueryAsync(token));
        }
    }

    [Fact]
    public async Task 逐行数量校验只看本单_他单超量不拦下合规单()
    {
        var token = CancellationToken.None;
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync(token);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(token);
        try
        {
            var line = await PickPurchaseLineAsync(connection, transaction, token);
            // 违规单：数量超过该采购行余量；合规单：只收 1 个。
            await SeedDocumentAsync(connection, transaction, ViolatingNo, line, line.Limit + 500, token);
            await SeedDocumentAsync(connection, transaction, CompliantNo, line, 1, token);
            var plan = await LoadPlanAsync(connection, transaction, 1607, "APPROVE", "qty-not-exceed", token);

            // 对照：违规单本身必须被拦下（证明夹具真的触发判据，而非规则没跑）。
            await Assert.ThrowsAsync<EffectValidationException>(() =>
                Executor.ValidateAsync(connection, transaction, plan, "APPROVE", token, [ScopeType, ViolatingNo]));

            // 关键断言：同库存在超量单据时，合规单仍必须放行。
            await Executor.ValidateAsync(connection, transaction, plan, "APPROVE", token, [ScopeType, CompliantNo]);
        }
        finally
        {
            await transaction.RollbackAsync(token);
        }
    }
}
