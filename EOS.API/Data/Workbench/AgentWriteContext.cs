namespace EOS.API.Data.Workbench;

/// <summary>
/// 本次写入是否由**助手代表用户**发起。
///
/// 助手动作与控制器写入走同一套写管线、同一个请求，因此用请求作用域里的一个标记把
/// "这次写是助手动作"传到审计写入器：只有审计身份会因此变成
/// <c>ACTOR_TYPE=Agent</c> / <c>CLIENT_TYPE=Agent</c>，
/// **经办人与权限主体仍是真实用户**（<c>ACTOR_USER_ID</c> 恒为使用者）。
///
/// <para>
/// 标记**不参与任何授权判定**：授权一律由策略层按当前用户重新求值、fail-closed。
/// </para>
/// </summary>
public sealed class AgentWriteContext
{
    private int _active;

    /// <summary>当前是否处于"助手代表用户"的写入范围内。</summary>
    public bool IsActive => Volatile.Read(ref _active) > 0;

    /// <summary>进入"助手代表用户"的写入范围；释放时退出（可嵌套）。</summary>
    public IDisposable Begin()
    {
        Interlocked.Increment(ref _active);
        return new Scope(this);
    }

    private sealed class Scope(AgentWriteContext owner) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                Interlocked.Decrement(ref owner._active);
            }
        }
    }
}
