using EOS.API.Features.Assistant.ModelAccess;
using Microsoft.Extensions.Options;

namespace EOS.API.Features.Assistant.Parameters;

/// <summary>
/// 运行期参数在**某个域**上的只读视图，以 <see cref="IOptions{T}"/> 的形状供既有消费方取用。
///
/// <para>
/// 它**不是配置绑定**：<c>Value</c> 每次访问都从运行期快照现取，所以
/// ① 改参数立刻生效（不必重建消费方）；② 不存在第二份默认值——默认值只有参数目录那一份；
/// ③ 消费方一行都不用改（处境、诊断、配置写的读取点当初就是按 <c>IOptions&lt;T&gt;</c> 注入的）。
/// </para>
///
/// <para>
/// 之所以不把消费方改成"直接读快照"：那要动 6 个类的构造签名与十来处取值点，而收益只是把
/// "取参数"这件事换个写法。**数据来源的切换才是本次的目的**，这个适配器实现了它——
/// 原来绑 <c>IConfiguration</c> 的四个节已全部取消。
/// </para>
/// </summary>
public sealed class RuntimeParameterView<T>(
    IAssistantRuntimeConfig runtime,
    Func<AssistantPolicyValues, T> select) : IOptions<T>
    where T : class
{
    /// <inheritdoc />
    public T Value => select(runtime.Current.Policy);
}
