namespace EOS.API.Features.Assistant.Tools;

/// <summary>
/// 工具可选实现：接收服务端注入的**本次工具调用身份**（会话 + 工具调用 ID）。
///
/// <para>
/// 用途只有一个：让服务端据此推导写入类动作的幂等键
/// （<c>SHA256(会话 + 工具调用 + 动作名 + 规范化参数)</c>）。
/// 注入发生在同一请求内、紧邻执行，不跨请求残留；**它不构成权限依据**。
/// </para>
/// </summary>
public interface IToolCallContextTool
{
    void UseToolCallContext(long conversationId, string toolCallId);
}
