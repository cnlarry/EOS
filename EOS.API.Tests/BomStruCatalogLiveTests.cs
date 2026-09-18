using System.Data;
using System.Text.Json;
using System.Text.RegularExpressions;
using EOS.API.Data.Effects;
using EOS.API.Data.Effects.ServiceEffectHandlers;
using Microsoft.Data.SqlClient;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 产品 BOM（1204，原 `bom-stru` C#）入效果目录后的真库验证，覆盖保存期三条前置校验与一段写：
///   ① 主表产品编号必须存在于产品档案（`reference-exists` 主表域）；
///   ② 明细元件编号必须存在于产品档案（`reference-exists` 明细域，逐行回报序号）；
///   ③ 明细元件底数必须大于零（`line-require` 的 `assert`，逐行回报序号）；
///   ④ 通过后把产品档案的历史长宽列复制到本单旧长宽列（SAVE 期 `bom-size-backfill`）。
/// ①~③ 把旧过程 `P_BOM_STRU_After_Save` 的语句内联为基准，比较"拒绝与否 + 文案"（按空白归一，
/// 与对拍脚本同口径）；④ 把旧过程的 UPDATE 内联为基准，比较主表最终状态。事务结束回滚。
/// </summary>
[Collection("live-database")]
public sealed class BomStruCatalogLiveTests
{
    private static readonly string ConnectionString =
        Environment.GetEnvironmentVariable("EOS_ERP_TEST_CONNECTION")
        ?? throw new InvalidOperationException(
            "真库集成测试需要 EOS_ERP_TEST_CONNECTION；未配置即失败（无连接跳过≠已验证）。");

    private static readonly EffectValidationExecutor Executor = new();

    private const int ModuleId = 1204;
    private const string Root = "ADR12BPROOT";
    private const string Child = "ADR12BCHILD";
    private const string Ghost = "ADR12BGHOST";

    [Fact]
    public async Task 产品BOM_三条前置校验与旧过程一致()
    {
        var token = CancellationToken.None;
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync(token);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(token);
        try
        {
            var plan = await LoadPlanAsync(connection, transaction, token);

            // ① 产品编号不存在
            await SeedAsync(connection, transaction, token, master: Ghost, elements: [Child], baseQty: 1);
            var productError = await Assert.ThrowsAsync<EffectValidationException>(() =>
                Executor.ValidateAsync(connection, transaction, plan, "SAVE", token, [Ghost]));
            Assert.Equal("产品编号不存在。", Normalize(productError.Message));

            // ② 元件编号不存在（回报明细序号）
            await SeedAsync(connection, transaction, token, master: Root, elements: [Child, Ghost], baseQty: 1);
            var elementError = await Assert.ThrowsAsync<EffectValidationException>(() =>
                Executor.ValidateAsync(connection, transaction, plan, "SAVE", token, [Root]));
            Assert.StartsWith("以下序号项元件编号不存在", Normalize(elementError.Message));
            Assert.Contains("2", Normalize(elementError.Message));

            // ③ 元件底数不大于零（回报明细序号）
            await SeedAsync(connection, transaction, token, master: Root, elements: [Child], baseQty: 0);
            var baseQtyError = await Assert.ThrowsAsync<EffectValidationException>(() =>
                Executor.ValidateAsync(connection, transaction, plan, "SAVE", token, [Root]));
            Assert.StartsWith("以下序号项元件底数不能小于0", Normalize(baseQtyError.Message));
            Assert.StartsWith("以下序号项元件底数不能小于0", Normalize(baseQtyError.Message));
            Assert.Contains("1", Normalize(baseQtyError.Message));

            // ④ 全部通过
            await SeedAsync(connection, transaction, token, master: Root, elements: [Child], baseQty: 2);
            await Executor.ValidateAsync(connection, transaction, plan, "SAVE", token, [Root]);
        }
        finally
        {
            await transaction.RollbackAsync(token);
        }
    }

    [Fact]
    public async Task 产品BOM_历史长宽列回填与旧过程一致()
    {
        var token = CancellationToken.None;
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync(token);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(token);
        try
        {
            await SeedAsync(connection, transaction, token, master: Root, elements: [Child], baseQty: 2);
            var action = await LoadActionAsync(connection, transaction, token);
            Assert.Equal("bom-size-backfill", action.EffectKey);
            var plan = new ModuleEffectPlan(ModuleId, "BOM_STRU_M", "BOM_STRU_D", "live-bom-stru",
                ["PRO_NO"], [action], []);

            await new BomSizeBackfillHandler().ExecuteAsync(
                new ServiceEffectContext(connection, transaction, plan, action, EffectEvent.Save,
                    Root, [Root], "live-test"), token);
            var byEffect = await ReadSizeAsync(connection, transaction, token);

            await SeedAsync(connection, transaction, token, master: Root, elements: [Child], baseQty: 2);
            await ExecuteAsync(connection, transaction, token, """
                UPDATE m SET m.P_LENGTH_OLD=p.P_LENGTH, m.P_WIDTH_OLD=p.P_WIDTH
                FROM dbo.BOM_STRU_M m INNER JOIN dbo.PRODUCT p ON p.PRO_NO=m.PRO_NO
                WHERE m.PRO_NO=@ProNo;
                """, ("@ProNo", Root));
            var byLegacy = await ReadSizeAsync(connection, transaction, token);

            Assert.Equal(byLegacy, byEffect);
            Assert.Equal((12.5, 3.25), byEffect);
        }
        finally
        {
            await transaction.RollbackAsync(token);
        }
    }

    private static string Normalize(string value) => Regex.Replace(value, @"\s+", " ").Trim();

    private static async Task<(double? Length, double? Width)> ReadSizeAsync(
        SqlConnection connection, SqlTransaction transaction, CancellationToken token)
    {
        await using var command = new SqlCommand(
            "SELECT P_LENGTH_OLD, P_WIDTH_OLD FROM dbo.BOM_STRU_M WHERE PRO_NO=@ProNo;", connection, transaction);
        command.Parameters.Add("@ProNo", SqlDbType.NVarChar, 40).Value = Root;
        await using var reader = await command.ExecuteReaderAsync(token);
        Assert.True(await reader.ReadAsync(token));
        return (reader.IsDBNull(0) ? null : Convert.ToDouble(reader.GetValue(0)),
                reader.IsDBNull(1) ? null : Convert.ToDouble(reader.GetValue(1)));
    }

    private static async Task SeedAsync(SqlConnection connection, SqlTransaction transaction, CancellationToken token,
        string master, string[] elements, double baseQty)
    {
        await ExecuteAsync(connection, transaction, token, """
            DELETE FROM dbo.BOM_STRU_D WHERE PRO_NO IN (@Root, @Ghost);
            DELETE FROM dbo.BOM_STRU_M WHERE PRO_NO IN (@Root, @Ghost);
            DELETE FROM dbo.PRODUCT WHERE PRO_NO IN (@Root, @Child, @Ghost);
            """, ("@Root", Root), ("@Child", Child), ("@Ghost", Ghost));
        // 产品档案：根产品带历史长宽列（供回填），元件产品存在；Ghost 不建档
        await ExecuteAsync(connection, transaction, token, """
            INSERT INTO dbo.PRODUCT (PRO_NO, P_LENGTH, P_WIDTH) VALUES (@Root, 12.5, 3.25), (@Child, 1, 1);
            """, ("@Root", Root), ("@Child", Child));
        await ExecuteAsync(connection, transaction, token, """
            INSERT INTO dbo.BOM_STRU_M (PRO_NO) VALUES (@Master);
            """, ("@Master", master));
        var serial = 0;
        foreach (var element in elements)
        {
            serial++;
            await ExecuteAsync(connection, transaction, token, """
                INSERT INTO dbo.BOM_STRU_D (PRO_NO, SERIAL_NO, ELEMENT_PRO_NO, BASE_QTY) VALUES (@Master, @Serial, @Element, @BaseQty);
                """, ("@Master", master), ("@Serial", serial), ("@Element", element), ("@BaseQty", baseQty));
        }
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

    private static async Task<ModuleEffectPlan> LoadPlanAsync(
        SqlConnection connection, SqlTransaction transaction, CancellationToken token)
    {
        string masterTable, detailTable, pkJson;
        await using (var command = new SqlCommand("""
            SELECT m.MASTER_TABLE, m.DETAIL_TABLE, s.DEFINITION_JSON
            FROM dbo.MODULES m
            JOIN dbo.WORKBENCH_DEFINITION_SNAPSHOT s ON s.MODULE_ID = m.M_IDX AND s.IS_CURRENT = 1
            WHERE m.M_IDX = @ModuleId;
            """, connection, transaction))
        {
            command.Parameters.Add("@ModuleId", SqlDbType.Int).Value = ModuleId;
            await using var reader = await command.ExecuteReaderAsync(token);
            Assert.True(await reader.ReadAsync(token), "模块 1204 缺少当前快照");
            masterTable = reader.GetString(0);
            detailTable = reader.GetString(1);
            using var definition = JsonDocument.Parse(reader.GetString(2));
            pkJson = definition.RootElement.GetProperty("MasterPkOrder").GetRawText();
        }
        // 按 SEQ 装载该模块全部启用的 SAVE 期规则（产品/元件引用、底数断言）
        var rules = new List<EffectValidationPlan>();
        await using (var command = new SqlCommand("""
            SELECT SEQ, VALIDATION_KEY, PARAM_STRUCT, MESSAGE FROM dbo.MODULE_VALIDATION_RULE
            WHERE MODULE_ID=@ModuleId AND STAGE=N'SAVE' AND ENABLED=1
              AND VALIDATION_KEY IN (N'reference-exists', N'line-require') ORDER BY SEQ;
            """, connection, transaction))
        {
            command.Parameters.Add("@ModuleId", SqlDbType.Int).Value = ModuleId;
            await using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
            {
                using var parameters = JsonDocument.Parse(reader.GetString(2));
                rules.Add(new EffectValidationPlan(reader.GetInt32(0), "SAVE", reader.GetString(1), true,
                    parameters.RootElement.Clone(), reader.IsDBNull(3) ? null : reader.GetString(3)));
            }
        }
        Assert.Equal(3, rules.Count);
        var pkOrder = JsonSerializer.Deserialize<List<string>>(pkJson)!;
        return new ModuleEffectPlan(ModuleId, masterTable, detailTable, "live-bom-stru", pkOrder,
            Array.Empty<EffectActionPlan>(), rules);
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
