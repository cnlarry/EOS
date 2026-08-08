using System.Text.Json;

namespace EOS.API.Services;

/// <summary>
/// 业务卡片快照构建器（纯函数，可单测）。
/// 快照只放最小安全字段：标题 + 少量摘要字段 + 动作声明；详情点击时由客户端
/// 重新走 EOS.API 授权获取实时数据，快照本身不携带敏感明细。
/// </summary>
public static class ImCardSnapshot
{
    private static readonly string[] PreferredFieldPatterns = ["单号", "状态", "金额", "日期"];

    public static string Build(
        string cardType,
        string moduleTitle,
        string entityId,
        IReadOnlyDictionary<string, string> labels,
        IReadOnlyDictionary<string, object?> row)
    {
        var fields = new List<Dictionary<string, string?>>();
        var usedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var pattern in PreferredFieldPatterns)
        {
            var match = labels.FirstOrDefault(kv =>
                !usedKeys.Contains(kv.Key) &&
                kv.Value.Contains(pattern, StringComparison.OrdinalIgnoreCase) &&
                row.TryGetValue(kv.Key, out var value) && !IsBlank(value));
            if (match.Key is null)
            {
                continue;
            }

            usedKeys.Add(match.Key);
            fields.Add(new Dictionary<string, string?>
            {
                ["label"] = match.Value,
                ["value"] = FormatValue(row[match.Key]),
            });
        }

        var payload = new Dictionary<string, object?>
        {
            ["cardType"] = cardType,
            ["entityId"] = entityId,
            ["title"] = $"{moduleTitle} {entityId}",
            ["fields"] = fields,
            ["actions"] = new[]
            {
                new Dictionary<string, string?>
                {
                    ["id"] = "open-detail",
                    ["label"] = "打开单据",
                    ["url"] = null,
                },
            },
        };

        return JsonSerializer.Serialize(payload);
    }

    private static string? FormatValue(object? value)
    {
        if (IsBlank(value))
        {
            return null;
        }

        var text = value?.ToString() ?? string.Empty;
        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }

    private static bool IsBlank(object? value)
        => value is null || string.IsNullOrWhiteSpace(value.ToString());
}
