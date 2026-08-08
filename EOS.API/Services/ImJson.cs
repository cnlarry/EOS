using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;

namespace EOS.API.Services;

/// <summary>
/// IM 内容 JSON 序列化选项：中文等非 ASCII 字符以原字符写入数据库（不转成 \uXXXX），
/// 保证站内搜索、预览和排障可读；同时仍转义 &lt; &gt; &amp; 等 HTML 敏感字符，
/// 避免消息内容被误当作 HTML 渲染。仅用于消息正文/卡片/系统消息等业务载荷。
/// </summary>
public static class ImJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
    };

    public static string Serialize(object value) => JsonSerializer.Serialize(value, Options);
}
