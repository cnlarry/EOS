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
    /// 单据生命周期列全集（系统列组 + 归属三列）：单据主表的标准系统列。
    /// </summary>
    public static readonly string[] LifecycleColumns =
    [
        "CREATE_PERSON", "CREATE_DATE", "LAST_UPDATE_BY", "LAST_UPDATE_DATE",
        "CONFIRM_TAG", "CONFIRM_PERSON", "CONFIRM_DATE",
        "FINISHED_TAG", "FINISHED_PERSON", "FINISHED_DATE",
        "OWNER", "OWNER_G", "CI",
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
    /// 数据归属三列：CI=行公司（取当前用户所属公司），OWNER=建单用户账号，
    /// OWNER_G=建单用户主组。服务端独占写入（新建覆盖回填、更新忽略客户端提交），
    /// 表单新增/编辑态隐藏、浏览态只读。
    /// </summary>
    public static readonly string[] OwnershipColumns = ["CI", "OWNER", "OWNER_G"];

    /// <summary>
    /// 归属兜底公司：会话用户无公司归属时回填此值并记警告。
    /// 哨兵公司（COMPANY.COMPANY_ID='DEFAULT'），不得指向任何真实客户公司——
    /// 否则无法归属的行会被静默算到该客户名下。
    /// </summary>
    public const string DefaultCompanyId = "DEFAULT";

    /// <summary>
    /// 是否单据生命周期系统列（系统列组，全 13 列含归属三列）。
    /// 字段管理侧据此打标：默认隐藏、结构只读、不可删除。
    /// </summary>
    internal static bool IsLifecycleColumn(string fieldId) =>
        LifecycleColumns.Contains(fieldId, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 无副作用批核能力（自动批核模块且无效果链/流程定义）：
    /// 保存路径的自动批核本就是纯状态翻转，显式批核/解批同口径。
    /// 服务端分支与表单按钮显隐共用此判定，两边不得分叉。
    /// （遗留批核过程字段已物理删除，故不再有"批核 SP"这一维度。）
    /// </summary>
    internal static bool IsStatelessApproveCapable(
        bool autoApprove,
        bool effectEnabled,
        bool hasFlow) =>
        autoApprove && !effectEnabled && !hasFlow;

    /// <summary>
    /// 模块是否具备批核能力（"能力 → 列"单向强制的输入侧）。
    /// 只认会触达 CONFIRM_TAG 写/读路径的后端事实（自动批核、效果引擎接管、工作流、
    /// 批核/解批效果链；解批镜像批核链，同样需要状态位），不认仅控制显示的 FORM_BUTTONS。
    /// </summary>
    internal static bool NeedsApproveColumn(
        bool autoApprove,
        bool effectEnabled,
        bool hasWorkflow,
        IEnumerable<string>? enabledActionEventCodes)
    {
        if (autoApprove || effectEnabled || hasWorkflow)
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

    /// <summary>
    /// 模块是否具备批核能力中的"可配置审批流程"口径：效果引擎接管、自动批核、已配置流程任一声明即可。
    /// 与 <see cref="NeedsApproveColumn"/> 同源，避免退役遗留批核过程时连带关闭流程配置入口。
    /// </summary>
    internal static bool HasApproveCapability(
        bool autoApprove,
        bool effectEnabled,
        bool hasFlow) =>
        autoApprove
        || effectEnabled
        || hasFlow;
}
