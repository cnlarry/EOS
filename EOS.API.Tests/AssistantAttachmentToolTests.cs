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
/// list_attachments 的**门禁、定位与输出**（离线，走假网关）：
/// 无浏览权拒绝；能看单据不等于能看附件（FILE_VIEW 单独一道门）；记录不在数据范围内按防探测口径回答；
/// 只给元数据并明说"不含内容"；没有附件时如实说"没有"；超上限要截断。
///
/// <para>
/// 真实读取（附件仓储的按主键查询）不在离线用例里重复验证——这里只钉住**工具自己**会做错的那部分。
/// </para>
/// </summary>
public sealed class AssistantAttachmentToolTests
{
    private const int ModuleId = 1606;
    private const string Key = "DD26080160";

    private static ModuleRights Rights(bool canBrowse = true, bool canFileView = true) => new(
        CanBrowse: canBrowse, CanViewCost: false, CanViewSecrecy: false, CanSetup: false,
        DeniedMasterFields: new HashSet<string>(), DeniedDetailFields: new HashSet<string>(),
        CanAddNew: false, CanEdit: false, CanDelete: false, CanApprove: false, CanDeapprove: false,
        CanEndCase: false, CanUnEndCase: false, CanFileView: canFileView, CanFileUpda: false,
        CanFileEdit: false, CanFileDele: false,
        DenyNewMasterFields: new HashSet<string>(), DenyNewDetailFields: new HashSet<string>(),
        DenyModiMasterFields: new HashSet<string>(), DenyModiDetailFields: new HashSet<string>(),
        DataFilter: "", ExecuteTag: "A");

    private static WorkbenchDefinition Definition() => new(
        ModuleId, "送货单", "COP_SEND_M", null, [], [], null,
        HasAdd: false, HasEdit: false, DetailNoSave: false,
        MasterPkOrder: [Key], DetailNoFields: string.Empty, HasWorkflow: false);

    private static JsonElement Args(int moduleId, string? key = null) =>
        JsonDocument.Parse(key is null
            ? "{\"module_id\":" + moduleId + "}"
            : "{\"module_id\":" + moduleId + ",\"_keys\":[\"" + key + "\"]}").RootElement.Clone();

    private static AssistantPolicyValues Policy(AssistantToolLimitsOptions limits) =>
        AssistantPolicyValues.Default with { ToolLimits = limits };

    private static AttachmentListTool Tool(
        FakePermissions permissions, FakeWorkbenchGateway workbench, FakeAttachmentGateway attachments,
        AssistantToolLimitsOptions? limits = null) =>
        new(new AssistantRecordLocator(workbench, permissions), attachments,
            AssistantTestRuntime.Fixed(new AssistantSettings(), Policy(limits ?? new AssistantToolLimitsOptions())));

    private static AttachmentDto Attachment(
        string clientFileName = "送货单扫描件.pdf", long sizeBytes = 2048, string? remark = null) =>
        new(7, ModuleId, "COP_SEND_M", "[\"" + Key + "\"]", 1, "20261001-abc.pdf", clientFileName,
            "application/pdf", sizeBytes, new string('a', 64), remark, "u1", "张三",
            new DateTime(2026, 10, 1, 9, 12, 0, DateTimeKind.Unspecified));

    [Fact]
    public async Task Denies_When_Module_Not_Browsable()
    {
        var result = await Tool(
                new FakePermissions(Rights(canBrowse: false)), new FakeWorkbenchGateway(), new FakeAttachmentGateway())
            .ExecuteAsync("u1", Args(ModuleId, Key), CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Contains("没有模块", result.ContentForModel, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Denies_When_File_View_Is_Missing()
    {
        // 能看单据 ≠ 能看它的附件：附件端点另有 FILE_VIEW 门，助手侧必须自己判
        var attachments = new FakeAttachmentGateway { Items = [Attachment()] };
        var result = await Tool(
                new FakePermissions(Rights(canFileView: false)), new FakeWorkbenchGateway(), attachments)
            .ExecuteAsync("u1", Args(ModuleId, Key), CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Contains("FILE_VIEW", result.ContentForModel, StringComparison.Ordinal);
        // 没权限就不该查：连查询都不该发生
        Assert.Null(attachments.LastKeys);
    }

    [Fact]
    public async Task Answers_Probe_Safe_When_The_Record_Is_Out_Of_Scope()
    {
        var workbench = new FakeWorkbenchGateway { VisibleRows = [] };
        var result = await Tool(new FakePermissions(Rights()), workbench, new FakeAttachmentGateway())
            .ExecuteAsync("u1", Args(ModuleId, Key), CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal(AssistantToolExtensions.NotFoundMessage, result.ContentForModel);
    }

    [Fact]
    public async Task Lists_Metadata_And_Says_Content_Is_Not_Included()
    {
        var attachments = new FakeAttachmentGateway
        {
            Items =
            [
                Attachment(sizeBytes: 2048, remark: "客户签收"),
                Attachment(clientFileName: "装箱清单.xlsx", sizeBytes: 3 * 1024 * 1024),
            ],
        };
        var result = await Tool(new FakePermissions(Rights()), new FakeWorkbenchGateway(), attachments)
            .ExecuteAsync("u1", Args(ModuleId, Key), CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Equal("COP_SEND_M", attachments.LastMasterTable);
        Assert.Equal([Key], attachments.LastKeys);
        Assert.Contains("附件 2 个", result.ContentForModel, StringComparison.Ordinal);
        Assert.Contains("送货单扫描件.pdf · 2 KB · 张三", result.ContentForModel, StringComparison.Ordinal);
        Assert.Contains("客户签收", result.ContentForModel, StringComparison.Ordinal);
        // 字节换算由工具做：把 3145728 直接扔给模型，它多半会换算错
        Assert.Contains("装箱清单.xlsx · 3 MB", result.ContentForModel, StringComparison.Ordinal);
        // "有附件"与"能读内容"是两件事，这句话必须出现
        Assert.Contains("只有文件元数据，不含文件内容", result.ContentForModel, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Says_None_Instead_Of_Guessing()
    {
        var result = await Tool(new FakePermissions(Rights()), new FakeWorkbenchGateway(), new FakeAttachmentGateway())
            .ExecuteAsync("u1", Args(ModuleId, Key), CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Contains("附件：0 个（这张单没有上传过附件）", result.ContentForModel, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Truncates_When_Over_The_Limit()
    {
        var attachments = new FakeAttachmentGateway { Items = [Attachment(), Attachment("第二个.pdf")] };
        var tool = Tool(new FakePermissions(Rights()), new FakeWorkbenchGateway(), attachments,
            new AssistantToolLimitsOptions { AttachmentListMax = 1 });

        var result = await tool.ExecuteAsync("u1", Args(ModuleId, Key), CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Contains("（只列出前 1 个）", result.ContentForModel, StringComparison.Ordinal);
        Assert.DoesNotContain("第二个.pdf", result.ContentForModel, StringComparison.Ordinal);
    }

    [Fact]
    public void Keys_Are_Encoded_As_A_Json_Array()
    {
        // 手拼逗号串会一条也匹配不到，而"匹配不到"会被读成"没有附件"——这里把它钉成断言
        Assert.Equal("[\"A\",\"B\"]", AttachmentGateway.EncodeKeys(["A", "B"]));
        Assert.Equal("[\"DD26080160\"]", AttachmentGateway.EncodeKeys([Key]));
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
            [new Dictionary<string, object?> { ["SEND_NO"] = Key }];

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

    private sealed class FakeAttachmentGateway : IAttachmentGateway
    {
        public IReadOnlyList<AttachmentDto> Items { get; init; } = [];

        public string? LastMasterTable { get; private set; }

        public IReadOnlyList<string>? LastKeys { get; private set; }

        public Task<IReadOnlyList<AttachmentDto>> ListAsync(
            int moduleId, string masterTable, IReadOnlyList<string> keys, CancellationToken token)
        {
            LastMasterTable = masterTable;
            LastKeys = keys;
            return Task.FromResult(Items);
        }
    }
}
