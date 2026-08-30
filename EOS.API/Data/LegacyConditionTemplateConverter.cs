using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace EOS.API.Data;

/// <summary>
/// SYSQR_DEFAULT F_TYPE/F_EXPR DSL → FILTER_TEMPLATE JSON 转换器（仿 ADR-008 LegacyChooserFilterConverter 三档法）。
/// 报表条件模板（FILTER_TEMPLATE）与 ADR-008 的 FILTER_STRUCT 同构，新增 {p.X} 参数占位符表示用户填值。
/// 运行时：{p.X} 从 SYSQR_USER 取用户值参数化绑定。
/// 关键区分：选择器过滤（ADR-008）是固定条件，报表条件是用户填值的参数。
/// </summary>
internal static class LegacyConditionTemplateConverter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly Regex SelectSourcePattern = new(
        @"^\s*select\s+(\w+)\s+C_ID\s*,\s*(\w+)\s+C_VALUE\s+from\s+(\w+)\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>转换结果分段。</summary>
    public sealed record ConvertResult(
        string? Template,
        string? Error);

    /// <summary>三档转换入口。</summary>
    public static ConvertResult Convert(int type, string? expression, string? defaultValue, string? field, string? parameterName)
    {
        expression = (expression ?? string.Empty).Trim();
        defaultValue = (defaultValue ?? string.Empty).Trim();
        field = (field ?? string.Empty).Trim();
        parameterName = (parameterName ?? string.Empty).Trim();

        // 空字段/无效类型 → 跳过
        if (string.IsNullOrWhiteSpace(field) && type != 2)
            return new(null, null); // 无字段的不可用条件，静默跳过

        switch (type)
        {
            case 1: return ConvertRange(expression, defaultValue, field, parameterName);
            case 2: return ConvertFixedSelect(expression, defaultValue, field, parameterName);
            case 3: return ConvertDataSelect(expression, defaultValue, field, parameterName);
            case 4: return ConvertFixedMultiSelect(expression, defaultValue, field, parameterName);
            case 5: return ConvertDataMultiSelect(expression, defaultValue, field, parameterName);
            default: return new(null, $"未知类型 F_TYPE={type}");
        }
    }

    /// <summary>F_TYPE=1 范围条件：FIELD >= {p.字段名} AND FIELD <= {p.字段名}_TO。</summary>
    private static ConvertResult ConvertRange(string? expression, string? defaultValue, string field, string? parameterName)
    {
        var paramName = string.IsNullOrWhiteSpace(parameterName) ? field.Split('.').LastOrDefault() ?? "VALUE" : parameterName;
        var items = new List<object>();
        items.Add(new { field, op = "GE", value = $"{{p.{paramName}}}" });
        if (!string.IsNullOrWhiteSpace(defaultValue))
            items.Add(new { field, op = "LE", value = $"{{p.{paramName}_TO}}" });
        var template = new { logic = "AND", items };
        return new(JsonSerializer.Serialize(template, JsonOptions), null);
    }

    /// <summary>F_TYPE=2 固定单选：option 列表 + = 条件。</summary>
    private static ConvertResult ConvertFixedSelect(string? expression, string? defaultValue, string field, string? parameterName)
    {
        // 解析 F_EXPR = "标签:值;标签:值" 或 "已批核:{FIELD}=true;未批核:{FIELD}=false"
        var options = ParseOptions(expression);
        if (options.Count == 0 && string.IsNullOrWhiteSpace(expression))
            return new(null, "F_TYPE=2 但 F_EXPR 为空");

        var paramName = string.IsNullOrWhiteSpace(parameterName) ? field.Split('.').LastOrDefault() ?? "SEL" : parameterName;
        // 尝试从 F_EXPR 提取字段（如 {MOC_PRODUCE_M.CONFIRM_TAG}=true 中的字段）
        var actualField = ExtractFieldFromExpression(expression) ?? field;
        var items = new List<object>();
        items.Add(new { field = actualField, op = "EQ", value = $"{{p.{paramName}}}" });
        var template = new { logic = "AND", items, options, parameterName = paramName };
        return new(JsonSerializer.Serialize(template, JsonOptions), null);
    }

    /// <summary>F_TYPE=3 数据源单选：SELECT 列 C_ID, 列 C_VALUE FROM 表。</summary>
    private static ConvertResult ConvertDataSelect(string? expression, string? defaultValue, string field, string? parameterName)
    {
        var match = SelectSourcePattern.Match(expression ?? string.Empty);
        if (!match.Success)
            return new(null, "F_TYPE=3 但 F_EXPR 非有效 SELECT 语句");

        var paramName = string.IsNullOrWhiteSpace(parameterName) ? field.Split('.').LastOrDefault() ?? "DS" : parameterName;
        var items = new List<object>();
        items.Add(new { field, op = "EQ", value = $"{{p.{paramName}}}" });
        var template = new
        {
            logic = "AND",
            items,
            selectSource = new { table = match.Groups[3].Value, idColumn = match.Groups[1].Value, valueColumn = match.Groups[2].Value },
            parameterName = paramName
        };
        return new(JsonSerializer.Serialize(template, JsonOptions), null);
    }

    /// <summary>F_TYPE=4 固定多选：逗号分隔。</summary>
    private static ConvertResult ConvertFixedMultiSelect(string? expression, string? defaultValue, string field, string? parameterName)
    {
        var options = ParseOptions(expression);
        if (options.Count == 0 && string.IsNullOrWhiteSpace(expression))
            return new(null, "F_TYPE=4 但 F_EXPR 为空");

        var paramName = string.IsNullOrWhiteSpace(parameterName) ? field.Split('.').LastOrDefault() ?? "MS" : parameterName;
        var items = new List<object>();
        items.Add(new { field, op = "IN", value = $"{{p.{paramName}}}" });
        var template = new { logic = "AND", items, options, parameterName = paramName };
        return new(JsonSerializer.Serialize(template, JsonOptions), null);
    }

    /// <summary>F_TYPE=5 数据源多选：同 F_TYPE=3 但 IN 多值。</summary>
    private static ConvertResult ConvertDataMultiSelect(string? expression, string? defaultValue, string field, string? parameterName)
    {
        var match = SelectSourcePattern.Match(expression ?? string.Empty);
        if (!match.Success)
            return new(null, "F_TYPE=5 但 F_EXPR 非有效 SELECT 语句");

        var paramName = string.IsNullOrWhiteSpace(parameterName) ? field.Split('.').LastOrDefault() ?? "DMS" : parameterName;
        var items = new List<object>();
        items.Add(new { field, op = "IN", value = $"{{p.{paramName}}}" });
        var template = new
        {
            logic = "AND",
            items,
            selectSource = new { table = match.Groups[3].Value, idColumn = match.Groups[1].Value, valueColumn = match.Groups[2].Value },
            parameterName = paramName
        };
        return new(JsonSerializer.Serialize(template, JsonOptions), null);
    }

    /// <summary>解析选项串：标签:值;标签:值。兼容「标签:{字段}=值」旧格式（值取 = 右侧）。</summary>
    private static List<object> ParseOptions(string? expression)
    {
        var result = new List<object>();
        if (string.IsNullOrWhiteSpace(expression)) return result;
        foreach (var item in expression.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var sep = item.IndexOf(':');
            if (sep <= 0) continue;
            var label = item[..sep].Trim();
            var value = item[(sep + 1)..].Trim();
            // 兼容「{字段}=值」格式：值取 = 右侧；否则整体为值
            var eqSign = value.IndexOf('=');
            if (eqSign > 0) value = value[(eqSign + 1)..].Trim();
            result.Add(new { label, value });
        }
        return result;
    }

    /// <summary>从 F_EXPR 提取字段引用（如 {MOC_PRODUCE_M.CONFIRM_TAG}=true → MOC_PRODUCE_M.CONFIRM_TAG）。</summary>
    private static string? ExtractFieldFromExpression(string? expression)
    {
        if (string.IsNullOrWhiteSpace(expression)) return null;
        var match = Regex.Match(expression, @"\{([^}]+)\}");
        if (match.Success) return match.Groups[1].Value;
        return null;
    }
}