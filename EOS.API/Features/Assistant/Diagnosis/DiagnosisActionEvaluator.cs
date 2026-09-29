using EOS.API.Data;
using EOS.API.Data.Workbench;
using EOS.API.Features.Assistant.Tools;
using EOS.API.Security;

namespace EOS.API.Features.Assistant.Diagnosis;

/// <summary>动作可执行性判定的输入事实（状态位 / 流程归属 / 权限位）。</summary>
public sealed record DiagnosisActionFacts(
    ModulePermission Permission,
    string UserId,
    bool Confirmed,
    bool Finished,
    bool HasFlow,
    string? FlowState,
    bool IsStarter,
    bool IsCurrentApprover);

/// <summary>一条阻塞原因（稳定原因码 + 涉事动作 + 用户可见文案）。状态类阻塞不针对某个动作，<c>Action</c> 为空。</summary>
public sealed record DiagnosisBlockerFact(string Code, string Action, string Message, string Source);

/// <summary>
/// 动作可执行性的判定**只有一处**：<see cref="GetModuleFlowTool.EvaluateActions"/>（状态 × 权限位 × 归属）。
/// 本类只做两件事——喂它事实、把它给出的原因折成稳定原因码与固定文案；**不重写判定**。
///
/// <para>
/// 说明：权限类原因（"你没有批核权限"）是**动作相关**的，不是"这张单此刻为什么办不下去"的根因——
/// 它在归因里排在校验与字段保护之后，只在没有更具体原因时才作为结论说出（但一旦说出就必须明说，不含糊）。
/// </para>
/// </summary>
public static class DiagnosisActionEvaluator
{
    /// <summary>「没有删除权限」：删除门禁最常给出的原因码，助手侧动作门禁复用同一句文案。</summary>
    public const string NoDeleteRightCode = "NO_DELETE_RIGHT";

    /// <summary>动作原因 → 稳定原因码。只登记"阻塞类"原因。</summary>
    private static readonly IReadOnlyDictionary<string, string> ReasonCodes = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["无批核权限"] = "NO_APPROVE_RIGHT",
        ["无编辑权限"] = "NO_EDIT_RIGHT",
        ["无删除权限"] = NoDeleteRightCode,
        ["无结案权限"] = "NO_ENDCASE_RIGHT",
        ["无取消结案权限"] = "NO_UNENDCASE_RIGHT",
        ["当前待办人不是你"] = "NOT_CURRENT_APPROVER",
        ["流程在途，须先撤回"] = LifecycleEditGuards.FlowInProgressCode,
        ["只有发起人可撤回在途流程"] = "NOT_FLOW_STARTER",
        ["单据已结案"] = LifecycleEditGuards.FinishedCode,
        ["单据已结案，先取消结案"] = LifecycleEditGuards.FinishedCode,
        ["单据已批核"] = LifecycleEditGuards.ConfirmedCode,
        ["单据已批核，先解批"] = LifecycleEditGuards.ConfirmedCode,
    };

    /// <summary>原因码 → 用户可见文案（与保存路径的拒绝文案同源）。</summary>
    private static readonly IReadOnlyDictionary<string, string> Messages = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["NO_APPROVE_RIGHT"] = "你没有「批核」权限。",
        ["NO_EDIT_RIGHT"] = "你没有「编辑」权限。",
        ["NO_DELETE_RIGHT"] = "你没有「删除」权限。",
        ["NO_ENDCASE_RIGHT"] = "你没有「结案」权限。",
        ["NO_UNENDCASE_RIGHT"] = "你没有「取消结案」权限。",
        ["NOT_CURRENT_APPROVER"] = "当前待办人不是你，无法审批该单据。",
        ["NOT_FLOW_STARTER"] = "审批流程在途且你不是发起人，无法撤回。",
        [LifecycleEditGuards.FlowInProgressCode] = LifecycleEditGuards.FlowInProgressMessage,
        [LifecycleEditGuards.FinishedCode] = LifecycleEditGuards.FinishedMessage,
        [LifecycleEditGuards.ConfirmedCode] = LifecycleEditGuards.ConfirmedMessage,
    };

    /// <summary>不构成阻塞项的动作原因：还没到那一步，或该模块本就没有这个动作。</summary>
    internal static readonly IReadOnlySet<string> NonBlockerReasons = new HashSet<string>(StringComparer.Ordinal)
    {
        "单据未批核",
        "你是发起人且单据未确认",
        "流程模块无直接解批动作，按审批链流转",
    };

    /// <summary>状态类原因码：对该记录的任何改动都成立，排在动作权限之前。</summary>
    internal static readonly IReadOnlySet<string> StateCodes = new HashSet<string>(StringComparer.Ordinal)
    {
        LifecycleEditGuards.FinishedCode,
        LifecycleEditGuards.ConfirmedCode,
        LifecycleEditGuards.FlowInProgressCode,
    };

    /// <summary>把 <see cref="GetModuleFlowTool.EvaluateActions"/> 的原因折成原因码；未登记的原因返回 null。</summary>
    internal static string? CodeFor(string reason) =>
        ReasonCodes.TryGetValue(reason, out var code) ? code : null;

    /// <summary>原因码对应的用户可见文案（与保存路径同一份）。</summary>
    internal static string MessageFor(string code) => Messages[code];

    /// <summary>按"状态类在前、动作权限在后"的顺序产出阻塞清单（同码只留一条）。</summary>
    public static IReadOnlyList<DiagnosisBlockerFact> Evaluate(DiagnosisActionFacts facts)
    {
        var input = new GetModuleFlowTool.FlowActionInput(
            facts.Permission.CanApprove, facts.Permission.CanEdit, facts.Permission.CanDelete,
            facts.Permission.CanEndCase, facts.Permission.CanUnEndCase, facts.UserId,
            facts.HasFlow, facts.FlowState, facts.IsStarter, facts.IsCurrentApprover,
            facts.Confirmed, facts.Finished);

        var state = new List<DiagnosisBlockerFact>();
        var action = new List<DiagnosisBlockerFact>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        void Add(string code, string action, string source, List<DiagnosisBlockerFact> bucket)
        {
            if (!seen.Add(code)) return;
            bucket.Add(new DiagnosisBlockerFact(code, action, Messages[code], source));
        }

        if (facts.Finished)
        {
            Add(LifecycleEditGuards.FinishedCode, string.Empty, "WorkbenchCommandHandler", state);
        }
        else if (facts.Confirmed)
        {
            Add(LifecycleEditGuards.ConfirmedCode, string.Empty, "WorkbenchCommandHandler", state);
        }

        if (facts.HasFlow && facts.FlowState == WorkflowStates.MonitorInProgress)
        {
            Add(LifecycleEditGuards.FlowInProgressCode, string.Empty, "WorkbenchCommandHandler", state);
        }

        foreach (var (name, allowed, reason) in GetModuleFlowTool.EvaluateActions(input))
        {
            if (allowed) continue;
            var code = CodeFor(reason);
            if (code is null || NonBlockerReasons.Contains(reason)) continue;
            Add(code, StateCodes.Contains(code) ? string.Empty : name,
                "get_module_flow.EvaluateActions", StateCodes.Contains(code) ? state : action);
        }

        return [.. state, .. action];
    }
}
