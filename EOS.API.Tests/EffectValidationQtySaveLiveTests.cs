using System.Data;
using System.Text.Json;
using EOS.API.Data.Effects;
using Microsoft.Data.SqlClient;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// B4 保存侧不超量规则的真库验证（不依赖发布与 HTTP）：直接装载模块计划并调用执行器，
/// 断言"按分组求和 + 容差 + 诊断行回填"的实际行为与设计一致。
/// 与其它真库用例同属 live-database 集合：本类按 TOP 1 无排序挑采购行造数，
/// 与并行的真库用例同表会互相干扰。
/// </summary>
[Trait("Category", "Integration")]
[Collection("live-database")]
public sealed class EffectValidationQtySaveLiveTests
{
    private static readonly string ConnectionString =
        Environment.GetEnvironmentVariable("MSSQL_ERP_CONN")
        ?? throw new InvalidOperationException("真库集成测试需要 MSSQL_ERP_CONN");

    private static readonly EffectValidationExecutor Executor = new();

    private static async Task<ModuleEffectPlan> LoadPlanAsync(
        SqlConnection connection, SqlTransaction transaction, int moduleId, string validationKey, CancellationToken token)
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
            command.Parameters.Add("@ModuleId", SqlDbType.Int).Value = moduleId;
            command.Parameters.Add("@Key", SqlDbType.NVarChar, 50).Value = validationKey;
            await using var reader = await command.ExecuteReaderAsync(token);
            Assert.True(await reader.ReadAsync(token), $"模块 {moduleId} 缺少当前快照或 SAVE 期 {validationKey} 配置");
            masterTable = reader.GetString(0);
            detailTable = reader.IsDBNull(1) ? null! : reader.GetString(1);
            using var definition = JsonDocument.Parse(reader.GetString(2));
            pkJson = definition.RootElement.GetProperty("MasterPkOrder").GetRawText();
            paramStruct = reader.GetString(3);
        }
        var pkOrder = JsonSerializer.Deserialize<List<string>>(pkJson)!;
        using var parameters = JsonDocument.Parse(paramStruct);
        return new ModuleEffectPlan(moduleId, masterTable, detailTable, $"live-qty-{moduleId}", pkOrder,
            Array.Empty<EffectActionPlan>(),
            [new EffectValidationPlan(1, "SAVE", validationKey, true, parameters.RootElement.Clone(), null)]);
    }

    /// <summary>取一张现存收料单 + 一条现存采购行，构造"本单收料量 = 上限 - 已收 + delta"。</summary>
    private static async Task<(string ReceiveType, string ReceiveNo, string Serial, double Delta)> SeedAsync(
        SqlConnection connection, SqlTransaction transaction, double delta, CancellationToken token)
    {
        string type, no, serial;
        await using (var pick = new SqlCommand("""
            SELECT TOP 1 LTRIM(RTRIM(d.PURCHASE_TYPE)), LTRIM(RTRIM(d.PURCHASE_NO)), CONVERT(nvarchar(20), d.SERIAL_NO)
            FROM dbo.PUR_PURCHASE_D d
            INNER JOIN dbo.PUR_PURCHASE_M m ON m.PURCHASE_TYPE = d.PURCHASE_TYPE AND m.PURCHASE_NO = d.PURCHASE_NO
            WHERE LTRIM(RTRIM(d.PURCHASE_TYPE)) <> '';
            """, connection, transaction))
        {
            await using var reader = await pick.ExecuteReaderAsync(token);
            Assert.True(await reader.ReadAsync(token), "库内缺少可用于验证的采购行");
            type = reader.GetString(0); no = reader.GetString(1); serial = reader.GetString(2);
        }

        var (receiveType, receiveNo) = ("E2EQTY", "E2EQTY1");
        await using (var master = new SqlCommand("""
            IF NOT EXISTS (SELECT 1 FROM dbo.PUR_RECEIVE_M WHERE RECEIVE_TYPE=@T AND RECEIVE_NO=@N)
                INSERT INTO dbo.PUR_RECEIVE_M (RECEIVE_TYPE, RECEIVE_NO, RECEIVE_DATE, CI, CREATE_PERSON, LAST_UPDATE_BY)
                VALUES (@T, @N, '2026-09-16', 'E2E', 'E2E', 'E2E');
            """, connection, transaction))
        {
            master.Parameters.Add("@T", SqlDbType.NVarChar, 10).Value = receiveType;
            master.Parameters.Add("@N", SqlDbType.NVarChar, 20).Value = receiveNo;
            await master.ExecuteNonQueryAsync(token);
        }
        await using (var detail = new SqlCommand("""
            INSERT INTO dbo.PUR_RECEIVE_D
                (RECEIVE_TYPE, RECEIVE_NO, SERIAL_NO, PURCHASE_TYPE, PURCHASE_NO, PURCHASE_SERIAL_NO, PRO_NO, DEPOT_ID, QTY)
            SELECT @RT, @RN,
                   (SELECT ISNULL(MAX(SERIAL_NO),0) + 1 FROM dbo.PUR_RECEIVE_D WHERE RECEIVE_TYPE=@RT AND RECEIVE_NO=@RN),
                   d.PURCHASE_TYPE, d.PURCHASE_NO, d.SERIAL_NO, d.PRO_NO, d.DEPOT_ID,
                   ISNULL(d.QTY,0) - ISNULL(d.RECEIVE_QTY,0) + @Delta
            FROM dbo.PUR_PURCHASE_D d
            WHERE d.PURCHASE_TYPE=@PT AND d.PURCHASE_NO=@PN AND CONVERT(nvarchar(20), d.SERIAL_NO)=@S;
            """, connection, transaction))
        {
            detail.Parameters.Add("@RT", SqlDbType.NVarChar, 10).Value = receiveType;
            detail.Parameters.Add("@RN", SqlDbType.NVarChar, 20).Value = receiveNo;
            detail.Parameters.Add("@Delta", SqlDbType.Decimal).Value = (decimal)delta;
            detail.Parameters.Add("@PT", SqlDbType.NVarChar, 10).Value = type;
            detail.Parameters.Add("@PN", SqlDbType.NVarChar, 20).Value = no;
            detail.Parameters.Add("@S", SqlDbType.NVarChar, 20).Value = serial;
            await detail.ExecuteNonQueryAsync(token);
        }
        return (receiveType, receiveNo, serial, delta);
    }

    [Theory]
    [InlineData(1607)] // 收料单
    [InlineData(1507)] // 生产计划
    [InlineData(1502)] // 制令单（含 PRODUCE_*_TAG 开关）
    [InlineData(1423)] // 退料单
    [InlineData(1412)] // 备货返仓单（含 FITOUT_*_TAG 开关，8 条断言）
    [InlineData(1505)] // 入库单
    [InlineData(1519)] // 入库单管理(外)
    [InlineData(1406)] // 送货单（含 SEND_ORDER_TAG 开关）
    public async Task 八个模块的保存侧不超量规则都能装载并执行(int moduleId)
    {
        var token = CancellationToken.None;
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync(token);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(token);
        try
        {
            var plan = await LoadPlanAsync(connection, transaction, moduleId, "qty-not-exceed", token);
            var keyValues = await PickKeyValuesAsync(connection, transaction, plan, token);
            if (keyValues is null)
                return; // 该表暂无数据，跳过（不算证据，也不算失败）

            try
            {
                await Executor.ValidateAsync(connection, transaction, plan, "SAVE", token, keyValues);
            }
            catch (EffectValidationException)
            {
                // 现存单据本就超量（历史数据）——属正常业务拒绝，证明规则跑通
            }
            // 关键：不得出现 EffectConfigException（配置错）或 SqlException（列/表写错）
        }
        finally
        {
            await transaction.RollbackAsync(token);
        }
    }

    private static async Task<string[]?> PickKeyValuesAsync(
        SqlConnection connection, SqlTransaction transaction, ModuleEffectPlan plan, CancellationToken token)
    {
        var columns = string.Join(", ", plan.MasterPkOrder.Select(column => "[" + column + "]"));
        await using var command = new SqlCommand(
            $"SELECT TOP 1 {columns} FROM dbo.[{plan.MasterTable}];", connection, transaction);
        await using var reader = await command.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token))
            return null;
        var values = new string[plan.MasterPkOrder.Count];
        for (var index = 0; index < values.Length; index++)
            values[index] = reader.IsDBNull(index)
                ? string.Empty
                : Convert.ToString(reader.GetValue(index), System.Globalization.CultureInfo.InvariantCulture)!.Trim();
        return values;
    }

    [Fact]
    public async Task 收料超采购被拒并回填诊断行()
    {
        var token = CancellationToken.None;
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync(token);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(token);
        try
        {
            var (type, no, serial, _) = await SeedAsync(connection, transaction, 100, token);
            var plan = await LoadPlanAsync(connection, transaction, 1607, "qty-not-exceed", token);

            var exception = await Assert.ThrowsAsync<EffectValidationException>(() =>
                Executor.ValidateAsync(connection, transaction, plan, "SAVE", token, [type, no]));

            Assert.Contains("以下项收料数量超出采购数量", exception.Message);
            Assert.DoesNotContain("{ROWS}", exception.Message);
            // 诊断行回填了采购序号（单列内联之外的多列形态：序号 + 采购量 + 已收 + 本单量）
            Assert.Contains(serial, exception.Message);
        }
        finally
        {
            await transaction.RollbackAsync(token);
        }
    }

    [Fact]
    public async Task 容差内的收料量放行()
    {
        var token = CancellationToken.None;
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync(token);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(token);
        try
        {
            // 正好等于"上限 - 已收 + 0.1"：既有实现用 +0.1 容差放行，目录侧 offset 必须同口径
            var (type, no, _, _) = await SeedAsync(connection, transaction, 0.1, token);
            var plan = await LoadPlanAsync(connection, transaction, 1607, "qty-not-exceed", token);
            await Executor.ValidateAsync(connection, transaction, plan, "SAVE", token, [type, no]);
        }
        finally
        {
            await transaction.RollbackAsync(token);
        }
    }
}
