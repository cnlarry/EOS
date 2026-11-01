using System.Data;
using System.Text.Json;
using EOS.API.Data;
using EOS.API.Features.Assistant.Metrics;
using EOS.API.Features.Assistant.Tools;
using EOS.API.Models;
using EOS.API.Security;
using EOS.API.Telemetry;
using Xunit;

namespace EOS.API.Tests;

public sealed class ResolveMetricToolTests
{
    private const string SalesAmountRowFilter =
        """{"table":"COP_ORDER_M","on":["ORDER_TYPE","ORDER_NO"],"conditions":[{"column":"APPROVED","op":"=","value":true}]}""";

    private static ModuleRights Rights(bool canBrowse = true, string dataFilter = "", string execTag = "A") => new(
        CanBrowse: canBrowse, CanViewCost: false, CanViewSecrecy: false, CanSetup: false,
        DeniedMasterFields: new HashSet<string>(), DeniedDetailFields: new HashSet<string>(),
        CanAddNew: false, CanEdit: false, CanDelete: false, CanApprove: false, CanDeapprove: false,
        CanEndCase: false, CanUnEndCase: false, CanFileView: false, CanFileUpda: false,
        CanFileEdit: false, CanFileDele: false,
        DenyNewMasterFields: new HashSet<string>(), DenyNewDetailFields: new HashSet<string>(),
        DenyModiMasterFields: new HashSet<string>(), DenyModiDetailFields: new HashSet<string>(),
        DataFilter: dataFilter, ExecuteTag: execTag);

    private sealed class FakePermissions(ModulePermission permission) : IPermissionService
    {
        public Task<ModulePermission> GetAsync(string userId, int moduleId, CancellationToken cancellationToken) =>
            Task.FromResult(permission);

        public Task<ReportRights> GetReportAsync(
            string userId, int moduleId, string reportId, CancellationToken cancellationToken) =>
            throw new NotSupportedException("测试替身不涉及报表权");

        public Task<ModulePermission> RequireAsync(string userId, int moduleId, PermissionAction action, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class FakeSearchGateway(Func<int, WorkbenchDefinition?> resolve) : IWorkbenchSearchGateway
    {
        /// <summary>同一个定义应答所有模块（多数用例只关心"定义可用"这一件事）。</summary>
        public FakeSearchGateway(WorkbenchDefinition? definition) : this(_ => definition)
        {
        }

        /// <summary>按模块应答：用来验证"候选建不出定义就换下一个"。</summary>
        public List<int> AskedModules { get; } = [];

        public Task<IReadOnlyList<SystemKnowledgeModule>> ListAssistantModulesAsync(string? keyword, CancellationToken token) =>
            Task.FromResult<IReadOnlyList<SystemKnowledgeModule>>([]);

        public Task<int?> FindGenericModuleIdByTitleAsync(string titleKeyword, CancellationToken token) =>
            Task.FromResult<int?>(null);

        public Task<WorkbenchDefinition?> GetDefinitionAsync(int moduleId, string userId, string? execTag,
            bool canViewCost, bool canViewSecrecy, IReadOnlySet<string> deniedMasterFields,
            IReadOnlySet<string> deniedDetailFields, CancellationToken token)
        {
            AskedModules.Add(moduleId);
            return Task.FromResult(resolve(moduleId));
        }

        public Task<WorkbenchData> GetRowsAsync(WorkbenchDefinition definition, bool detail,
            IReadOnlyDictionary<string, string> keys, int page, int pageSize, CancellationToken token,
            WorkbenchQuery? query = null, string? keyword = null, string? sortField = null,
            string? sortDirection = null, int? groupIndex = null, string? groupValue = null, string? dataFilter = null) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<Dictionary<string, object?>>> GetExportRowsByKeysAsync(
            WorkbenchDefinition definition, IReadOnlyList<IReadOnlyList<string>> keys, CancellationToken token,
            int? groupIndex = null, string? groupValue = null, IReadOnlyList<WorkbenchField>? exportFields = null,
            string? dataFilter = null) => throw new NotSupportedException();

        public Task<FormDefinition?> GetFormDefinitionAsync(WorkbenchDefinition definition, string userId, string mode,
            bool canViewCost, bool canViewSecrecy, IReadOnlySet<string> deniedMasterFields,
            IReadOnlySet<string> deniedDetailFields, IReadOnlySet<string> deniedNewMasterFields,
            IReadOnlySet<string> deniedNewDetailFields, IReadOnlySet<string> deniedModiMasterFields,
            IReadOnlySet<string> deniedModiDetailFields, CancellationToken token, bool canAddNew = false,
            bool canEdit = false, bool canDelete = false, bool canApprove = false, bool canDeapprove = false,
            bool canEndCase = false, bool canUnEndCase = false, bool canFileView = false, bool canFileUpda = false,
            bool canFileEdit = false, bool canFileDele = false, bool canSetup = false) =>
            throw new NotSupportedException();
    }

    private sealed class FakeMetricRepository(
        MetricDefinitionRow? metric, IReadOnlyList<int> moduleIds,
        IReadOnlyList<MetricDefinitionRow>? searchResults = null) : IMetricRepository
    {
        public MetricPlan? ExecutedPlan { get; private set; }

        public List<string> SearchedKeywords { get; } = [];

        public Task<MetricDefinitionRow?> GetAsync(string metricId, CancellationToken token) =>
            Task.FromResult(metricId == metric?.MetricId ? metric : null);

        public Task<IReadOnlyList<MetricDefinitionRow>> SearchConfirmedAsync(string keyword, CancellationToken token)
        {
            SearchedKeywords.Add(keyword);
            return Task.FromResult(searchResults ?? []);
        }

        public Task<IReadOnlyList<int>> FindModuleIdsByTableAsync(string table, CancellationToken token) =>
            Task.FromResult(moduleIds);

        // The tool compiles the plan before the executor runs; capture it for assertions.
        public Task<decimal?> CaptureExecuteAsync(MetricPlan plan, CancellationToken token)
        {
            ExecutedPlan = plan;
            return Task.FromResult<decimal?>(1234.5m);
        }
    }

    private sealed class CapturingExecutor(FakeMetricRepository repository) : IMetricExecutor
    {
        public Task<decimal?> ExecuteAsync(MetricPlan plan, CancellationToken token) =>
            repository.CaptureExecuteAsync(plan, token);
    }

    private sealed class FakeProbe : IMetricSchemaProbe
    {
        public Task<bool> TableExistsAsync(string table, CancellationToken token) =>
            Task.FromResult(table is "COP_ORDER_D" or "COP_ORDER_M");

        public Task<IReadOnlySet<string>> GetColumnsAsync(string table, CancellationToken token)
        {
            IReadOnlySet<string> columns = table switch
            {
                "COP_ORDER_D" => new HashSet<string>(
                    ["ORDER_NO", "ORDER_TYPE", "SERIAL_NO", "AMOUNT_TAX", "AMOUNT", "QTY", "REBATE", "PRO_NO"],
                    StringComparer.OrdinalIgnoreCase),
                "COP_ORDER_M" => new HashSet<string>(
                    ["ORDER_NO", "ORDER_TYPE", "APPROVED", "ORDER_DATE", "CLIENT_ID", "OWNER"],
                    StringComparer.OrdinalIgnoreCase),
                _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            };
            return Task.FromResult(columns);
        }
    }

    private static WorkbenchField Field(string key) => new(key, key, "decimal", 100, null, false);

    private static WorkbenchDefinition Definition() => new(
        1405, "客户订单", "COP_ORDER_M", "COP_ORDER_D",
        MasterFields: [Field("ORDER_NO"), Field("ORDER_TYPE"), Field("APPROVED"), Field("ORDER_DATE"), Field("CLIENT_ID"), Field("OWNER")],
        DetailFields: [Field("ORDER_NO"), Field("ORDER_TYPE"), Field("SERIAL_NO"), Field("AMOUNT_TAX"), Field("AMOUNT"), Field("QTY"), Field("REBATE"), Field("PRO_NO")],
        DefaultSort: null, HasAdd: true, HasEdit: true, DetailNoSave: false,
        MasterPkOrder: ["ORDER_TYPE", "ORDER_NO"], DetailNoFields: string.Empty,
        HasWorkflow: true, UserId: "u1", ExecTag: "A");

    private sealed class FakeWhitelist(IMetricSchemaProbe probe) : IMetricFieldWhitelist
    {
        /// <summary>按表模拟"这一列对该用户不可见"（字段级禁看/成本/保密），用于验证拒绝路径。</summary>
        public Dictionary<string, HashSet<string>> Hidden { get; } = new(StringComparer.OrdinalIgnoreCase);

        public void Hide(string table, string column)
        {
            if (!Hidden.TryGetValue(table, out var columns))
            {
                columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                Hidden[table] = columns;
            }
            columns.Add(column);
        }

        public async Task<IReadOnlySet<string>> ReadAsync(string table, bool canViewCost, bool canViewSecrecy,
            IReadOnlySet<string> deniedFields, CancellationToken token)
        {
            var hidden = Hidden.GetValueOrDefault(table) ?? [];
            var columns = await probe.GetColumnsAsync(table, token);
            return columns
                .Where(column => !deniedFields.Contains(column) && !hidden.Contains(column))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>字段中文名（跨表同一 key 只有一个名字，够用例表达"日期"这类业务说法）。</summary>
        public Dictionary<string, string> Labels { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Task<IReadOnlyDictionary<string, string>> ReadLabelsAsync(string table,
            IReadOnlyCollection<string> columns, CancellationToken token)
        {
            var found = Labels
                .Where(pair => columns.Contains(pair.Key, StringComparer.OrdinalIgnoreCase))
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
            return Task.FromResult<IReadOnlyDictionary<string, string>>(found);
        }
    }

    private static MetricDefinitionRow Metric(
        string confirmStatus = "CONFIRMED", string? rowFilter = SalesAmountRowFilter) => new(
        "sales_amount", "销售额", "SUM(AMOUNT_TAX)", "COP_ORDER_D",
        "ORDER_DATE,CLIENT_ID,PRO_NO", 1, confirmStatus, rowFilter, null);

    private static ResolveMetricTool CreateTool(
        MetricDefinitionRow? metric, IReadOnlyList<int> moduleIds,
        bool canBrowse = true, WorkbenchDefinition? definition = null,
        IReadOnlyList<MetricDefinitionRow>? searchResults = null)
    {
        var repository = new FakeMetricRepository(metric, moduleIds, searchResults);
        var probe = new FakeProbe();
        var permission = new ModulePermission(Rights(canBrowse: canBrowse));
        return new ResolveMetricTool(
            repository, new CapturingExecutor(repository), probe,
            new MetricDefinitionValidator(probe), new FakeWhitelist(probe),
            new FakePermissions(permission),
            new FakeSearchGateway(definition ?? Definition()),
            new WorkbenchScopeFilter(new ApiMetrics()));
    }

    private static JsonElement Args(string json) =>
        JsonSerializer.Deserialize<JsonElement>(json);

    [Fact]
    public async Task Rejects_Missing_MetricId()
    {
        var tool = CreateTool(Metric(), [1405]);
        var result = await tool.ExecuteAsync("u1", Args("""{"metric_id":""}"""), CancellationToken.None);
        Assert.False(result.Ok);
    }

    [Fact]
    public async Task Reports_Undefined_Metric()
    {
        var tool = CreateTool(null, [1405]);
        var result = await tool.ExecuteAsync("u1", Args("""{"metric_id":"gross_margin"}"""), CancellationToken.None);
        Assert.False(result.Ok);
        Assert.Contains("尚无定义", result.ContentForModel);
    }

    [Fact]
    public async Task Rejects_Candidate_Metric()
    {
        var tool = CreateTool(Metric(confirmStatus: "CANDIDATE", rowFilter: null), [1405]);
        var result = await tool.ExecuteAsync("u1", Args("""{"metric_id":"sales_amount"}"""), CancellationToken.None);
        Assert.False(result.Ok);
        Assert.Contains("业务确认", result.ContentForModel);
    }

    [Fact]
    public async Task Resolves_Business_Word_To_The_Only_Confirmed_Metric()
    {
        // 用户说的是「营业额」而不是标识：命中唯一已确认口径就直接算，省掉一次 enum_metrics 往返。
        var tool = CreateTool(null, [1405], searchResults: [Metric()]);
        var result = await tool.ExecuteAsync("u1", Args("""{"metric_id":"营业额"}"""), CancellationToken.None);

        Assert.True(result.Ok, result.ContentForModel);
        Assert.Contains("1234.5", result.ContentForModel);
        Assert.Contains("按业务词「营业额」解析", result.ContentForModel);
    }

    [Fact]
    public async Task Rejects_Ambiguous_Business_Word_And_Lists_Candidates()
    {
        // 业务词命中多条口径时不能猜：猜错口径算出来的数比拒绝更糟。
        var tool = CreateTool(null, [1405], searchResults:
        [
            Metric(),
            new MetricDefinitionRow("sales_amount_ex", "销售额（未税）", "SUM(AMOUNT)", "COP_ORDER_D",
                "ORDER_DATE", 1, "CONFIRMED", SalesAmountRowFilter, "俗称销售额（未税）"),
        ]);
        var result = await tool.ExecuteAsync("u1", Args("""{"metric_id":"销售额"}"""), CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Contains("sales_amount（销售额）", result.ContentForModel);
        Assert.Contains("sales_amount_ex（销售额（未税））", result.ContentForModel);
    }

    [Fact]
    public async Task Rejects_When_Source_Table_Has_No_Module()
    {
        var tool = CreateTool(Metric(), []);
        var result = await tool.ExecuteAsync("u1", Args("""{"metric_id":"sales_amount"}"""), CancellationToken.None);
        Assert.False(result.Ok);
        Assert.Contains("未关联任何模块", result.ContentForModel);
    }

    [Fact]
    public async Task Denies_Browse_Outside_Permission()
    {
        var tool = CreateTool(Metric(), [1405], canBrowse: false);
        var result = await tool.ExecuteAsync("u1", Args("""{"metric_id":"sales_amount"}"""), CancellationToken.None);
        Assert.False(result.Ok);
        Assert.Contains("浏览权限", result.ContentForModel);
    }

    [Fact]
    public async Task Computes_Confirmed_Metric_With_RowFilter_And_Evidence()
    {
        var tool = CreateTool(Metric(), [1405]);
        var result = await tool.ExecuteAsync("u1", Args("""{"metric_id":"sales_amount"}"""), CancellationToken.None);
        Assert.True(result.Ok, result.ContentForModel);
        Assert.Contains("1234.5", result.ContentForModel);
        Assert.Contains("依据链", result.ContentForModel);
        Assert.Contains("sales_amount", result.ContentForModel);
        // 可用维度随结果给出：下次带过滤不必再枚举口径，也不会"省掉过滤重算"。
        Assert.Contains("可用维度 ORDER_DATE、CLIENT_ID、PRO_NO", result.ContentForModel);
    }

    [Fact]
    public async Task Accepts_Chinese_Dimension_Label()
    {
        // 模型/用户说的是"订单日期"而不是列名 ORDER_DATE：按中文名映射回标识再算，
        // 不要回一句"维度名不符合标识符规则"让它自己猜。
        var repository = new FakeMetricRepository(Metric(), [1405]);
        var probe = new FakeProbe();
        var whitelist = new FakeWhitelist(probe);
        whitelist.Labels["ORDER_DATE"] = "订单日期";
        var tool = new ResolveMetricTool(
            repository, new CapturingExecutor(repository), probe,
            new MetricDefinitionValidator(probe), whitelist,
            new FakePermissions(new ModulePermission(Rights())),
            new FakeSearchGateway(Definition()),
            new WorkbenchScopeFilter(new ApiMetrics()));

        var result = await tool.ExecuteAsync("u1", Args(
            """{"metric_id":"sales_amount","dimensions":{"订单日期":{"from":"2026-09-01","to":"2026-09-30"}}}"""),
            CancellationToken.None);

        Assert.True(result.Ok, result.ContentForModel);
        Assert.Contains("[ORDER_DATE]>=@dim", repository.ExecutedPlan!.Sql);
    }

    [Fact]
    public async Task Unknown_Dimension_Lists_Available_Dimensions_With_Labels()
    {
        // 映射不到时必须把"能用什么"一次说全（带中文名），否则模型下一步会省掉过滤重算——
        // 那等于把"9 月"偷换成"全部时间"，答出的数字是另一个问题的答案。
        var repository = new FakeMetricRepository(Metric(), [1405]);
        var probe = new FakeProbe();
        var whitelist = new FakeWhitelist(probe);
        whitelist.Labels["ORDER_DATE"] = "订单日期";
        whitelist.Labels["CLIENT_ID"] = "客户";
        var tool = new ResolveMetricTool(
            repository, new CapturingExecutor(repository), probe,
            new MetricDefinitionValidator(probe), whitelist,
            new FakePermissions(new ModulePermission(Rights())),
            new FakeSearchGateway(Definition()),
            new WorkbenchScopeFilter(new ApiMetrics()));

        var result = await tool.ExecuteAsync("u1", Args(
            """{"metric_id":"sales_amount","dimensions":{"日期":{"from":"2026-09-01","to":"2026-09-30"}}}"""),
            CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Contains("可用维度：ORDER_DATE（订单日期）、CLIENT_ID（客户）、PRO_NO", result.ContentForModel);
        Assert.Null(repository.ExecutedPlan);
    }

    [Fact]
    public async Task Compiled_Plan_Contains_RowFilter_Exists_And_Dimension_Parameters()
    {
        var repository = new FakeMetricRepository(Metric(), [1405]);
        var probe = new FakeProbe();
        var tool = new ResolveMetricTool(
            repository, new CapturingExecutor(repository), probe,
            new MetricDefinitionValidator(probe), new FakeWhitelist(probe),
            new FakePermissions(new ModulePermission(Rights())),
            new FakeSearchGateway(Definition()),
            new WorkbenchScopeFilter(new ApiMetrics()));
        var result = await tool.ExecuteAsync("u1", Args(
            """{"metric_id":"sales_amount","dimensions":{"CLIENT_ID":"C001","ORDER_DATE":{"from":"2026-01-01","to":"2026-03-31"}}}"""),
            CancellationToken.None);
        Assert.True(result.Ok, result.ContentForModel);

        var plan = repository.ExecutedPlan;
        Assert.NotNull(plan);
        Assert.Contains("EXISTS (SELECT 1 FROM dbo.[COP_ORDER_M] WITH (NOLOCK)" +
                        " WHERE [ORDER_TYPE]=dbo.[COP_ORDER_D].[ORDER_TYPE] AND [ORDER_NO]=dbo.[COP_ORDER_D].[ORDER_NO]",
            plan.Sql);
        Assert.Contains("[APPROVED]=@rf", plan.Sql);
        Assert.Contains("[CLIENT_ID]=@dim", plan.Sql);
        Assert.Contains("([ORDER_DATE]>=@dim", plan.Sql);
        Assert.DoesNotContain("SELECT AMOUNT", plan.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Skips_Candidates_That_Cannot_Build_A_Definition()
    {
        // 只按表名匹配时，最先命中的可能是"以来源表为主表"、却建不出工作台定义的模块
        // （自定义承载页、或表未纳入工作台）：这时应继续试下一个候选，而不是当场判"权限不足"——
        // 实测按错误的结论回话会把模型带偏（它会拿同一个口径反复重试）。
        var gateway = new FakeSearchGateway(moduleId => moduleId == 1405 ? Definition() : null);
        var repository = new FakeMetricRepository(Metric(), [1406, 1405]);
        var probe = new FakeProbe();
        var tool = new ResolveMetricTool(
            repository, new CapturingExecutor(repository), probe,
            new MetricDefinitionValidator(probe), new FakeWhitelist(probe),
            new FakePermissions(new ModulePermission(Rights())), gateway,
            new WorkbenchScopeFilter(new ApiMetrics()));

        var result = await tool.ExecuteAsync("u1", Args("""{"metric_id":"sales_amount"}"""), CancellationToken.None);

        Assert.True(result.Ok, result.ContentForModel);
        Assert.Contains("1234.5", result.ContentForModel);
        Assert.Equal(new[] { 1406, 1405 }, gateway.AskedModules);
    }

    [Fact]
    public async Task Rejects_When_No_Candidate_Can_Carry_The_Metric()
    {
        // 候选都建不出定义（都不是工作台承载页）：拒绝文案要说清是"没有挂靠在可查询的工作台模块上"，
        // 不能含糊成"权限范围不足"——原因说不清会让模型重试同一个口径，白烧工具轮
        var gateway = new FakeSearchGateway(_ => null);
        var repository = new FakeMetricRepository(Metric(), [14996]);
        var probe = new FakeProbe();
        var tool = new ResolveMetricTool(
            repository, new CapturingExecutor(repository), probe,
            new MetricDefinitionValidator(probe), new FakeWhitelist(probe),
            new FakePermissions(new ModulePermission(Rights())), gateway,
            new WorkbenchScopeFilter(new ApiMetrics()));

        var result = await tool.ExecuteAsync("u1", Args("""{"metric_id":"sales_amount"}"""), CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Contains("通用工作台承载页", result.ContentForModel);
        Assert.DoesNotContain("权限范围内构建", result.ContentForModel);
    }

    [Fact]
    public async Task Rejects_Dimension_Outside_Declared_Keys()
    {
        var tool = CreateTool(Metric(), [1405]);
        var result = await tool.ExecuteAsync("u1", Args(
            """{"metric_id":"sales_amount","dimensions":{"REGION":"east"}}"""), CancellationToken.None);
        Assert.False(result.Ok);
        Assert.Contains("可用维度", result.ContentForModel);
    }

    [Fact]
    public async Task Rejects_When_RowFilter_Column_Hidden_By_Field_Permission()
    {
        // APPROVED 被字段级权限禁止：行过滤列对该用户不可见，计算必须拒绝——
        // 否则等于绕开字段级禁止去用它做筛选。
        var repository = new FakeMetricRepository(Metric(), [1405]);
        var probe = new FakeProbe();
        var whitelist = new FakeWhitelist(probe);
        whitelist.Hide("COP_ORDER_M", "APPROVED");
        var tool = new ResolveMetricTool(
            repository, new CapturingExecutor(repository), probe,
            new MetricDefinitionValidator(probe), whitelist,
            new FakePermissions(new ModulePermission(Rights())),
            new FakeSearchGateway(Definition()),
            new WorkbenchScopeFilter(new ApiMetrics()));

        var result = await tool.ExecuteAsync("u1", Args("""{"metric_id":"sales_amount"}"""), CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Contains("不可见", result.ContentForModel);
    }

    [Fact]
    public async Task Computes_When_Aggregate_Column_Is_Absent_From_Personal_Column_View()
    {
        // 列选择（SYS_USER_COLUMN）只是显示偏好：明细字段视图里没有 AMOUNT_TAX，不代表这个人
        // 无权汇总总金额。口径白名单与字段视图分离，否则同一口径会因"谁问的"而时算得出时算不出。
        var definition = new WorkbenchDefinition(
            1405, "客户订单", "COP_ORDER_M", "COP_ORDER_D",
            MasterFields: [Field("ORDER_NO"), Field("ORDER_TYPE"), Field("APPROVED"), Field("ORDER_DATE"), Field("CLIENT_ID")],
            DetailFields: [Field("ORDER_NO"), Field("ORDER_TYPE"), Field("SERIAL_NO"), Field("AMOUNT")],
            DefaultSort: null, HasAdd: true, HasEdit: true, DetailNoSave: false,
            MasterPkOrder: ["ORDER_TYPE", "ORDER_NO"], DetailNoFields: string.Empty,
            HasWorkflow: true, UserId: "u1", ExecTag: "A");
        var tool = CreateTool(Metric(), [1405], definition: definition);

        var result = await tool.ExecuteAsync("u1", Args("""{"metric_id":"sales_amount"}"""), CancellationToken.None);

        Assert.True(result.Ok, result.ContentForModel);
        Assert.Contains("1234.5", result.ContentForModel);
    }

    [Fact]
    public async Task Filters_By_Full_Record_Key_Set()
    {
        // 「这张单多少钱」：按完整主键精确过滤，不靠维度（口径维度里通常没有单号）。
        var repository = new FakeMetricRepository(Metric(), [1405]);
        var probe = new FakeProbe();
        var tool = new ResolveMetricTool(
            repository, new CapturingExecutor(repository), probe,
            new MetricDefinitionValidator(probe), new FakeWhitelist(probe),
            new FakePermissions(new ModulePermission(Rights())),
            new FakeSearchGateway(Definition()),
            new WorkbenchScopeFilter(new ApiMetrics()));

        var result = await tool.ExecuteAsync("u1", Args(
            """{"metric_id":"sales_amount","record":{"ORDER_TYPE":"CGD","ORDER_NO":"CGD26100010"}}"""),
            CancellationToken.None);

        Assert.True(result.Ok, result.ContentForModel);
        var sql = repository.ExecutedPlan!.Sql;
        Assert.Contains("[ORDER_TYPE]=@dim", sql);
        Assert.Contains("[ORDER_NO]=@dim", sql);
        Assert.Contains("按单据主键精确过滤", result.ContentForModel);
    }

    [Fact]
    public async Task Rejects_Incomplete_Record_Key_Set()
    {
        // 只给单号会在跨单别重号时静默放大范围，所以主键必须给齐。
        var tool = CreateTool(Metric(), [1405]);
        var result = await tool.ExecuteAsync("u1", Args(
            """{"metric_id":"sales_amount","record":{"ORDER_NO":"CGD26100010"}}"""),
            CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Contains("必须给齐主键", result.ContentForModel);
        Assert.Contains("ORDER_TYPE", result.ContentForModel);
    }

    [Fact]
    public async Task Rejects_Record_Key_Outside_Master_Primary_Key()
    {
        var tool = CreateTool(Metric(), [1405]);
        var result = await tool.ExecuteAsync("u1", Args(
            """{"metric_id":"sales_amount","record":{"ORDER_TYPE":"CGD","ORDER_NO":"X","SERIAL_NO":"1"}}"""),
            CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Contains("未识别：SERIAL_NO", result.ContentForModel);
    }

    [Fact]
    public async Task Reports_Every_Distinct_Candidate_Failure()
    {
        // 候选模块的失败原因各不相同：只回第一条会把"按表名碰巧命中"的模块的结论当答案，
        // 模型据此重试同一个口径只会得到同样的结果。
        var gateway = new FakeSearchGateway(moduleId => moduleId switch
        {
            14989 => new WorkbenchDefinition(
                14989, "客户未送货明细", "COP_ORDER_D", null,
                MasterFields: [Field("ORDER_NO"), Field("AMOUNT_TAX")],
                DetailFields: [], DefaultSort: null, HasAdd: false, HasEdit: false, DetailNoSave: false,
                MasterPkOrder: ["ORDER_NO"], DetailNoFields: string.Empty,
                HasWorkflow: false, UserId: "u1", ExecTag: "A"),
            _ => Definition(),
        });
        var repository = new FakeMetricRepository(Metric(), [14989, 1405]);
        var probe = new FakeProbe();
        var whitelist = new FakeWhitelist(probe);
        whitelist.Hide("COP_ORDER_M", "APPROVED");
        var tool = new ResolveMetricTool(
            repository, new CapturingExecutor(repository), probe,
            new MetricDefinitionValidator(probe), whitelist,
            new FakePermissions(new ModulePermission(Rights())), gateway,
            new WorkbenchScopeFilter(new ApiMetrics()));

        var result = await tool.ExecuteAsync("u1", Args("""{"metric_id":"sales_amount"}"""), CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Contains("ORDER_DATE", result.ContentForModel);
        Assert.Contains("APPROVED", result.ContentForModel);
    }
}

public sealed class ResolveMetricNullResultTests
{
    private sealed class NullExecutor : IMetricExecutor
    {
        public Task<decimal?> ExecuteAsync(MetricPlan plan, CancellationToken token) =>
            Task.FromResult<decimal?>(null);
    }

    [Fact]
    public async Task Null_Result_Reports_No_Data_With_Evidence()
    {
        var metric = new MetricDefinitionRow("inventory_turnover", "库存周转率",
            "SUM(QTY) / NULLIF(SUM(IN_QTY),0)", "INV_PRO_DEPOT", "DEPOT_ID,PRO_NO", 1,
            "CONFIRMED", null, null);
        var probe = new EmptyProbe();
        var tool = new ResolveMetricTool(
            new SingleMetricRepository(metric),
            new NullExecutor(),
            probe,
            new MetricDefinitionValidator(probe),
            new ProbeWhitelist(probe),
            new AlwaysBrowsePermissions(),
            new NullGateway(),
            new WorkbenchScopeFilter(new ApiMetrics()));
        var result = await tool.ExecuteAsync("u1",
            JsonSerializer.Deserialize<JsonElement>("""{"metric_id":"inventory_turnover"}"""),
            CancellationToken.None);
        Assert.True(result.Ok, result.ContentForModel);
        Assert.Contains("没有数据", result.ContentForModel);
        Assert.Contains("依据链", result.ContentForModel);
    }

    private sealed class SingleMetricRepository(MetricDefinitionRow metric) : IMetricRepository
    {
        public Task<MetricDefinitionRow?> GetAsync(string metricId, CancellationToken token) =>
            Task.FromResult<MetricDefinitionRow?>(metric);

        public Task<IReadOnlyList<MetricDefinitionRow>> SearchConfirmedAsync(string keyword, CancellationToken token) =>
            Task.FromResult<IReadOnlyList<MetricDefinitionRow>>([]);

        public Task<IReadOnlyList<int>> FindModuleIdsByTableAsync(string table, CancellationToken token) =>
            Task.FromResult<IReadOnlyList<int>>([1408]);
    }

    private sealed class EmptyProbe : IMetricSchemaProbe
    {
        public Task<bool> TableExistsAsync(string table, CancellationToken token) => Task.FromResult(true);

        public Task<IReadOnlySet<string>> GetColumnsAsync(string table, CancellationToken token) =>
            Task.FromResult<IReadOnlySet<string>>(
                new HashSet<string>(["QTY", "IN_QTY", "DEPOT_ID", "PRO_NO"], StringComparer.OrdinalIgnoreCase));
    }

    private sealed class ProbeWhitelist(IMetricSchemaProbe probe) : IMetricFieldWhitelist
    {
        public Task<IReadOnlySet<string>> ReadAsync(string table, bool canViewCost, bool canViewSecrecy,
            IReadOnlySet<string> deniedFields, CancellationToken token) =>
            probe.GetColumnsAsync(table, token);

        public Task<IReadOnlyDictionary<string, string>> ReadLabelsAsync(string table,
            IReadOnlyCollection<string> columns, CancellationToken token) =>
            Task.FromResult<IReadOnlyDictionary<string, string>>(
                columns.ToDictionary(column => column, column => column, StringComparer.OrdinalIgnoreCase));
    }

    private sealed class AlwaysBrowsePermissions : IPermissionService
    {
        public Task<ModulePermission> GetAsync(string userId, int moduleId, CancellationToken cancellationToken) =>
            Task.FromResult(new ModulePermission(new ModuleRights(
                CanBrowse: true, CanViewCost: false, CanViewSecrecy: false, CanSetup: false,
                DeniedMasterFields: new HashSet<string>(), DeniedDetailFields: new HashSet<string>(),
                CanAddNew: false, CanEdit: false, CanDelete: false, CanApprove: false, CanDeapprove: false,
                CanEndCase: false, CanUnEndCase: false, CanFileView: false, CanFileUpda: false,
                CanFileEdit: false, CanFileDele: false,
                DenyNewMasterFields: new HashSet<string>(), DenyNewDetailFields: new HashSet<string>(),
                DenyModiMasterFields: new HashSet<string>(), DenyModiDetailFields: new HashSet<string>(),
                DataFilter: string.Empty, ExecuteTag: "A")));

        public Task<ReportRights> GetReportAsync(
            string userId, int moduleId, string reportId, CancellationToken cancellationToken) =>
            throw new NotSupportedException("测试替身不涉及报表权");

        public Task<ModulePermission> RequireAsync(string userId, int moduleId, PermissionAction action, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class NullGateway : IWorkbenchSearchGateway
    {
        public Task<IReadOnlyList<SystemKnowledgeModule>> ListAssistantModulesAsync(string? keyword, CancellationToken token) =>
            Task.FromResult<IReadOnlyList<SystemKnowledgeModule>>([]);

        public Task<int?> FindGenericModuleIdByTitleAsync(string titleKeyword, CancellationToken token) =>
            Task.FromResult<int?>(null);

        public Task<WorkbenchDefinition?> GetDefinitionAsync(int moduleId, string userId, string? execTag,
            bool canViewCost, bool canViewSecrecy, IReadOnlySet<string> deniedMasterFields,
            IReadOnlySet<string> deniedDetailFields, CancellationToken token) => Task.FromResult<WorkbenchDefinition?>(
            new WorkbenchDefinition(1408, "库存", "INV_PRO_DEPOT", null,
                MasterFields: [new WorkbenchField("QTY", "数量", "decimal", 100, null, false),
                    new WorkbenchField("IN_QTY", "入库", "decimal", 100, null, false),
                    new WorkbenchField("DEPOT_ID", "仓库", "nvarchar", 100, null, false),
                    new WorkbenchField("PRO_NO", "产品", "nvarchar", 100, null, false)],
                DetailFields: [], DefaultSort: null, HasAdd: true, HasEdit: true, DetailNoSave: false,
                MasterPkOrder: ["DEPOT_ID", "PRO_NO"], DetailNoFields: string.Empty,
                HasWorkflow: false, UserId: "u1", ExecTag: "A"));

        public Task<WorkbenchData> GetRowsAsync(WorkbenchDefinition definition, bool detail,
            IReadOnlyDictionary<string, string> keys, int page, int pageSize, CancellationToken token,
            WorkbenchQuery? query = null, string? keyword = null, string? sortField = null,
            string? sortDirection = null, int? groupIndex = null, string? groupValue = null, string? dataFilter = null) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<Dictionary<string, object?>>> GetExportRowsByKeysAsync(
            WorkbenchDefinition definition, IReadOnlyList<IReadOnlyList<string>> keys, CancellationToken token,
            int? groupIndex = null, string? groupValue = null, IReadOnlyList<WorkbenchField>? exportFields = null,
            string? dataFilter = null) => throw new NotSupportedException();

        public Task<FormDefinition?> GetFormDefinitionAsync(WorkbenchDefinition definition, string userId, string mode,
            bool canViewCost, bool canViewSecrecy, IReadOnlySet<string> deniedMasterFields,
            IReadOnlySet<string> deniedDetailFields, IReadOnlySet<string> deniedNewMasterFields,
            IReadOnlySet<string> deniedNewDetailFields, IReadOnlySet<string> deniedModiMasterFields,
            IReadOnlySet<string> deniedModiDetailFields, CancellationToken token, bool canAddNew = false,
            bool canEdit = false, bool canDelete = false, bool canApprove = false, bool canDeapprove = false,
            bool canEndCase = false, bool canUnEndCase = false, bool canFileView = false, bool canFileUpda = false,
            bool canFileEdit = false, bool canFileDele = false, bool canSetup = false) =>
            throw new NotSupportedException();
    }
}
