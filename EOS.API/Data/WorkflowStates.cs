namespace EOS.API.Data;

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
    /// 单据批核/结案状态列（WorkbenchCommandHandler 内联清单单点化）：
    /// 记录读取契约必须返回这些列，即使不在表单定义内。
    /// </summary>
    public static readonly string[] RecordStatusColumns =
        ["CONFIRM_TAG", "CONFIRM_PERSON", "CONFIRM_DATE", "FINISHED_TAG"];
}
