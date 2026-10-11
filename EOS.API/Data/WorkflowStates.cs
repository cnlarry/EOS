namespace EOS.API.Data;

using System.Text.Json;

using EOS.API.Data.Effects;
using EOS.API.Models;

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
    /// 单据生命周期列全集：单据主表的标准系统列
    /// （建立/修改/批核/结案的人与日期，以及批核/结案状态位）。
    /// </summary>
    public static readonly string[] LifecycleColumns =
    [
        "CREATE_PERSON", "CREATE_DATE", "LAST_UPDATE_BY", "LAST_UPDATE_DATE",
        "CONFIRM_TAG", "CONFIRM_PERSON", "CONFIRM_DATE",
        "FINISHED_TAG", "FINISHED_PERSON", "FINISHED_DATE",
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
    /// 是否单据生命周期系统列（系统列组）。
    /// 字段管理侧据此打标：默认隐藏、结构只读、不可删除。
    /// </summary>
    internal static bool IsLifecycleColumn(string fieldId) =>
        LifecycleColumns.Contains(fieldId, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 无副作用批核能力（既无效果链、又无流程定义）：这条路径的批核/解批与保存路径的自动批核
    /// **同为纯状态翻转**（只改 CONFIRM_TAG 三列），显式批核/解批同口径。
    ///
    /// **判据不能绑在 `AUTO_APPROVE` 上**（曾经如此，已修）：自动批核只决定"保存之后要不要自动翻转
    /// 状态"，与"能不能手工批核"是两件事。绑上去的后果是——把一个模块的自动批核关掉（例如月结单要
    /// 改成"建单 / 审核"两个权限位），它的手工批核入口会**跟着一起消失**：服务端 404
    /// `WORKFLOW_NOT_SUPPORTED`，前端工具栏也不显示按钮，于是"关了自动批核就再没人能审"。
    ///
    /// 服务端分支与表单按钮显隐共用此判定，两边不得分叉。
    /// （遗留批核过程字段已物理删除，故不再有"批核 SP"这一维度。）
    /// </summary>
    internal static bool IsStatelessApproveCapable(
        bool hasApproveEffects,
        bool hasFlow) =>
        !hasApproveEffects && !hasFlow;

    /// <summary>
    /// 批核三列（状态位 + 经办人 + 日期）：模块声明「启用批核」时的勾选校验按它判定
    /// （ADR-035 §6），与发布门 `lifecycle_columns` 的"能力 → 列"单向强制同源。
    /// </summary>
    public static readonly string[] ApproveColumns = ["CONFIRM_TAG", "CONFIRM_PERSON", "CONFIRM_DATE"];

    /// <summary>
    /// 已发布定义里**启用**的效果动作事件码（未启用、占位行不计）。
    /// 从 WorkbenchDefinitionBuilder 移到此处：批核能力判定与效果链判定共用同一处解析。
    /// </summary>
    internal static IEnumerable<string> EnabledActionEventCodes(WorkbenchDefinition definition)
    {
        if (definition.BusinessActions is not { ValueKind: JsonValueKind.Array } actions)
        {
            yield break;
        }
        foreach (var action in actions.EnumerateArray())
        {
            if (action.ValueKind != JsonValueKind.Object
                || !action.TryGetProperty("enabled", out var enabled)
                || enabled.ValueKind != JsonValueKind.True
                || !action.TryGetProperty("eventCode", out var code)
                || code.ValueKind != JsonValueKind.String)
            {
                continue;
            }
            var text = code.GetString();
            if (!string.IsNullOrWhiteSpace(text))
            {
                yield return text;
            }
        }
    }

    /// <summary>定义是否带"批核时会执行的效果链"（APPROVE_EFFECT / DEAPPROVE 且已启用）。</summary>
    internal static bool HasApproveEffects(WorkbenchDefinition definition) =>
        HasApproveEffects(EnabledActionEventCodes(definition));

    /// <summary>
    /// 定义里是否有**接在效果链上且已启用**的动作（ADR-035 §2 第 4 条）：引擎接管的判据由
    /// "模块开关"改成它——"这个模块配了动作"是唯一的事实。
    ///
    /// `MANUAL`（自定义按钮）不算：它只在用户点击时运行，不参与任何单据事件链，按钮通道
    /// 也不经过 <c>EffectEngineInvoker.IsEnabledFor</c>（实测 6 个调用点全在事件链上）。
    /// </summary>
    internal static bool HasEnabledEngineActions(WorkbenchDefinition definition)
    {
        foreach (var code in EnabledActionEventCodes(definition))
        {
            if (!BusinessActionCatalog.IsManualEvent(code))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// 事件码里是否含批核/解批效果。判据单点：无副作用批核的判定（ADR-035）与
    /// 发布门的列强制都从它取，避免"两边各写一遍"。
    /// </summary>
    internal static bool HasApproveEffects(IEnumerable<string>? enabledActionEventCodes)
    {
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
                && parsed is EffectEvent.ApproveEffect or EffectEvent.Deapprove)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// 模块是否具备批核能力（"能力 → 列"单向强制的输入侧）。
    /// 只认会触达 CONFIRM_TAG 写/读路径的后端事实（**模块声明启用批核**、自动批核、工作流、
    /// 批核/解批效果链；解批镜像批核链，同样需要状态位），不认仅控制显示的 FORM_BUTTONS。
    /// ADR-035：第一维由"效果引擎接管"换成模块声明 `APPROVE_ENABLED`——引擎接管是**实现方式**
    /// （该事件有没有启用的动作行），不是"要不要状态位"的前提。
    /// </summary>
    internal static bool NeedsApproveColumn(
        bool approveEnabled,
        bool autoApprove,
        bool hasWorkflow,
        IEnumerable<string>? enabledActionEventCodes)
    {
        if (approveEnabled || autoApprove || hasWorkflow)
        {
            return true;
        }
        return HasApproveEffects(enabledActionEventCodes);
    }

    /// <summary>
    /// 模块是否有批核入口（工具栏批核/解批按钮显隐的判据）：
    /// **模块声明「启用批核」∨ 自动批核 ∨ 有审批流程**。
    ///
    /// ADR-035：原第一维是"效果引擎接管"，而那是**实现方式**——拿它当入口的前提，就会出现
    /// "关掉某个实现方式 → 业务入口静默消失"（`AUTO_APPROVE` 那次已发生过一次，见
    /// <see cref="NeedsApproveColumn"/> 的注释；"效果引擎开关"这次是同一坑的另一面：
    /// 实测有 12 个模块的入口只靠它撑着）。声明这一维保证"只想简单批核"（没有流程、
    /// 没有效果链）的模块也有入口。
    ///
    /// "有批核效果链"那一维不在这里：调用方若需要，用
    /// <see cref="HasApproveEffects(WorkbenchDefinition)"/> 并集进来——保持本方法能被
    /// 只拿到三个布尔值的调用方（如 FlowDefinitionService 读列）复用。
    /// </summary>
    internal static bool HasApproveCapability(
        bool approveEnabled,
        bool autoApprove,
        bool hasFlow) =>
        approveEnabled
        || autoApprove
        || hasFlow;
}
