using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace EOS.API.Features.Assistant.Actions;

/// <summary>
/// 助手执行类动作的幂等键**由服务端生成**。
///
/// 为什么不能交给模型：模型在工具失败、网络抖动或重复确认时会自行重试，而它每次生成的键
/// 都可能不同——幂等保护随即形同虚设。因此键从"这次工具调用"本身推导：
/// <c>SHA256(会话 + 工具调用 + 动作名 + 规范化参数)</c> 取前 32 位，同一轮工具调用重放即同一个键。
///
/// <para>键**不出现在模型可见的工具参数 schema 里**：模型无需、也不应知道它。</para>
/// </summary>
public static class AssistantActionIdempotency
{
    /// <summary>键长度上限与写管线（<c>WORKBENCH_IDEMPOTENCY.IDEMPOTENCY_KEY</c>）一致。</summary>
    public const int KeyLength = 32;

    /// <summary>
    /// 生成幂等键。<paramref name="canonicalArguments"/> 必须是**规范化**后的参数文本：
    /// 同一逻辑请求的不同书写（键序、空白、大小写）必须落到同一个键上。
    /// </summary>
    public static string Create(long conversationId, string toolCallId, string actionName, string canonicalArguments)
    {
        var payload = string.Join('\u001f',
            conversationId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            toolCallId ?? string.Empty,
            actionName ?? string.Empty,
            canonicalArguments ?? string.Empty);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(payload));
        return Convert.ToHexString(hash).ToLowerInvariant()[..KeyLength];
    }

    /// <summary>
    /// 把一次动作请求里的**一行**规范化成稳定文本：主表字段与明细列按键名排序，大小写与空白归一。
    /// 逐行规范化（而不是整体一把）使"同一次调用里被处理过的行"各自拥有稳定的键——
    /// 模型重发同一批、少发一行、或只重发其中一行，已处理过的行都不会被重复写入。
    /// </summary>
    public static string Canonicalize(AssistantActionRequest request, AssistantActionRow row)
    {
        var builder = new StringBuilder();
        builder.Append(request.ModuleId).Append('|')
            .Append(AssistantRecordActionNames.For(request.Kind)).Append('|')
            .Append(string.Join('\u001e', row.Keys.Select(Normalize)));
        if (row.Values is { Count: > 0 })
        {
            builder.Append('\u001d')
                .Append(string.Join('\u001e', row.Values
                    .OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(item => $"{Normalize(item.Key)}={Normalize(item.Value)}")));
        }
        if (row.Details is { Count: > 0 })
        {
            builder.Append('\u001c')
                .Append(string.Join('\u001b', row.Details.Select(detail => string.Join('\u001e', detail
                    .OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(item => $"{Normalize(item.Key)}={Normalize(item.Value)}")))));
        }
        if (row.DetailSerials is { Count: > 0 })
        {
            builder.Append('\u001a').Append(string.Join('\u001e', row.DetailSerials.Select(Normalize)));
        }
        return builder.ToString();
    }

    /// <summary>空值与空白归一成空串，其余去掉首尾空白——模型多写一个空格不该算出另一个键。</summary>
    private static string Normalize(string? value) => value?.Trim() ?? string.Empty;

    /// <summary>把 JSON 值按稳定顺序序列化（供参数里出现自由结构时使用）。</summary>
    internal static string CanonicalizeJson(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => "{" + string.Join(",", element.EnumerateObject()
            .OrderBy(property => property.Name, StringComparer.Ordinal)
            .Select(property => $"{property.Name}:{CanonicalizeJson(property.Value)}")) + "}",
        JsonValueKind.Array => "[" + string.Join(",", element.EnumerateArray().Select(CanonicalizeJson)) + "]",
        JsonValueKind.String => element.GetString()?.Trim() ?? string.Empty,
        JsonValueKind.Null or JsonValueKind.Undefined => string.Empty,
        _ => element.GetRawText(),
    };
}
