using System.Data;
using EOS.API.Data;
using EOS.API.Data.Effects;
using EOS.API.Models;
using EOS.API.Tests.Tools;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 「事件 → 校验阶段」在真库配置上的端到端钉住：模块 1502（制令单）实配 2 条启用的 SAVE 期规则
/// （`reference-exists` + `qty-not-exceed`）。造一张违反它们的单据：
/// **保存期校验把它拒掉**（证明规则是活的），而**结案照常成功**（结案事件不带校验闸，
/// 不会顺带跑一遍保存期规则）。
///
/// 判别力：把 `StageFor(Endcase)` 改回落 "SAVE" ⇒ 本用例的结案腿变红
/// （结案会被保存期规则拦下）；把模块的 SAVE 规则停用 ⇒ 保存腿变红。
///
/// 需要 MSSQL_ERP_CONN；用自造键造数（不借真实主档的取样值写入），收尾只删自造行，
/// 审计行按设计保留。
/// </summary>
[Collection("live-database")]
public sealed class StageForSaveVsEndcaseLiveTests : IAsyncLifetime
{
    private static readonly string ConnectionString =
        Environment.GetEnvironmentVariable("MSSQL_ERP_CONN")
        ?? throw new InvalidOperationException("真库集成测试需要 MSSQL_ERP_CONN；未配置即失败（无连接跳过≠已验证）。");

    private const int ProduceModule = 1502;
    private const string ProduceType = "ZZSTG001";
    private const string ProduceNo = "ZZSTG-1";
    // 违反 reference-exists 的两个引用：库里都不存在（订单号非空即参与判定，产品按明细行判定）
    private const string OrderType = "ZZSTG";
    private const string MissingOrderNo = "ZZSTG-NO-SUCH-ORDER";
    private const string MissingProduct = "ZZSTG-NO-SUCH-PRO";
    private const string TestUser = "ZZSTG0001";

    public async Task InitializeAsync()
    {
        await using var connection = await OpenAsync();
        await CleanupAsync(connection);
        await ExecAsync(connection, """
            INSERT INTO dbo.MOC_PRODUCE_M
                (PRODUCE_TYPE, PRODUCE_NO, PRODUCE_DATE, PRO_NO, ORDER_TYPE, ORDER_NO, ORDER_SERIAL_NO,
                 CONFIRM_TAG, FINISHED_TAG)
                VALUES (@type, @no, '2026-09-27', @product, @orderType, @orderNo, 999, 0, 0);
            INSERT INTO dbo.MOC_PRODUCE_D
                (PRODUCE_TYPE, PRODUCE_NO, PRO_NO, SERIAL_NO, NEED_QTY, USED_QTY, FINISHED_TAG)
                VALUES (@type, @no, @product, 1, 1, 0, 0);
            """,
            ("@type", ProduceType), ("@no", ProduceNo), ("@product", MissingProduct),
            ("@orderType", OrderType), ("@orderNo", MissingOrderNo));
    }

    public async Task DisposeAsync()
    {
        await using var connection = await OpenAsync();
        await CleanupAsync(connection);
    }

    private static async Task CleanupAsync(SqlConnection connection) =>
        await ExecAsync(connection, """
            DELETE FROM dbo.MOC_PRODUCE_D WHERE LTRIM(RTRIM(PRODUCE_TYPE)) = @type;
            DELETE FROM dbo.MOC_PRODUCE_M WHERE LTRIM(RTRIM(PRODUCE_TYPE)) = @type;
            """, ("@type", ProduceType));

    [Fact]
    public async Task 违反保存期规则的单据_保存被拒而结案放行()
    {
        var definition = await LoadDefinitionAsync();
        var plan = new EffectPlanLoader().Load(definition);

        // 夹具前置：实配的 2 条启用 SAVE 期规则（少了这条断言，"保存被拒"可能只是碰巧）
        Assert.Equal(2, plan.Rules.Count(rule =>
            rule.Enabled && rule.Stage.Equals("SAVE", StringComparison.OrdinalIgnoreCase)));
        Assert.Equal(["PRODUCE_TYPE", "PRODUCE_NO"], definition.MasterPkOrder);

        // 保存期：同一张违规单据被拒——证明规则本身是活的
        await using (var connection = await OpenAsync())
        {
            await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync();
            var pipeline = EffectShadowRunner.BuildPipelineFor(ConnectionString);
            var exception = await Assert.ThrowsAsync<EffectValidationException>(async () =>
                await pipeline.ValidateWithinTransactionAsync(
                    connection, transaction, plan, EffectEvent.Save, CancellationToken.None, KeyValues));
            Assert.False(string.IsNullOrWhiteSpace(exception.Message));
            await transaction.RollbackAsync();
        }

        // 结案期：同一张单据照常结案——结案事件没有校验阶段，不会顺带跑保存期规则
        var result = await Approvals().FinishAsync(
            definition, KeyValues, finish: true, TestUser, TestUser, null, CancellationToken.None);
        Assert.Equal(RecordAccessStatus.Ok, result.Status);

        await using (var connection = await OpenAsync())
        {
            Assert.True(await ScalarAsync<bool>(connection, """
                SELECT ISNULL(FINISHED_TAG,0) FROM dbo.MOC_PRODUCE_M
                 WHERE LTRIM(RTRIM(PRODUCE_TYPE)) = @type AND LTRIM(RTRIM(PRODUCE_NO)) = @no;
                """, ("@type", ProduceType), ("@no", ProduceNo)));
        }
    }

    private static IReadOnlyList<string> KeyValues => [ProduceType, ProduceNo];

    /// <summary>按运行期同一条路径取模块定义：当前已发布快照（IS_CURRENT=1）。</summary>
    private static async Task<WorkbenchDefinition> LoadDefinitionAsync()
    {
        var provider = new WorkbenchDefinitionProvider(Connections(), NullLogger<WorkbenchDefinitionProvider>.Instance);
        await provider.RefreshModuleAsync(ProduceModule, CancellationToken.None);
        Assert.True(provider.TryGetBaseline(ProduceModule, out var definition, out _),
            $"模块 {ProduceModule} 缺少当前定义快照");
        return definition;
    }

    private static WorkbenchApprovalService Approvals()
    {
        var connections = Connections();
        var provider = new WorkbenchDefinitionProvider(connections, NullLogger<WorkbenchDefinitionProvider>.Instance);
        var auditWriter = new WorkbenchAuditWriter(
            connections, new HttpContextAccessor(), provider, Options.Create(new AuditSettings()));
        var engine = new EffectEngineInvoker(
            new EffectEngineSettings { Enabled = true },
            new EffectPlanLoader(),
            EffectShadowRunner.BuildPipelineFor(ConnectionString),
            NullLogger<EffectEngineInvoker>.Instance);
        var workflow = new WorkflowEngine(connections, auditWriter, provider, engine, NullLogger<WorkflowEngine>.Instance);
        return new WorkbenchApprovalService(
            connections, auditWriter, workflow, engine, new WorkbenchIdempotency(),
            NullLogger<WorkbenchApprovalService>.Instance);
    }

    private static DbConnectionFactory Connections() =>
        new(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:ErpDatabase"] = ConnectionString })
            .Build());

    private static async Task<SqlConnection> OpenAsync()
    {
        var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        return connection;
    }

    private static async Task ExecAsync(SqlConnection connection, string sql, params (string Name, object? Value)[] parameters)
    {
        await using var command = new SqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<T?> ScalarAsync<T>(SqlConnection connection, string sql, params (string Name, object? Value)[] parameters)
    {
        await using var command = new SqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }
        var scalar = await command.ExecuteScalarAsync();
        return scalar is null or DBNull ? default : (T)Convert.ChangeType(scalar, typeof(T));
    }
}
