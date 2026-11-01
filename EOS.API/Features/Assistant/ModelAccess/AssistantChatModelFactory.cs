using Microsoft.Extensions.Options;

namespace EOS.API.Features.Assistant.ModelAccess;

/// <summary>
/// 按**供应商协议**造对话客户端。判定入口只有这一处。
///
/// <para>
/// 原先这段 switch 只长在 <see cref="ResolvingChatModel"/> 里；判官要按自己的模型行造客户端后，
/// 如果各自复制一份 switch，将来新增一种协议时会出现"对话换了实现、判官还走老路"——
/// 而那正是最不容易被发现的一类偏差（判官照样出结论，只是走的不是同一套客户端）。
/// </para>
/// </summary>
public static class AssistantChatModelFactory
{
    public static IChatModel Create(
        IHttpClientFactory httpClientFactory, AssistantSettings settings, string? providerCode) =>
        AssistantProviderCatalog.ProtocolOf(providerCode) switch
        {
            _ => new OpenAiCompatibleChatModel(httpClientFactory, Options.Create(settings)),
        };
}
