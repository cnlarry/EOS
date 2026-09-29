using EOS.API.Data;
using EOS.API.Data.Workbench;
using EOS.API.Features.Assistant.Actions;
using EOS.API.Features.Assistant.Governance;
using EOS.API.Features.Assistant.Tools;
using EOS.API.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 批核族「操作请求卡」的**只读**预判：逐行给出可执行 / 不可执行及原因。
///
/// <para>
/// 用例不连库：定义与权限用固定替身，行数据由替身网关给出，因此可以精确构造
/// "已结案 / 未批核 / 不在数据范围" 这几种逐行状态。审计走 best-effort，连不上库不影响判定。
/// </para>
/// <para>
/// 判别性：把逐行判定改成"整卡一个结论"、或让模块级拒绝被吞掉，本用例必须变红。
/// </para>
/// </summary>
public sealed class ApprovalRequestPrejudgeTests
{
    private const int ModuleId = 1403;

    /// <summary>指向一个必然连不上的端口：审计是 best-effort，写不进去不该影响预判结论。</summary>
    private const string UnreachableConnection =
        "Server=127.0.0.1,1;Database=EOS.ERP;User Id=zz;Password=zz;Connect Timeout=1;TrustServerCertificate=true";

    [Fact]
    public async Task 未批核的单据可以批核()
    {
        var (service, gateway) = Build();
        gateway.Rows.Add(Row("ZZ-1", confirmed: 0, finished: 0));

        var preview = await service.PreviewAsync(
            "u1", ModuleId, AssistantApprovalAction.Approve, [["ZZ-1"]], CancellationToken.None);

        Assert.Null(preview.ModuleDenialCode);
        var row = Assert.Single(preview.Rows);
        Assert.True(row.Allowed);
        Assert.Equal("未批核", row.Status);
        Assert.Null(row.DenialCode);
    }

    [Fact]
    public async Task 已结案的单据批核被拒且给出原因码与文案()
    {
        var (service, gateway) = Build();
        gateway.Rows.Add(Row("ZZ-2", confirmed: 1, finished: 1));

        var preview = await service.PreviewAsync(
            "u1", ModuleId, AssistantApprovalAction.Approve, [["ZZ-2"]], CancellationToken.None);

        var row = Assert.Single(preview.Rows);
        Assert.False(row.Allowed);
        Assert.Equal("已结案", row.Status);
        Assert.Equal(LifecycleEditGuards.FinishedCode, row.DenialCode);
        Assert.False(string.IsNullOrWhiteSpace(row.DenialMessage));
    }

    [Fact]
    public async Task 单据未结案时取消结案不适用()
    {
        var (service, gateway) = Build();
        gateway.Rows.Add(Row("ZZ-3", confirmed: 0, finished: 0));

        var preview = await service.PreviewAsync(
            "u1", ModuleId, AssistantApprovalAction.UnEndCase, [["ZZ-3"]], CancellationToken.None);

        var row = Assert.Single(preview.Rows);
        Assert.False(row.Allowed);
        Assert.Equal(AssistantApprovalRequestService.NotApplicableCode, row.DenialCode);
    }

    [Fact]
    public async Task 缺动作位时模块级拒绝并明说原因()
    {
        // 有浏览权限但没有批核动作位：策略层抛出，助手侧必须折成"你没有批核权限"而不是"操作失败"。
        var (service, _) = Build(AssistantActionTestHarness.Rights(approve: false));

        var preview = await service.PreviewAsync(
            "u1", ModuleId, AssistantApprovalAction.Approve, [["ZZ-9"]], CancellationToken.None);

        Assert.Equal("NO_APPROVE_RIGHT", preview.ModuleDenialCode);
        Assert.Equal("你没有「批核」权限。", preview.ModuleDenialMessage);
        Assert.Empty(preview.Rows);
    }

    [Fact]
    public async Task 模块没有可写页面时整卡拒绝()
    {
        var (service, _) = Build(writable: false);

        var preview = await service.PreviewAsync(
            "u1", ModuleId, AssistantApprovalAction.Approve, [["ZZ-9"]], CancellationToken.None);

        Assert.Equal(WorkbenchDenialCodes.NoWritablePage, preview.ModuleDenialCode);
        Assert.Empty(preview.Rows);
    }

    [Fact]
    public async Task 数据范围外的行按防探测口径拒答()
    {
        // 替身网关只返回 ZZ-1：ZZ-404 既可能不存在，也可能不在你的数据范围内——两者同一条文案。
        var (service, gateway) = Build();
        gateway.Rows.Add(Row("ZZ-1", confirmed: 0, finished: 0));

        var preview = await service.PreviewAsync(
            "u1", ModuleId, AssistantApprovalAction.Approve, [["ZZ-1"], ["ZZ-404"]], CancellationToken.None);

        Assert.Equal(2, preview.Rows.Count);
        Assert.True(preview.Rows[0].Allowed);
        Assert.False(preview.Rows[1].Allowed);
        Assert.Equal(AssistantApprovalRequestService.OutOfScopeCode, preview.Rows[1].DenialCode);
        Assert.Equal(AssistantToolExtensions.NotFoundMessage, preview.Rows[1].DenialMessage);
    }

    [Fact]
    public async Task 超过请求卡上限即整卡拒绝()
    {
        var (service, gateway) = Build(limits: new AssistantActionLimitsOptions { MaxApprovalRequestRecords = 2 });
        gateway.Rows.Add(Row("ZZ-1", confirmed: 0, finished: 0));

        var preview = await service.PreviewAsync(
            "u1", ModuleId, AssistantApprovalAction.Approve, [["ZZ-1"], ["ZZ-2"], ["ZZ-3"]], CancellationToken.None);

        Assert.Equal("TOO_MANY_RECORDS", preview.ModuleDenialCode);
        Assert.Empty(preview.Rows);
    }

    [Fact]
    public async Task 空行即整卡拒绝()
    {
        var (service, _) = Build();

        var preview = await service.PreviewAsync(
            "u1", ModuleId, AssistantApprovalAction.Approve, [], CancellationToken.None);

        Assert.Equal("INVALID_ARGUMENTS", preview.ModuleDenialCode);
    }

    [Fact]
    public async Task 确认只留痕不执行并返回确认行数()
    {
        var (service, _) = Build();

        var confirmed = await service.ConfirmAsync(
            "u1", ModuleId, AssistantApprovalAction.Approve, [["ZZ-1"], ["ZZ-2"]], CancellationToken.None);

        // 这条路径不做任何处置：它只返回"确认了几行"，处置由界面直接调既有端点完成。
        Assert.Equal(2, confirmed);
    }

    private static (AssistantApprovalRequestService Service, RoughGateway Gateway) Build(
        ModuleRights? rights = null,
        AssistantActionLimitsOptions? limits = null,
        bool writable = true)
    {
        var definition = AssistantSituationDoubles.Definition(
            ModuleId, "测试单据", ["DOC_NO", "CONFIRM_TAG", "FINISHED_TAG"], pkOrder: ["DOC_NO"]);
        var permissions = new AssistantActionTestHarness.FixedPermissions(
            rights ?? AssistantActionTestHarness.Rights(approve: true, deapprove: true, endCase: true, unEndCase: true));
        var policy = AssistantActionTestHarness.Policy(
            new AssistantActionTestHarness.FixedDefinitionSource(definition, null),
            permissions,
            writable ? [ModuleId] : []);

        var gateway = new RoughGateway();
        var connections = AssistantActionTestHarness.Connections(UnreachableConnection);
        var agent = AssistantActionTestHarness.AgentContext();
        var audit = AssistantActionTestHarness.AuditWriter(connections, agent);
        var service = new AssistantApprovalRequestService(
            policy, gateway, audit, agent,
            Options.Create(limits ?? new AssistantActionLimitsOptions()),
            NullLogger<AssistantApprovalRequestService>.Instance);
        return (service, gateway);
    }

    private static Dictionary<string, object?> Row(string docNo, int confirmed, int finished) => new()
    {
        ["DOC_NO"] = docNo,
        ["CONFIRM_TAG"] = confirmed,
        ["FINISHED_TAG"] = finished,
    };

    /// <summary>只提供本用例需要的行为：按主键取行，其余成员给空实现。</summary>
    private sealed class RoughGateway : IWorkbenchSearchGateway
    {
        public List<Dictionary<string, object?>> Rows { get; } = [];

        public Task<IReadOnlyList<SystemKnowledgeModule>> ListAssistantModulesAsync(
            string? keyword, CancellationToken token)
            => Task.FromResult<IReadOnlyList<SystemKnowledgeModule>>([]);

        public Task<int?> FindGenericModuleIdByTitleAsync(string titleKeyword, CancellationToken token)
            => Task.FromResult<int?>(null);

        public Task<WorkbenchDefinition?> GetDefinitionAsync(
            int moduleId, string userId, string? execTag, bool canViewCost, bool canViewSecrecy,
            IReadOnlySet<string> deniedMasterFields, IReadOnlySet<string> deniedDetailFields,
            CancellationToken token)
            => Task.FromResult<WorkbenchDefinition?>(null);

        public Task<WorkbenchData> GetRowsAsync(
            WorkbenchDefinition definition, bool detail, IReadOnlyDictionary<string, string> keys,
            int page, int pageSize, CancellationToken token, WorkbenchQuery? query = null,
            string? keyword = null, string? sortField = null, string? sortDirection = null,
            int? groupIndex = null, string? groupValue = null, string? dataFilter = null)
            => Task.FromResult(new WorkbenchData([], 0, page, pageSize));

        public Task<IReadOnlyList<Dictionary<string, object?>>> GetExportRowsByKeysAsync(
            WorkbenchDefinition definition, IReadOnlyList<IReadOnlyList<string>> keys, CancellationToken token,
            int? groupIndex = null, string? groupValue = null,
            IReadOnlyList<WorkbenchField>? exportFields = null, string? dataFilter = null)
        {
            var wanted = keys.Select(row => row.Count > 0 ? row[0] : string.Empty).ToHashSet(StringComparer.OrdinalIgnoreCase);
            return Task.FromResult<IReadOnlyList<Dictionary<string, object?>>>(
                [.. Rows.Where(row => wanted.Contains(row["DOC_NO"]?.ToString() ?? string.Empty))]);
        }

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
            bool canFileEdit = false, bool canFileDele = false, bool canSetup = false)
            => Task.FromResult<FormDefinition?>(null);
    }
}
