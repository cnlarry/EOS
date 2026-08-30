using System.Text.Json;
using EOS.API.Models;

namespace EOS.API.Data;

/// <summary>
/// FILTER_TEMPLATE JSON → ReportCondition 解析器（ADR-009 §6 运行时消费）。
/// FILTER_TEMPLATE 与 ADR-008 的 FILTER_STRUCT 同构，新增 {p.X} 参数占位符：
///   - 范围条件（F_TYPE=1）：items[0].op=GE/LE，无 options/selectSource → Type=1
///   - 固定单选（F_TYPE=2）：op=EQ + options + parameterName → Type=2
///   - 数据源单选（F_TYPE=3）：op=EQ + selectSource → Type=3
///   - 固定多选（F_TYPE=4）：op=IN + options → Type=4
///   - 数据源多选（F_TYPE=5）：op=IN + selectSource → Type=5
/// 解析失败 → null（调用方回退 F_TYPE/F_EXPR 旧 DSL 或 fail-closed 空选项）。
/// </summary>
internal static class ConditionTemplateParser
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>解析结果为 ReportCondition 所需字段（Type 按模板结构推导）。</summary>
    public sealed record ParseResult(
        int Type,
        string? Field,
        string? Expression,
        string? DefaultValue,
        string? ParameterName,
        IReadOnlyList<ReportOption> Options,
        ReportSelectSource? SelectSource,
        string? DefaultValueTo);

    public static ParseResult? TryParse(string? template, string? legacyDefault, string? legacyDefaultTo)
    {
        if (string.IsNullOrWhiteSpace(template)) return null;
        try
        {
            using var doc = JsonDocument.Parse(template);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            var items = ReadItems(root);
            if (items.Count == 0) return null;
            var first = items[0];

            var field = first.GetPropertyOrNull("field")?.GetString()?.Trim();
            var op = first.GetPropertyOrNull("op")?.GetString()?.Trim().ToUpperInvariant();

            // 参数占位符：{p.X} 或 {P.X}
            var value = first.GetPropertyOrNull("value")?.GetString();
            var parameterName = ExtractParameterName(value)
                ?? root.GetPropertyOrNull("parameterName")?.GetString();

            // 选项 / 数据源
            var options = ReadOptions(root);
            var selectSource = ReadSelectSource(root);

            var defaultValue = (legacyDefault ?? string.Empty).Trim();
            var defaultValueTo = (legacyDefaultTo ?? string.Empty).Trim();

            // 推导 Type：优先 selectSource → 3/5；options → 2/4；否则范围 1
            int type;
            if (selectSource is not null)
                type = op == "IN" ? 5 : 3;
            else if (options.Count > 0)
                type = op == "IN" ? 4 : 2;
            else
                type = 1;

            // 范围条件的 TO 值占位符来自 items[1]
            if (type == 1 && items.Count > 1 && string.IsNullOrWhiteSpace(defaultValueTo))
                defaultValueTo = ExtractParameterName(items[1].GetPropertyOrNull("value")?.GetString()) ?? string.Empty;

            return new ParseResult(
                type,
                string.IsNullOrWhiteSpace(field) ? null : field,
                string.IsNullOrWhiteSpace(defaultValue) ? null : defaultValue,
                string.IsNullOrWhiteSpace(defaultValue) ? null : defaultValue,
                string.IsNullOrWhiteSpace(parameterName) ? null : parameterName,
                options,
                selectSource,
                string.IsNullOrWhiteSpace(defaultValueTo) ? null : defaultValueTo);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static List<JsonElement> ReadItems(JsonElement root)
    {
        var result = new List<JsonElement>();
        if (!root.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array) return result;
        foreach (var item in items.EnumerateArray())
            if (item.ValueKind == JsonValueKind.Object) result.Add(item);
        return result;
    }

    private static List<ReportOption> ReadOptions(JsonElement root)
    {
        var result = new List<ReportOption>();
        if (!root.TryGetProperty("options", out var options) || options.ValueKind != JsonValueKind.Array) return result;
        foreach (var option in options.EnumerateArray())
        {
            if (option.ValueKind != JsonValueKind.Object) continue;
            var label = option.GetPropertyOrNull("label")?.GetString()?.Trim() ?? string.Empty;
            var value = option.GetPropertyOrNull("value")?.GetString()?.Trim() ?? string.Empty;
            if (value.Length == 0) continue;
            result.Add(new ReportOption(label.Length == 0 ? value : label, value));
        }
        return result;
    }

    private static ReportSelectSource? ReadSelectSource(JsonElement root)
    {
        if (!root.TryGetProperty("selectSource", out var source) || source.ValueKind != JsonValueKind.Object) return null;
        var table = source.GetPropertyOrNull("table")?.GetString()?.Trim();
        var idColumn = source.GetPropertyOrNull("idColumn")?.GetString()?.Trim();
        var valueColumn = source.GetPropertyOrNull("valueColumn")?.GetString()?.Trim();
        if (string.IsNullOrWhiteSpace(table) || string.IsNullOrWhiteSpace(idColumn) || string.IsNullOrWhiteSpace(valueColumn))
            return null;
        return new ReportSelectSource(table, idColumn, valueColumn);
    }

    /// <summary>从 {p.X} 占位符提取参数名。</summary>
    private static string? ExtractParameterName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var match = System.Text.RegularExpressions.Regex.Match(value, @"\{p\.([A-Za-z_][A-Za-z0-9_]*)\}", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value : null;
    }

    private static JsonElement? GetPropertyOrNull(this JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        return element.TryGetProperty(name, out var value) ? value : null;
    }
}
