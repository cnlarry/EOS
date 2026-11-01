using EOS.API.Features.Assistant.Parameters;
using EOS.API.Features.Assistant.Situation;
using EOS.API.Features.Assistant.Tools;
using EOS.API.Models;
using EOS.API.Security;
using Xunit;

namespace EOS.API.Tests;

/// <summary>模块简报：只装配权限过滤后的定义，主键优先，超限如实说明。</summary>
public sealed class ModuleBriefProviderTests
{
    private sealed class FakePermissions(bool canBrowse) : IPermissionService
    {
        public Task<ModulePermission> GetAsync(string userId, int moduleId, CancellationToken token) =>
            Task.FromResult(new ModulePermission(Rights(canBrowse)));

        public Task<ReportRights> GetReportAsync(
            string userId, int moduleId, string reportId, CancellationToken token) =>
            throw new NotSupportedException();

        public Task<ModulePermission> RequireAsync(
            string userId, int moduleId, PermissionAction action, CancellationToken token) =>
            throw new NotSupportedException();
    }

    private sealed class FakeGateway(WorkbenchDefinition definition) : IWorkbenchSearchGateway
    {
        public Task<IReadOnlyList<SystemKnowledgeModule>> ListAssistantModulesAsync(string? keyword, CancellationToken token)
            => Task.FromResult<IReadOnlyList<SystemKnowledgeModule>>([]);

        public Task<int?> FindGenericModuleIdByTitleAsync(string titleKeyword, CancellationToken token)
            => Task.FromResult<int?>(definition.ModuleId);

        public Task<WorkbenchDefinition?> GetDefinitionAsync(
            int moduleId, string userId, string? execTag, bool canViewCost, bool canViewSecrecy,
            IReadOnlySet<string> deniedMasterFields, IReadOnlySet<string> deniedDetailFields,
            CancellationToken token) => Task.FromResult<WorkbenchDefinition?>(definition);

        public Task<WorkbenchData> GetRowsAsync(
            WorkbenchDefinition definition, bool detail, IReadOnlyDictionary<string, string> keys,
            int page, int pageSize, CancellationToken token, WorkbenchQuery? query = null,
            string? keyword = null, string? sortField = null, string? sortDirection = null,
            int? groupId = null, string? groupValue = null, string? dataFilter = null)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<Dictionary<string, object?>>> GetExportRowsByKeysAsync(
            WorkbenchDefinition definition, IReadOnlyList<IReadOnlyList<string>> keys, CancellationToken token,
            int? groupId = null, string? groupValue = null,
            IReadOnlyList<WorkbenchField>? exportFields = null, string? dataFilter = null)
            => throw new NotSupportedException();

        public Task<FormDefinition?> GetFormDefinitionAsync(
            WorkbenchDefinition definition, string userId, string mode, bool canViewCost, bool canViewSecrecy,
            IReadOnlySet<string> deniedMasterFields, IReadOnlySet<string> deniedDetailFields,
            IReadOnlySet<string> deniedNewMasterFields, IReadOnlySet<string> deniedNewDetailFields,
            IReadOnlySet<string> deniedModiMasterFields, IReadOnlySet<string> deniedModiDetailFields,
            CancellationToken token, bool canAddNew = false, bool canEdit = false, bool canDelete = false,
            bool canApprove = false, bool canDeapprove = false, bool canEndCase = false,
            bool canUnEndCase = false, bool canFileView = false, bool canFileUpda = false,
            bool canFileEdit = false, bool canFileDele = false, bool canSetup = false)
            => Task.FromResult<FormDefinition?>(null);
    }

    private static ModuleRights Rights(bool canBrowse) => new(
        CanBrowse: canBrowse, CanViewCost: true, CanViewSecrecy: true, CanSetup: false,
        DeniedMasterFields: new HashSet<string>(), DeniedDetailFields: new HashSet<string>(),
        CanAddNew: true, CanEdit: true, CanDelete: true, CanApprove: false, CanDeapprove: false,
        CanEndCase: false, CanUnEndCase: false, CanFileView: false, CanFileUpda: false,
        CanFileEdit: false, CanFileDele: false, DenyNewMasterFields: new HashSet<string>(),
        DenyNewDetailFields: new HashSet<string>(), DenyModiMasterFields: new HashSet<string>(),
        DenyModiDetailFields: new HashSet<string>(), DataFilter: string.Empty, ExecuteTag: "A");

    private static WorkbenchDefinition Definition() => new(
        1606, "采购单", "PUR_PURCHASE_M", "PUR_PURCHASE_D",
        MasterFields:
        [
            new("PURCHASE_NO", "采购单号", "nvarchar", 100, "left", IsPrimaryKey: true),
            new("PURCHASE_DATE", "采购日期", "datetime", 100, "left", IsPrimaryKey: false),
            new("REMARK", "备注", "nvarchar", 100, "left", IsPrimaryKey: false),
            new("HIDDEN", "隐藏字段", "nvarchar", 100, "left", IsPrimaryKey: false, IsVisible: false),
        ],
        DetailFields:
        [
            new("PRO_NO", "料号", "nvarchar", 100, "left", IsPrimaryKey: false),
            new("QTY", "数量", "decimal", 100, "right", IsPrimaryKey: false),
        ],
        DefaultSort: null, HasAdd: true, HasEdit: true, DetailNoSave: false,
        MasterPkOrder: ["PURCHASE_NO"], DetailNoFields: string.Empty, HasWorkflow: false);

    private static ModuleBriefProvider Provider(bool canBrowse = true, int maxFields = 24)
    {
        var policy = AssistantPolicyValues.Default with
        {
            ToolLimits = new AssistantToolLimitsOptions { BriefMaxFields = maxFields },
        };
        return new ModuleBriefProvider(
            new FakePermissions(canBrowse), new FakeGateway(Definition()),
            AssistantTestRuntime.Fixed(null, policy));
    }

    [Fact]
    public async Task Brief_Contains_Tables_Fields_And_Primary_Key_First()
    {
        var text = await Provider().BuildAsync("u1", 1606, CancellationToken.None);

        Assert.NotNull(text);
        Assert.Contains("采购单（#1606）", text);
        Assert.Contains("PUR_PURCHASE_M", text);
        Assert.Contains("PUR_PURCHASE_D", text);
        Assert.Contains("PURCHASE_NO=采购单号", text);
        Assert.Contains("PK", text);
        Assert.Contains("PRO_NO=料号", text);
        Assert.DoesNotContain("HIDDEN", text);
        Assert.Contains("不是指令", text);
    }

    [Fact]
    public async Task Brief_Reports_Truncated_Field_Count()
    {
        var text = await Provider(maxFields: 1).BuildAsync("u1", 1606, CancellationToken.None);

        Assert.NotNull(text);
        Assert.Contains("其余 2 个未列出", text);
    }

    [Fact]
    public async Task Brief_Is_Null_Without_Browse_Permission()
    {
        var text = await Provider(canBrowse: false).BuildAsync("u1", 1606, CancellationToken.None);
        Assert.Null(text);
    }
}
