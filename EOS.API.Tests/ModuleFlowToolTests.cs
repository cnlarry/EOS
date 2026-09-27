using System.Text.Json;
using EOS.API.Data;
using EOS.API.Features.Assistant.Tools;
using EOS.API.Models;
using EOS.API.Security;
using Xunit;

namespace EOS.API.Tests;

public sealed class ModuleFlowToolTests
{
    private static ModuleRights Rights(
        bool canBrowse = true, bool canApprove = true, bool canEdit = true,
        bool canDelete = true, bool canEndCase = true, bool canUnEndCase = true) => new(
        CanBrowse: canBrowse, CanViewCost: false, CanViewSecrecy: false, CanSetup: false,
        DeniedMasterFields: new HashSet<string>(), DeniedDetailFields: new HashSet<string>(),
        CanAddNew: false, CanEdit: canEdit, CanDelete: canDelete, CanApprove: canApprove,
        CanDeapprove: canApprove, CanEndCase: canEndCase, CanUnEndCase: canUnEndCase,
        CanFileView: false, CanFileUpda: false, CanFileEdit: false, CanFileDele: false,
        DenyNewMasterFields: new HashSet<string>(), DenyNewDetailFields: new HashSet<string>(),
        DenyModiMasterFields: new HashSet<string>(), DenyModiDetailFields: new HashSet<string>(),
        DataFilter: string.Empty, ExecuteTag: "A");

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
            Task.FromResult<int?>(1405);

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

    private sealed class FakeFlowGateway(
        FlowDefinitionInfo? definition, FlowInstanceInfo? instance, RecordStateInfo record,
        ApprovalConfirmInfo? confirm = null) : IModuleFlowGateway
    {
        public Task<FlowDefinitionInfo?> GetFlowDefinitionAsync(int moduleId, CancellationToken token) =>
            Task.FromResult(definition);

        public Task<FlowInstanceInfo?> GetInstanceAsync(int moduleId, string keyCondition, CancellationToken token) =>
            Task.FromResult(instance);

        public Task<RecordStateInfo> GetRecordStateAsync(string masterTable, IReadOnlyList<string> pkColumns,
            IReadOnlyList<string> keyValues, CancellationToken token) => Task.FromResult(record);

        public Task<ApprovalConfirmInfo?> GetApprovalConfirmAsync(int moduleId, string keyCondition, CancellationToken token) =>
            Task.FromResult(confirm);
    }

    private sealed class PerModulePermissions(IReadOnlyDictionary<int, bool> browse) : IPermissionService
    {
        public Task<ModulePermission> GetAsync(string userId, int moduleId, CancellationToken cancellationToken) =>
            Task.FromResult(new ModulePermission(Rights(
                canBrowse: !browse.TryGetValue(moduleId, out var denied) || !denied)));

        public Task<ModulePermission> RequireAsync(string userId, int moduleId, PermissionAction action, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private static WorkbenchDefinition Definition() => new(
        1405, "客户订单", "COP_ORDER_M", null,
        MasterFields: [], DetailFields: [], DefaultSort: null, HasAdd: true, HasEdit: true,
        DetailNoSave: false, MasterPkOrder: ["ORDER_TYPE", "ORDER_NO"], DetailNoFields: string.Empty,
        HasWorkflow: true);

    private static FlowDefinitionInfo Flow() => new("客户订单二级审批",
    [
        new("001", "一级审批", ["admin"], false, 0, [], false),
        new("002", "二级审批", ["admin"], false, 0, [], false),
    ]);

    private static GetModuleFlowTool CreateTool(
        WorkbenchDefinition? definition = null,
        FlowDefinitionInfo? flow = null,
        FlowInstanceInfo? instance = null,
        RecordStateInfo? record = null,
        ModulePermission? permission = null) =>
        new(new FakeSearchGateway(definition ?? Definition()),
            new FakeFlowGateway(flow, instance, record ?? new(true, false, false)),
            new FakePermissions(permission ?? new ModulePermission(Rights())));

    [Fact]
    public async Task Denies_WithoutBrowsePermission()
    {
        var tool = CreateTool(permission: new ModulePermission(Rights(canBrowse: false)));

        var result = await tool.ExecuteAsync("u1",
            JsonSerializer.SerializeToElement(new { module_id = 1405 }), CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Contains("浏览权限", result.ContentForModel);
    }

    [Fact]
    public async Task DefinitionOnly_ListsSteps_WhenNoKeys()
    {
        var tool = CreateTool(flow: Flow());

        var result = await tool.ExecuteAsync("u1",
            JsonSerializer.SerializeToElement(new { module_id = 1405 }), CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Contains("共 2 步", result.ContentForModel);
        Assert.Contains("admin", result.ContentForModel);
    }

    [Fact]
    public async Task InProgress_MyTask_AllowsApprove()
    {
        var tool = CreateTool(flow: Flow(),
            instance: new(7, "0", "admin", "002", "二级审批", ["admin"], 3));

        var result = await tool.ExecuteAsync("admin",
            JsonSerializer.SerializeToElement(new { module_id = 1405, _keys = new[] { "DD", "26080001" } }),
            CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Contains("流转：在途", result.ContentForModel);
        Assert.Contains("审批：可", result.ContentForModel);
        Assert.Contains("编辑：否", result.ContentForModel);
        Assert.Contains("--引用--> 本模块", result.ContentForModel);
    }

    [Fact]
    public async Task Edges_ToUnbrowsableTarget_AreHidden()
    {
        var gateway = new FakeSearchGateway(new(
            1604, "厂商报价单", "PUR_QUOTE_M", null,
            MasterFields: [], DetailFields: [], DefaultSort: null, HasAdd: true, HasEdit: true,
            DetailNoSave: false, MasterPkOrder: ["QUOTE_TYPE", "QUOTE_NO"], DetailNoFields: string.Empty,
            HasWorkflow: true));
        var tool = new GetModuleFlowTool(gateway,
            new FakeFlowGateway(null, null, new(true, false, false)),
            new PerModulePermissions(new Dictionary<int, bool> { [1602] = true }));

        var result = await tool.ExecuteAsync("u1",
            JsonSerializer.SerializeToElement(new { module_id = 1604, _keys = new[] { "QT", "1" } }),
            CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Contains("上下游：已核对的关系边中暂无本模块", result.ContentForModel);
        Assert.DoesNotContain("1602", result.ContentForModel);
    }

    [Fact]
    public async Task Edges_ToBrowsableTarget_AreShown_WithApprovalConfirm()
    {
        var gateway = new FakeSearchGateway(new(
            1604, "厂商报价单", "PUR_QUOTE_M", null,
            MasterFields: [], DetailFields: [], DefaultSort: null, HasAdd: true, HasEdit: true,
            DetailNoSave: false, MasterPkOrder: ["QUOTE_TYPE", "QUOTE_NO"], DetailNoFields: string.Empty,
            HasWorkflow: true));
        var tool = new GetModuleFlowTool(gateway,
            new FakeFlowGateway(null, null, new(true, true, false),
                new("厂商报价单 流程审批完成", "admin")),
            new PerModulePermissions(new Dictionary<int, bool>()));

        var result = await tool.ExecuteAsync("u1",
            JsonSerializer.SerializeToElement(new { module_id = 1604, _keys = new[] { "QT", "1" } }),
            CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Contains("--批核--> #1602", result.ContentForModel);
        Assert.Contains("终审确认：厂商报价单 流程审批完成（admin）", result.ContentForModel);
    }

    [Fact]
    public async Task InProgress_NotMyTask_DeniesApprove_ButStarterCanWithdraw()
    {
        var tool = CreateTool(flow: Flow(),
            instance: new(7, "0", "admin", "002", "二级审批", ["approver01"], 3));

        var result = await tool.ExecuteAsync("admin",
            JsonSerializer.SerializeToElement(new { module_id = 1405, _keys = new[] { "DD", "26080001" } }),
            CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Contains("审批：否", result.ContentForModel);
        Assert.Contains("撤回：可", result.ContentForModel);
    }

    [Fact]
    public async Task KeysLengthMismatch_IsDenied()
    {
        var tool = CreateTool(flow: Flow());

        var result = await tool.ExecuteAsync("u1",
            JsonSerializer.SerializeToElement(new { module_id = 1405, _keys = new[] { "DD" } }),
            CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Contains("主键长度不符", result.ContentForModel);
    }

    [Fact]
    public void EvaluateActions_Finished_OnlyUnfinish()
    {
        var actions = GetModuleFlowTool.EvaluateActions(new GetModuleFlowTool.FlowActionInput(
            true, true, true, true, true, "u1", false, null, false, false, true, true));

        Assert.Contains(actions, action => action.Action == "取消结案" && action.Allowed);
        Assert.Contains(actions, action => action.Action == "批核" && !action.Allowed);
        Assert.Contains(actions, action => action.Action == "删除" && !action.Allowed);
    }

    [Fact]
    public void EvaluateActions_DirectApprove_WhenNoFlow()
    {
        var actions = GetModuleFlowTool.EvaluateActions(new GetModuleFlowTool.FlowActionInput(
            true, true, true, true, false, "u1", false, null, false, false, false, false));

        Assert.Contains(actions, action => action.Action == "批核" && action.Allowed);
        Assert.Contains(actions, action => action.Action == "结案" && action.Allowed);
    }
}
