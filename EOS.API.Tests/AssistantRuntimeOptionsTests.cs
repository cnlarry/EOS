using EOS.API.Features.Assistant.ModelAccess;
using EOS.API.Features.Assistant.Parameters;
using EOS.API.Features.Assistant.Situation;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 运行期参数视图的接线口径（ADR-030 §14.4 订正 ①）。
///
/// <para>
/// 为什么要有这条测试：四个域改由 <see cref="RuntimeParameterView{T}"/> 提供取值，而
/// <c>Program.cs</c> 里**同时**保留了 <c>AddOptions&lt;T&gt;().Bind(…).ValidateOnStart()</c>
/// （那条管线现在只承载启动期的红线守卫）。于是"取值到底由谁提供"取决于容器对
/// **同一个服务类型的多次注册取最后一个**这条语义——它是对的，但**光看代码证明不了**，
/// 而且四个域当前的取值恰好都等于默认值，"参数没生效"在界面上看不出来。
/// 所以在这里把注册顺序复刻一遍，断死它。
/// </para>
/// </summary>
public sealed class AssistantRuntimeOptionsTests
{
    private sealed class StubRuntime(AssistantPolicyValues policy) : IAssistantRuntimeConfig
    {
        public AssistantRuntimeSnapshot Current { get; } = new(null, new AssistantSettings()) { Policy = policy };
    }

    [Fact]
    public void Runtime_View_Takes_Precedence_Over_The_Options_Pipeline()
    {
        var policy = AssistantPolicyValues.Default with
        {
            Situation = new AssistantSituationBudgetOptions { ResidentTokenLimit = 777 },
        };
        var runtime = new StubRuntime(policy);

        var services = new ServiceCollection();
        // ① 先挂 options 管线（承载 ValidateOnStart 的红线守卫）
        services.AddOptions<AssistantSituationBudgetOptions>()
            .Bind(new ConfigurationBuilder().Build())
            .ValidateOnStart();
        // ② 再用运行期参数视图顶上（与 Program.cs 的顺序一致）
        services.AddSingleton<IAssistantRuntimeConfig>(runtime);
        services.AddSingleton<IOptions<AssistantSituationBudgetOptions>>(sp =>
            new RuntimeParameterView<AssistantSituationBudgetOptions>(
                sp.GetRequiredService<IAssistantRuntimeConfig>(), values => values.Situation));

        using var provider = services.BuildServiceProvider();
        var resolved = provider.GetRequiredService<IOptions<AssistantSituationBudgetOptions>>();

        Assert.IsType<RuntimeParameterView<AssistantSituationBudgetOptions>>(resolved);
        // 取到的是**快照里的值**，不是 options 管线里那个空的默认实例
        Assert.Equal(777, resolved.Value.ResidentTokenLimit);
    }

    [Fact]
    public void Runtime_View_Reads_The_Current_Snapshot_On_Every_Access()
    {
        // "改完立刻生效"靠的就是这一点：视图不缓存值，每次访问都回快照取
        var runtime = new MutableRuntime();
        var view = new RuntimeParameterView<AssistantSituationBudgetOptions>(
            runtime, values => values.Situation);

        Assert.Equal(300, view.Value.ResidentTokenLimit);

        runtime.Policy = AssistantPolicyValues.Default with
        {
            Situation = new AssistantSituationBudgetOptions { ResidentTokenLimit = 5 },
        };

        Assert.Equal(5, view.Value.ResidentTokenLimit);
    }

    private sealed class MutableRuntime : IAssistantRuntimeConfig
    {
        public AssistantPolicyValues Policy { get; set; } = AssistantPolicyValues.Default;

        public AssistantRuntimeSnapshot Current => new(null, new AssistantSettings()) { Policy = Policy };
    }
}
