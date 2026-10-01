namespace EOS.API.Features.Assistant.ModelAccess;

/// <summary>
/// 助手模型密钥的存取（ADR-030 §3：**密钥不入库**）。
///
/// <para>
/// 库里只存**环境变量名**，密钥本体落在环境变量里。抽出这个接口有两个目的：
/// 一是把"密钥只走环境变量"这件事写成代码上的约定，而不是散落各处的
/// <c>Environment.GetEnvironmentVariable</c>；二是让测试能换成假的实现，
/// 不必真的去改进程的环境变量。
/// </para>
/// </summary>
public interface IAssistantSecretStore
{
    /// <summary>读取密钥。**只在服务端内部使用**（注入 Authorization 头），绝不下发前端。</summary>
    string? Read(string envVarName);

    /// <summary>是否已配置（未配置时助手会以"未配置"快速失败，而不是发一次注定 401 的请求）。</summary>
    bool IsConfigured(string envVarName);

    /// <summary>
    /// 写入密钥。返回是否也写进了**用户级**环境变量（即重启后是否还在）。
    ///
    /// <para>
    /// 进程级写入立即生效，所以**不需要重启服务**；用户级写入让它熬过重启。
    /// 服务账户在部分环境（Linux、受限账户）没有改用户级变量的权限，那时只有本次进程生效——
    /// 这是**可接受的降级**，但不能假装成功，所以把结果如实返回给调用方。
    /// </para>
    /// </summary>
    (bool ProcessUpdated, bool Persisted) Write(string envVarName, string secret);

    /// <summary>仅用于界面显示：形如 <c>****abcd</c> 的掩码（只露末四位）。</summary>
    string? MaskedTail(string envVarName);
}

/// <summary>基于环境变量的实现。</summary>
public sealed class EnvironmentSecretStore(ILogger<EnvironmentSecretStore> logger) : IAssistantSecretStore
{
    /// <inheritdoc />
    public string? Read(string envVarName) =>
        string.IsNullOrWhiteSpace(envVarName) ? null : Environment.GetEnvironmentVariable(envVarName);

    /// <inheritdoc />
    public bool IsConfigured(string envVarName) => !string.IsNullOrWhiteSpace(Read(envVarName));

    /// <inheritdoc />
    public (bool ProcessUpdated, bool Persisted) Write(string envVarName, string secret)
    {
        // 进程级：立即生效。用这个重载而不是带 target 的那个，是刻意的——
        // 带 target 的写法在写之前不会改当前进程，切换后仍要用旧密钥去发请求。
        Environment.SetEnvironmentVariable(envVarName, secret);

        var persisted = false;
        try
        {
            Environment.SetEnvironmentVariable(envVarName, secret, EnvironmentVariableTarget.User);
            persisted = true;
        }
        catch (Exception ex) when (ex is PlatformNotSupportedException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            // 只记变量名，绝不记密钥本体
            logger.LogWarning(ex, "写入用户级环境变量 {EnvVarName} 失败，密钥只在当前进程内生效（重启后需重新设置）。", envVarName);
        }

        return (true, persisted);
    }

    /// <inheritdoc />
    public string? MaskedTail(string envVarName)
    {
        var value = Read(envVarName);
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        // 太短就整体打码：露出的部分越少越好，这里只求"能看出配没配、配的是哪一个"
        return value.Length <= 4 ? new string('*', value.Length) : $"****{value[^4..]}";
    }
}
