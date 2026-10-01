using EOS.API.Features.Assistant.ModelAccess;

namespace EOS.API.Features.Assistant.Parameters;

/// <summary>
/// 按**当前当事人**给出的生效参数：全局（快照）→ 模块 → 用户，三层叠加后的结果。
///
/// <para>
/// 为什么不是把作用域也算进运行期快照：快照是**单例**（进程内一份），而"他自己的覆盖"与
/// "这个模块的覆盖"是每个请求都不同的事实——塞进单例就等于要么按人重建进程级快照，
/// 要么把某个人/某个模块的值发给所有人。所以分工是：单例持全局那一层，
/// 本服务在一次请求内把上面两层压上去。
/// </para>
///
/// <para>
/// 叠加复用 <see cref="AssistantParameterScopeRules.Layer"/> 与
/// <see cref="AssistantParameterResolver.Interpret"/>——**取值口径只有一处实现**，
/// 作用域不引入第二套解释。没有覆盖行时直接返回快照里的全局解释结果，零额外开销。
/// </para>
/// </summary>
public interface IAssistantEffectiveParameters
{
    /// <summary>取当前生效的参数。同一次请求内重复调用只查一次库。</summary>
    Task<AssistantPolicyValues> ForAsync(string userId, int? moduleId, CancellationToken token);
}

/// <inheritdoc />
public sealed class AssistantEffectiveParameters(
    IAssistantRuntimeConfig runtime,
    AssistantParameterScopeStore scopeStore,
    ILogger<AssistantEffectiveParameters> logger) : IAssistantEffectiveParameters
{
    // 注册为 Scoped，所以这个记忆只活在一次请求里：请求之间不共享"某个人的限额"。
    private string? _memoKey;
    private AssistantPolicyValues? _memo;

    /// <inheritdoc />
    public async Task<AssistantPolicyValues> ForAsync(string userId, int? moduleId, CancellationToken token)
    {
        var key = $"{userId}|{moduleId?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "-"}";
        if (_memoKey == key && _memo is not null) return _memo;

        var values = await ComposeAsync(userId, moduleId, token);
        _memoKey = key;
        _memo = values;
        return values;
    }

    private async Task<AssistantPolicyValues> ComposeAsync(string userId, int? moduleId, CancellationToken token)
    {
        var snapshot = runtime.Current;
        if (snapshot.ParameterRows.Count == 0)
        {
            // 快照里还没有全局参数行（服务刚起、或迁移没跑完）：退回快照里的全局策略，
            // 不在这里再查一次库——那会把"配置读取"变成每个请求两条互不一致的路径
            return snapshot.Policy;
        }

        var scopes = await scopeStore.ListForAsync(userId, moduleId, token);
        if (scopes.Count == 0) return snapshot.Policy;

        var (rows, layerProblems) = AssistantParameterScopeRules.Layer(snapshot.ParameterRows, scopes);
        var values = AssistantParameterResolver.Interpret(rows);

        foreach (var problem in layerProblems)
        {
            logger.LogWarning("助手参数作用域覆盖未生效：{Problem}", problem);
        }

        return layerProblems.Count == 0
            ? values
            : values with { Problems = [.. layerProblems, .. values.Problems] };
    }
}
