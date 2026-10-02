using System.Text.Json;
using EOS.API.Features.Assistant;
using EOS.API.Features.Assistant.ModelAccess;
using EOS.API.Features.Assistant.Parameters;
using EOS.API.Features.Assistant.Tools;
using EOS.API.Models;
using EOS.API.Security;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// get_record_history 的**门禁、定位与输出**（离线，走假网关）：
/// 无浏览权一律拒绝；记录不在数据范围内按防探测口径回答；主键可由页面处境推断；
/// 历史为空时如实说"无"；超出上限要截断并说清还有多少条没列。
///
/// <para>
/// 真实读取（工作流引擎的历史、审计的摘要素）不在离线用例里重复验证——这里只钉住**工具自己**
/// 会做错的那部分：权限门、可见性校验、主键口径与输出口径。
/// </para>
/// </summary>
public sealed class AssistantRecordHistoryToolTests
{
    private const int ModuleId = 1204;
    private const string Key = "BOM-1";

    private static ModuleRights Rights(bool canBrowse = true, string dataFilter = "") => new(
        CanBrowse: canBrowse, CanViewCost: false, CanViewSecrecy: false, CanSetup: false,
        DeniedMasterFields: new HashSet<string>(), DeniedDetailFields: new HashSet<string>(),
        CanAddNew: false, CanEdit: false, CanDelete: false, CanApprove: false, CanDeapprove: false,
        CanEndCase: false, CanUnEndCase: false, CanFileView: false, CanFileUpda: false,
        CanFileEdit: false, CanFileDele: false,
        DenyNewMasterFields: new HashSet<string>(), DenyNewDetailFields: new HashSet<string>(),
        DenyModiMasterFields: new HashSet<string>(), DenyModiDetailFields: new HashSet<string>(),
        DataFilter: dataFilter, ExecuteTag: "A");

    private static WorkbenchDefinition Definition(params string[] pkOrder) => new(
        ModuleId, "产品BOM表", "BOM_STRU_M", null, [], [], null,
        HasAdd: false, HasEdit: false, DetailNoSave: false,
        MasterPkOrder: pkOrder.Length > 0 ? pkOrder : [Key],
        DetailNoFields: string.Empty, HasWorkflow: false);

    private static JsonElement Args(string json) => JsonDocument.Parse(json).RootElement.Clone();

    /// <summary>拼工具参数 JSON（用拼接而不是原始插值字符串：花括号在其中的歧义会把测试自己绊倒）。</summary>
    private static JsonElement ArgsFor(int moduleId, string? key = null) =>
        Args(key is null
            ? "{\"module_id\":" + moduleId + "}"
            : "{\"module_id\":" + moduleId + ",\"_keys\":[\"" + key + "\"]}");

    private static AssistantPolicyValues Policy(AssistantToolLimitsOptions limits) =>
        AssistantPolicyValues.Default with { ToolLimits = limits };

    private static RecordHistoryTool Tool(
        FakePermissions permissions, FakeWorkbenchGateway gateway, FakeHistoryGateway history,
        AssistantToolLimitsOptions? limits = null) =>
        new(new AssistantRecordLocator(gateway, permissions), history,
            AssistantTestRuntime.Fixed(new AssistantSettings(), Policy(limits ?? new AssistantToolLimitsOptions())));

    [Fact]
    public async Task Denies_When_Module_Not_Browsable()
    {
        var permissions = new FakePermissions(Rights(canBrowse: false));
        var result = await Tool(permissions, new FakeWorkbenchGateway(), new FakeHistoryGateway())
            .ExecuteAsync("u1", ArgsFor(ModuleId, Key), CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Contains("没有模块", result.ContentForModel, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Answers_Probe_Safe_When_The_Record_Is_Out_Of_Scope()
    {
        // 记录不在数据范围内：沿用防探测口径（不区分"不存在"与"不在范围内"），助手侧不得改口
        var gateway = new FakeWorkbenchGateway { VisibleRows = [] };
        var result = await Tool(new FakePermissions(Rights()), gateway, new FakeHistoryGateway())
            .ExecuteAsync("u1", ArgsFor(ModuleId, Key), CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal(AssistantToolExtensions.NotFoundMessage, result.ContentForModel);
    }

    [Fact]
    public async Task Infers_The_Key_From_The_Page_And_Renders_Both_Sections()
    {
        var history = new FakeHistoryGateway
        {
            Approvals =
            [
                new RecordApprovalEntry("task", "1", "编制", "张三", "Y", "同意", "2026-09-01 10:00:00"),
            ],
            Activities =
            [
                new RecordActivityEntry("2026-10-01 09:12:00", "UPDATE", true, "admin", null, "修改产品BOM表"),
                new RecordActivityEntry("2026-09-30 17:02:00", "APPROVE", false, "admin", "NO_APPROVE_RIGHT", "批核被拒"),
            ],
        };
        var tool = Tool(new FakePermissions(Rights()), new FakeWorkbenchGateway(), history);
        tool.UsePageContext(new PageContext(ModuleId, "产品BOM表", "view", Key));

        var result = await tool.ExecuteAsync("u1", ArgsFor(ModuleId), CancellationToken.None);

        Assert.True(result.Ok);
        // 用户不必报主键：页面开着的这张单就是目标
        Assert.Equal([Key], history.LastKeys);
        Assert.Contains("审批历史（1 条）", result.ContentForModel, StringComparison.Ordinal);
        Assert.Contains("编制 · 张三", result.ContentForModel, StringComparison.Ordinal);
        Assert.Contains("同意", result.ContentForModel, StringComparison.Ordinal);
        Assert.Contains("最近操作", result.ContentForModel, StringComparison.Ordinal);
        Assert.Contains("失败 APPROVE", result.ContentForModel, StringComparison.Ordinal);
        Assert.Contains("NO_APPROVE_RIGHT", result.ContentForModel, StringComparison.Ordinal);
        // 只给摘要素：这句话必须出现，否则用户会以为"没有明细＝没有改动"
        Assert.Contains("不含字段级明细与明细原文", result.ContentForModel, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Says_None_Instead_Of_Inventing_A_Reason()
    {
        var result = await Tool(new FakePermissions(Rights()), new FakeWorkbenchGateway(), new FakeHistoryGateway())
            .ExecuteAsync("u1", ArgsFor(ModuleId, Key), CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Contains("审批历史（0 条）", result.ContentForModel, StringComparison.Ordinal);
        // 没走过审批不等于"审批被卡住"：如实说"无"，不编原因
        Assert.Contains("该模块可能未启用流程，或这张单还没走过审批", result.ContentForModel, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Truncates_When_Over_The_Limit()
    {
        var history = new FakeHistoryGateway
        {
            Approvals =
            [
                new RecordApprovalEntry("task", "1", "编制", "张三", "Y", null, "2026-09-01 10:00:00"),
                new RecordApprovalEntry("task", "2", "审核", "李四", "Y", null, "2026-09-02 10:00:00"),
            ],
        };
        var tool = Tool(new FakePermissions(Rights()), new FakeWorkbenchGateway(), history,
            new AssistantToolLimitsOptions { RecordHistoryMax = 1, RecordActivityDays = 30 });

        var result = await tool.ExecuteAsync(
            "u1", ArgsFor(ModuleId, Key), CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Contains("另有 1 条未列出", result.ContentForModel, StringComparison.Ordinal);
        // 时间窗来自参数，不是写死的数字
        Assert.Equal(30, history.LastDays);
    }

    private sealed class FakePermissions(ModuleRights rights) : IPermissionService
    {
        public Task<ModulePermission> GetAsync(string userId, int moduleId, CancellationToken cancellationToken) =>
            Task.FromResult(new ModulePermission(rights));

        public Task<ModulePermission> RequireAsync(string userId, int moduleId, PermissionAction action, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class FakeWorkbenchGateway : IWorkbenchSearchGateway
    {
        /// <summary>"按主键取行"的结果：空 = 记录不可见（防探测口径就靠它触发）。</summary>
        public IReadOnlyList<Dictionary<string, object?>> VisibleRows { get; init; } =
            [new Dictionary<string, object?> { ["BOM_NO"] = Key }];

        public Task<IReadOnlyList<SystemKnowledgeModule>> ListAssistantModulesAsync(string? keyword, CancellationToken token) =>
            Task.FromResult<IReadOnlyList<SystemKnowledgeModule>>([]);

        public Task<int?> FindGenericModuleIdByTitleAsync(string titleKeyword, CancellationToken token) =>
            Task.FromResult<int?>(ModuleId);

        public Task<WorkbenchDefinition?> GetDefinitionAsync(
            int moduleId, string userId, string? execTag, bool canViewCost, bool canViewSecrecy,
            IReadOnlySet<string> deniedMasterFields, IReadOnlySet<string> deniedDetailFields,
            CancellationToken token) => Task.FromResult<WorkbenchDefinition?>(Definition());

        public Task<WorkbenchData> GetRowsAsync(
            WorkbenchDefinition definition, bool detail, IReadOnlyDictionary<string, string> keys,
            int page, int pageSize, CancellationToken token, WorkbenchQuery? query = null,
            string? keyword = null, string? sortField = null, string? sortDirection = null,
            int? groupIndex = null, string? groupValue = null, string? dataFilter = null) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<Dictionary<string, object?>>> GetExportRowsByKeysAsync(
            WorkbenchDefinition definition, IReadOnlyList<IReadOnlyList<string>> keys, CancellationToken token,
            int? groupIndex = null, string? groupValue = null,
            IReadOnlyList<WorkbenchField>? exportFields = null, string? dataFilter = null) =>
            Task.FromResult(VisibleRows);

        public Task<FormDefinition?> GetFormDefinitionAsync(
            WorkbenchDefinition definition, string userId, string mode,
            bool canViewCost, bool canViewSecrecy,
            IReadOnlySet<string> deniedMasterFields, IReadOnlySet<string> deniedDetailFields,
            IReadOnlySet<string> deniedNewMasterFields, IReadOnlySet<string> deniedNewDetailFields,
            IReadOnlySet<string> deniedModiMasterFields, IReadOnlySet<string> deniedModiDetailFields,
            CancellationToken token,
            bool canAddNew = false, bool canEdit = false, bool canDelete = false,
            bool canApprove = false, bool canDeapprove = false,
            bool canEndCase = false, bool canUnEndCase = false,
            bool canFileView = false, bool canFileUpda = false,
            bool canFileEdit = false, bool canFileDele = false, bool canSetup = false) =>
            throw new NotSupportedException();
    }

    private sealed class FakeHistoryGateway : IRecordHistoryGateway
    {
        public IReadOnlyList<RecordApprovalEntry> Approvals { get; init; } = [];

        public IReadOnlyList<RecordActivityEntry> Activities { get; init; } = [];

        public IReadOnlyList<string>? LastKeys { get; private set; }

        public int? LastDays { get; private set; }

        public Task<IReadOnlyList<RecordApprovalEntry>> GetApprovalHistoryAsync(
            int moduleId, IReadOnlyList<string> pkColumns, IReadOnlyList<string> keys, CancellationToken token)
        {
            LastKeys = keys;
            return Task.FromResult(Approvals);
        }

        public Task<IReadOnlyList<RecordActivityEntry>> GetRecentActivityAsync(
            int moduleId, IReadOnlyList<string> keys, int max, int days, CancellationToken token)
        {
            LastDays = days;
            return Task.FromResult(Activities);
        }
    }
}
