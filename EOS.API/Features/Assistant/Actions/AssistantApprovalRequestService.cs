using EOS.API.Data;
using EOS.API.Data.Workbench;
using EOS.API.Features.Assistant.Diagnosis;
using EOS.API.Features.Assistant.Governance;
using EOS.API.Features.Assistant.Tools;
using EOS.API.Models;
using EOS.API.Security;
using Microsoft.Extensions.Options;

namespace EOS.API.Features.Assistant.Actions;

/// <summary>
/// 批核族「操作请求卡」的**只读**预判与两条留痕。
///
/// <para>
/// 三件事在这里同时成立：
/// <list type="number">
/// <item>**只读**：它不执行任何批核族动作，也不提供批量执行入口——那些端点在助手侧不存在调用点
/// （结构断言见 <c>NoApprovalEndpointCallTests</c>）；</item>
/// <item>**不另写判定口径**：模块级走策略层的批核族授权入口，逐行走流向判定的同一段判定代码
/// （状态 × 权限位）；</item>
/// <item>**执行主体是用户的点击**：确认之后由界面直接调既有批核族端点，
/// 这里只留"助手发起请求"与"用户确认"两条审计，使来源可分辨。</item>
/// </list>
/// </para>
/// </summary>
public sealed class AssistantApprovalRequestService(
    WorkbenchAccessPolicy policy,
    IWorkbenchSearchGateway gateway,
    WorkbenchAuditWriter auditWriter,
    AgentWriteContext agentWrites,
    IOptions<AssistantActionLimitsOptions> limits,
    ILogger<AssistantApprovalRequestService> logger)
{
    /// <summary>逐行判不了、或该模块当前没有这个动作时的稳定原因码。</summary>
    public const string NotApplicableCode = "ACTION_NOT_APPLICABLE";

    /// <summary>记录不在数据范围内（或在防探测口径下不可见）时的稳定原因码。</summary>
    public const string OutOfScopeCode = "RECORD_NOT_FOUND";

    /// <summary>
    /// 预判：逐行给出"此刻能不能处置 / 为什么不能"。不发一条写请求，也不改任何数据。
    /// </summary>
    public async Task<ApprovalRequestPreview> PreviewAsync(
        string userId, int moduleId, AssistantApprovalAction action,
        IReadOnlyList<IReadOnlyList<string>> rows, CancellationToken token)
    {
        var limit = limits.Value.MaxApprovalRequestRecords;
        if (rows.Count == 0)
        {
            return Blocked(moduleId, action, "INVALID_ARGUMENTS", "请求卡至少要有一行单据。");
        }
        if (rows.Count > limit)
        {
            return Blocked(moduleId, action, "TOO_MANY_RECORDS",
                $"一次最多准备 {limit} 行的请求卡（本次 {rows.Count} 行），请分批提交。");
        }

        WorkbenchDecision<WorkbenchDefinitionAccess> decision;
        try
        {
            decision = await policy.AuthorizeWorkflowAsync(
                userId, moduleId, AssistantApprovalActionNames.ToPermissionAction(action), token);
        }
        catch (PermissionDeniedException)
        {
            // 动作位不足由策略层抛出（HTTP 口径是 403）：助手侧折成稳定原因码，
            // 文案复用诊断侧那一份——权限问题必须明说，而是不是"操作失败"。
            return Blocked(moduleId, action, MissingRightCode(action),
                DiagnosisActionEvaluator.MessageFor(MissingRightCode(action)));
        }

        if (!decision.Allowed)
        {
            var denial = decision.Denial!;
            return Blocked(moduleId, action, denial.Code, denial.Message ?? AssistantActionGate.MessageFor(denial.Code));
        }

        var access = decision.Value!;
        var definition = access.Definition;
        var permission = new ModulePermission(access.Rights);
        var read = await ReadRowsAsync(definition, access.Rights.DataFilter, rows, token);

        var outcomes = new List<ApprovalRequestRow>(rows.Count);
        foreach (var keys in rows)
        {
            var row = FindRow(definition, read, keys);
            if (row is null)
            {
                // 防探测口径：不区分"不存在"与"不在你的数据范围内"，两者都由既有文案回答。
                outcomes.Add(new ApprovalRequestRow(
                    keys, string.Empty, false, OutOfScopeCode, AssistantToolExtensions.NotFoundMessage));
                continue;
            }

            outcomes.Add(Judge(action, permission, userId, row, keys));
        }

        var preview = new ApprovalRequestPreview(
            definition.ModuleId, definition.Title, action, null, null, outcomes, Notes());
        await WriteRequestAuditAsync(definition.ModuleId, action, outcomes, userId, token);
        return preview;
    }

    /// <summary>
    /// 用户在卡片上点了确认：**只留一条确认审计**，不执行任何处置。
    /// 真正的执行由界面直接调既有批核族端点完成（那里的授权与审计照旧）。
    /// </summary>
    public async Task<int> ConfirmAsync(
        string userId, int moduleId, AssistantApprovalAction action,
        IReadOnlyList<IReadOnlyList<string>> keys, CancellationToken token)
    {
        var limit = limits.Value.MaxApprovalRequestRecords;
        var confirmed = keys.Count > limit ? keys.Take(limit).ToList() : keys;
        var label = AssistantApprovalActionNames.LabelOf(action);
        var resources = Summarize(confirmed);
        try
        {
            using var agentScope = agentWrites.Begin();
            await auditWriter.WriteBestEffortAsync(
                moduleId > 0 ? moduleId : null, resources, AssistantRecordActionService.ConfirmAuditAction,
                $"用户确认执行{label}：{confirmed.Count} 行。" + limits.Value.Snapshot(), userId,
                "WORKBENCH_APPROVAL", result: 1, fieldChanges: null, token);
        }
        catch (Exception ex)
        {
            // 确认审计是 best-effort 的：它写不进去不该拦住用户已经点下的那一次执行。
            logger.LogWarning(ex, "请求卡确认审计写入失败 module={ModuleId}", moduleId);
        }

        return confirmed.Count;
    }

    /// <summary>逐行判定：与流向判定共用同一段代码，不在这里重写状态与权限口径。</summary>
    private static ApprovalRequestRow Judge(
        AssistantApprovalAction action, ModulePermission permission, string userId,
        IReadOnlyDictionary<string, object?> row, IReadOnlyList<string> keys)
    {
        var confirmed = Flag(row, "CONFIRM_TAG");
        var finished = Flag(row, "FINISHED_TAG");
        var status = finished ? "已结案" : confirmed ? "已批核（未结案）" : "未批核";

        var facts = new GetModuleFlowTool.FlowActionInput(
            permission.CanApprove, permission.CanEdit, permission.CanDelete,
            permission.CanEndCase, permission.CanUnEndCase, userId,
            HasFlow: false, InstanceState: null, IsStarter: false, IsCurrentApprover: false,
            Confirmed: confirmed, Finished: finished);
        var verdicts = GetModuleFlowTool.EvaluateActions(facts);

        foreach (var label in AssistantApprovalActionNames.VerdictLabels(action))
        {
            var verdict = verdicts.FirstOrDefault(item => item.Action == label);
            if (verdict.Action is null)
            {
                continue;
            }
            if (verdict.Allowed)
            {
                return new ApprovalRequestRow(keys, status, true, null, null);
            }

            var code = DiagnosisActionEvaluator.CodeFor(verdict.Reason);
            return new ApprovalRequestRow(
                keys, status, false, code ?? NotApplicableCode,
                code is null ? verdict.Reason : DiagnosisActionEvaluator.MessageFor(code));
        }

        // 该模块当前状态下没有这个动作（例如未结案时的「取消结案」）：如实说不适用，不猜一个原因。
        return new ApprovalRequestRow(
            keys, status, false, NotApplicableCode, "该单据当前状态下没有这个动作可执行。");
    }

    private static string MissingRightCode(AssistantApprovalAction action) => action switch
    {
        AssistantApprovalAction.Approve or AssistantApprovalAction.Deapprove => "NO_APPROVE_RIGHT",
        AssistantApprovalAction.EndCase => "NO_ENDCASE_RIGHT",
        AssistantApprovalAction.UnEndCase => "NO_UNENDCASE_RIGHT",
        _ => "NO_PERMISSION",
    };

    private async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> ReadRowsAsync(
        WorkbenchDefinition definition, string? dataFilter,
        IReadOnlyList<IReadOnlyList<string>> keys, CancellationToken token)
    {
        try
        {
            var rows = await gateway.GetExportRowsByKeysAsync(
                definition, keys, token, groupIndex: null, groupValue: null,
                exportFields: null, dataFilter: dataFilter);
            return [.. rows.Select(row => (IReadOnlyDictionary<string, object?>)row)];
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 读不到就整卡拒答："逐行都不可执行"与"没读到"在界面上必须分得开。
            logger.LogWarning(ex, "请求卡读取单据失败 module={ModuleId}", definition.ModuleId);
            return [];
        }
    }

    private static IReadOnlyDictionary<string, object?>? FindRow(
        WorkbenchDefinition definition, IReadOnlyList<IReadOnlyDictionary<string, object?>> rows,
        IReadOnlyList<string> keys)
    {
        var order = definition.MasterPkOrder;
        if (order.Count == 0 || keys.Count != order.Count)
        {
            return null;
        }

        foreach (var row in rows)
        {
            var matched = true;
            for (var index = 0; index < order.Count; index++)
            {
                row.TryGetValue(order[index], out var value);
                var text = value?.ToString()?.Trim() ?? string.Empty;
                if (!string.Equals(text, keys[index].Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    matched = false;
                    break;
                }
            }
            if (matched)
            {
                return row;
            }
        }

        return null;
    }

    private static bool Flag(IReadOnlyDictionary<string, object?> row, string column)
    {
        if (!row.TryGetValue(column, out var value) || value is null)
        {
            return false;
        }
        return value switch
        {
            bool flag => flag,
            _ => value.ToString()?.Trim() is "1" or "true" or "True" or "已批核" or "已结案",
        };
    }

    private static ApprovalRequestPreview Blocked(
        int moduleId, AssistantApprovalAction action, string code, string message) =>
        new(moduleId, string.Empty, action, code, message, [], []);

    private static IReadOnlyList<string> Notes() =>
    [
        "逐行结论复用流向判定的同一段判定代码（状态 × 权限位），不另写一套口径；执行时服务端仍会独立重新授权。",
        "确认后由界面直接调既有的批核 / 解批 / 结案 / 取消结案端点，助手侧不持有这四个端点。",
        "本卡不展开审批流实例；配了流程的模块，最终准入以端点判定为准。",
    ];

    /// <summary>助手发起请求卡：AI 发起一条、用户确认一条，"谁在什么时候点了确认"因此可独立检索。</summary>
    private async Task WriteRequestAuditAsync(
        int moduleId, AssistantApprovalAction action, IReadOnlyList<ApprovalRequestRow> outcomes,
        string userId, CancellationToken token)
    {
        var allowed = outcomes.Count(row => row.Allowed);
        var label = AssistantApprovalActionNames.LabelOf(action);
        try
        {
            using var agentScope = agentWrites.Begin();
            await auditWriter.WriteBestEffortAsync(
                moduleId, Summarize([.. outcomes.Select(row => row.Keys)]),
                AssistantRecordActionService.ConfirmAuditAction,
                $"助手发起操作请求卡{label}：{outcomes.Count} 行，可执行 {allowed} 行。" + limits.Value.Snapshot(),
                userId, "WORKBENCH_APPROVAL", result: 1, fieldChanges: null, token);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "请求卡发起审计写入失败 module={ModuleId}", moduleId);
        }
    }

    private static string Summarize(IReadOnlyList<IReadOnlyList<string>> keys) =>
        string.Join(',', keys
            .Select(row => string.Join('/', row))
            .Where(text => text.Length > 0)
            .Take(AssistantActionLimits.MaxAuditResourceKeys));
}
