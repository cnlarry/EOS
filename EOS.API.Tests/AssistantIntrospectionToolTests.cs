using System.Text.Json;
using EOS.API.Data;
using EOS.API.Features.Assistant.Tools;
using EOS.API.Models;
using EOS.API.Security;
using Xunit;

namespace EOS.API.Tests;

public sealed class AssistantIntrospectionToolTests
{
    private sealed class FakePermissions(IReadOnlyDictionary<int, ModulePermission> permissions) : IPermissionService
    {
        public Task<ModulePermission> GetAsync(string userId, int moduleId, CancellationToken cancellationToken) =>
            Task.FromResult(permissions.TryGetValue(moduleId, out var permission)
                ? permission
                : new ModulePermission(Rights(false)));

        public Task<ModulePermission> RequireAsync(string userId, int moduleId, PermissionAction action, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class FakeGateway(
        IReadOnlyList<SystemKnowledgeModule> modules,
        IReadOnlyDictionary<int, WorkbenchDefinition?> definitions) : IWorkbenchSearchGateway
    {
        public Task<IReadOnlyList<SystemKnowledgeModule>> ListAssistantModulesAsync(string? keyword, CancellationToken token) =>
            Task.FromResult<IReadOnlyList<SystemKnowledgeModule>>(modules
                .Where(m => string.IsNullOrWhiteSpace(keyword) || m.Title.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                .ToArray());

        public Task<int?> FindGenericModuleIdByTitleAsync(string titleKeyword, CancellationToken token) =>
            Task.FromResult<int?>(modules.FirstOrDefault(m => m.Title.Contains(titleKeyword, StringComparison.OrdinalIgnoreCase))?.Id);

        public IReadOnlySet<string>? LastDeniedMaster { get; private set; }
        public bool LastCanViewCost { get; private set; } = true;
        public bool LastCanViewSecrecy { get; private set; } = true;

        public Task<WorkbenchDefinition?> GetDefinitionAsync(int moduleId, string userId, string? execTag,
            bool canViewCost, bool canViewSecrecy, IReadOnlySet<string> deniedMasterFields,
            IReadOnlySet<string> deniedDetailFields, CancellationToken token)
        {
            LastDeniedMaster = deniedMasterFields;
            LastCanViewCost = canViewCost;
            LastCanViewSecrecy = canViewSecrecy;
            return Task.FromResult(definitions.TryGetValue(moduleId, out var definition) ? definition : null);
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

    private static ModulePermission Permission(bool canBrowse) => new(Rights(canBrowse));

    private static ModuleRights Rights(bool canBrowse) => new(
        CanBrowse: canBrowse, CanViewCost: false, CanViewSecrecy: false, CanSetup: false,
        DeniedMasterFields: new HashSet<string>(), DeniedDetailFields: new HashSet<string>(),
        CanAddNew: false, CanEdit: false, CanDelete: false, CanApprove: false, CanDeapprove: false,
        CanEndCase: false, CanUnEndCase: false, CanFileView: false, CanFileUpda: false,
        CanFileEdit: false, CanFileDele: false, DenyNewMasterFields: new HashSet<string>(),
        DenyNewDetailFields: new HashSet<string>(), DenyModiMasterFields: new HashSet<string>(),
        DenyModiDetailFields: new HashSet<string>(), DataFilter: string.Empty, ExecuteTag: "A");

    private static WorkbenchDefinition Definition(int moduleId, string title) => new(
        moduleId, title, "MASTER_TABLE", "DETAIL_TABLE",
        [new WorkbenchField("DOC_NO", "单号", "nvarchar", 100, null, true)],
        [new WorkbenchField("QTY", "数量", "decimal", 80, null, false)],
        null, false, false, false, ["DOC_NO"], string.Empty, false);

    [Fact]
    public async Task ListModules_ReturnsOnlyBrowsableWorkbenchModules()
    {
        var gateway = new FakeGateway(
            [new(100, "可见订单"), new(200, "不可见订单")],
            new Dictionary<int, WorkbenchDefinition?>());
        var permissions = new FakePermissions(new Dictionary<int, ModulePermission>
        {
            [100] = Permission(true),
            [200] = Permission(false),
        });

        var result = await new ListModulesTool(gateway, permissions).ExecuteAsync("u1", JsonDocument.Parse("{}").RootElement, CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Contains("100 可见订单", result.ContentForModel);
        Assert.DoesNotContain("不可见订单", result.ContentForModel);
    }

    [Fact]
    public async Task DescribeModule_UsesPermissionFilteredDefinition()
    {
        var gateway = new FakeGateway(
            [new(100, "客户订单")],
            new Dictionary<int, WorkbenchDefinition?> { [100] = Definition(100, "客户订单") });
        var permissions = new FakePermissions(new Dictionary<int, ModulePermission> { [100] = Permission(true) });

        var result = await new DescribeModuleTool(gateway, permissions).ExecuteAsync(
            "u1", JsonSerializer.SerializeToElement(new { module_id = 100 }), CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Contains("主表：MASTER_TABLE", result.ContentForModel);
        Assert.Contains("DOC_NO 单号", result.ContentForModel);
        Assert.Contains("QTY 数量", result.ContentForModel);
    }

    [Fact]
    public async Task ListModules_FiltersByKeyword()
    {
        var gateway = new FakeGateway(
            [new(100, "客户订单"), new(101, "采购单")],
            new Dictionary<int, WorkbenchDefinition?>());
        var permissions = new FakePermissions(new Dictionary<int, ModulePermission>
        {
            [100] = Permission(true),
            [101] = Permission(true),
        });

        var result = await new ListModulesTool(gateway, permissions).ExecuteAsync("u1",
            JsonSerializer.SerializeToElement(new { keyword = "采购" }), CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Contains("101 采购单", result.ContentForModel);
        Assert.DoesNotContain("客户订单", result.ContentForModel);
    }

    [Fact]
    public async Task DescribeModule_DeniesNonWorkbenchModule()
    {
        var gateway = new FakeGateway(
            [new(2306, "用户权限")],
            new Dictionary<int, WorkbenchDefinition?>());
        var permissions = new FakePermissions(new Dictionary<int, ModulePermission>
        {
            [2306] = Permission(true),
        });

        var result = await new DescribeModuleTool(gateway, permissions).ExecuteAsync(
            "u1", JsonSerializer.SerializeToElement(new { module_id = 2306 }), CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Contains("通用工作台", result.ContentForModel);
    }

    [Fact]
    public async Task DescribeModule_PassesScopeToDefinition()
    {
        var gateway = new FakeGateway(
            [new(100, "客户订单")],
            new Dictionary<int, WorkbenchDefinition?> { [100] = Definition(100, "客户订单") });
        var rights = Rights(true) with
        {
            CanViewCost = false,
            CanViewSecrecy = false,
            DeniedMasterFields = new HashSet<string> { "SECRET_COL" },
        };
        var permissions = new FakePermissions(new Dictionary<int, ModulePermission>
        {
            [100] = new ModulePermission(rights),
        });

        var result = await new DescribeModuleTool(gateway, permissions).ExecuteAsync(
            "u1", JsonSerializer.SerializeToElement(new { module_id = 100 }), CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Contains("SECRET_COL", gateway.LastDeniedMaster!);
        Assert.False(gateway.LastCanViewCost);
        Assert.False(gateway.LastCanViewSecrecy);
        Assert.DoesNotContain("SECRET_COL", result.ContentForModel.Split("主表字段")[0]);
    }

    [Fact]
    public async Task DescribeModule_DeniesWithoutBrowsePermission()
    {
        var gateway = new FakeGateway([new(100, "客户订单")], new Dictionary<int, WorkbenchDefinition?>
        {
            [100] = Definition(100, "客户订单"),
        });
        var permissions = new FakePermissions(new Dictionary<int, ModulePermission> { [100] = Permission(false) });

        var result = await new DescribeModuleTool(gateway, permissions).ExecuteAsync(
            "u1", JsonSerializer.SerializeToElement(new { module_id = 100 }), CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Contains("浏览权限", result.ContentForModel);
    }
}
