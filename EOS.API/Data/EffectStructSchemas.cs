using System.Text.Json;
using System.Text.RegularExpressions;

namespace EOS.API.Data;

/// <summary>
/// 效果参数/反向结构 Schema v0.1。
/// 当前登记：每个效果键允许的根键白名单（来自翻译落库数据），
/// 反向结构 kind 枚举，以及库存类 fieldMap 的闭式数量表达
/// （单字段标识符 或 terms 加减项，禁止拼接表达式字符串）。
/// 深度 Schema（服务效果各键的字段级约束）随效果注册细化，但新增根键必须先登记。
/// </summary>
public static class EffectStructSchemas
{
    private static readonly Regex Identifier = new(@"^[A-Z][A-Z0-9_]*$", RegexOptions.Compiled);
    private static readonly Regex TermField = new(@"^[A-Z][A-Z0-9_]*$", RegexOptions.Compiled);

    /// <summary>反向结构 kind 枚举（覆盖现有种子与快照补偿语义）。</summary>
    private static readonly IReadOnlySet<string> ReverseKinds = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "auto-reverse",
        "no-reverse",
        "recompute",
        "reverse-flow",
        "snapshot",
    };

    /// <summary>反向结构允许的根键。</summary>
    private static readonly IReadOnlySet<string> ReverseRootKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "kind",
        "note",
    };

    /// <summary>库存类 fieldMap 允许的键。</summary>
    private static readonly IReadOnlySet<string> FieldMapKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "masterDate",
        "qty",
        "detail",
        "amount",
    };

    /// <summary>参数级条件允许的类型（与动作级 CONDITION_STRUCT 同族封闭集）。</summary>
    private static readonly IReadOnlySet<string> ConditionTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "field-compare",
        "value-eq",
        "value-neq",
        "not-exists",
        "switch",
    };

    private static readonly IReadOnlySet<string> CompareOperators = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "EQ",
        "NEQ",
        "GT",
        "GE",
        "LT",
        "LE",
    };

    /// <summary>效果键 → 允许的参数根键（v0.1，数据来源：P1 翻译落库参数）。</summary>
    private static readonly IReadOnlyDictionary<string, IReadOnlySet<string>> ParamRootKeys =
        new Dictionary<string, IReadOnlySet<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["adjust-projection"] = Set("mode", "inFields", "getFields"),
            ["balance-adjust"] = Set("client", "supplier", "bank"),
            ["callback-reprice"] = Set("fields", "sendTargets", "returnTargets", "deapprove"),
            ["client-price-sync"] = Set("master", "detail", "quoteRefs", "preserveOld"),
            ["completion-close"] = Set("targets", "condition", "marker", "direction", "offsets"),
            ["employee-contract-sync"] = Set("targetTable", "fields", "source", "includeSelfOnApprove"),
            ["field-accumulate"] = Set("mode", "targets"),
            ["field-copy"] = Set("targetTable", "field", "fields", "targets", "sourceField", "headerFields"),
            ["hr-usage-sync"] = Set("targetTable", "scopeKey", "sourceFields", "targetFields"),
            ["inventory-move"] = Set("direction", "mrp", "depotField", "targetTable", "fieldMap"),
            ["link-stamp"] = Set("targetTable", "field", "fields", "targets", "mode", "finish"),
            ["mould-balance-adjust"] = Set("targetTable", "mode", "qty", "amount", "dateField", "dateFields", "dateMode", "dateValueField", "useCountField"),
            ["mould-batch-apply"] = Set("branchField", "acceptTargets", "scrapTarget", "productFields"),
            ["mrp-plan-alloc"] = Set("targetTable", "mode", "scope", "field", "fields", "stockSource", "note"),
            ["order-change-apply"] = Set("detail", "totals"),
            ["payment-date-calc"] = Set("targetField", "dateField", "monthField", "paymentDaysFrom"),
            ["produce-change-apply"] = Set("master", "detail"),
            ["purchase-change-apply"] = Set("master", "detail", "totals"),
            ["quote-parameter-recalc"] = Set("targetTable", "mode", "feeFields"),
            ["return-writeback"] = Set("order", "produce", "conditions"),
            ["sample-stock-adjust"] = Set("direction", "targetTable", "fieldMap"),
            ["set-state"] = Set("targetTable", "stateField", "stateValue", "state", "sourceField", "source", "dateField", "dateMode", "targets"),
            ["stamp-last-activity"] = Set("targetTable", "field", "fields", "sourceField", "matchBy", "condition"),
            ["supplier-price-sync"] = Set("master", "detail", "quoteRefs", "preserveOld"),
        };

    /// <summary>校验动作级 params/reverse 结构，返回问题列表。</summary>
    public static IReadOnlyList<string> ValidateActionStructs(
        string effectKey,
        string? paramsJson,
        string? reverseJson)
    {
        var issues = new List<string>();
        if (!string.IsNullOrWhiteSpace(paramsJson))
            issues.AddRange(ValidateParams(effectKey, paramsJson!));
        if (!string.IsNullOrWhiteSpace(reverseJson))
            issues.AddRange(ValidateReverse(reverseJson!));
        return issues;
    }

    public static IReadOnlyList<string> ValidateReverse(string json)
    {
        var issues = new List<string>();
        if (!TryParseObject(json, out var root, out var parseIssue))
        {
            issues.Add("反向结构不是合法 JSON 对象：" + parseIssue);
            return issues;
        }
        foreach (var property in root.EnumerateObject())
        {
            if (!ReverseRootKeys.Contains(property.Name))
                issues.Add($"反向结构含未登记键 '{property.Name}'（仅允许 kind/note）。");
        }
        if (!root.TryGetProperty("kind", out var kind) || kind.ValueKind != JsonValueKind.String)
        {
            issues.Add("反向结构缺少字符串 kind。");
        }
        else if (!ReverseKinds.Contains(kind.GetString()!))
        {
            issues.Add($"反向结构 kind '{kind.GetString()}' 不在枚举内。");
        }
        if (root.TryGetProperty("note", out var note) && note.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
            issues.Add("反向结构 note 必须是字符串。");
        return issues;
    }

    public static IReadOnlyList<string> ValidateParams(string effectKey, string json)
    {
        var issues = new List<string>();
        if (!TryParseObject(json, out var root, out var parseIssue))
        {
            issues.Add("效果参数不是合法 JSON 对象：" + parseIssue);
            return issues;
        }
        if (!ParamRootKeys.TryGetValue(effectKey, out var allowed))
        {
            issues.Add($"效果键 '{effectKey}' 尚未登记参数 Schema（保留键/未注册键不得携带参数）。");
            return issues;
        }
        foreach (var property in root.EnumerateObject())
        {
            if (!allowed.Contains(property.Name))
                issues.Add($"效果参数含未登记根键 '{property.Name}'（键 '{effectKey}' 允许：{string.Join(",", allowed)}）。");
            ValidateClosedStrings(property.Value, property.Name, issues);
        }
        if ((effectKey.Equals("completion-close", StringComparison.OrdinalIgnoreCase)
                || effectKey.Equals("stamp-last-activity", StringComparison.OrdinalIgnoreCase))
            && root.TryGetProperty("condition", out var condition))
        {
            issues.AddRange(ValidateConditionStruct(condition));
        }
        if (effectKey.Equals("inventory-move", StringComparison.OrdinalIgnoreCase)
            || effectKey.Equals("sample-stock-adjust", StringComparison.OrdinalIgnoreCase))
        {
            issues.AddRange(ValidateFieldMap(root));
        }
        return issues;
    }

    /// <summary>查询效果键的参数根键白名单（编辑器下拉/表单渲染用）。</summary>
    public static bool TryGetParamRootKeys(string effectKey, out IReadOnlyList<string> rootKeys)
    {
        if (ParamRootKeys.TryGetValue(effectKey, out var keys))
        {
            rootKeys = keys.OrderBy(key => key, StringComparer.OrdinalIgnoreCase).ToList();
            return true;
        }
        rootKeys = [];
        return false;
    }

    public static IReadOnlyList<string> AllReverseKinds() =>
        ReverseKinds.OrderBy(kind => kind, StringComparer.OrdinalIgnoreCase).ToList();

    private static IReadOnlyList<string> ValidateConditionStruct(JsonElement value)
    {
        var issues = new List<string>();
        if (value.ValueKind != JsonValueKind.Object
            || !value.TryGetProperty("logic", out var logic) || logic.ValueKind != JsonValueKind.String
            || logic.GetString() is not ("AND" or "OR"))
        {
            issues.Add("参数 condition 必须是 {logic:AND/OR, items:[…]} 结构化条件。");
            return issues;
        }
        if (!value.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array || items.GetArrayLength() == 0)
        {
            issues.Add("参数 condition.items 必须是非空数组。");
            return issues;
        }
        foreach (var item in items.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("type", out var type)
                || type.ValueKind != JsonValueKind.String || !ConditionTypes.Contains(type.GetString()!))
            {
                issues.Add($"参数 condition.items 存在非法类型 '{item}'。");
                continue;
            }
            if (type.GetString()!.Equals("field-compare", StringComparison.OrdinalIgnoreCase)
                && (!item.TryGetProperty("op", out var op) || op.ValueKind != JsonValueKind.String
                    || !CompareOperators.Contains(op.GetString()!)))
            {
                issues.Add("参数 condition field-compare 缺少闭式 op。");
            }
        }
        return issues;
    }

    private static IReadOnlyList<string> ValidateFieldMap(JsonElement root)
    {
        var issues = new List<string>();
        if (!root.TryGetProperty("fieldMap", out var fieldMap) || fieldMap.ValueKind != JsonValueKind.Object)
        {
            issues.Add("库存类效果参数缺少 fieldMap 对象。");
            return issues;
        }
        foreach (var property in fieldMap.EnumerateObject())
        {
            if (!FieldMapKeys.Contains(property.Name))
            {
                issues.Add($"fieldMap 含未登记键 '{property.Name}'（仅允许 masterDate/qty/detail/amount）。");
                continue;
            }
            switch (property.Name)
            {
                case "qty":
                    ValidateQty(property.Value, issues);
                    break;
                case "detail":
                    if (property.Value.ValueKind != JsonValueKind.Array)
                        issues.Add("fieldMap.detail 必须是字段名数组。");
                    else
                        foreach (var item in property.Value.EnumerateArray())
                            if (item.ValueKind != JsonValueKind.String || !Identifier.IsMatch(item.GetString() ?? string.Empty))
                                issues.Add($"fieldMap.detail 存在非法字段名 '{item}'。");
                    break;
                case "masterDate":
                case "amount":
                    if (property.Value.ValueKind != JsonValueKind.String || !Identifier.IsMatch(property.Value.GetString() ?? string.Empty))
                        issues.Add($"fieldMap.{property.Name} 必须是字段名（大写标识符）。");
                    break;
            }
        }
        return issues;
    }

    private static void ValidateQty(JsonElement value, ICollection<string> issues)
    {
        if (value.ValueKind == JsonValueKind.String)
        {
            var text = value.GetString() ?? string.Empty;
            if (!Identifier.IsMatch(text))
                issues.Add($"fieldMap.qty 单字段形态必须是字段名（当前 '{text}' 含拼接/非法字符）。");
            return;
        }
        if (value.ValueKind == JsonValueKind.Object
            && value.TryGetProperty("terms", out var terms) && terms.ValueKind == JsonValueKind.Array)
        {
            if (terms.GetArrayLength() == 0)
            {
                issues.Add("fieldMap.qty.terms 不能为空。");
                return;
            }
            foreach (var term in terms.EnumerateArray())
            {
                var field = term.ValueKind == JsonValueKind.Object
                    && term.TryGetProperty("field", out var f) && f.ValueKind == JsonValueKind.String
                        ? f.GetString()
                        : null;
                var coef = term.ValueKind == JsonValueKind.Object
                    && term.TryGetProperty("coef", out var c) && c.ValueKind == JsonValueKind.Number
                    && c.TryGetInt32(out var n)
                        ? n
                        : (int?)null;
                if (field is null || !TermField.IsMatch(field))
                    issues.Add("fieldMap.qty.terms 存在非法 field。");
                if (coef is not (1 or -1))
                    issues.Add("fieldMap.qty.terms 的 coef 仅允许 1 / -1。");
            }
            return;
        }
        issues.Add("fieldMap.qty 必须是单字段名或 {terms:[…]} 闭式加减项。");
    }

    /// <summary>叶子字符串闭式守卫：禁止表达式算子与拼接符（kebab/下划线词不受影响）。</summary>
    private static void ValidateClosedStrings(JsonElement value, string path, ICollection<string> issues)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.String:
                var text = value.GetString() ?? string.Empty;
                if (text.Contains('+') || text.Contains('*')
                    || text.Contains('(') || text.Contains(')') || text.Contains('=')
                    || text.Contains('<') || text.Contains('>'))
                {
                    issues.Add($"{path} 含表达式/拼接形态，违反闭式参数约定：'{text}'。");
                }
                break;
            case JsonValueKind.Array:
                foreach (var item in value.EnumerateArray())
                    ValidateClosedStrings(item, path, issues);
                break;
            case JsonValueKind.Object:
                foreach (var property in value.EnumerateObject())
                    ValidateClosedStrings(property.Value, $"{path}.{property.Name}", issues);
                break;
        }
    }

    private static bool TryParseObject(string json, out JsonElement root, out string issue)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                root = default;
                issue = "根节点必须是对象";
                return false;
            }
            root = doc.RootElement.Clone();
            issue = string.Empty;
            return true;
        }
        catch (JsonException exception)
        {
            root = default;
            issue = exception.Message;
            return false;
        }
    }

    private static IReadOnlySet<string> Set(params string[] keys) =>
        new HashSet<string>(keys, StringComparer.OrdinalIgnoreCase);
}
