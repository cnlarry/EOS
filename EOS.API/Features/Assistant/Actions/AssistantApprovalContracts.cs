using System.Text.Json;

using EOS.API.Features.Assistant.Tools;
using EOS.API.Security;

namespace EOS.API.Features.Assistant.Actions;

/// <summary>
/// 批核族的四个处置动作。它们**不可代理**：这里只用于"准备一张给用户点确认的请求卡"，
/// 助手侧没有执行它们的能力，也没有对应的动作注册项。
/// </summary>
public enum AssistantApprovalAction
{
    Approve,
    Deapprove,
    EndCase,
    UnEndCase,
}

/// <summary>处置动作的名字与文案：请求卡的参数、既有端点路径段与界面标签共用同一份字面量。</summary>
public static class AssistantApprovalActionNames
{
    /// <summary>既有批核族端点上的路径段（前端点了确认之后直接调它们，不经助手）。</summary>
    public const string Approve = "approve";
    public const string Deapprove = "deapprove";
    public const string EndCase = "endcase";
    public const string UnEndCase = "unendcase";

    public static string Of(AssistantApprovalAction action) => action switch
    {
        AssistantApprovalAction.Approve => Approve,
        AssistantApprovalAction.Deapprove => Deapprove,
        AssistantApprovalAction.EndCase => EndCase,
        AssistantApprovalAction.UnEndCase => UnEndCase,
        _ => throw new ArgumentOutOfRangeException(nameof(action), action, "未知的处置动作。"),
    };

    /// <summary>界面与卡片上的动作名（与流向判定给出的动作名同一份字面量）。</summary>
    public static string LabelOf(AssistantApprovalAction action) => action switch
    {
        AssistantApprovalAction.Approve => "批核",
        AssistantApprovalAction.Deapprove => "解批",
        AssistantApprovalAction.EndCase => "结案",
        AssistantApprovalAction.UnEndCase => "取消结案",
        _ => throw new ArgumentOutOfRangeException(nameof(action), action, "未知的处置动作。"),
    };

    /// <summary>流向判定里可能承载该动作的候选动作名（有流程的模块用「审批 / 送审」，无流程用「批核」）。</summary>
    public static IReadOnlyList<string> VerdictLabels(AssistantApprovalAction action) => action switch
    {
        AssistantApprovalAction.Approve => ["批核", "审批", "送审"],
        AssistantApprovalAction.Deapprove => ["解批"],
        AssistantApprovalAction.EndCase => ["结案"],
        AssistantApprovalAction.UnEndCase => ["取消结案"],
        _ => [],
    };

    public static PermissionAction ToPermissionAction(AssistantApprovalAction action) => action switch
    {
        AssistantApprovalAction.Approve => PermissionAction.Approve,
        AssistantApprovalAction.Deapprove => PermissionAction.Deapprove,
        AssistantApprovalAction.EndCase => PermissionAction.EndCase,
        AssistantApprovalAction.UnEndCase => PermissionAction.UnEndCase,
        _ => throw new ArgumentOutOfRangeException(nameof(action), action, "未知的处置动作。"),
    };

    public static bool TryParse(string? value, out AssistantApprovalAction action)
    {
        switch (value?.Trim().ToLowerInvariant())
        {
            case Approve:
                action = AssistantApprovalAction.Approve;
                return true;
            case Deapprove:
                action = AssistantApprovalAction.Deapprove;
                return true;
            case EndCase:
                action = AssistantApprovalAction.EndCase;
                return true;
            case UnEndCase:
                action = AssistantApprovalAction.UnEndCase;
                return true;
            default:
                action = default;
                return false;
        }
    }
}

/// <summary>请求卡里的一行：主键 + 当前状态 + 此刻可否执行及其原因。</summary>
public sealed record ApprovalRequestRow(
    IReadOnlyList<string> Keys,
    string Status,
    bool Allowed,
    string? DenialCode,
    string? DenialMessage);

/// <summary>
/// 一次「操作请求卡」的预判结果（**只读**）：逐行的可执行性与原因。
/// 模块级被拒时逐行为空——模块都进不去，谈不上"哪一行能做"。
/// </summary>
public sealed record ApprovalRequestPreview(
    int ModuleId,
    string ModuleTitle,
    AssistantApprovalAction Action,
    string? ModuleDenialCode,
    string? ModuleDenialMessage,
    IReadOnlyList<ApprovalRequestRow> Rows,
    IReadOnlyList<string> Notes);

/// <summary>请求卡里的一行（下发形状）。</summary>
public sealed record ApprovalRequestRowDraft(
    IReadOnlyList<string> Keys,
    string Status,
    bool Allowed,
    string? DenialCode,
    string? DenialMessage);

/// <summary>
/// 请求卡（下发形状）：模型经工具、界面经端点拿到的是同一份形状，
/// 界面因此不需要为"重算逐行判定"另立一套解析。
/// </summary>
public sealed record ApprovalRequestDraft(
    string Kind,
    int ModuleId,
    string ModuleTitle,
    string Action,
    bool Blocked,
    string? ModuleDenialCode,
    string? ModuleDenialMessage,
    IReadOnlyList<ApprovalRequestRowDraft> Rows,
    IReadOnlyList<string> Notes);

/// <summary>请求卡与界面之间的线格式：一处定义，两个消费者。</summary>
public static class AssistantApprovalDtos
{
    /// <summary>请求卡草稿的 kind 标识。</summary>
    public const string DraftKind = "approval-request-preview";

    public static ApprovalRequestDraft ToDraft(ApprovalRequestPreview preview) => new(
        DraftKind,
        preview.ModuleId,
        preview.ModuleTitle,
        AssistantApprovalActionNames.Of(preview.Action),
        preview.ModuleDenialCode is not null,
        preview.ModuleDenialCode,
        preview.ModuleDenialMessage,
        [.. preview.Rows.Select(row => new ApprovalRequestRowDraft(
            row.Keys, row.Status, row.Allowed, row.DenialCode, row.DenialMessage))],
        preview.Notes);
}

/// <summary>一次请求卡的输入：处置动作 + 目标模块 + 逐行主键。</summary>
public sealed record ApprovalRequestArguments(
    AssistantApprovalAction Action,
    int ModuleId,
    string? ModuleTitle,
    IReadOnlyList<IReadOnlyList<string>> Rows);

/// <summary>
/// 请求卡的参数契约（工具与端点共用一份：界面与模型提交同一种形状）。
///
/// <para>
/// 这里**没有幂等键字段**：确认后的执行由界面直接调既有批核族端点，键由那一步给出；
/// 助手侧只准备请求卡，不持有执行能力。
/// </para>
/// </summary>
public static class AssistantApprovalRequestArguments
{
    public const string ParametersJson = """
        {
          "type": "object",
          "properties": {
            "module_id": { "type": "integer", "description": "模块 ID；与 module_title 二选一" },
            "module_title": { "type": "string", "description": "模块中文名关键字；与 module_id 二选一" },
            "action": { "type": "string", "enum": ["approve", "deapprove", "endcase", "unendcase"],
                        "description": "要交给用户确认的处置：批核 / 解批 / 结案 / 取消结案" },
            "rows": {
              "type": "array",
              "description": "逐行列出要处置的单据主键；缺省时按当前页面处境推断",
              "items": {
                "type": "object",
                "properties": {
                  "keys": { "type": "array", "items": { "type": "string" },
                            "description": "单据主键值，顺序与模块主键一致" }
                }
              }
            }
          },
          "required": ["action", "rows"]
        }
        """;

    /// <summary>解析参数；失败时返回 null 并给出可直接回喂模型的原因。</summary>
    public static ApprovalRequestArguments? TryParse(JsonElement arguments, out string error)
    {
        error = string.Empty;
        if (!AssistantApprovalActionNames.TryParse(arguments.GetStringArg("action"), out var action))
        {
            error = "参数 action 必须是 approve、deapprove、endcase 或 unendcase。";
            return null;
        }

        if (!arguments.TryGetProperty("rows", out var rowsElement) || rowsElement.ValueKind != JsonValueKind.Array)
        {
            error = "参数 rows 必须是数组，且至少一行。";
            return null;
        }

        var rows = new List<IReadOnlyList<string>>();
        foreach (var item in rowsElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                error = "rows 的每一项都必须是对象。";
                return null;
            }
            rows.Add(ReadKeys(item));
        }

        if (rows.Count == 0)
        {
            error = "rows 不能为空。";
            return null;
        }
        if (rows.Count > Governance.AssistantActionLimits.MaxApprovalRequestRecords)
        {
            error = $"一次最多准备 {Governance.AssistantActionLimits.MaxApprovalRequestRecords} 行的请求卡"
                + $"（本次 {rows.Count} 行），请分批提交。";
            return null;
        }

        return new ApprovalRequestArguments(
            action, arguments.GetIntArg("module_id"), arguments.GetStringArg("module_title"), rows);
    }

    /// <summary>只取参数里的模块与主键行（界面重算逐行判定时用，不关心模块名）。</summary>
    public static IReadOnlyList<string> ReadKeys(JsonElement row)
    {
        if (!row.TryGetProperty("keys", out var keys) || keys.ValueKind != JsonValueKind.Array)
        {
            return [];
        }
        return [.. keys.EnumerateArray().Select(item => item.ValueKind switch
        {
            JsonValueKind.String => item.GetString() ?? string.Empty,
            JsonValueKind.Null => string.Empty,
            _ => item.GetRawText(),
        })];
    }
}
