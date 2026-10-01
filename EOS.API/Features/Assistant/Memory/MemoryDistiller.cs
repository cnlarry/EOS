using System.Text.Json;
using EOS.API.Features.Assistant.Parameters;

namespace EOS.API.Features.Assistant.Memory;

/// <summary>
/// auto-distillation parsing: model output → pending candidates.
/// Thresholds: &lt;40 discard; 40-79 pending; &gt;=80 pending marked suggested.
/// Nothing becomes active without explicit user confirmation.
/// </summary>
public sealed record DistilledCandidate(string Type, string Key, string Value, int Confidence, string Reason);

public static class MemoryDistiller
{
    // 三个阈值与"取最近几轮"原先是这里的公开常量，现已搬进参数目录的 MEMORY 域
    // （AssistantMemoryLimitsOptions）：调用方把当轮解析出来的 limits 传进来。

    public static string BuildDistillPrompt(
        IReadOnlyList<(string Role, string Content)> exchanges, AssistantMemoryLimitsOptions limits)
    {
        var lines = exchanges
            .Where(exchange => exchange.Content.Length > 0)
            .TakeLast(limits.DistillTurns)
            .Select(exchange => $"{exchange.Role}：{exchange.Content}");
        return "你是 ERP 工作助手的记忆提炼器。从以下对话中提取值得长期记住的用户事实与偏好（部门、常用模块、查询偏好），"
            + "忽略一次性问题与单据明细。只输出 JSON，不要输出其他文字："
            + "{\"memories\":[{\"type\":\"preference|fact|favorite\",\"key\":\"20字以内标题\",\"value\":\""
            + $"{limits.MaxValueLength}字以内内容\",\"confidence\":0-100整数,\"reason\":\"提炼依据\"}}]}}"
            + "\n" + string.Join("\n", lines);
    }

    /// <summary>
    /// 解析模型输出。<paramref name="limits"/> 为 null 时用参数默认值（单测与离线调用不必自己造参数对象）。
    /// 生产路径（ChatService 的提炼）总是把当轮解析出来的参数传进来。
    /// </summary>
    public static IReadOnlyList<DistilledCandidate> Parse(
        string modelOutput, AssistantMemoryLimitsOptions? limits = null)
    {
        limits ??= new AssistantMemoryLimitsOptions();
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
                if (confidence < limits.KeepThreshold || confidence > 100) continue;
                if (key.Length > limits.MaxKeyLength || value.Length > limits.MaxValueLength) continue;
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
                if (candidates.Count >= limits.MaxCandidates) break;
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
