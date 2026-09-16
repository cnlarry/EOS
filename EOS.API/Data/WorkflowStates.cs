namespace EOS.API.Data;

using EOS.API.Data.Effects;

/// <summary>
/// 工作流状态常量：消灭 WorkflowEngine 全文魔法串。
/// 值对应库内 WF_MONITOR.WF_STATE（流程实例状态）与 WF_MYTASK / WF_MYTASK_LOG.APPROVE_STATE
/// （任务审批状态）语义；SQL 一律引用本类常量，禁止散落裸字符。
/// </summary>
public static class WorkflowStates
{
    /// <summary>流程实例：在途（WF_MONITOR.WF_STATE='0'）。</summary>
    public const string MonitorInProgress = "0";

    /// <summary>流程实例：已完成（WF_MONITOR.WF_STATE='1'）。</summary>
    public const string MonitorCompleted = "1";

    /// <summary>流程实例：已撤回（WF_MONITOR.WF_STATE='2'）。</summary>
    public const string MonitorWithdrawn = "2";

    /// <summary>任务审批：同意（WF_MYTASK.APPROVE_STATE='Y'）。</summary>
    public const char Approved = 'Y';

    /// <summary>任务审批：驳回（WF_MYTASK.APPROVE_STATE='N'）。</summary>
    public const char Rejected = 'N';

    /// <summary>任务审批：区间跳过（WF_MYTASK.APPROVE_STATE='S'）。</summary>
    public const char Skipped = 'S';

    /// <summary>任务审批：发起人送审（WF_MYTASK_LOG.APPROVE_STATE='A'，MYTASK_ID=0 非任务级动作）。</summary>
    public const char Submitted = 'A';

    /// <summary>任务审批：撤回（WF_MYTASK.APPROVE_STATE='W'）。</summary>
    public const char WithdrawnTask = 'W';

    /// <summary>
    /// 单据生命周期列全集（ADR-013 §3.3 / §3.5 系统列组）：单据主表的标准系统列。
    /// OWNER/OWNER_G 为数据范围行归属（同族但语义不同），纳入列集治理但不参与状态位口径。
    /// </summary>
    public static readonly string[] LifecycleColumns =
    [
        "CREATE_PERSON", "CREATE_DATE", "LAST_UPDATE_BY", "LAST_UPDATE_DATE",
        "CONFIRM_TAG", "CONFIRM_PERSON", "CONFIRM_DATE",
        "FINISHED_TAG", "FINISHED_PERSON", "FINISHED_DATE",
        "OWNER", "OWNER_G",
    ];

    /// <summary>
    /// 生命周期状态位：批核/结案开关。服务端独占写入，表单新增/编辑态隐藏。
    /// </summary>
    public static readonly string[] LifecycleTagColumns = ["CONFIRM_TAG", "FINISHED_TAG"];

    /// <summary>
    /// 生命周期经办人/日期：服务端持有（保存/批核/结案/工作流末步统一写入），
    /// 客户端提交一律拒绝；表单新增/编辑态隐藏、浏览态只读显示。
    /// </summary>
    public static readonly string[] LifecycleActorColumns =
    [
        "CREATE_PERSON", "CREATE_DATE", "LAST_UPDATE_BY", "LAST_UPDATE_DATE",
        "CONFIRM_PERSON", "CONFIRM_DATE", "FINISHED_PERSON", "FINISHED_DATE",
    ];

    /// <summary>
    /// 单据批核/结案状态列（WorkbenchCommandHandler 内联清单单点化）：
    /// 记录读取契约必须返回这些列，即使不在表单定义内。
    /// </summary>
    public static readonly string[] RecordStatusColumns =
        ["CONFIRM_TAG", "CONFIRM_PERSON", "CONFIRM_DATE", "FINISHED_TAG", "FINISHED_PERSON", "FINISHED_DATE"];

    /// <summary>
    /// 是否单据生命周期系统列（ADR-013 §3.5 系统列组，全 12 列含 OWNER/OWNER_G）。
    /// 字段管理侧据此打标：默认隐藏、结构只读、不可删除。
    /// </summary>
    internal static bool IsLifecycleColumn(string fieldId) =>
        LifecycleColumns.Contains(fieldId, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 模块是否具备批核能力（ADR-013 §3.7 能力 → 列单向强制的输入侧）。
    /// 只认会触达 CONFIRM_TAG 写/读路径的后端事实（自动批核、批核 SP、工作流、批核/解批效果链；
    /// 解批镜像批核链，同样需要状态位），不认仅控制显示的 FORM_BUTTONS。
    /// </summary>
    internal static bool NeedsApproveColumn(
        bool autoApprove,
        bool hasWorkflowSproc,
        bool hasWorkflow,
        IEnumerable<string>? enabledActionEventCodes)
    {
        if (autoApprove || hasWorkflowSproc || hasWorkflow)
        {
            return true;
        }
        if (enabledActionEventCodes is null)
        {
            return false;
        }
        foreach (var code in enabledActionEventCodes)
        {
            if (string.IsNullOrWhiteSpace(code))
            {
                continue;
            }
            if (EffectEventMapper.TryParse(code, out var parsed)
                && (parsed is EffectEvent.ApproveEffect or EffectEvent.Deapprove))
            {
                return true;
            }
        }
        return false;
    }
}
