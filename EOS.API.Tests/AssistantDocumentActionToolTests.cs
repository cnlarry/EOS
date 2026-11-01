using System.Text.Json;
using EOS.API.Data.DocumentActions;
using EOS.API.Features.Assistant.Tools;
using EOS.API.Models;
using EOS.API.Security;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 自定义单据动作工具：确认型动作只出卡（绝不执行），非确认型动作才执行；
/// 动作键、参数与按钮授权都来自服务端，工具不自己判定权限。
/// </summary>
public sealed class AssistantDocumentActionToolTests
{
    private sealed class FakeActions(params AssistantDocumentActionInfo[] actions) : IDocumentActionGateway
    {
        public List<(string ActionKey, bool Confirm)> Calls { get; } = [];

        public Task<AssistantDocumentActionListing?> ListAsync(
            string userId, int moduleId, CancellationToken token) =>
            Task.FromResult<AssistantDocumentActionListing?>(
                new AssistantDocumentActionListing("客户订单", actions));

        public Task<AssistantDocumentActionInfo?> GetAsync(
            string userId, int moduleId, string actionKey, CancellationToken token) =>
            Task.FromResult(actions.FirstOrDefault(action =>
                action.Key.Equals(actionKey, StringComparison.OrdinalIgnoreCase)));

        public Task<DocumentActionExecution?> ExecuteAsync(
            string userId, string employeeName, int moduleId, string actionKey,
            IReadOnlyList<string> keys, JsonElement? parameters, bool confirm,
            string idempotencyKey, CancellationToken token)
        {
            Calls.Add((actionKey, confirm));
            return Task.FromResult<DocumentActionExecution?>(new DocumentActionExecution(
                DocumentActionStatus.Ok,
                new DocumentActionResult(DocumentActionOutcome.Message, confirm ? "已执行" : "将执行"),
                RequiresConfirmation: !confirm));
        }
    }

    private sealed class FakeWorkbench : IWorkbenchSearchGateway
    {
        public Task<IReadOnlyList<SystemKnowledgeModule>> ListAssistantModulesAsync(string? keyword, CancellationToken token)
            => Task.FromResult<IReadOnlyList<SystemKnowledgeModule>>([]);

        public Task<int?> FindGenericModuleIdByTitleAsync(string titleKeyword, CancellationToken token)
            => Task.FromResult<int?>(1606);

        public Task<WorkbenchDefinition?> GetDefinitionAsync(
            int moduleId, string userId, string? execTag, bool canViewCost, bool canViewSecrecy,
            IReadOnlySet<string> deniedMasterFields, IReadOnlySet<string> deniedDetailFields,
            CancellationToken token) => Task.FromResult<WorkbenchDefinition?>(null);

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

    private static CurrentUserContext User()
    {
        var context = new DefaultHttpContext
        {
            User = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(
                [
                    new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.NameIdentifier, "u1"),
                    new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Name, "测试用户"),
                ], "test")),
        };
        return new CurrentUserContext(new HttpContextAccessor { HttpContext = context });
    }

    private static AssistantDocumentActionInfo Action(string key, bool confirm, JsonElement? parameters = null) =>
        new(1606, "客户订单", key, "重算金额", confirm, "master", parameters);

    private static JsonElement Params() =>
        JsonDocument.Parse("""{"fields":[{"key":"to","label":"目标","type":"string","required":true}]}""")
            .RootElement.Clone();

    [Fact]
    public async Task List_Shows_Authorized_Actions_With_Params()
    {
        var actions = new FakeActions(Action("recalc", true, Params()));
        var tool = new ListDocumentActionsTool(actions, new FakeWorkbench());

        var result = await tool.ExecuteAsync("u1", JsonSerializer.SerializeToElement(new
        {
            module_id = 1606,
        }), CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Contains("key=recalc", result.ContentForModel);
        Assert.Contains("confirm=true", result.ContentForModel);
        Assert.Contains("to=目标(string,必填)", result.ContentForModel);
    }

    [Fact]
    public async Task Apply_ConfirmTag_Only_Previews_And_Never_Writes()
    {
        var actions = new FakeActions(Action("recalc", true, Params()));
        var tool = new ApplyDocumentActionTool(actions, new FakeWorkbench(), User());
        tool.UseToolCallContext(1, "call-1");

        var result = await tool.ExecuteAsync("u1", JsonSerializer.SerializeToElement(new
        {
            module_id = 1606,
            action_key = "recalc",
            keys = new[] { "A001" },
            @params = new Dictionary<string, string> { ["to"] = "B" },
        }), CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Single(actions.Calls);
        Assert.False(actions.Calls[0].Confirm); // 只探路
        var draft = Assert.IsType<AssistantDocumentActionDraft>(result.Draft);
        Assert.Equal("document-action", draft.Kind);
        Assert.Equal("A001", Assert.Single(draft.Keys));
        Assert.Equal("B", draft.Values["to"]);
    }

    [Fact]
    public async Task Apply_NonConfirm_Executes_With_Server_Derived_Key()
    {
        var actions = new FakeActions(Action("recalc", false));
        var tool = new ApplyDocumentActionTool(actions, new FakeWorkbench(), User());
        tool.UseToolCallContext(1, "call-2");

        var result = await tool.ExecuteAsync("u1", JsonSerializer.SerializeToElement(new
        {
            module_id = 1606,
            action_key = "recalc",
            keys = new[] { "A001" },
        }), CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Single(actions.Calls);
        Assert.True(actions.Calls[0].Confirm);
        var draft = Assert.IsType<AssistantDocumentActionResultDraft>(result.Draft);
        Assert.Equal("document-action-result", draft.Kind);
    }

    [Fact]
    public async Task Preview_NonConfirm_Action_Says_No_Probe_Instead_Of_Pretending()
    {
        var actions = new FakeActions(Action("recalc", false));
        var tool = new PreviewDocumentActionTool(actions, new FakeWorkbench(), User());

        var result = await tool.ExecuteAsync("u1", JsonSerializer.SerializeToElement(new
        {
            module_id = 1606,
            action_key = "recalc",
            keys = new[] { "A001" },
        }), CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Empty(actions.Calls);
        Assert.Contains("不提供探路", result.ContentForModel);
    }

    [Fact]
    public async Task Apply_Requires_Key_And_Tool_Call_Identity()
    {
        var actions = new FakeActions(Action("recalc", false));
        var tool = new ApplyDocumentActionTool(actions, new FakeWorkbench(), User());

        var noContext = await tool.ExecuteAsync("u1", JsonSerializer.SerializeToElement(new
        {
            module_id = 1606,
            action_key = "recalc",
            keys = new[] { "A001" },
        }), CancellationToken.None);
        Assert.False(noContext.Ok);
        Assert.Contains("幂等键", noContext.ContentForModel);

        tool.UseToolCallContext(1, "call-3");
        var noKey = await tool.ExecuteAsync("u1", JsonSerializer.SerializeToElement(new
        {
            module_id = 1606,
            action_key = "recalc",
        }), CancellationToken.None);
        Assert.False(noKey.Ok);
        Assert.Contains("主键", noKey.ContentForModel);
    }
}
