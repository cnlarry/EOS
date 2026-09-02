using System.Text.Json;
using EOS.API.Data;
using EOS.API.Features.Assistant;
using EOS.API.Features.Assistant.ModelAccess;
using EOS.API.Features.Assistant.Tools;
using EOS.API.Models;
using EOS.API.Security;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// Unit tests for the draft-level write tool: draft_record validation (whitelist filtering,
/// missing-required, type checks, add-new permission gate) and ChatService Drafts passthrough.
/// Execution reuse of the existing save pipeline is covered on the workbench side; these tests
/// focus on the tool's own semantics.
/// </summary>
public sealed class AssistantDraftToolTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static FormFieldDefinition Field(
        string key, string label, string type = "nvarchar",
        bool required = false, string? defaultValue = null,
        bool readOnly = false, bool pk = false, bool autoInc = false, bool serverFilled = false, bool visible = true) =>
        new(key, label, type, 50, null, required, null, null, defaultValue, readOnly, visible,
            false, false, null, [], pk, autoInc, false, false, false, serverFilled, null);

    private static LegacyModuleRights Rights(bool canBrowse = true, bool canAddNew = true) => new(
        CanBrowse: canBrowse, CanViewCost: false, CanViewSecrecy: false, CanSetup: false,
        DeniedMasterFields: new HashSet<string>(), DeniedDetailFields: new HashSet<string>(),
        CanAddNew: canAddNew, CanEdit: false, CanDelete: false, CanApprove: false, CanDeapprove: false,
        CanEndCase: false, CanUnEndCase: false, CanFileView: false, CanFileUpda: false,
        CanFileEdit: false, CanFileDele: false,
        DenyNewMasterFields: new HashSet<string>(), DenyNewDetailFields: new HashSet<string>(),
        DenyModiMasterFields: new HashSet<string>(), DenyModiDetailFields: new HashSet<string>(),
        DataFilter: string.Empty, ExecuteTag: "A");

    private sealed class FakePermissions(LegacyModuleRights rights) : IPermissionService
    {
        public Task<ModulePermission> GetAsync(string userId, int moduleId, CancellationToken cancellationToken)
            => Task.FromResult(new ModulePermission(rights));

        public Task<ModulePermission> RequireAsync(string userId, int moduleId, PermissionAction action, CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }

    private sealed class FakeGateway(FormDefinition? form) : IWorkbenchSearchGateway
    {
        public WorkbenchDefinition LastDefinition { get; set; } = Definition();

        public Task<int?> FindGenericModuleIdByTitleAsync(string titleKeyword, CancellationToken token)
            => Task.FromResult<int?>(1606);

        public Task<WorkbenchDefinition?> GetDefinitionAsync(int moduleId, string userId, string? execTag,
            bool canViewCost, bool canViewSecrecy, IReadOnlySet<string> deniedMasterFields,
            IReadOnlySet<string> deniedDetailFields, CancellationToken token)
            => Task.FromResult<WorkbenchDefinition?>(LastDefinition);

        public Task<WorkbenchData> GetRowsAsync(WorkbenchDefinition definition, bool detail,
            IReadOnlyDictionary<string, string> keys, int page, int pageSize, CancellationToken token,
            WorkbenchQuery? query = null, string? keyword = null, string? sortField = null,
            string? sortDirection = null, int? groupIndex = null, string? groupValue = null, string? dataFilter = null)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<Dictionary<string, object?>>> GetExportRowsByKeysAsync(
            WorkbenchDefinition definition, IReadOnlyList<IReadOnlyList<string>> keys, CancellationToken token,
            int? groupIndex = null, string? groupValue = null,
            IReadOnlyList<WorkbenchField>? exportFields = null, string? dataFilter = null)
            => throw new NotSupportedException();

        public Task<FormDefinition?> GetFormDefinitionAsync(WorkbenchDefinition definition, string userId, string mode,
            bool canViewCost, bool canViewSecrecy, IReadOnlySet<string> deniedMasterFields,
            IReadOnlySet<string> deniedDetailFields, IReadOnlySet<string> deniedNewMasterFields,
            IReadOnlySet<string> deniedNewDetailFields, IReadOnlySet<string> deniedModiMasterFields,
            IReadOnlySet<string> deniedModiDetailFields, CancellationToken token,
            bool canAddNew = false, bool canEdit = false, bool canDelete = false, bool canApprove = false,
            bool canDeapprove = false, bool canEndCase = false, bool canUnEndCase = false,
            bool canFileView = false, bool canFileUpda = false, bool canFileEdit = false, bool canFileDele = false,
            bool canSetup = false)
            => Task.FromResult(form);
    }

    private static WorkbenchDefinition Definition() => new(
        1606, "客户订单", "COP_ORDER_M", null,
        MasterFields: [], DetailFields: [], DefaultSort: null, HasAdd: true, HasEdit: true,
        DetailNoSave: false, MasterPkOrder: ["ORDER_NO"], DetailNoFields: string.Empty,
        HasWorkflow: false);

    private static FormDefinition Form() => new(
        1606, "客户订单", "COP_ORDER_M", null, HasAdd: true, HasEdit: true, Mode: "new",
        MasterFields:
        [
            Field("ORDER_NO", "订单号", readOnly: true, pk: true),
            Field("CLIENT_ID", "客户", required: true),
            Field("AMOUNT", "金额", type: "decimal"),
            Field("ORDER_DATE", "日期", type: "datetime"),
            Field("REMARK", "备注"),
        ],
        DetailFields: [], MasterPkOrder: ["ORDER_NO"], DetailNoFields: string.Empty,
        DetailDfVerify: string.Empty);

    private static DraftRecordTool CreateTool(bool canAddNew = true, FormDefinition? form = null)
    {
        var gateway = new FakeGateway(form ?? Form());
        var permissions = new FakePermissions(Rights(canAddNew: canAddNew));
        return new DraftRecordTool(gateway, permissions);
    }

    [Fact]
    public async Task Draft_Normalizes_Values_And_Carries_Payload()
    {
        var tool = CreateTool();
        var args = JsonSerializer.SerializeToElement(new
        {
            module_id = 1606,
            values = new Dictionary<string, string>
            {
                ["CLIENT_ID"] = "C001",
                ["AMOUNT"] = "123.45",
                ["ORDER_DATE"] = "2026-08-25",
                ["REMARK"] = "助手生成",
                ["HACKED_FIELD"] = "x", // 白名单外剔除
                ["ORDER_NO"] = "FAKE",  // 只读主键剔除
            },
        }, JsonOptions);

        var result = await tool.ExecuteAsync("u1", args, CancellationToken.None);

        Assert.True(result.Ok);
        var draft = Assert.IsType<AssistantFormDraft>(result.Draft);
        Assert.Equal(1606, draft.ModuleId);
        Assert.Equal(4, draft.Values.Count);
        Assert.Equal("C001", draft.Values["CLIENT_ID"]);
        Assert.False(draft.Values.ContainsKey("HACKED_FIELD"));
        Assert.False(draft.Values.ContainsKey("ORDER_NO"));
        Assert.Contains(draft.UnknownKeys, k => k == "HACKED_FIELD");
        Assert.DoesNotContain(draft.Warnings, w => w.Contains("AMOUNT")); // 数值合法无警告
    }

    [Fact]
    public async Task Draft_Reports_Missing_Required_And_Type_Errors()
    {
        var tool = CreateTool();
        var args = JsonSerializer.SerializeToElement(new
        {
            module_id = 1606,
            values = new Dictionary<string, string>
            {
                ["AMOUNT"] = "不是数字", // 类型错误 → 剔除+警告
                ["REMARK"] = "ok",
            },
        }, JsonOptions);

        var result = await tool.ExecuteAsync("u1", args, CancellationToken.None);

        Assert.True(result.Ok);
        var draft = Assert.IsType<AssistantFormDraft>(result.Draft);
        Assert.Single(draft.Values); // AMOUNT 类型错误被剔除，REMARK 合法保留
        Assert.Equal("ok", draft.Values["REMARK"]);
        Assert.Contains(draft.MissingRequired, m => m.Contains("CLIENT_ID"));
        Assert.Contains(draft.Warnings, w => w.Contains("需要数值"));
    }

    [Fact]
    public async Task Draft_Denied_Without_AddNew_Permission()
    {
        var tool = CreateTool(canAddNew: false);
        var args = JsonSerializer.SerializeToElement(new
        {
            module_id = 1606,
            values = new Dictionary<string, string> { ["REMARK"] = "x" },
        }, JsonOptions);

        var result = await tool.ExecuteAsync("u1", args, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Null(result.Draft);
    }

    [Fact]
    public async Task Registry_Exposes_RiskLevels_In_Definitions()
    {
        var gateway = new FakeGateway(Form());
        var permissions = new FakePermissions(Rights());
        var registry = new AssistantToolRegistry(
        [
            new SearchRecordsTool(gateway, permissions),
            new GetRecordDetailTool(gateway, permissions),
            new GetFormSchemaTool(gateway, permissions),
            new DraftRecordTool(gateway, permissions),
        ]);

        // 只读工具标 [READ]，草稿工具标 [DRAFT]——风险分级随描述下发模型
        foreach (var name in new[] { SearchRecordsTool.ToolName, GetRecordDetailTool.ToolName, GetFormSchemaTool.ToolName })
        {
            Assert.StartsWith("[READ]", registry.Definitions.Single(d => d.Name == name).Description);
        }

        Assert.StartsWith("[DRAFT]", registry.Definitions.Single(d => d.Name == DraftRecordTool.ToolName).Description);
    }

    [Fact]
    public async Task ChatService_Forwards_Draft_To_Completed_Event()
    {
        // 最小编排：脚本第一轮直接调 draft_record，第二轮文本收尾
        var model = new ScriptedModel();
        var repo = new ForwardingRepo();
        var gateway = new FakeGateway(Form());
        var permissions = new FakePermissions(Rights());
        var draftTool = new DraftRecordTool(gateway, permissions);
        var service = new ChatService(repo, model, new AssistantToolRegistry([draftTool]),
            Options.Create(new AssistantSettings { SystemPrompt = "SYS" }), NullLogger<ChatService>.Instance);

        var events = new List<ChatStreamEvent>();
        await foreach (var evt in service.StreamReplyAsync("u1", 7, "建单", null, "corr", CancellationToken.None))
        {
            events.Add(evt);
        }

        var done = events.OfType<ChatStreamEvent.Completed>().Single();
        Assert.NotNull(done.Drafts);
        var draft = Assert.IsType<AssistantFormDraft>(Assert.Single(done.Drafts!));
        Assert.Equal(1606, draft.ModuleId);
    }

    /// <summary>两轮脚本：tool_call → 文本收尾。</summary>
    private sealed class ScriptedModel : IChatModel
    {
        public string ModelName => "fake-model";

        public bool IsConfigured => true;

        private int _round;

        public async IAsyncEnumerable<ChatDelta> StreamAsync(
            IReadOnlyList<ChatMessage> messages,
            IReadOnlyList<ToolDefinition>? tools,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            if (_round++ == 0)
            {
                yield return new ChatDelta(null, null,
                    [new ProposedToolCallFragment(0, "c1", DraftRecordTool.ToolName, """{"module_id":1606,"values":{"REMARK":"助手"}}""")]);
                yield break;
            }

            yield return new ChatDelta("草稿已展示给用户确认。", null);
        }
    }

    /// <summary>历史回放用户消息的最小仓储。</summary>
    private sealed class ForwardingRepo : IAssistantRepository
    {
        private readonly List<(int Role, string Content)> _history = [];

        public Task<AssistantSessionDto> CreateSessionAsync(string userId, CancellationToken token)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<AssistantSessionDto>> ListSessionsAsync(string userId, int limit, CancellationToken token)
            => throw new NotSupportedException();

        public Task<AssistantSessionDto?> GetSessionAsync(string userId, long sessionId, CancellationToken token)
            => Task.FromResult<AssistantSessionDto?>(null);

        public Task<int> DeleteSessionAsync(string userId, long sessionId, CancellationToken token)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<AssistantMessageDto>> ListMessagesAsync(string userId, long sessionId, CancellationToken token)
            => throw new NotSupportedException();

        public Task<AssistantMessageDto> AddUserMessageAsync(
            string userId, long sessionId, string content, string correlationId, CancellationToken token)
        {
            _history.Add((1, content));
            return Task.FromResult(new AssistantMessageDto(1, sessionId, 1, content, null, null, null, null, correlationId, DateTimeOffset.UtcNow));
        }

        public Task<AssistantMessageDto> AddAssistantMessageAsync(
            string userId, long sessionId, string content, string modelName,
            int? promptTokens, int? completionTokens, int? elapsedMs, string correlationId,
            CancellationToken token)
            => Task.FromResult(new AssistantMessageDto(2, sessionId, 2, content, modelName, promptTokens, completionTokens, elapsedMs, correlationId, DateTimeOffset.UtcNow));

        public Task<IReadOnlyList<(int Role, string Content)>> LoadRecentHistoryAsync(
            string userId, long sessionId, int maxMessages, CancellationToken token)
            => Task.FromResult<IReadOnlyList<(int Role, string Content)>>([.. _history]);

        public Task UpdateToolCallsJsonAsync(string userId, long messageId, string toolCallsJson, CancellationToken token)
            => Task.CompletedTask;
    }
}
