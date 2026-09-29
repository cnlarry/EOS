using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using EOS.API.Features.Assistant.Actions;
using EOS.API.Security;

namespace EOS.API.Features.Assistant.Tools;

/// <summary>
/// 记录动作工具的公共部分：模块定位（ID 或中文名）与"用户不必报主键"的处境推断。
/// 主键推断只作用于**修改 / 删除**，且只作定位——工具内部仍按当前用户重新授权与取数。
/// </summary>
public abstract class AssistantRecordActionToolBase(
    IWorkbenchSearchGateway gateway,
    CurrentUserContext userContext) : AssistantToolBase, IPageContextTool
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private PageContext? _page;

    public void UsePageContext(PageContext page) => _page = page;

    /// <summary>经办人取当前登录用户的显示名（与统一表单的保存路径同一处取值）。</summary>
    protected string EmployeeName => userContext.EmployeeName;

    public override string ParametersJson => AssistantRecordActionArguments.ParametersJson;

    /// <summary>解析模块 ID：显式 ID 优先，其次按中文名关键字在通用工作台模块里查。</summary>
    protected async Task<int> ResolveModuleIdAsync(JsonElement arguments, CancellationToken token)
    {
        var moduleId = arguments.GetIntArg("module_id");
        if (moduleId > 0)
        {
            return moduleId;
        }
        var title = arguments.GetStringArg("module_title");
        return string.IsNullOrWhiteSpace(title)
            ? 0
            : await gateway.FindGenericModuleIdByTitleAsync(title.Trim(), token) ?? 0;
    }

    /// <summary>缺主键的行按页面处境补：当前打开的单据优先，其次是列表里选中的那一行。</summary>
    protected AssistantActionRequest WithPageKeys(AssistantActionRequest request)
    {
        if (request.Kind == AssistantRecordActionKind.Insert || _page is null)
        {
            return request;
        }

        IReadOnlyList<string>? fallback = _page.Selection is { Count: > 0 } selection
            ? selection
            : string.IsNullOrWhiteSpace(_page.DocNo) ? null : new[] { _page.DocNo!.Trim() };
        if (fallback is null)
        {
            return request;
        }

        return request with
        {
            Rows = [.. request.Rows.Select(row => row.Keys.Count > 0 ? row : row with { Keys = fallback })],
        };
    }

    protected static string DescribeModule(int moduleId, string title)
        => title.Length > 0 ? $"{title}（#{moduleId}）" : $"模块 #{moduleId}";

    protected static string RenderPreview(AssistantActionPreview preview)
    {
        if (preview.ModuleDenialCode is not null)
        {
            return $"无法对{DescribeModule(preview.ModuleId, preview.ModuleTitle)}执行"
                + $"{AssistantRecordActionNames.For(preview.Kind)}：{preview.ModuleDenialMessage ?? preview.ModuleDenialCode}"
                + $"（原因码 {preview.ModuleDenialCode}）。";
        }

        var allowed = preview.Rows.Count(row => row.Allowed);
        var text = new StringBuilder($"预演{AssistantRecordActionNames.For(preview.Kind)}"
            + $"{DescribeModule(preview.ModuleId, preview.ModuleTitle)}：可执行 {allowed} 行、"
            + $"不可执行 {preview.Rows.Count - allowed} 行。");
        foreach (var row in preview.Rows)
        {
            text.AppendLine().Append("- ").Append(row.Keys.Count > 0 ? string.Join('/', row.Keys) : "（新单）")
                .Append(row.Allowed ? "：可以执行。" : $"：不可执行（{row.DenialCode}）{row.DenialMessage}");
            foreach (var impact in row.Impacts ?? [])
            {
                text.AppendLine().Append("    影响面：")
                    .Append(impact.EffectKey).Append(' ').Append(impact.EventCode);
                if (impact.TargetTable.Length > 0)
                {
                    text.Append(" → ").Append(impact.TargetTable).Append('.').Append(impact.TargetField)
                        .Append(" [").Append(impact.OpCode).Append(']');
                }
            }
        }
        foreach (var note in preview.Notes)
        {
            text.AppendLine().Append("备注：").Append(note);
        }
        return text.ToString().TrimEnd();
    }

    protected static string Serialize(object payload) => JsonSerializer.Serialize(payload, JsonOptions);
}

/// <summary>
/// preview_record_action：**预演**新增 / 修改 / 删除（真实事务内跑完整条路径后强制回滚），
/// 逐行给出"能不能做、为什么不能"，删除另列级联影响面。不落库、不占单号、不产生流程待办。
/// </summary>
public sealed class PreviewRecordActionTool(
    AssistantRecordActionService actions,
    IWorkbenchSearchGateway gateway,
    CurrentUserContext userContext) : AssistantRecordActionToolBase(gateway, userContext)
{
    public const string ToolName = "preview_record_action";

    public override string Name => ToolName;

    public override AssistantToolRisk Risk => AssistantToolRisk.Draft;

    public override string Description =>
        "预演新增 / 修改 / 删除单据：逐行给出可执行 / 不可执行及原因，删除另列级联影响面。"
        + "预演在真实事务内跑完整条路径后无条件回滚，不落库、不占单号、不产生流程待办。";

    public override async Task<ToolExecutionResult> ExecuteAsync(
        string userId, JsonElement arguments, CancellationToken token)
    {
        var request = AssistantRecordActionArguments.TryParse(arguments, out var error);
        if (request is null)
        {
            return ToolExecutionResult.Deny(error);
        }
        var moduleId = await ResolveModuleIdAsync(arguments, token);
        if (moduleId <= 0)
        {
            return ToolExecutionResult.Deny("未找到匹配的 ERP 模块。请向用户确认准确的模块名称或给出 module_id。");
        }

        request = WithPageKeys(request with { ModuleId = moduleId });
        var preview = await actions.PreviewAsync(userId, EmployeeName, request, token);
        var draft = new
        {
            kind = "record-action-preview",
            moduleId = preview.ModuleId,
            moduleTitle = preview.ModuleTitle,
            action = AssistantRecordActionNames.For(preview.Kind),
            blocked = preview.ModuleDenialCode is not null,
            moduleDenialCode = preview.ModuleDenialCode,
            moduleDenialMessage = preview.ModuleDenialMessage,
            rows = preview.Rows.Select(row => new
            {
                keys = row.Keys,
                allowed = row.Allowed,
                denialCode = row.DenialCode,
                denialMessage = row.DenialMessage,
                impacts = row.Impacts?.Select(impact => new
                {
                    effectKey = impact.EffectKey,
                    eventCode = impact.EventCode,
                    effectName = impact.EffectName,
                    targetTable = impact.TargetTable,
                    targetField = impact.TargetField,
                    opCode = impact.OpCode,
                }),
            }),
            notes = preview.Notes,
        };
        return new ToolExecutionResult(
            preview.ModuleDenialCode is null, Serialize(preview) + "\n" + RenderPreview(preview), draft);
    }
}

/// <summary>
/// apply_record_action：**执行**新增 / 修改 / 删除。
///
/// <para>
/// 每次执行都按当前用户独立重新授权（不复用预演结论），并按行先跑一遍真实事务内的预演——
/// 预演判不下来的行不写。幂等键由服务端从工具调用身份推导，**不在参数 schema 里**。
/// </para>
/// <para>
/// 批核 / 解批 / 结案 / 取消结案不在这里：它们是职权行使，助手侧**不存在**对应的动作面。
/// </para>
/// </summary>
public sealed class ApplyRecordActionTool(
    AssistantRecordActionService actions,
    IWorkbenchSearchGateway gateway,
    CurrentUserContext userContext) : AssistantRecordActionToolBase(gateway, userContext), IToolCallContextTool
{
    public const string ToolName = "apply_record_action";

    private long _conversationId;
    private string? _toolCallId;

    public override string Name => ToolName;

    public override AssistantToolRisk Risk => AssistantToolRisk.Write;

    public override string Description =>
        "执行新增 / 修改 / 删除单据（用户自己录的数据，可代劳）：服务端重新授权、按行预演通过后落库，"
        + "写入走统一表单同一条管线并进审计。批核、解批、结案、取消结案不可代劳，请引导用户自己在界面上操作。";

    /// <summary>服务端注入本次工具调用身份，用于推导幂等键；它不构成权限依据。</summary>
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
            // 没有工具调用身份就算不出服务端幂等键：宁可拒绝，也不接受一个无法防重的写入。
            return ToolExecutionResult.Deny("缺少本次工具调用的服务端身份，无法生成幂等键，已拒绝执行。");
        }

        var request = AssistantRecordActionArguments.TryParse(arguments, out var error);
        if (request is null)
        {
            return ToolExecutionResult.Deny(error);
        }
        var moduleId = await ResolveModuleIdAsync(arguments, token);
        if (moduleId <= 0)
        {
            return ToolExecutionResult.Deny("未找到匹配的 ERP 模块。请向用户确认准确的模块名称或给出 module_id。");
        }

        request = WithPageKeys(request with { ModuleId = moduleId });
        var execution = await actions.ExecuteAsync(
            userId, EmployeeName, request, _conversationId, _toolCallId, token);
        var succeeded = execution.Rows.Count(row => row.Succeeded);
        var text = new StringBuilder(execution.ModuleDenialCode is not null
            ? $"无法对{DescribeModule(execution.ModuleId, execution.ModuleTitle)}执行"
              + $"{AssistantRecordActionNames.For(execution.Kind)}：{execution.ModuleDenialMessage ?? execution.ModuleDenialCode}"
            : $"执行{AssistantRecordActionNames.For(execution.Kind)}"
              + $"{DescribeModule(execution.ModuleId, execution.ModuleTitle)}：成功 {succeeded} 行、"
              + $"失败 {execution.Rows.Count - succeeded} 行。");
        foreach (var row in execution.Rows)
        {
            text.AppendLine().Append("- ").Append(row.Keys.Count > 0 ? string.Join('/', row.Keys) : "（新单）")
                .Append(row.Succeeded ? "：已保存。" : $"：未保存（{row.Code}）{row.Message}");
        }

        var draft = new
        {
            kind = "record-action-result",
            moduleId = execution.ModuleId,
            moduleTitle = execution.ModuleTitle,
            action = AssistantRecordActionNames.For(execution.Kind),
            rows = execution.Rows.Select(row => new
            {
                keys = row.Keys,
                succeeded = row.Succeeded,
                code = row.Code,
                message = row.Message,
                resultKeys = row.ResultKeys,
                idempotencyKey = row.IdempotencyKey,
            }),
        };
        return new ToolExecutionResult(succeeded > 0, text.ToString().TrimEnd(), draft);
    }
}
