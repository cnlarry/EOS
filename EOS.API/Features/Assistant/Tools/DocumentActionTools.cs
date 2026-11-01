using System.Text;
using System.Text.Json;
using EOS.API.Data.DocumentActions;
using EOS.API.Features.Assistant.Actions;
using EOS.API.Features.Assistant.Governance;
using EOS.API.Features.Assistant.ModelAccess;
using EOS.API.Features.Assistant.Parameters;
using EOS.API.Security;

namespace EOS.API.Features.Assistant.Tools;

/// <summary>
/// 自定义单据动作的确认卡：动作键、单据主键、参数声明与预演结论。
/// 用户在卡上确认后，前端直接调既有的 <c>POST /document-workbench/{moduleId}/action/{actionKey}</c>，
/// 助手不持有第二条执行入口。
/// </summary>
public sealed record AssistantDocumentActionDraft(
    string Kind,
    int ModuleId,
    string ModuleTitle,
    string ActionKey,
    string ActionLabel,
    IReadOnlyList<string> Keys,
    bool ConfirmTag,
    JsonElement? Params,
    IReadOnlyDictionary<string, string> Values,
    string? PreviewMessage);

/// <summary>自定义单据动作的执行结果卡（与确认卡分开，界面不必猜是哪一种）。</summary>
public sealed record AssistantDocumentActionResultDraft(
    string Kind,
    int ModuleId,
    string ModuleTitle,
    string ActionKey,
    string ActionLabel,
    string? Message,
    IReadOnlyList<DocumentActionWarning>? Warnings,
    int? TargetModuleId,
    IReadOnlyList<string>? TargetKey);

/// <summary>列动作 / 预演 / 执行三件共用的模块、主键与参数解析。</summary>
public abstract class AssistantDocumentActionToolBase(
    IWorkbenchSearchGateway gateway,
    IAssistantRuntimeConfig? runtime = null) : AssistantToolBase, IPageContextTool
{
    private PageContext? _page;

    protected IAssistantRuntimeConfig? Runtime { get; } = runtime;

    public void UsePageContext(PageContext page) => _page = page;

    protected async Task<int> ResolveModuleIdAsync(JsonElement arguments, CancellationToken token)
    {
        var moduleId = arguments.GetIntArg("module_id");
        if (moduleId > 0) return moduleId;
        var title = arguments.GetStringArg("module_title");
        return string.IsNullOrWhiteSpace(title)
            ? 0
            : await gateway.FindGenericModuleIdByTitleAsync(title.Trim(), token) ?? 0;
    }

    /// <summary>缺主键时按页面处境补：当前打开的单据优先，其次是列表里选中的那一行。</summary>
    protected IReadOnlyList<string> ResolveKeys(JsonElement arguments)
    {
        var keys = ReadStringArray(arguments, "keys");
        if (keys.Count > 0) return keys;
        if (_page is null) return [];

        if (_page.Selection is { Count: > 0 } selection)
        {
            return [.. selection.Select(value => value?.Trim() ?? string.Empty)];
        }

        return string.IsNullOrWhiteSpace(_page.DocNo) ? [] : [_page.DocNo!.Trim()];
    }

    protected static JsonElement? ReadParams(JsonElement arguments) =>
        arguments.TryGetProperty("params", out var parameters)
        && parameters.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)
            ? parameters.Clone()
            : null;

    protected static IReadOnlyDictionary<string, string> ReadParamValues(JsonElement arguments)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!arguments.TryGetProperty("params", out var parameters) || parameters.ValueKind != JsonValueKind.Object)
        {
            return values;
        }

        foreach (var property in parameters.EnumerateObject())
        {
            values[property.Name] = property.Value.ValueKind switch
            {
                JsonValueKind.String => property.Value.GetString() ?? string.Empty,
                JsonValueKind.Null or JsonValueKind.Undefined => string.Empty,
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                _ => property.Value.GetRawText(),
            };
        }

        return values;
    }

    protected static IReadOnlyList<string> ReadStringArray(JsonElement arguments, string name)
    {
        if (!arguments.TryGetProperty(name, out var element) || element.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return [.. element.EnumerateArray()
            .Select(item => item.ValueKind == JsonValueKind.String ? item.GetString() ?? string.Empty : string.Empty)
            .Select(value => value.Trim())];
    }

    protected static string RenderParams(JsonElement? declaration)
    {
        if (declaration is not { ValueKind: JsonValueKind.Object } document
            || !document.TryGetProperty("fields", out var fields)
            || fields.ValueKind != JsonValueKind.Array)
        {
            return "无参数";
        }

        var parts = new List<string>();
        foreach (var field in fields.EnumerateArray())
        {
            if (field.ValueKind != JsonValueKind.Object) continue;
            var key = Text(field, "key");
            var label = Text(field, "label");
            var type = Text(field, "type");
            var required = field.TryGetProperty("required", out var requiredValue)
                && requiredValue.ValueKind == JsonValueKind.True;
            if (key.Length == 0) continue;
            parts.Add($"{key}={label}({type}{(required ? ",必填" : string.Empty)})");
        }

        return parts.Count == 0 ? "无参数" : string.Join('、', parts);
    }

    private static string Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()?.Trim() ?? string.Empty
            : string.Empty;
}

/// <summary>list_document_actions：当前用户在该模块上真正有权点击的自定义动作。</summary>
public sealed class ListDocumentActionsTool(
    IDocumentActionGateway actions,
    IWorkbenchSearchGateway gateway,
    IAssistantRuntimeConfig? runtime = null) : AssistantDocumentActionToolBase(gateway, runtime)
{
    public const string ToolName = "list_document_actions";

    public override string Name => ToolName;

    public override AssistantToolRisk Risk => AssistantToolRisk.Read;

    public override string Description =>
        "列出当前用户在某模块上可执行的自定义单据动作（按钮级授权 fail-closed），"
        + "含动作键、显示名、是否需要确认与参数声明。执行前先看这里，不要猜动作键。";

    public override string ParametersJson => """
        {
          "type": "object",
          "properties": {
            "module_id": { "type": "integer", "description": "模块 ID；与 module_title 二选一" },
            "module_title": { "type": "string", "description": "模块中文名关键字；与 module_id 二选一" }
          }
        }
        """;

    public override async Task<ToolExecutionResult> ExecuteAsync(
        string userId, JsonElement arguments, CancellationToken token)
    {
        var moduleId = await ResolveModuleIdAsync(arguments, token);
        if (moduleId <= 0)
        {
            return ToolExecutionResult.Deny("未找到匹配的 ERP 模块。请向用户确认准确的模块名称或给出 module_id。");
        }

        var listing = await actions.ListAsync(userId, moduleId, token);
        if (listing is null)
        {
            return this.DenyBrowse($"#{moduleId}");
        }

        if (listing.Actions.Count == 0)
        {
            return ToolExecutionResult.Success(
                $"模块 {listing.ModuleTitle}（#{moduleId}）当前没有你可执行的自定义动作（按钮授权默认关闭）。");
        }

        var sb = new StringBuilder($"模块 {listing.ModuleTitle}（#{moduleId}）可执行自定义动作 {listing.Actions.Count} 个：");
        foreach (var action in listing.Actions)
        {
            sb.AppendLine().Append("- key=").Append(action.Key)
                .Append(" label=").Append(action.Label)
                .Append(" confirm=").Append(action.ConfirmTag ? "true（执行前只出确认卡）" : "false（可直接执行）")
                .Append(" params=").Append(RenderParams(action.Params));
        }

        return ToolExecutionResult.Success(sb.ToString().TrimEnd());
    }
}

/// <summary>
/// preview_document_action：对 CONFIRM_TAG=1 的动作跑服务端探路（真实事务内执行后回滚），
/// 返回确认卡；CONFIRM_TAG=0 的动作没有探路口径，如实说明而不是假装预演过。
/// </summary>
public sealed class PreviewDocumentActionTool(
    IDocumentActionGateway actions,
    IWorkbenchSearchGateway gateway,
    CurrentUserContext userContext,
    IAssistantRuntimeConfig? runtime = null) : AssistantDocumentActionToolBase(gateway, runtime)
{
    public const string ToolName = "preview_document_action";

    public override string Name => ToolName;

    public override AssistantToolRisk Risk => AssistantToolRisk.Draft;

    public override string Description =>
        "预演一个自定义单据动作：仅在动作声明了确认（CONFIRM_TAG=1）时可用，服务端在同一事务内执行后回滚，"
        + "只把“将会发生什么”带回；不提供确认的动作没有探路口径。";

    public override string ParametersJson => """
        {
          "type": "object",
          "properties": {
            "module_id": { "type": "integer", "description": "模块 ID；与 module_title 二选一" },
            "module_title": { "type": "string", "description": "模块中文名关键字；与 module_id 二选一" },
            "action_key": { "type": "string", "description": "list_document_actions 返回的动作键" },
            "keys": { "type": "array", "items": { "type": "string" }, "description": "单据主键值，顺序与模块主键一致；缺省时按当前页面处境推断" },
            "params": { "type": "object", "additionalProperties": { "type": "string" }, "description": "动作参数（键值对，键来自 list_document_actions 的 params 声明）" }
          },
          "required": ["action_key"]
        }
        """;

    public override async Task<ToolExecutionResult> ExecuteAsync(
        string userId, JsonElement arguments, CancellationToken token)
    {
        var moduleId = await ResolveModuleIdAsync(arguments, token);
        if (moduleId <= 0)
        {
            return ToolExecutionResult.Deny("未找到匹配的 ERP 模块。请向用户确认准确的模块名称或给出 module_id。");
        }

        var actionKey = arguments.GetStringArg("action_key")?.Trim();
        if (string.IsNullOrWhiteSpace(actionKey))
        {
            return ToolExecutionResult.Deny("缺少 action_key（动作键）。");
        }

        var info = await actions.GetAsync(userId, moduleId, actionKey, token);
        if (info is null)
        {
            return ToolExecutionResult.Deny($"动作 {actionKey} 不存在，或当前用户没有该按钮的授权。");
        }

        if (!info.ConfirmTag)
        {
            return ToolExecutionResult.Success(
                $"动作「{info.Label}」声明为无需确认，因此服务端不提供探路（没有“执行后回滚”的语义）。"
                + "需要执行时用 apply_document_action；它会直接写入并进审计。");
        }

        var keys = ResolveKeys(arguments);
        if (keys.Count == 0)
        {
            return ToolExecutionResult.Deny("缺少单据主键：请在 keys 里给出，或先在界面上打开/选中一张单据。");
        }

        var execution = await actions.ExecuteAsync(
            userId, userContext.EmployeeName, moduleId, info.Key, keys,
            ReadParams(arguments), confirm: false, idempotencyKey: "assistant-preview", token);
        if (execution is null)
        {
            return this.DenyBrowse($"#{moduleId}");
        }

        if (execution.Status != DocumentActionStatus.Ok || execution.Result is null)
        {
            return ToolExecutionResult.Deny(
                $"{execution.ErrorCode ?? "ACTION_FAILED"}：{execution.ErrorMessage ?? "动作预演未完成。"}");
        }

        var message = execution.Result.Message;
        var draft = ToDraft(moduleId, info, keys, arguments, message);
        var text = $"预演动作「{info.Label}」：{message ?? "将执行该操作"}（未写入任何数据）。"
            + "已生成确认卡，用户点确认后由界面直接调用既有动作端点。";
        return new ToolExecutionResult(true, text, draft);
    }

    internal static AssistantDocumentActionDraft ToDraft(
        int moduleId, AssistantDocumentActionInfo info, IReadOnlyList<string> keys,
        JsonElement arguments, string? message) =>
        new("document-action", moduleId, info.ModuleTitle, info.Key, info.Label, keys, info.ConfirmTag,
            info.Params, ReadParamValues(arguments), message);
}

/// <summary>
/// apply_document_action：CONFIRM_TAG=1 的动作**只准备确认卡、不执行**（执行主体是用户点击）；
/// CONFIRM_TAG=0 的动作按服务端推导的幂等键直接执行。
/// </summary>
public sealed class ApplyDocumentActionTool(
    IDocumentActionGateway actions,
    IWorkbenchSearchGateway gateway,
    CurrentUserContext userContext,
    IAssistantRuntimeConfig? runtime = null) : AssistantDocumentActionToolBase(gateway, runtime), IToolCallContextTool
{
    public const string ToolName = "apply_document_action";

    private long _conversationId;
    private string? _toolCallId;

    public override string Name => ToolName;

    public override AssistantToolRisk Risk => AssistantToolRisk.Write;

    public override string Description =>
        "执行一个自定义单据动作。CONFIRM_TAG=1 的动作只生成确认卡、本轮绝不执行（用户点确认后由界面调用既有端点）；"
        + "CONFIRM_TAG=0 的动作直接执行，走服务端按钮授权、幂等与审计。";

    public override string ParametersJson => """
        {
          "type": "object",
          "properties": {
            "module_id": { "type": "integer", "description": "模块 ID；与 module_title 二选一" },
            "module_title": { "type": "string", "description": "模块中文名关键字；与 module_id 二选一" },
            "action_key": { "type": "string", "description": "list_document_actions 返回的动作键" },
            "keys": { "type": "array", "items": { "type": "string" }, "description": "单据主键值，顺序与模块主键一致；缺省时按当前页面处境推断" },
            "params": { "type": "object", "additionalProperties": { "type": "string" }, "description": "动作参数（键值对，键来自 list_document_actions 的 params 声明）" }
          },
          "required": ["action_key"]
        }
        """;

    public void UseToolCallContext(long conversationId, string toolCallId)
    {
        _conversationId = conversationId;
        _toolCallId = toolCallId;
    }

    public override async Task<ToolExecutionResult> ExecuteAsync(
        string userId, JsonElement arguments, CancellationToken token)
    {
        if (string.IsNullOrEmpty(_toolCallId))
        {
            return ToolExecutionResult.Deny("缺少本次工具调用的服务端身份，无法生成幂等键，已拒绝执行。");
        }

        var moduleId = await ResolveModuleIdAsync(arguments, token);
        if (moduleId <= 0)
        {
            return ToolExecutionResult.Deny("未找到匹配的 ERP 模块。请向用户确认准确的模块名称或给出 module_id。");
        }

        var actionKey = arguments.GetStringArg("action_key")?.Trim();
        if (string.IsNullOrWhiteSpace(actionKey))
        {
            return ToolExecutionResult.Deny("缺少 action_key（动作键）。");
        }

        var info = await actions.GetAsync(userId, moduleId, actionKey, token);
        if (info is null)
        {
            return ToolExecutionResult.Deny($"动作 {actionKey} 不存在，或当前用户没有该按钮的授权。");
        }

        var keys = ResolveKeys(arguments);
        if (keys.Count == 0)
        {
            return ToolExecutionResult.Deny("缺少单据主键：请在 keys 里给出，或先在界面上打开/选中一张单据。");
        }

        // 确认型动作：只探路并成卡，本轮不执行（与批核族“只准备请求”同一责任口径）。
        if (info.ConfirmTag)
        {
            var preview = await actions.ExecuteAsync(
                userId, userContext.EmployeeName, moduleId, info.Key, keys,
                ReadParams(arguments), confirm: false, idempotencyKey: "assistant-preview", token);
            if (preview is null)
            {
                return this.DenyBrowse($"#{moduleId}");
            }

            if (preview.Status != DocumentActionStatus.Ok || preview.Result is null)
            {
                return ToolExecutionResult.Deny(
                    $"{preview.ErrorCode ?? "ACTION_FAILED"}：{preview.ErrorMessage ?? "动作预演未完成。"}");
            }

            var draft = PreviewDocumentActionTool.ToDraft(
                moduleId, info, keys, arguments, preview.Result.Message);
            return new ToolExecutionResult(true,
                $"动作「{info.Label}」需要确认：已生成确认卡（{preview.Result.Message ?? "将执行该操作"}）。"
                + "本轮未写入任何数据；用户点确认后由界面直接调用既有动作端点。", draft);
        }

        var idempotencyKey = AssistantActionIdempotency.Create(
            _conversationId, _toolCallId!, info.Key,
            string.Join('\u001f', keys) + "\u001f" + CanonicalizeParams(arguments));
        var execution = await actions.ExecuteAsync(
            userId, userContext.EmployeeName, moduleId, info.Key, keys,
            ReadParams(arguments), confirm: true, idempotencyKey, token);
        if (execution is null)
        {
            return this.DenyBrowse($"#{moduleId}");
        }

        if (execution.Status != DocumentActionStatus.Ok || execution.Result is null)
        {
            return ToolExecutionResult.Deny(
                $"{execution.ErrorCode ?? "ACTION_FAILED"}：{execution.ErrorMessage ?? "动作未完成。"}");
        }

        var result = execution.Result;
        var warnings = result.Warnings is { Count: > 0 }
            ? "；警告：" + string.Join("；", result.Warnings.Select(warning => warning.Message))
            : string.Empty;
        var resultDraft = new AssistantDocumentActionResultDraft(
            "document-action-result", moduleId, info.ModuleTitle, info.Key, info.Label,
            result.Message, result.Warnings, result.TargetModuleId, result.TargetKey);
        return new ToolExecutionResult(true,
            $"动作「{info.Label}」已执行：{result.Message ?? result.Outcome.ToString()}{warnings}", resultDraft);
    }

    internal static string CanonicalizeParams(JsonElement arguments) =>
        ReadParams(arguments) is { } parameters
            ? AssistantActionIdempotency.CanonicalizeJson(parameters)
            : string.Empty;
}
