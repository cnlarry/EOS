using EOS.API.Features.Assistant.ModelAccess;
using EOS.API.Features.Assistant.Parameters;

namespace EOS.API.Tests;

/// <summary>
/// 测试用的固定运行期配置快照。
///
/// <para>
/// 助手配置现在来自数据库（模型表 + 设置表），而绝大多数测试只关心"给 ChatService 一套参数"。
/// 让它们各自去伪造目录、设置表与密钥存储，既啰嗦又把测试和存储细节绑在一起；
/// 这里给一个只读快照即可——<see cref="IAssistantRuntimeConfig"/> 这个接口存在的意义就是这个。
/// </para>
///
/// <para>
/// 注意 <c>Model</c> 传 null：这些用例的 <c>IChatModel</c> 是脚本化的假实现，是否"已配置"由它自己回答，
/// 快照只负责提供参数。
/// </para>
/// </summary>
internal static class AssistantTestRuntime
{
    public static IAssistantRuntimeConfig Fixed(AssistantSettings? settings = null) =>
        new SnapshotConfig(settings ?? new AssistantSettings());

    /// <summary>
    /// 固定策略值（不走库）：用于"参数改了之后行为跟着变"这类断言——
    /// 它们要的是一套确定的参数，而不是一套从数据库解析出来的参数。
    /// </summary>
    public static IAssistantRuntimeConfig Fixed(AssistantSettings? settings, AssistantPolicyValues policy) =>
        new SnapshotConfig(settings ?? new AssistantSettings(), policy);

    private sealed class SnapshotConfig : IAssistantRuntimeConfig
    {
        public SnapshotConfig(AssistantSettings settings, AssistantPolicyValues? policy = null) =>
            Current = new AssistantRuntimeSnapshot(null, settings) { Policy = policy ?? AssistantPolicyValues.Default };

        public AssistantRuntimeSnapshot Current { get; }
    }
}
