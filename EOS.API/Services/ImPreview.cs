using System.Text.Json;
using EOS.API.Models;

namespace EOS.API.Services;

/// <summary>
/// 会话列表最后一条消息的脱敏预览生成器。
/// 只生成摘要文本（文本截断、卡片/文件/系统消息固定文案），严禁把完整正文写入预览。
/// </summary>
public static class ImPreview
{
    public static string Build(ImMessageType type, string contentJson, int maxLength = 80)
    {
        var normalized = Math.Max(1, maxLength);
        switch (type)
        {
            case ImMessageType.Text:
                return Truncate(ReadText(contentJson), normalized);
            case ImMessageType.Card:
                return $"[卡片]：{ReadCardType(contentJson)}";
            case ImMessageType.File:
                return "[文件]";
            case ImMessageType.System:
                return "[系统消息]";
            default:
                return string.Empty;
        }
    }

    private static string ReadText(string contentJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(contentJson);
            return doc.RootElement.TryGetProperty("text", out var text) ? text.GetString() ?? string.Empty : string.Empty;
        }
        catch (JsonException)
        {
            return string.Empty;
        }
    }

    private static string ReadCardType(string contentJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(contentJson);
            return doc.RootElement.TryGetProperty("cardType", out var cardType)
                ? cardType.GetString() ?? "未知"
                : "未知";
        }
        catch (JsonException)
        {
            return "未知";
        }
    }

    private static string Truncate(string value, int maxLength)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        return value.Length <= maxLength ? value : value[..maxLength] + "…";
    }
}
