namespace EOS.API.Data.Workbench;

/// <summary>
/// 单据生命周期与流程状态对编辑的拦截口径（错误码 + 用户可见文案）。
///
/// <para>
/// 保存路径与只读诊断路径必须说同一句话：服务端拒绝时给出的文案就是"这条记录为什么改不了"的权威表述，
/// 诊断侧照抄一份会漂移（用户看到两种说法，且没有任何测试会拦）。
/// 因此文案只此一处，两处共同引用。
/// </para>
/// </summary>
internal static class LifecycleEditGuards
{
    internal const string FinishedCode = "FINISHED_EDIT_FORBIDDEN";
    internal const string FinishedMessage = "记录已结案，禁止编辑（请先取消结案）。";

    internal const string ConfirmedCode = "CONFIRMED_EDIT_FORBIDDEN";
    internal const string ConfirmedMessage = "记录已批核，禁止编辑（请先解批）。";

    internal const string FlowInProgressCode = "FLOW_IN_PROGRESS_EDIT_FORBIDDEN";
    internal const string FlowInProgressMessage = "记录流程正在审批中，禁止编辑（请先撤回流程）。";
}
