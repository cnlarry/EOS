using System.Data;
using System.Text.Json;
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
/// 效果链预演的真库验收：在事务内跑真实的批核生效链并回滚后，**库内逐表逐列零变化**。
///
/// 为什么必须碰库：预演的唯一价值主张是"跑真的但不落库"，而"不落库"只有把前后的
/// 数据指纹逐表对拍才能证明——纯内存断言证明不了回滚是否真的兜住了。
/// 指纹用 BINARY_CHECKSUM(*) 聚合，覆盖表里每一列，而不是抽查几个字段。
///
/// 需要 EOS_ERP_TEST_CONNECTION。
/// </summary>
[Collection("live-database")]
public sealed class EffectSimulationLiveTests
{
    private static readonly string ConnectionString =
        Environment.GetEnvironmentVariable("EOS_ERP_TEST_CONNECTION")
        ?? throw new InvalidOperationException("真库集成测试需要 EOS_ERP_TEST_CONNECTION；未配置即失败（无连接跳过≠已验证）。");

    private static DbConnectionFactory Connections()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:ErpDatabase"] = ConnectionString })
            .Build();
        return new DbConnectionFactory(configuration);
    }

    private static EffectSimulationService CreateSimulation(DbConnectionFactory connections)
    {
        var provider = new WorkbenchDefinitionProvider(connections, NullLogger<WorkbenchDefinitionProvider>.Instance);
        var auditWriter = new WorkbenchAuditWriter(
            connections, new HttpContextAccessor(), provider, Options.Create(new AuditSettings()));
        var engine = new EffectEngineInvoker(
            new EffectEngineSettings { Enabled = true },
            new EffectPlanLoader(),
            EffectShadowRunner.BuildPipelineFor(ConnectionString),
            NullLogger<EffectEngineInvoker>.Instance);
        var workflow = new WorkflowEngine(connections, auditWriter, provider, engine, NullLogger<WorkflowEngine>.Instance);
        var approvals = new WorkbenchApprovalService(
            connections, auditWriter, workflow, engine, new WorkbenchIdempotency(),
            NullLogger<WorkbenchApprovalService>.Instance);
        return new EffectSimulationService(connections, new EffectPlanLoader(), approvals,
            NullLogger<EffectSimulationService>.Instance);
    }

    private sealed record Candidate(int ModuleId, string MasterTable, IReadOnlyList<string> PkColumns, IReadOnlyList<string> KeyValues);

    /// <summary>挑一个"引擎已接管 + 有已发布快照 + 有启用的批核效果动作 + 能找到一张未批核单据"的模块。</summary>
    private static async Task<Candidate> FindCandidateAsync(SqlConnection connection, CancellationToken token)
    {
        await using var moduleCommand = new SqlCommand(
            """
            SELECT TOP 20 m.M_IDX, LTRIM(RTRIM(m.MASTER_TABLE))
            FROM dbo.MODULES m WITH (NOLOCK)
            WHERE ISNULL(m.EFFECT_ENGINE_TAG,0)=1 AND LTRIM(RTRIM(ISNULL(m.MASTER_TABLE,'')))<>''
              AND EXISTS (SELECT 1 FROM dbo.MODULE_BUSINESS_ACTION a WITH (NOLOCK)
                          WHERE a.MODULE_ID=m.M_IDX AND LTRIM(RTRIM(a.EVENT_CODE))='APPROVE_EFFECT' AND ISNULL(a.ENABLED,1)=1)
              AND EXISTS (SELECT 1 FROM dbo.WORKBENCH_DEFINITION_SNAPSHOT s WITH (NOLOCK)
                          WHERE s.MODULE_ID=m.M_IDX AND s.IS_CURRENT=1)
            ORDER BY m.M_IDX;
            """, connection);
        var modules = new List<(int ModuleId, string Table)>();
        await using (var reader = await moduleCommand.ExecuteReaderAsync(token))
        {
            while (await reader.ReadAsync(token))
            {
                modules.Add((reader.GetInt32(0), reader.GetString(1)));
            }
        }

        foreach (var (moduleId, table) in modules)
        {
            if (!await WorkbenchSql.ColumnExistsAsync(connection, null, table, "CONFIRM_TAG", token))
            {
                continue;
            }
            var pk = await WorkbenchSql.GetPrimaryKeyColumnsAsync(connection, null, table, token);
            if (pk.Count == 0)
            {
                continue;
            }
            var projection = string.Join(',', pk.Select(column => $"LTRIM(RTRIM([{column}]))"));
            await using var documentCommand = new SqlCommand(
                $"SELECT TOP 1 {projection} FROM dbo.[{table}] WITH (NOLOCK) WHERE ISNULL(CONFIRM_TAG,0)=0 ORDER BY {projection};",
                connection);
            await using var documentReader = await documentCommand.ExecuteReaderAsync(token);
            if (!await documentReader.ReadAsync(token))
            {
                continue;
            }
            var keys = new List<string>();
            for (var index = 0; index < pk.Count; index++)
            {
                keys.Add(documentReader.IsDBNull(index) ? string.Empty : documentReader.GetString(index));
            }
            if (keys.Any(string.IsNullOrWhiteSpace))
            {
                continue;
            }
            return new Candidate(moduleId, table, pk, keys);
        }

        throw new InvalidOperationException("库内找不到可用于预演的样本（需引擎接管 + 已发布快照 + 未批核单据）。");
    }

    /// <summary>取一张表的内容指纹（覆盖所有列），以及该模块单据主表/明细表的指纹。</summary>
    private static async Task<string> FingerprintAsync(SqlConnection connection, string sql, CancellationToken token)
    {
        await using var command = new SqlCommand(sql, connection);
        var value = await command.ExecuteScalarAsync(token);
        return Convert.ToString(value ?? "<null>", System.Globalization.CultureInfo.InvariantCulture)!;
    }

    private static string TableFingerprint(string table) =>
        $"SELECT ISNULL(CONVERT(nvarchar(40), COUNT_BIG(*)),'0') + N':' + ISNULL(CONVERT(nvarchar(40), CHECKSUM_AGG(BINARY_CHECKSUM(*))),'0') FROM dbo.[{table}] WITH (NOLOCK);";

    /// <summary>
    /// 预演的核心断言：同一事务内跑完真实的「校验闸 → 状态翻转 → 效果链」再回滚之后，
    /// 库内**逐表**指纹与预演前完全一致（行数 + 全列校验和）。
    /// </summary>
    [Fact]
    public async Task 预演批核生效链_回滚后库内逐表零变化()
    {
        var token = CancellationToken.None;
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync(token);
        var candidate = await FindCandidateAsync(connection, token);

        var definition = await LoadPublishedDefinitionAsync(connection, candidate.ModuleId, token);
        var before = await SnapshotAsync(connection, token);

        var report = await CreateSimulation(Connections()).SimulateAsync(
            definition, "APPROVE_EFFECT", candidate.KeyValues, approve: true, executor: "simulation-test", token);

        Assert.True(report.RolledBack);
        Assert.True(report.Precondition.Passed, $"前置守卫未通过：{report.Precondition.Code} {report.Precondition.Message}");
        Assert.NotEmpty(report.Effects);
        Assert.All(report.Effects, step =>
            Assert.Contains(step.Outcome, new[] { "ran", "skipped", "failed" }));
        Assert.Equal(report.Effects.Count, report.Counts.Total);

        var after = await SnapshotAsync(connection, token);
        Assert.Equal(before, after);

        // 单据自身的批核状态不能被预演翻转——这是"回滚真的兜住了"最直观的一条。
        var tag = await ReadConfirmTagAsync(connection, candidate, token);
        Assert.False(tag);
    }

    /// <summary>解批一张未批核单据：与真实路径同码拒绝，且效果链一步都不跑。</summary>
    [Fact]
    public async Task 解批一张未批核单据_按真实路径同码拒绝且效果为空()
    {
        var token = CancellationToken.None;
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync(token);
        var candidate = await FindCandidateAsync(connection, token);
        var definition = await LoadPublishedDefinitionAsync(connection, candidate.ModuleId, token);

        var report = await CreateSimulation(Connections()).SimulateAsync(
            definition, "DEAPPROVE", candidate.KeyValues, approve: false, executor: "simulation-test", token);

        Assert.True(report.RolledBack);
        Assert.False(report.Precondition.Passed);
        Assert.Equal("WORKFLOW_STATE_CONFLICT", report.Precondition.Code);
        Assert.Empty(report.Effects);
        Assert.Equal(0, report.Counts.Total);
    }

    /// <summary>单据不存在：同样是报告里的前置守卫失败，而不是抛异常——报告本身就是产品。</summary>
    [Fact]
    public async Task 预演一张不存在的单据_前置守卫报记录不存在()
    {
        var token = CancellationToken.None;
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync(token);
        var candidate = await FindCandidateAsync(connection, token);
        var definition = await LoadPublishedDefinitionAsync(connection, candidate.ModuleId, token);
        var missing = candidate.KeyValues.Select((_, index) => index == 0 ? "ZZSIM" : "ZZSIM000001").ToList();

        var report = await CreateSimulation(Connections()).SimulateAsync(
            definition, "APPROVE_EFFECT", missing, approve: true, executor: "simulation-test", token);

        Assert.True(report.RolledBack);
        Assert.False(report.Precondition.Passed);
        Assert.Equal("RECORD_NOT_FOUND", report.Precondition.Code);
        Assert.Empty(report.Effects);
    }

    /// <summary>
    /// 预演要回答的第一个问题是"为什么这一步没动数据"，而两种原因必须分得开：
    /// ① 动作条件对本单不成立（规则问题）；② 条件成立但定位键一行都没中（键配错了）。
    ///
    /// 这里用同一个目标列、同一种算子构造两条动作做对照：把"条件未命中"退回成
    /// "返回 0 行"的老口径，本用例必须变红——它是这条语义的判别式。
    /// </summary>
    [Fact]
    public async Task 预演区分_条件未命中_与_匹配0行()
    {
        var token = CancellationToken.None;
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync(token);
        var candidate = await FindCandidateAsync(connection, token);
        var pk = candidate.PkColumns[0];

        var condition = JsonSerializer.SerializeToElement(new
        {
            logic = "AND",
            items = new object[]
            {
                new { type = "value-eq", field = new { scope = "MASTER", field = pk }, value = "ZZSIM_NEVER_MATCH" },
            },
        });
        var plan = new ModuleEffectPlan(
            candidate.ModuleId, candidate.MasterTable, null, "simulation-discrimination-test",
            candidate.PkColumns,
            [
                new EffectActionPlan(
                    1, "APPROVE_EFFECT", "field-accumulate", "条件不成立的动作", true, "BLOCK",
                    condition, null, null,
                    [new EffectOpPlan(1, candidate.MasterTable, pk, "ASSIGN",
                        new EffectSourceRef("CONSTANT", null, null, "ZZSIM"), null, null, null, null, null)]),
                new EffectActionPlan(
                    2, "APPROVE_EFFECT", "field-accumulate", "定位键不中的动作", true, "BLOCK",
                    null, null, null,
                    [new EffectOpPlan(1, candidate.MasterTable, pk, "ASSIGN",
                        new EffectSourceRef("CONSTANT", null, null, "ZZSIM"), null, null,
                        [new EffectMatchItem(pk, new EffectSourceRef("CONSTANT", null, null, "ZZSIM_NEVER_MATCH"))],
                        null, null)]),
            ],
            []);

        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(token);
        IReadOnlyList<EffectStepResult> steps;
        try
        {
            steps = await EffectShadowRunner.BuildPipelineFor(ConnectionString).ExecuteWithinTransactionAsync(
                connection, transaction, plan, EffectEvent.ApproveEffect,
                string.Join(',', candidate.KeyValues), "simulation-test", token, candidate.KeyValues, simulate: true);
        }
        finally
        {
            await transaction.RollbackAsync(token);
        }

        Assert.Equal(2, steps.Count);

        var skipped = steps[0];
        Assert.Equal(EffectStepOutcome.Skipped, skipped.Outcome);
        Assert.False(skipped.ConditionMatched);
        Assert.False(string.IsNullOrWhiteSpace(skipped.SkipReason));
        Assert.Equal(0, skipped.RowsAffected);

        var noRows = steps[1];
        Assert.Equal(EffectStepOutcome.Ran, noRows.Outcome);
        Assert.True(noRows.ConditionMatched);
        Assert.Null(noRows.SkipReason);
        Assert.Equal(0, noRows.RowsAffected);
        Assert.NotNull(noRows.Ops);
        Assert.All(noRows.Ops!, op => Assert.Empty(op.Changes));
    }

    private static async Task<WorkbenchDefinition> LoadPublishedDefinitionAsync(
        SqlConnection connection, int moduleId, CancellationToken token)
    {
        await using var command = new SqlCommand(
            "SELECT TOP 1 DEFINITION_JSON FROM dbo.WORKBENCH_DEFINITION_SNAPSHOT WITH (NOLOCK) WHERE MODULE_ID=@Id AND IS_CURRENT=1;",
            connection);
        command.Parameters.Add("@Id", SqlDbType.Int).Value = moduleId;
        var json = await command.ExecuteScalarAsync(token) as string
            ?? throw new InvalidOperationException($"模块 {moduleId} 无已发布快照。");
        return JsonSerializer.Deserialize<WorkbenchDefinition>(json, WorkbenchDefinitionProvider.JsonOptions)!;
    }

    private static async Task<bool> ReadConfirmTagAsync(
        SqlConnection connection, Candidate candidate, CancellationToken token)
    {
        var where = string.Join(" AND ", candidate.PkColumns.Select((column, index) =>
            $"LTRIM(RTRIM([{column}]))=@k{index}"));
        await using var command = new SqlCommand(
            $"SELECT ISNULL(CONFIRM_TAG,0) FROM dbo.[{candidate.MasterTable}] WITH (NOLOCK) WHERE {where};", connection);
        for (var index = 0; index < candidate.KeyValues.Count; index++)
        {
            command.Parameters.AddWithValue($"@k{index}", candidate.KeyValues[index]);
        }
        var value = await command.ExecuteScalarAsync(token);
        return value is not null && Convert.ToInt32(value) == 1;
    }

    /// <summary>
    /// 全库指纹：覆盖效果链会碰到的几类表——单据主表/明细、上游单据明细、库存余额与流水、
    /// 以及审计表（预演连审计都不该留下）。
    /// </summary>
    private static async Task<Dictionary<string, string>> SnapshotAsync(SqlConnection connection, CancellationToken token)
    {
        var snapshot = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var table in new[] { "PUR_RECEIVE_M", "PUR_RECEIVE_D", "PUR_PURCHASE_D", "COP_SEND_M", "COP_SEND_D",
                                      "INV_PRO_DEPOT", "INV_DEPOT_LOG", "PRODUCT", "AUDIT_EVENT" })
        {
            if (!await TableExistsAsync(connection, table, token))
            {
                continue;
            }
            snapshot[table] = await FingerprintAsync(connection, TableFingerprint(table), token);
        }
        return snapshot;
    }

    private static async Task<bool> TableExistsAsync(SqlConnection connection, string table, CancellationToken token)
    {
        await using var command = new SqlCommand(
            "SELECT CASE WHEN EXISTS (SELECT 1 FROM sys.tables t JOIN sys.schemas s ON s.schema_id=t.schema_id WHERE s.name=N'dbo' AND t.name=@Table) THEN 1 ELSE 0 END;",
            connection);
        command.Parameters.Add("@Table", SqlDbType.NVarChar, 100).Value = table;
        return Convert.ToInt32(await command.ExecuteScalarAsync(token)) == 1;
    }
}
