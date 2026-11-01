using EOS.API.Models;

namespace EOS.API.Data;

/// <summary>
/// 只读助手结构化筛选 → WorkbenchQuery 映射。
/// 字段一律从模块定义白名单解析（权限过滤后的 MasterFields），参数化由仓库层保证；
/// 目标字段在定义中不存在时记入 NotApplied，由调用方如实告知模型，绝不静默忽略。
/// </summary>
public static class AssistantQueryBuilder
{
    /// <summary>一条由模型给出的结构化筛选；字段与算子都必须落在模块定义的白名单内。</summary>
    public sealed record Filter(string Field, string Operator, string? Value = null, string? ValueTo = null, string? Logic = null);

    private static readonly HashSet<string> Operators = new(StringComparer.OrdinalIgnoreCase)
    {
        "eq", "ne", "gt", "gte", "lt", "lte",
        "contains", "notcontains", "startswith", "endswith",
        "empty", "notempty", "between",
    };

    private static readonly HashSet<string> ComparisonOperators = new(StringComparer.OrdinalIgnoreCase)
    {
        "eq", "ne", "gt", "gte", "lt", "lte", "between",
    };

    /// <summary>日期/金额格式非法时抛 ArgumentException（调用方转 400）。</summary>
    public static AssistantFilterResult Build(
        WorkbenchDefinition definition,
        string? dateFrom,
        string? dateTo,
        string? status,
        string? amountMin,
        string? amountMax,
        IReadOnlyList<Filter>? filters = null)
    {
        var notApplied = new List<string>();
        var conditions = new List<WorkbenchQueryCondition>();
        var fields = definition.MasterFields;

        if (!string.IsNullOrWhiteSpace(dateFrom) || !string.IsNullOrWhiteSpace(dateTo))
        {
            var dateField = FindDateField(fields);
            if (dateField is null)
            {
                notApplied.Add("date");
            }
            else
            {
                var from = ParseDate(dateFrom, "dateFrom");
                var to = ParseDate(dateTo, "dateTo");
                conditions.Add(from is not null && to is not null
                    ? new WorkbenchQueryCondition(dateField.Key, "between", from, to, null)
                    : from is not null
                        ? new WorkbenchQueryCondition(dateField.Key, "gte", from, null, null)
                        : new WorkbenchQueryCondition(dateField.Key, "lte", to, null, null));
            }
        }

        if (!string.IsNullOrWhiteSpace(status))
        {
            var (fieldKey, value) = ResolveStatus(fields, status!);
            if (fieldKey is null)
            {
                notApplied.Add("status");
            }
            else
            {
                conditions.Add(new WorkbenchQueryCondition(fieldKey, "eq", value, null, null));
            }
        }

        if (!string.IsNullOrWhiteSpace(amountMin) || !string.IsNullOrWhiteSpace(amountMax))
        {
            var amountField = FindAmountField(fields);
            if (amountField is null)
            {
                notApplied.Add("amount");
            }
            else
            {
                var min = ParseAmount(amountMin, "amountMin");
                var max = ParseAmount(amountMax, "amountMax");
                conditions.Add(min is not null && max is not null
                    ? new WorkbenchQueryCondition(amountField.Key, "between", min, max, null)
                    : min is not null
                        ? new WorkbenchQueryCondition(amountField.Key, "gte", min, null, null)
                        : new WorkbenchQueryCondition(amountField.Key, "lte", max, null, null));
            }
        }

        if (filters is { Count: > 0 })
        {
            if (filters.Count > 20)
            {
                notApplied.Add($"conditions（超过 20 条，只应用前 20 条）");
            }

            foreach (var filter in filters.Take(20))
            {
                var field = fields.FirstOrDefault(item =>
                    item.IsVisible
                    && item.Key.Equals(filter.Field?.Trim(), StringComparison.OrdinalIgnoreCase));
                if (field is null || field.IsVirtual)
                {
                    notApplied.Add($"field:{filter.Field}");
                    continue;
                }

                var op = filter.Operator?.Trim().ToLowerInvariant() ?? string.Empty;
                if (!Operators.Contains(op))
                {
                    notApplied.Add($"operator:{filter.Operator}");
                    continue;
                }

                if (!ComparisonOperators.Contains(op) && !field.IsQueryable)
                {
                    notApplied.Add($"field:{filter.Field}（该字段不支持此运算符）");
                    continue;
                }

                if (op == "between" && (string.IsNullOrWhiteSpace(filter.Value) || string.IsNullOrWhiteSpace(filter.ValueTo)))
                {
                    notApplied.Add($"between:{filter.Field}（缺少起始或结束值）");
                    continue;
                }

                var logic = string.Equals(filter.Logic?.Trim(), "or", StringComparison.OrdinalIgnoreCase) ? "or" : "and";
                conditions.Add(new WorkbenchQueryCondition(
                    field.Key, op, filter.Value?.Trim(), filter.ValueTo?.Trim(), null, logic));
            }
        }

        return new AssistantFilterResult(
            conditions.Count == 0 ? null : new WorkbenchQuery(conditions),
            notApplied);
    }

    /// <summary>
    /// 排序字段白名单：只接受可见字段的 Key；非法/缺失返回 null，由调用方如实告知而不是静默回退。
    /// </summary>
    public static (string? Field, string Direction) ResolveSort(
        WorkbenchDefinition definition, string? field, string? direction)
    {
        if (string.IsNullOrWhiteSpace(field))
        {
            return (null, "asc");
        }

        var match = definition.MasterFields.FirstOrDefault(item =>
            item.IsVisible && item.Key.Equals(field.Trim(), StringComparison.OrdinalIgnoreCase));
        if (match is null)
        {
            return (null, "asc");
        }

        return (match.Key, string.Equals(direction?.Trim(), "desc", StringComparison.OrdinalIgnoreCase)
            ? "desc"
            : "asc");
    }

    /// <summary>
    /// 状态归一化 → 白名单字段：approved/unapproved 命中"批核"标签字段（如 APPROVED），
    /// finished/unfinished 命中"结案"标签字段（如 FINISHED）。值按 bit 字段输出 "1"/"0"。
    /// </summary>
    public static (string? FieldKey, string Value) ResolveStatus(
        IReadOnlyList<WorkbenchField> fields,
        string status)
    {
        return status.Trim().ToLowerInvariant() switch
        {
            "approved" => (FindStatusField(fields, "批核"), "1"),
            "unapproved" => (FindStatusField(fields, "批核"), "0"),
            "finished" => (FindStatusField(fields, "结案"), "1"),
            "unfinished" => (FindStatusField(fields, "结案"), "0"),
            _ => (null, string.Empty),
        };
    }

    /// <summary>优先业务日期字段（盘点日期/采购日期/单据日期/建立日期），其次任意"日期"时间字段。</summary>
    private static WorkbenchField? FindDateField(IReadOnlyList<WorkbenchField> fields)
    {
        foreach (var preferred in new[] { "盘点日期", "采购日期", "单据日期", "建立日期" })
        {
            var field = fields.FirstOrDefault(f =>
                f.IsQueryable && IsDateLike(f.DataType) &&
                f.Label.Contains(preferred, StringComparison.OrdinalIgnoreCase));
            if (field is not null)
            {
                return field;
            }
        }

        return fields.FirstOrDefault(f => f.IsQueryable && IsDateLike(f.DataType) && f.Label.Contains("日期"));
    }

    private static WorkbenchField? FindAmountField(IReadOnlyList<WorkbenchField> fields)
        => fields.FirstOrDefault(f =>
            f.IsQueryable && IsNumericLike(f.DataType) &&
            (f.Label.Contains("金额") || f.Label.Contains("总额")));

    /// <summary>状态字段：标签同时含"批核/结案"与"状态"（排除"批核日期/批核人"等非状态列）。</summary>
    private static string? FindStatusField(IReadOnlyList<WorkbenchField> fields, string labelPart)
        => fields.FirstOrDefault(f =>
            f.Label.Contains(labelPart, StringComparison.OrdinalIgnoreCase) &&
            f.Label.Contains("状态", StringComparison.OrdinalIgnoreCase))?.Key;

    private static string? ParseDate(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (!DateTime.TryParse(value.Trim(), out var parsed))
        {
            throw new ArgumentException($"{name} 不是有效日期");
        }

        return parsed.ToString("yyyy-MM-dd");
    }

    private static string? ParseAmount(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (!decimal.TryParse(value.Trim(), System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out _))
        {
            throw new ArgumentException($"{name} 不是有效金额");
        }

        return value.Trim();
    }

    private static bool IsDateLike(string dataType)
        => dataType.Contains("date", StringComparison.OrdinalIgnoreCase) ||
           dataType.Contains("time", StringComparison.OrdinalIgnoreCase);

    private static bool IsNumericLike(string dataType)
    {
        var type = dataType.ToLowerInvariant();
        return type is "int" or "bigint" or "smallint" or "tinyint" or "float" or "real"
            or "decimal" or "numeric" or "money" or "smallmoney"
            || type.Contains("decimal") || type.Contains("numeric")
            || type.Contains("float") || type.Contains("int");
    }
}

public sealed record AssistantFilterResult(WorkbenchQuery? Query, IReadOnlyList<string> NotApplied);
