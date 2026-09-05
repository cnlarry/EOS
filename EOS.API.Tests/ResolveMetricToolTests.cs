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
        """{"table":"COP_ORDER_M","on":["ORDER_TYPE","ORDER_NO"],"conditions":[{"column":"CONFIRM_TAG","op":"=","value":true}]}""";

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

        public Task<ModulePermission> RequireAsync(string userId, int moduleId, PermissionAction action, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class FakeSearchGateway(WorkbenchDefinition? definition) : IWorkbenchSearchGateway
    {
        public Task<IReadOnlyList<SystemKnowledgeModule>> ListAssistantModulesAsync(string? keyword, CancellationToken token) =>
            Task.FromResult<IReadOnlyList<SystemKnowledgeModule>>([]);

        public Task<int?> FindGenericModuleIdByTitleAsync(string titleKeyword, CancellationToken token) =>
            Task.FromResult<int?>(null);

        public Task<WorkbenchDefinition?> GetDefinitionAsync(int moduleId, string userId, string? execTag,
            bool canViewCost, bool canViewSecrecy, IReadOnlySet<string> deniedMasterFields,
            IReadOnlySet<string> deniedDetailFields, CancellationToken token) =>
            Task.FromResult(definition);

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

    private sealed class FakeMetricRepository(MetricDefinitionRow? metric, IReadOnlyList<int> moduleIds) : IMetricRepository
    {
        public MetricPlan? ExecutedPlan { get; private set; }

        public Task<MetricDefinitionRow?> GetAsync(string metricId, CancellationToken token) =>
            Task.FromResult(metric);

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
                    ["ORDER_NO", "ORDER_TYPE", "CONFIRM_TAG", "ORDER_DATE", "CLIENT_ID", "OWNER"],
                    StringComparer.OrdinalIgnoreCase),
                _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            };
            return Task.FromResult(columns);
        }
    }

    private static WorkbenchField Field(string key) => new(key, key, "decimal", 100, null, false);

    private static WorkbenchDefinition Definition() => new(
        1405, "客户订单", "COP_ORDER_M", "COP_ORDER_D",
        MasterFields: [Field("ORDER_NO"), Field("ORDER_TYPE"), Field("CONFIRM_TAG"), Field("ORDER_DATE"), Field("CLIENT_ID"), Field("OWNER")],
        DetailFields: [Field("ORDER_NO"), Field("ORDER_TYPE"), Field("SERIAL_NO"), Field("AMOUNT_TAX"), Field("AMOUNT"), Field("QTY"), Field("REBATE"), Field("PRO_NO")],
        DefaultSort: null, HasAdd: true, HasEdit: true, DetailNoSave: false,
        MasterPkOrder: ["ORDER_TYPE", "ORDER_NO"], DetailNoFields: string.Empty,
        HasWorkflow: true, UserId: "u1", ExecTag: "A");

    private static MetricDefinitionRow Metric(
        string confirmStatus = "CONFIRMED", string? rowFilter = SalesAmountRowFilter) => new(
        "sales_amount", "销售额", "SUM(AMOUNT_TAX)", "COP_ORDER_D",
        "ORDER_DATE,CLIENT_ID,PRO_NO", 1, confirmStatus, rowFilter, null);

    private static ResolveMetricTool CreateTool(
        MetricDefinitionRow? metric, IReadOnlyList<int> moduleIds,
        bool canBrowse = true, WorkbenchDefinition? definition = null)
    {
        var repository = new FakeMetricRepository(metric, moduleIds);
        var permission = new ModulePermission(Rights(canBrowse: canBrowse));
        return new ResolveMetricTool(
            repository, new CapturingExecutor(repository), new FakeProbe(),
            new MetricDefinitionValidator(new FakeProbe()),
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
    }

    [Fact]
    public async Task Compiled_Plan_Contains_RowFilter_Exists_And_Dimension_Parameters()
    {
        var repository = new FakeMetricRepository(Metric(), [1405]);
        var tool = new ResolveMetricTool(
            repository, new CapturingExecutor(repository), new FakeProbe(),
            new MetricDefinitionValidator(new FakeProbe()),
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
        Assert.Contains("[CONFIRM_TAG]=@rf", plan.Sql);
        Assert.Contains("[CLIENT_ID]=@dim", plan.Sql);
        Assert.Contains("([ORDER_DATE]>=@dim", plan.Sql);
        Assert.DoesNotContain("SELECT AMOUNT", plan.Sql, StringComparison.OrdinalIgnoreCase);
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
        // Master whitelist without CONFIRM_TAG: the row-filter column is not visible
        // to this user, so computation must be refused.
        var definition = new WorkbenchDefinition(
            1405, "客户订单", "COP_ORDER_M", "COP_ORDER_D",
            MasterFields: [Field("ORDER_NO"), Field("ORDER_TYPE"), Field("ORDER_DATE"), Field("CLIENT_ID"), Field("OWNER")],
            DetailFields: [Field("ORDER_NO"), Field("ORDER_TYPE"), Field("SERIAL_NO"), Field("AMOUNT_TAX"), Field("AMOUNT"), Field("QTY"), Field("REBATE"), Field("PRO_NO")],
            DefaultSort: null, HasAdd: true, HasEdit: true, DetailNoSave: false,
            MasterPkOrder: ["ORDER_TYPE", "ORDER_NO"], DetailNoFields: string.Empty,
            HasWorkflow: true, UserId: "u1", ExecTag: "A");
        var tool = CreateTool(Metric(), [1405], definition: definition);
        var result = await tool.ExecuteAsync("u1", Args("""{"metric_id":"sales_amount"}"""), CancellationToken.None);
        Assert.False(result.Ok);
        Assert.Contains("不可见", result.ContentForModel);
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
        var tool = new ResolveMetricTool(
            new SingleMetricRepository(metric),
            new NullExecutor(),
            new EmptyProbe(),
            new MetricDefinitionValidator(new EmptyProbe()),
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
