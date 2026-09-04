using System.Text.Json;

namespace EOS.API.Features.Assistant.Memory;

/// <summary>
/// M9 auto-distillation parsing: model output → pending candidates.
/// Thresholds: &lt;40 discard; 40-79 pending; &gt;=80 pending marked suggested.
/// Nothing becomes active without explicit user confirmation.
/// </summary>
public sealed record DistilledCandidate(string Type, string Key, string Value, int Confidence, string Reason);

public static class MemoryDistiller
{
    public const int MaxCandidates = 5;
    public const int SuggestThreshold = 80;
    public const int KeepThreshold = 40;

    public static string BuildDistillPrompt(IReadOnlyList<(string Role, string Content)> exchanges)
    {
        var lines = exchanges
            .Where(exchange => exchange.Content.Length > 0)
            .TakeLast(10)
            .Select(exchange => $"{exchange.Role}：{exchange.Content}");
        return "你是 ERP 工作助手的记忆提炼器。从以下对话中提取值得长期记住的用户事实与偏好（部门、常用模块、查询偏好），"
            + "忽略一次性问题与单据明细。只输出 JSON，不要输出其他文字："
            + "{\"memories\":[{\"type\":\"preference|fact|favorite\",\"key\":\"20字以内标题\",\"value\":\"2000字以内内容\",\"confidence\":0-100整数,\"reason\":\"提炼依据\"}]}"
            + "\n" + string.Join("\n", lines);
    }

    public static IReadOnlyList<DistilledCandidate> Parse(string modelOutput)
    {
        var candidates = new List<DistilledCandidate>();
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(modelOutput);
        }
        catch (JsonException)
        {
            return candidates;
        }

        using (document)
        {
            if (!document.RootElement.TryGetProperty("memories", out var memoriesEl)
                || memoriesEl.ValueKind != JsonValueKind.Array)
            {
                return candidates;
            }

            foreach (var item in memoriesEl.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                var type = GetString(item, "type");
                var key = GetString(item, "key")?.Trim() ?? string.Empty;
                var value = GetString(item, "value")?.Trim() ?? string.Empty;
                var reason = GetString(item, "reason")?.Trim() ?? string.Empty;
                var confidence = GetConfidence(item);
                if (type is null || key.Length == 0 || value.Length == 0 || confidence is null) continue;
                if (confidence < KeepThreshold || confidence > 100) continue;
                if (key.Length > AssistantMemoryStore.MaxMemoryKeyLength
                    || value.Length > AssistantMemoryStore.MaxMemoryValueLength) continue;
                try
                {
                    type = AssistantMemoryStore.ValidateType(type);
                }
                catch (ArgumentException)
                {
                    continue;
                }

                if (AssistantMemoryStore.ContainsSensitivePattern(key + "\n" + value)) continue;
                candidates.Add(new(type, key, value, confidence.Value, reason));
                if (candidates.Count >= MaxCandidates) break;
            }
        }

        return candidates;
    }

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString() : null;

    private static int? GetConfidence(JsonElement element)
    {
        if (!element.TryGetProperty("confidence", out var property)) return null;
        return property.ValueKind switch
        {
            JsonValueKind.Number when property.TryGetInt32(out var number) => number,
            _ => null,
        };
    }
}
