using EOS.API.Data.Workbench;
using EOS.API.Features.Assistant.Diagnosis;
using EOS.API.Models;
using EOS.API.Security;

namespace EOS.API.Features.Assistant.Actions;

/// <summary>门禁判定结果：允许时携带策略层载荷，拒绝时携带稳定原因码与用户可见文案。</summary>
public sealed record AssistantActionGateDecision(
    bool Allowed,
    string? Code,
    string? Message,
    WorkbenchFormAccess? Access)
{
    public static AssistantActionGateDecision Allow(WorkbenchFormAccess access) => new(true, null, null, access);
    public static AssistantActionGateDecision Deny(string code, string? message) => new(false, code, message, null);
}

/// <summary>
/// **门禁 2（助手侧预防）**：为助手动作算出"这个模块上这个动作此刻能不能做、不能做的原因是什么"。
///
/// <para>
/// 它是 <see cref="WorkbenchAccessPolicy"/> 的一个**消费方**，判定结果由策略层原样给出
/// （含稳定原因码），**不复制任何判定逻辑**——两套口径必然漂移，而漂移方向不可预测：
/// 要么拦住合法操作（用户困惑），要么放过非法操作（用户点了确认才被拒，信任崩塌）。
/// </para>
/// <para>
/// 它不是权限依据：执行前由服务端**独立重新授权**（同一次执行走同一个入口，但不复用本类的结论）。
/// </para>
/// </summary>
public sealed class AssistantActionGate(WorkbenchAccessPolicy policy)
{
    /// <summary>
    /// 按动作类型走策略层对应入口，与控制器端点用的是同一处判定：
    /// 新增 → `form-definition mode=new` 对应的表单访问；修改 → mode=edit；删除 → 删除路径（编辑访问 + 删除动作位）。
    /// </summary>
    public async Task<AssistantActionGateDecision> EvaluateAsync(
        string? userId, int moduleId, AssistantRecordActionKind kind, CancellationToken token)
    {
        var decision = kind switch
        {
            AssistantRecordActionKind.Insert => await policy.AuthorizeFormAsync(userId, moduleId, "new", token),
            AssistantRecordActionKind.Update => await policy.AuthorizeFormAsync(userId, moduleId, "edit", token),
            AssistantRecordActionKind.Delete => await AuthorizeDeleteAsync(userId, moduleId, token),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "未知的记录动作。"),
        };

        return decision.Allowed
            ? AssistantActionGateDecision.Allow(decision.Value!)
            : AssistantActionGateDecision.Deny(decision.Denial!.Code, decision.Denial.Message);
    }

    /// <summary>
    /// 删除路径的动作位不足由策略层原样抛出（控制器不捕获 → 403），助手侧把它折成稳定原因码，
    /// 文案与诊断侧同一份（不各写一句"你没有删除权限"）。
    /// </summary>
    private async Task<WorkbenchDecision<WorkbenchFormAccess>> AuthorizeDeleteAsync(
        string? userId, int moduleId, CancellationToken token)
    {
        try
        {
            return await policy.AuthorizeDeleteAsync(userId, moduleId, token);
        }
        catch (PermissionDeniedException)
        {
            return WorkbenchDecision<WorkbenchFormAccess>.Deny(new WorkbenchDenial(
                WorkbenchDenialKind.Forbidden,
                WorkbenchDenialCodes.DeleteNotPermitted,
                DiagnosisActionEvaluator.MessageFor(DiagnosisActionEvaluator.NoDeleteRightCode)));
        }
    }
}
