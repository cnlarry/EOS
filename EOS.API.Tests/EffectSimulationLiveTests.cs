using System.Data;
using System.Diagnostics;
using System.Security.Claims;
using System.Text.Json;
using EOS.API.Controllers;
using EOS.API.Data;
using EOS.API.Data.Effects;
using EOS.API.Models;
using EOS.API.Security;
using EOS.API.Tests.Tools;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
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

    private static WorkbenchApprovalService CreateApprovals(DbConnectionFactory connections)
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
        return new WorkbenchApprovalService(
            connections, auditWriter, workflow, engine, new WorkbenchIdempotency(),
            NullLogger<WorkbenchApprovalService>.Instance);
    }

    private static EffectSimulationService CreateSimulation(DbConnectionFactory connections) =>
        new(connections, new EffectPlanLoader(), CreateApprovals(connections),
            NullLogger<EffectSimulationService>.Instance);

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
                          WHERE a.M_IDX=m.M_IDX AND LTRIM(RTRIM(a.EVENT_CODE))='APPROVE_EFFECT' AND ISNULL(a.ENABLED,1)=1)
              AND EXISTS (SELECT 1 FROM dbo.WORKBENCH_DEFINITION_SNAPSHOT s WITH (NOLOCK)
                          WHERE s.M_IDX=m.M_IDX AND s.IS_CURRENT=1)
              -- 必须选一条真的会跑效果链的路径：配了审批流程的模块，批核这一步是送审，
              -- 预演被显式拒绝（预演只覆盖引擎接管那条），拿它做样本会测到另一条分支上。
              AND NOT EXISTS (SELECT 1 FROM dbo.WFFORM wf WITH (NOLOCK) WHERE wf.WF_M_IDX=m.M_IDX)
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

    /// <summary>
    /// 真实批核有三条不跑效果链的分支（无副作用批核 / 未启用引擎 / 送审），只有第四条会跑。
    /// 预演若对这四条一视同仁，报告就与真点不一样——所以三条都必须被显式拒绝。
    /// </summary>
    [Fact]
    public async Task 预演只放行会执行效果链的那一条分支()
    {
        var token = CancellationToken.None;
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync(token);
        var candidate = await FindCandidateAsync(connection, token);
        var flowModuleId = await FindFlowModuleAsync(connection, token);
        var noFlowModuleId = await FindNoFlowModuleAsync(connection, token);
        var approvals = CreateApprovals(Connections());

        // 无副作用批核：自动批核 + 无引擎 + 无流程 —— 真实路径只翻转状态位。
        var stateless = await approvals.CheckSimulationSupportedAsync(
            connection, Definition(candidate.ModuleId, candidate.MasterTable, candidate.PkColumns,
                autoApprove: true, effectEngine: false), approve: true, token);
        Assert.NotNull(stateless);
        Assert.Equal("SIMULATION_NOT_SUPPORTED", stateless!.ErrorCode);
        Assert.Contains("无副作用批核", stateless.ErrorMessage);

        // 引擎未接管：效果链根本不会被执行。
        var noEngine = await approvals.CheckSimulationSupportedAsync(
            connection, Definition(noFlowModuleId, candidate.MasterTable, candidate.PkColumns,
                autoApprove: false, effectEngine: false), approve: true, token);
        Assert.NotNull(noEngine);
        Assert.Equal("SIMULATION_NOT_SUPPORTED", noEngine!.ErrorCode);
        Assert.Contains("未启用效果引擎", noEngine.ErrorMessage);

        // 有流程且非自动批核：真实批核这一步是送审，效果链要等流程通过后才跑。
        var flow = await approvals.CheckSimulationSupportedAsync(
            connection, Definition(flowModuleId, candidate.MasterTable, candidate.PkColumns,
                autoApprove: false, effectEngine: true), approve: true, token);
        Assert.NotNull(flow);
        Assert.Equal("SIMULATION_NOT_SUPPORTED", flow!.ErrorCode);
        Assert.Contains("审批流程", flow.ErrorMessage);

        // 引擎接管且无流程：这条才是预演能如实报告的那条。
        var supported = await approvals.CheckSimulationSupportedAsync(
            connection, Definition(noFlowModuleId, candidate.MasterTable, candidate.PkColumns,
                autoApprove: false, effectEngine: true), approve: true, token);
        Assert.Null(supported);

        // 判据之外还要有执法者：预演服务必须真的就此打住，而不是"算出来了但没人在意"。
        var rejected = await Assert.ThrowsAsync<EffectSimulationService.UnsupportedModuleException>(() =>
            CreateSimulation(Connections()).SimulateAsync(
                Definition(noFlowModuleId, candidate.MasterTable, candidate.PkColumns,
                    autoApprove: false, effectEngine: false),
                "APPROVE_EFFECT", candidate.KeyValues, approve: true, executor: "simulation-test", token));
        Assert.Equal("SIMULATION_NOT_SUPPORTED", rejected.Code);
    }

    /// <summary>
    /// 预演端点的报告必须写明"这是哪一版配置跑出来的"：定义版本由基线缓存条目单独持有，
    /// 快照 JSON 本身不带它——不接住这个版本号，报告就只能给出一个空字段。
    /// </summary>
    [Fact]
    public async Task 预演端点_报告带上已发布定义的版本号()
    {
        var token = CancellationToken.None;
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync(token);
        var candidate = await FindCandidateAsync(connection, token);
        var account = await FindConfigAccountAsync(connection, token);
        var published = await LoadPublishedDefinitionAsync(connection, candidate.ModuleId, token);

        const string version = "module-live-v99";
        var connections = Connections();
        var provider = new WorkbenchDefinitionProvider(connections, NullLogger<WorkbenchDefinitionProvider>.Instance);
        // 明确把版本抹掉：这样"报告里有版本"只可能来自端点补写，不可能来自快照本身。
        provider.SeedBaselineForTest(candidate.ModuleId, published with { DefinitionVersion = null }, version);

        // 只接上这个动作真正用到的依赖（权限、定义缓存、预演服务、调用者上下文）：
        // 其余参数属于同控制器的其它动作，置空是为了把这条用例的依赖面说清楚。
        var controller = new ModuleBusinessConfigController(
            repository: null!,
            rightsRepository: new ModuleRightsRepository(connections, NullLogger<ModuleRightsRepository>.Instance),
            snapshotService: null!,
            documentActions: null!,
            documentActionAuthorization: null!,
            definitions: provider,
            simulation: CreateSimulation(connections),
            logger: NullLogger<ModuleBusinessConfigController>.Instance,
            userContext: UserContext(account));

        var result = await controller.Simulate(
            candidate.ModuleId,
            new EffectSimulationRequest("APPROVE_EFFECT", candidate.KeyValues.ToList()),
            token);

        var ok = Assert.IsType<OkObjectResult>(result);
        var report = Assert.IsType<EffectSimulationReportDto>(ok.Value);
        Assert.Equal(version, report.DefinitionVersion);
        Assert.True(report.RolledBack);
        Assert.NotEmpty(report.Effects);
    }

    /// <summary>
    /// 锁等待超时（1222）与命令超时（-2）各有上限，可能远早于总预算；它们同样是"跑不完"，
    /// 必须按超时上报。这里用另一条连接把单据行锁住，让预演在状态翻转那一步等锁。
    /// </summary>
    [Fact]
    public async Task 锁等待超时按超时上报且回滚零残留()
    {
        var token = CancellationToken.None;
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync(token);
        var candidate = await FindCandidateAsync(connection, token);
        var definition = await LoadPublishedDefinitionAsync(connection, candidate.ModuleId, token);

        await using var blocker = new SqlConnection(ConnectionString);
        await blocker.OpenAsync(token);
        await using var blocking = (SqlTransaction)await blocker.BeginTransactionAsync(token);
        var where = string.Join(" AND ", candidate.PkColumns.Select((column, index) => $"[{column}]=@k{index}"));
        await using (var hold = new SqlCommand(
            $"UPDATE dbo.[{candidate.MasterTable}] SET CONFIRM_PERSON=CONFIRM_PERSON WHERE {where};", blocker, blocking))
        {
            for (var index = 0; index < candidate.KeyValues.Count; index++)
            {
                hold.Parameters.AddWithValue($"@k{index}", candidate.KeyValues[index]);
            }
            await hold.ExecuteNonQueryAsync(token);
        }

        try
        {
            var stopped = Stopwatch.StartNew();
            await Assert.ThrowsAsync<EffectSimulationService.TimeoutException>(() =>
                CreateSimulation(Connections()).SimulateAsync(
                    definition, "APPROVE_EFFECT", candidate.KeyValues, approve: true, executor: "simulation-test", token));
            stopped.Stop();
            // 判据是"被锁等待上限掐断"，不是"等到总预算"：两者相差一个数量级。
            Assert.True(stopped.Elapsed < TimeSpan.FromSeconds(25),
                $"应由锁等待上限触发（约 5s），实得 {stopped.Elapsed.TotalSeconds:F1}s");
        }
        finally
        {
            await blocking.RollbackAsync(token);
        }

        Assert.False(await ReadConfirmTagAsync(connection, candidate, token));
    }

    /// <summary>
    /// 链内 BLOCK 失败不是"前置守卫没过"：闸门都通了，失败发生在效果链内部。
    /// 报告必须把它放进步骤里（并带上轨迹），否则配置者只知道失败、不知道停在哪一步。
    /// </summary>
    [Fact]
    public async Task 链内失败报成失败步骤而不是前置守卫()
    {
        var token = CancellationToken.None;
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync(token);
        var candidate = await FindCandidateAsync(connection, token);
        // 未实现的服务效果键：走到它必然抛错，且按 BLOCK 语义中断链路。
        var definition = new WorkbenchDefinition(
            candidate.ModuleId, $"测试模块 {candidate.ModuleId}", candidate.MasterTable, null, [], [], null,
            true, true, false, candidate.PkColumns, string.Empty, false,
            EffectEngine: JsonSerializer.SerializeToElement(new { enabled = true }),
            BusinessActions: JsonSerializer.SerializeToElement(new object[]
            {
                new { seq = 1, eventCode = "APPROVE_EFFECT", effectKey = "job-enqueue", enabled = true, failMode = "BLOCK" },
            }));

        var report = await CreateSimulation(Connections()).SimulateAsync(
            definition, "APPROVE_EFFECT", candidate.KeyValues, approve: true, executor: "simulation-test", token);

        Assert.True(report.RolledBack);
        Assert.True(report.Precondition.Passed);
        Assert.True(report.Validation.Passed);
        var step = Assert.Single(report.Effects);
        Assert.Equal("failed", step.Outcome);
        Assert.False(string.IsNullOrWhiteSpace(step.Message));
        Assert.Equal(1, report.Counts.Failed);
        Assert.False(await ReadConfirmTagAsync(connection, candidate, token));
    }

    /// <summary>预演门按写面收口：库内必须有同时具备 2301 设置权与模块配置权的账号，否则本用例无意义。</summary>
    private static async Task<string> FindConfigAccountAsync(SqlConnection connection, CancellationToken token)
    {
        await using var command = new SqlCommand(
            """
            SELECT TOP 1 LTRIM(RTRIM(USER_ID)) FROM dbo.SYSDD WITH (NOLOCK)
            WHERE M_IDX=2301 AND ISNULL(SETUP_TAG,0)=1 AND ISNULL(MODULE_CONFIG_TAG,0)=1
            ORDER BY USER_ID;
            """, connection);
        return await command.ExecuteScalarAsync(token) as string
            ?? throw new InvalidOperationException("库内没有同时具备 2301 设置权与模块配置权的账号，无法验证预演端点的门。");
    }

    private static CurrentUserContext UserContext(string userId)
    {
        var httpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [
                    new Claim(ClaimTypes.NameIdentifier, userId),
                    new Claim(ClaimTypes.Name, userId),
                ], "test")),
        };
        return new CurrentUserContext(new HttpContextAccessor { HttpContext = httpContext });
    }

    private static WorkbenchDefinition Definition(
        int moduleId, string masterTable, IReadOnlyList<string> pk, bool autoApprove, bool effectEngine) =>
        new(moduleId, $"测试模块 {moduleId}", masterTable, null, [], [], null, true, true, false,
            pk, string.Empty, false,
            AutoApprove: autoApprove,
            EffectEngine: effectEngine ? JsonSerializer.SerializeToElement(new { enabled = true }) : null);

    private static async Task<int> FindFlowModuleAsync(SqlConnection connection, CancellationToken token) =>
        Convert.ToInt32(await new SqlCommand(
            "SELECT TOP 1 wf.WF_M_IDX FROM dbo.WFFORM wf WITH (NOLOCK) WHERE EXISTS (SELECT 1 FROM dbo.WFFORM_FLOW f WITH (NOLOCK) WHERE f.WF_M_IDX=wf.WF_M_IDX) ORDER BY wf.WF_M_IDX;",
            connection).ExecuteScalarAsync(token));

    private static async Task<int> FindNoFlowModuleAsync(SqlConnection connection, CancellationToken token) =>
        Convert.ToInt32(await new SqlCommand(
            "SELECT TOP 1 m.M_IDX FROM dbo.MODULES m WITH (NOLOCK) WHERE NOT EXISTS (SELECT 1 FROM dbo.WFFORM wf WITH (NOLOCK) WHERE wf.WF_M_IDX=m.M_IDX) ORDER BY m.M_IDX;",
            connection).ExecuteScalarAsync(token));

    private static async Task<WorkbenchDefinition> LoadPublishedDefinitionAsync(
        SqlConnection connection, int moduleId, CancellationToken token)
    {
        await using var command = new SqlCommand(
            "SELECT TOP 1 DEFINITION_JSON FROM dbo.WORKBENCH_DEFINITION_SNAPSHOT WITH (NOLOCK) WHERE M_IDX=@Id AND IS_CURRENT=1;",
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
