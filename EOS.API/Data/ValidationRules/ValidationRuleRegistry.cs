using System.Text.Json;
using System.Text.RegularExpressions;

namespace EOS.API.Data.ValidationRules;

/// <summary>
/// 校验模板注册与参数 Schema 校验（v0）：
/// 目前只做结构与闭式集合校验（Key/阶段/参数形态），物理表字段、关系注册校验由接入
/// Definition 校验器时补充。禁止按模块号分支。
/// </summary>
public static class ValidationRuleRegistry
{
    /// <summary>所有模板共用的可选键：params.when ＝ 规则级适用条件（不成立即跳过该校验）。</summary>
    private static readonly IReadOnlySet<string> SharedParamKeys = KeySet("when");

    private static readonly IReadOnlyDictionary<string, IReadOnlySet<string>> ParamKeysByTemplate =
        new Dictionary<string, IReadOnlySet<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["qty-not-exceed"] = WithWhen(KeySet("mode", "checks")),
            ["reference-exists"] = WithWhen(KeySet("checks")),
            ["duplicate-check"] = WithWhen(KeySet(
                "mode", "table", "keyFields", "keySource", "excludeSelf", "filter", "diagnostics",
                "masterTable", "detailTable", "joinFields", "groupFields", "masterGroupFields",
                "documentDetailFields", "diagnosticFields", "maxRows")),
            ["line-require"] = WithWhen(KeySet("checks")),
        };

    private static readonly IReadOnlySet<string> QtyCheckKeys = KeySet("targetTable", "match", "thisQty", "usage", "limit", "offset", "message", "switch");
    private static readonly IReadOnlySet<string> QtySwitchKeys = KeySet("key", "expect");
    private static readonly IReadOnlySet<string> LineRequireCheckKeys = KeySet("scope", "field", "triggers", "message");
    private static readonly IReadOnlySet<string> LineRequireTriggerKeys = KeySet("scope", "field", "op", "value");
    private static readonly IReadOnlySet<string> LineRequireOps = KeySet("GT", "GE", "LT", "LE", "EQ", "NEQ");
    private static readonly IReadOnlySet<string> QtyBlockKeys = KeySet("scope", "terms", "fields");
    private static readonly IReadOnlySet<string> ReferenceCheckKeys = KeySet("refTable", "allowEmpty", "join", "refKey", "activeTag", "message", "lineField");
    private static readonly IReadOnlySet<string> ReferencePairKeys = KeySet("target", "source");
    private static readonly IReadOnlySet<string> ExcludeSelfKeys = KeySet("keyFields");
    private static readonly IReadOnlySet<string> KeySourceKeys = KeySet("scope", "fields");

    private static readonly HashSet<string> KnownKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "qty-not-exceed",
        "reference-exists",
        "duplicate-check",
        "line-require",
    };

    private static readonly HashSet<string> KnownStages = new(StringComparer.OrdinalIgnoreCase)
    {
        "SAVE",
        "APPROVE",
        "DEAPPROVE",
    };

    private static readonly HashSet<string> KnownScopes = new(StringComparer.OrdinalIgnoreCase)
    {
        "MASTER",
        "DETAIL",
        "TABLE",
        "TARGET",
        "CONSTANT",
    };

    public static bool IsKnownKey(string key) => KnownKeys.Contains(key);

    public static bool IsKnownStage(string stage) => KnownStages.Contains(stage);

    public static void Validate(ValidationRuleConfig rule, List<string> issues)
    {
        if (string.IsNullOrWhiteSpace(rule.RuleId))
            issues.Add($"校验规则缺少 ruleId");
        if (!IsKnownKey(rule.ValidationKey))
            issues.Add($"校验规则 {Label(rule)}：未知 validationKey '{rule.ValidationKey}'");
        if (!IsKnownStage(rule.Stage))
            issues.Add($"校验规则 {Label(rule)}：未知 stage '{rule.Stage}'（仅 SAVE/APPROVE/DEAPPROVE）");
        if (rule.Params is not { } p || p.ValueKind != JsonValueKind.Object)
        {
            issues.Add($"校验规则 {Label(rule)}：params 必须是 JSON 对象");
            return;
        }
        if (ParamKeysByTemplate.TryGetValue(rule.ValidationKey, out var allowedParams))
            RejectUnknownKeys(rule, p, allowedParams, "params", issues);
        if (GetObject(p, "when") is { } when)
            ValidateConditionShape(rule, when, "when", issues);
        switch (rule.ValidationKey.ToLowerInvariant())
        {
            case "qty-not-exceed":
                ValidateQtyNotExceed(rule, p, issues);
                break;
            case "reference-exists":
                ValidateReferenceExists(rule, p, issues);
                break;
            case "duplicate-check":
                ValidateDuplicateCheck(rule, p, issues);
                break;
            case "line-require":
                ValidateLineRequire(rule, p, issues);
                break;
        }
    }

    private static void ValidateQtyNotExceed(ValidationRuleConfig rule, JsonElement p, List<string> issues)
    {
        var mode = GetString(p, "mode");
        if (mode is not ("usage-not-exceed" or "not-below-progress" or "this-not-exceed"))
            issues.Add($"校验规则 {Label(rule)}：qty-not-exceed.mode 仅允许 usage-not-exceed / not-below-progress / this-not-exceed");
        var requireLimit = mode is null or "usage-not-exceed" or "this-not-exceed";
        var checks = GetArray(p, "checks");
        if (checks is not { } arr || arr.GetArrayLength() == 0)
        {
            issues.Add($"校验规则 {Label(rule)}：qty-not-exceed.checks 必须是非空数组");
            return;
        }
        var index = 0;
        foreach (var check in arr.EnumerateArray())
        {
            var where = $"checks[{index}]";
            if (check.ValueKind != JsonValueKind.Object)
            {
                issues.Add($"校验规则 {Label(rule)}：{where} 必须是对象");
                index++;
                continue;
            }
            RejectUnknownKeys(rule, check, QtyCheckKeys, where, issues);
            var switchElement = GetObject(check, "switch");
            if (switchElement is not null)
                ValidateSwitchShape(rule, switchElement.Value, $"{where}.switch", issues);
            var match = GetArray(check, "match");
            if (match is null || match.Value.GetArrayLength() == 0)
                issues.Add($"校验规则 {Label(rule)}：{where}.match 必须是非空数组");
            else
                ValidateMatchItems(rule, match.Value, $"{where}.match", issues);
            if (!ValidateQtyBlock(rule, check, "thisQty", $"{where}.thisQty", issues))
                issues.Add($"校验规则 {Label(rule)}：{where}.thisQty 缺失");
            var requireUsage = mode is null or "usage-not-exceed" or "not-below-progress";
            if (requireUsage && !ValidateQtyBlock(rule, check, "usage", $"{where}.usage", issues))
                issues.Add($"校验规则 {Label(rule)}：{where}.usage 缺失");
            var hasLimit = GetObject(check, "limit") is not null;
            if (requireLimit && !hasLimit)
                issues.Add($"校验规则 {Label(rule)}：{where}.limit 缺失（{mode ?? "usage-not-exceed"} 模式必须给出限额）");
            if (hasLimit)
                ValidateQtyBlock(rule, check, "limit", $"{where}.limit", issues);
            var offset = GetObject(check, "offset");
            if (offset is not null)
                ValidateQtyBlock(rule, offset.Value, "offset", $"{where}.offset", issues);
            index++;
        }
    }

    private static void ValidateSwitchShape(ValidationRuleConfig rule, JsonElement element, string where, List<string> issues)
    {
        RejectUnknownKeys(rule, element, QtySwitchKeys, where, issues);
        if (string.IsNullOrWhiteSpace(GetString(element, "key")))
            issues.Add($"校验规则 {Label(rule)}：{where}.key 不能为空（SYSSS 开关列名）");
        if (element.TryGetProperty("expect", out var expect) && expect.ValueKind != JsonValueKind.Number)
            issues.Add($"校验规则 {Label(rule)}：{where}.expect 必须是数字（0/1）");
    }

    /// <summary>
    /// line-require: document lines matching any trigger must carry a non-empty
    /// field (v1: DETAIL scope only, e.g. bad-quantity lines require a bad depot).
    /// </summary>
    private static void ValidateLineRequire(ValidationRuleConfig rule, JsonElement p, List<string> issues)
    {
        var checks = GetArray(p, "checks");
        if (checks is not { } arr || arr.GetArrayLength() == 0)
        {
            issues.Add($"校验规则 {Label(rule)}：line-require.checks 必须是非空数组");
            return;
        }
        var index = 0;
        foreach (var check in arr.EnumerateArray())
        {
            var where = $"checks[{index}]";
            if (check.ValueKind != JsonValueKind.Object)
            {
                issues.Add($"校验规则 {Label(rule)}：{where} 必须是对象");
                index++;
                continue;
            }
            RejectUnknownKeys(rule, check, LineRequireCheckKeys, where, issues);
            var scope = GetString(check, "scope");
            if (!string.Equals(scope, "DETAIL", StringComparison.OrdinalIgnoreCase))
                issues.Add($"校验规则 {Label(rule)}：{where}.scope 仅允许 DETAIL");
            if (string.IsNullOrWhiteSpace(GetString(check, "field")))
                issues.Add($"校验规则 {Label(rule)}：{where}.field 不能为空");
            var triggers = GetArray(check, "triggers");
            if (triggers is not { } triggersArr || triggersArr.GetArrayLength() == 0)
            {
                issues.Add($"校验规则 {Label(rule)}：{where}.triggers 必须是非空数组");
            }
            else
            {
                var triggerIndex = 0;
                foreach (var trigger in triggersArr.EnumerateArray())
                {
                    var triggerWhere = $"{where}.triggers[{triggerIndex}]";
                    if (trigger.ValueKind != JsonValueKind.Object)
                    {
                        issues.Add($"校验规则 {Label(rule)}：{triggerWhere} 必须是对象");
                    }
                    else
                    {
                        RejectUnknownKeys(rule, trigger, LineRequireTriggerKeys, triggerWhere, issues);
                        if (!string.Equals(GetString(trigger, "scope"), "DETAIL", StringComparison.OrdinalIgnoreCase))
                            issues.Add($"校验规则 {Label(rule)}：{triggerWhere}.scope 仅允许 DETAIL");
                        if (string.IsNullOrWhiteSpace(GetString(trigger, "field")))
                            issues.Add($"校验规则 {Label(rule)}：{triggerWhere}.field 不能为空");
                        var op = GetString(trigger, "op");
                        if (op is null || !LineRequireOps.Contains(op))
                            issues.Add($"校验规则 {Label(rule)}：{triggerWhere}.op 仅允许 GT/GE/LT/LE/EQ/NEQ");
                        if (trigger.TryGetProperty("value", out var value) && value.ValueKind != JsonValueKind.Number)
                            issues.Add($"校验规则 {Label(rule)}：{triggerWhere}.value 必须是数字");
                    }
                    triggerIndex++;
                }
            }
            index++;
        }
    }

    private static bool ValidateQtyBlock(ValidationRuleConfig rule, JsonElement parent, string prop, string where, List<string> issues)
    {
        var block = GetObject(parent, prop);
        if (block is null)
        {
            return false;
        }
        RejectUnknownKeys(rule, block.Value, QtyBlockKeys, where, issues);
        var scope = GetString(block.Value, "scope");
        if (scope is null || !KnownScopes.Contains(scope))
            issues.Add($"校验规则 {Label(rule)}：{where}.scope 必须是 MASTER/DETAIL/TABLE/TARGET/CONSTANT");
        var terms = GetArray(block.Value, "terms");
        if (terms is { } t && t.GetArrayLength() > 0)
        {
            foreach (var term in t.EnumerateArray())
            {
                var field = GetString(term, "field");
                if (string.IsNullOrWhiteSpace(field))
                    issues.Add($"校验规则 {Label(rule)}：{where}.terms[].field 不能为空");
                var coef = GetInt32(term, "coef");
                if (coef is not (1 or -1))
                    issues.Add($"校验规则 {Label(rule)}：{where}.terms[].coef 仅允许 1 / -1");
            }
        }
        else
        {
            var fields = GetArray(block.Value, "fields");
            if (fields is not { } f || f.GetArrayLength() == 0)
                issues.Add($"校验规则 {Label(rule)}：{where} 需要 terms 或 fields 之一");
        }
        return true;
    }

    private static void ValidateMatchItems(ValidationRuleConfig rule, JsonElement match, string where, List<string> issues)
    {
        foreach (var item in match.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                issues.Add($"校验规则 {Label(rule)}：{where}[] 必须是对象");
                continue;
            }
            RejectUnknownKeys(rule, item, ReferencePairKeys, where + "[]", issues);
            if (string.IsNullOrWhiteSpace(GetString(item, "target")))
                issues.Add($"校验规则 {Label(rule)}：{where}[].target 不能为空");
            var source = GetObject(item, "source");
            if (source is null || string.IsNullOrWhiteSpace(GetString(source.Value, "field")))
                issues.Add($"校验规则 {Label(rule)}：{where}[].source.field 不能为空");
            if (source is { } sourceObject)
            {
                var allowedSource = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "scope", "field", "table", "constant",
                };
                RejectUnknownKeys(rule, sourceObject, allowedSource, where + "[].source", issues);
            }
        }
    }

    private static void ValidateReferenceExists(ValidationRuleConfig rule, JsonElement p, List<string> issues)
    {
        var checks = GetArray(p, "checks");
        if (checks is not { } arr || arr.GetArrayLength() == 0)
        {
            issues.Add($"校验规则 {Label(rule)}：reference-exists.checks 必须是非空数组");
            return;
        }
        var index = 0;
        foreach (var check in arr.EnumerateArray())
        {
            var where = $"checks[{index}]";
            if (check.ValueKind != JsonValueKind.Object)
            {
                issues.Add($"校验规则 {Label(rule)}：{where} 必须是对象");
                index++;
                continue;
            }
            RejectUnknownKeys(rule, check, ReferenceCheckKeys, where, issues);
            if (string.IsNullOrWhiteSpace(GetString(check, "refTable")))
                issues.Add($"校验规则 {Label(rule)}：{where}.refTable 不能为空");
            var refKey = GetObject(check, "refKey");
            var join = GetArray(check, "join");
            if (refKey is null && (join is null || join.Value.GetArrayLength() == 0))
                issues.Add($"校验规则 {Label(rule)}：{where} 需要 refKey 或 join");
            if (refKey is not null && string.IsNullOrWhiteSpace(GetString(refKey.Value, "field")))
                issues.Add($"校验规则 {Label(rule)}：{where}.refKey.field 不能为空");
            if (refKey is { } refKeyObject)
            {
                var allowedRefKey = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "scope", "field" };
                RejectUnknownKeys(rule, refKeyObject, allowedRefKey, where + ".refKey", issues);
            }
            if (join is { } joinArray)
                foreach (var pair in joinArray.EnumerateArray())
                {
                    if (pair.ValueKind != JsonValueKind.Object)
                    {
                        issues.Add($"校验规则 {Label(rule)}：{where}.join[] 必须是对象");
                        continue;
                    }
                    RejectUnknownKeys(rule, pair, ReferencePairKeys, where + ".join[]", issues);
                    if (string.IsNullOrWhiteSpace(GetString(pair, "target")))
                        issues.Add($"校验规则 {Label(rule)}：{where}.join[].target 不能为空");
                    var pairSource = GetObject(pair, "source");
                    if (pairSource is null || string.IsNullOrWhiteSpace(GetString(pairSource.Value, "field")))
                        issues.Add($"校验规则 {Label(rule)}：{where}.join[].source.field 不能为空");
                    if (pairSource is { } pairSourceObject)
                    {
                        var allowedPairSource = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                        {
                            "scope", "field", "table", "constant",
                        };
                        RejectUnknownKeys(rule, pairSourceObject, allowedPairSource, where + ".join[].source", issues);
                    }
                }
            if (check.TryGetProperty("allowEmpty", out var allowEmpty)
                && allowEmpty.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                issues.Add($"校验规则 {Label(rule)}：{where}.allowEmpty 必须是布尔值");
            if (check.TryGetProperty("activeTag", out var activeTag))
            {
                if (activeTag.ValueKind != JsonValueKind.Object)
                {
                    issues.Add($"校验规则 {Label(rule)}：{where}.activeTag 必须是对象");
                }
                else
                {
                    var activeKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "field", "expect" };
                    RejectUnknownKeys(rule, activeTag, activeKeys, where + ".activeTag", issues);
                    if (string.IsNullOrWhiteSpace(GetString(activeTag, "field")))
                        issues.Add($"校验规则 {Label(rule)}：{where}.activeTag.field 不能为空");
                    if (activeTag.TryGetProperty("expect", out var expect) && expect.ValueKind != JsonValueKind.Number)
                        issues.Add($"校验规则 {Label(rule)}：{where}.activeTag.expect 必须是数字");
                }
            }
            index++;
        }
    }

    private static void ValidateDuplicateCheck(ValidationRuleConfig rule, JsonElement p, List<string> issues)
    {
        var mode = GetString(p, "mode");
        if (mode is not ("within-doc" or "entity" or "master-detail"))
            issues.Add($"校验规则 {Label(rule)}：duplicate-check.mode 仅允许 within-doc / entity / master-detail");
        if (mode == "master-detail")
        {
            ValidateMasterDetailUnique(rule, p, issues);
            return;
        }
        var withinDoc = mode == "within-doc";
        if (mode == "entity" && string.IsNullOrWhiteSpace(GetString(p, "table")))
            issues.Add($"校验规则 {Label(rule)}：duplicate-check.table 必填（entity 模式）");
        var keyFields = GetArray(p, "keyFields");
        if (keyFields is not { } k || k.GetArrayLength() == 0)
            issues.Add($"校验规则 {Label(rule)}：duplicate-check.keyFields 必须是非空数组");
        ValidateFilterAndDiagnostics(rule, p, withinDoc, issues);
        ValidatePlaceholders(rule, issues);
    }

    /// <summary>
    /// master-detail 形态：跨单据的"主表维度 × 明细分组键"唯一。要求声明主从表、关联列、
    /// 分组键与主表比较维度；documentDetailFields 为可选的"本单"限定（数量须与单据主键一致，
    /// 执行期校验）；diagnosticFields/maxRows 控制多行诊断。
    /// </summary>
    private static void ValidateMasterDetailUnique(ValidationRuleConfig rule, JsonElement p, List<string> issues)
    {
        if (string.IsNullOrWhiteSpace(GetString(p, "masterTable")))
            issues.Add($"校验规则 {Label(rule)}：duplicate-check.masterTable 必填（master-detail 模式）");
        if (string.IsNullOrWhiteSpace(GetString(p, "detailTable")))
            issues.Add($"校验规则 {Label(rule)}：duplicate-check.detailTable 必填（master-detail 模式）");
        if (GetArray(p, "groupFields") is not { } groupFields || groupFields.GetArrayLength() == 0)
            issues.Add($"校验规则 {Label(rule)}：duplicate-check.groupFields 必须是非空数组");
        if (GetArray(p, "masterGroupFields") is not { } masterGroupFields || masterGroupFields.GetArrayLength() == 0)
            issues.Add($"校验规则 {Label(rule)}：duplicate-check.masterGroupFields 必须是非空数组");

        var joinFields = GetObject(p, "joinFields");
        if (joinFields is null)
        {
            issues.Add($"校验规则 {Label(rule)}：duplicate-check.joinFields 必填（master-detail 模式）");
        }
        else
        {
            RejectUnknownKeys(rule, joinFields.Value, KeySet("master", "detail"), "joinFields", issues);
            var joinMaster = GetArray(joinFields.Value, "master");
            var joinDetail = GetArray(joinFields.Value, "detail");
            if (joinMaster is not { } masterArray || masterArray.GetArrayLength() == 0)
                issues.Add($"校验规则 {Label(rule)}：duplicate-check.joinFields.master 必须是非空数组");
            else if (joinDetail is not { } detailArray || detailArray.GetArrayLength() != masterArray.GetArrayLength())
                issues.Add($"校验规则 {Label(rule)}：duplicate-check.joinFields.detail 数量必须与 master 一致");
        }

        if (GetArray(p, "documentDetailFields") is { } documentDetailFields && documentDetailFields.GetArrayLength() == 0)
            issues.Add($"校验规则 {Label(rule)}：duplicate-check.documentDetailFields 不能为空数组（不需要则省略该键）");
        if (GetArray(p, "diagnosticFields") is { } diagnosticFields && diagnosticFields.GetArrayLength() == 0)
            issues.Add($"校验规则 {Label(rule)}：duplicate-check.diagnosticFields 不能为空数组（不需要则省略该键）");
        if (p.TryGetProperty("maxRows", out var maxRows)
            && (maxRows.ValueKind != JsonValueKind.Number || !maxRows.TryGetInt32(out var rows) || rows is < 1 or > 100))
        {
            issues.Add($"校验规则 {Label(rule)}：duplicate-check.maxRows 必须是 1..100 的整数");
        }
        ValidateFilterAndDiagnostics(rule, p, withinDoc: false, issues);
        ValidatePlaceholders(rule, issues);
    }

    private static void ValidateFilterAndDiagnostics(
        ValidationRuleConfig rule,
        JsonElement p,
        bool withinDoc,
        List<string> issues)
    {
        var exclude = GetObject(p, "excludeSelf");
        if (exclude is not null)
        {
            RejectUnknownKeys(rule, exclude.Value, ExcludeSelfKeys, "excludeSelf", issues);
            var excludeKeys = GetArray(exclude.Value, "keyFields");
            if (excludeKeys is null || excludeKeys.Value.GetArrayLength() == 0)
                issues.Add($"校验规则 {Label(rule)}：excludeSelf.keyFields 必须是非空数组");
        }
        var keySource = GetObject(p, "keySource");
        if (keySource is not null)
        {
            RejectUnknownKeys(rule, keySource.Value, KeySourceKeys, "keySource", issues);
            var keyScope = GetString(keySource.Value, "scope");
            if (keyScope is not ("MASTER" or "DETAIL"))
                issues.Add($"校验规则 {Label(rule)}：keySource.scope 仅允许 MASTER / DETAIL");
            var keySourceFields = GetArray(keySource.Value, "fields");
            if (keySourceFields is null || keySourceFields.Value.GetArrayLength() == 0)
                issues.Add($"校验规则 {Label(rule)}：keySource.fields 必须是非空数组");
            else if (GetArray(p, "keyFields") is { } declaredKeys && declaredKeys.GetArrayLength() != keySourceFields.Value.GetArrayLength())
            {
                issues.Add(
                    $"校验规则 {Label(rule)}：keySource.fields 数量（{keySourceFields.Value.GetArrayLength()}）"
                    + $"与 keyFields 数量（{declaredKeys.GetArrayLength()}）不一致");
            }
        }
        if (withinDoc && exclude is not null)
            issues.Add($"校验规则 {Label(rule)}：duplicate-check.excludeSelf 仅用于 entity 模式");
        if (withinDoc && GetObject(p, "filter") is not null)
            issues.Add($"校验规则 {Label(rule)}：duplicate-check.filter 仅用于 entity 模式");
        if (withinDoc && keySource is not null)
            issues.Add($"校验规则 {Label(rule)}：duplicate-check.keySource 仅用于 entity 模式");

        if (GetObject(p, "filter") is { } filter)
            ValidateConditionShape(rule, filter, "filter", issues);

        var diagnostics = GetArray(p, "diagnostics");
        if (diagnostics is not null)
        {
            if (withinDoc)
                issues.Add($"校验规则 {Label(rule)}：duplicate-check.diagnostics 仅用于 entity 模式");
            if (diagnostics.Value.GetArrayLength() == 0)
                issues.Add($"校验规则 {Label(rule)}：duplicate-check.diagnostics 必须是非空数组");
            foreach (var item in diagnostics.Value.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(item.GetString()))
                    issues.Add($"校验规则 {Label(rule)}：duplicate-check.diagnostics 只能是非空字符串数组");
            }
        }
    }

    /// <summary>
    /// 消息占位符必须能取到值：列名占位符要出现在 diagnostics / diagnosticFields 内，
    /// {ROWS} 是 master-detail 的多行整块占位符。
    /// </summary>
    private static void ValidatePlaceholders(ValidationRuleConfig rule, List<string> issues)
    {
        var declared = new List<string>();
        var p = rule.Params ?? default;
        foreach (var name in new[] { "diagnostics", "diagnosticFields" })
        {
            if (GetArray(p, name) is not { } array)
                continue;
            foreach (var item in array.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString()))
                    declared.Add(item.GetString()!.Trim());
            }
        }
        foreach (Match placeholder in Regex.Matches(rule.Message ?? string.Empty, "\\{([A-Za-z0-9_]+)\\}"))
        {
            var name = placeholder.Groups[1].Value;
            if (name.Equals("ROWS", StringComparison.OrdinalIgnoreCase))
                continue;
            if (!declared.Contains(name, StringComparer.OrdinalIgnoreCase))
                issues.Add($"校验规则 {Label(rule)}：消息占位符 {{{name}}} 未出现在 duplicate-check 的 diagnostics/diagnosticFields 内");
        }
    }

    private static void RejectUnknownKeys(
        ValidationRuleConfig rule,
        JsonElement element,
        IReadOnlySet<string> allowedKeys,
        string where,
        ICollection<string> issues)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (!allowedKeys.Contains(property.Name))
                issues.Add($"校验规则 {Label(rule)}：未知参数键 {where}.{property.Name}");
        }
    }

    private static IReadOnlySet<string> KeySet(params string[] keys) =>
        new HashSet<string>(keys, StringComparer.OrdinalIgnoreCase);

    private static IReadOnlySet<string> WithWhen(IReadOnlySet<string> keys) =>
        new HashSet<string>(keys.Concat(SharedParamKeys), StringComparer.OrdinalIgnoreCase);

    /// <summary>结构化条件形状校验（与 EffectConditionCompiler 的闭式契约一致）。</summary>
    private static void ValidateConditionShape(
        ValidationRuleConfig rule,
        JsonElement condition,
        string where,
        ICollection<string> issues)
    {
        var logic = GetString(condition, "logic");
        if (logic is not ("AND" or "OR"))
            issues.Add($"校验规则 {Label(rule)}：{where}.logic 仅允许 AND / OR");
        if (GetArray(condition, "items") is null)
            issues.Add($"校验规则 {Label(rule)}：{where}.items 必须是数组");
    }

    private static string Label(ValidationRuleConfig rule) =>
        string.IsNullOrWhiteSpace(rule.RuleId) ? $"({rule.ValidationKey})" : rule.RuleId;

    private static string? GetString(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static JsonElement? GetObject(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Object ? v : null;

    private static JsonElement? GetArray(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Array ? v : null;

    private static int? GetInt32(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : null;
}
