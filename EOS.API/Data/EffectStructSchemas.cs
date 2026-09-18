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
    private static readonly Regex TableColumnSource = new(@"^[A-Z][A-Z0-9_]*\.[A-Z][A-Z0-9_]*$", RegexOptions.Compiled);

    /// <summary>反向结构 kind 枚举（覆盖现有种子与快照补偿语义）。</summary>
    private static readonly IReadOnlySet<string> ReverseKinds = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "auto-reverse",
        "clear-on-deapprove",
        "no-reverse",
        "clear-refs",
        "net-replace",
        "none",
        "recompute",
        "recompute-excluding-self",
        "restore-active",
        "recalc-confirmed",
        "restore-old-price",
        "reverse-flow",
        "clear-finish",
        "clear-refs-unfinish",
        "restore-previous",
        "snapshot",
    };

    /// <summary>反向结构允许的根键。</summary>
    private static readonly IReadOnlySet<string> ReverseRootKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "kind",
        "note",
    };

    /// <summary>link-stamp targets 项允许的键。</summary>
    private static readonly IReadOnlySet<string> LinkStampTargetKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "table", "ref", "refs", "fromDetail", "sourceRefs",
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
        "blank",
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
            ["callback-reprice"] = Set("sendTargets", "returnTargets", "fields", "duplicateGuardMarked", "deapprove"),
            ["client-price-sync"] = Set("master", "detail", "quoteRefs", "preserveOld", "overwriteIfNewer"),
            ["completion-close"] = Set("targets", "condition", "marker", "direction", "offsets"),
            ["employee-contract-sync"] = Set("targetTable", "fields", "source", "includeSelfOnApprove"),
            ["employee-dimission-sync"] = Set("targetTable", "source"),
            ["field-accumulate"] = Set("mode", "targets"),
            ["field-copy"] = Set("targetTable", "field", "fields", "targets", "sourceField", "headerFields"),
            ["hr-usage-sync"] = Set("targetTable", "scopeKey", "sourceFields", "targetFields"),
            ["inventory-move"] = Set("direction", "mrp", "depotField", "targetTable", "fieldMap", "rowFilter"),
            ["link-stamp"] = Set("targetTable", "field", "fields", "targets", "mode", "finish"),
            ["mould-balance-adjust"] = Set("targetTable", "mode", "qty", "amount", "dateField", "dateFields", "dateMode", "dateValueField", "useCountField"),
            ["mould-batch-apply"] = Set("branchField", "acceptTargets", "scrapTarget", "productFields"),
            ["mrp-plan-alloc"] = Set("targetTable", "mode", "scope", "field", "fields", "stockSource", "note"),
            ["order-change-apply"] = Set("detail", "totals"),
            ["payment-date-calc"] = Set("targetField", "dateField", "monthField", "paymentDaysFrom"),
            ["produce-change-apply"] = Set("master", "detail", "projection"),
            ["purchase-change-apply"] = Set("master", "detail", "totals"),
            ["quote-parameter-recalc"] = Set("targetTable", "mode", "feeFields"),
            ["half-stock-move"] = Set("direction", "fieldMap"),
            ["car-filloil-sync"] = Set("master", "carTable", "oilcardTable"),
            ["detail-field-sync"] = Set("targetTable", "key", "pairs"),
            ["sample-edition-bump"] = Set("master"),            ["return-writeback"] = Set("order", "produce", "conditions"),
            ["mould-ids-sync"] = Set("table", "keyField", "valueField"),
            ["card-sibling-close"] = Set("table", "beginField", "endField", "keyField", "ownerField", "offsetDays"),
            ["fields-metadata-sync"] = Set("targetId", "prefix", "target", "source"),
            ["detail-flag-and-rollup"] = Set("masterTable", "detailTable", "keyField", "bomTable", "bomKeyField", "bomSerialField", "detailSerialField", "stateField", "priceField", "amountField", "qtyField", "roundDigits"),
            ["wage-month-doc-prune"] = Set("masterTable", "detailTable", "typeField", "noField", "monthField", "empField", "duplicateMessage"),
            ["doc-orphan-prune"] = Set("masterTable", "detailTable", "typeField", "noField", "masterProductField", "detailProductField", "detailRefField", "rootTable", "rootField"),
            ["sfc-plan-sync"] = Set("master", "detail", "more", "process", "processDetail", "product"),
            ["cus-account-sync"] = Set("master", "detail", "product", "roundDigits"),
            ["pur-apply-sync"] = Set("master", "detail", "more", "product", "orderMaster", "orderDetail"),
            ["bom-size-backfill"] = Set("sourceTable", "sourceKey", "masterKey", "fields"),
            ["cop-account-rollup"] = Set("master", "detail", "roundDigits"),
            ["cop-prepay-rollup"] = Set("master", "detail", "roundDigits"),
            ["purchase-due-rollup"] = Set("master", "detail", "roundDigits"),
            ["pur-prepay-rollup"] = Set("master", "detail", "roundDigits"),
            ["pur-pay-offset"] = Set("master", "offset", "due", "detail", "gateFlag", "negativeMessage", "exceedMessage", "dueMessage", "exceedOffset"),
            ["cop-receipt-offset"] = Set("master", "offset", "due", "detail", "gateFlag", "negativeMessage", "exceedMessage", "dueMessage", "exceedOffset"),
            ["cop-send-mo-flag"] = Set("detail", "typeField", "noField"),
            ["pur-purchase-sync"] = Set("master", "detail", "more", "product", "supplierPrice"),
            ["sample-stock-adjust"] = Set("direction", "targetTable", "fieldMap"),
            ["set-state"] = Set("targetTable", "stateField", "stateValue", "state", "sourceField", "source", "dateField", "dateMode", "targets"),
            ["stamp-last-activity"] = Set("targetTable", "field", "fields", "sourceField", "matchBy", "condition"),
            ["supplier-price-sync"] = Set("master", "detail", "quoteRefs", "preserveOld", "overwriteIfNewer"),
        };

    /// <summary>balance-adjust 往来分支允许的键（credit/prepay 及金额列覆盖）。</summary>
    private static readonly IReadOnlySet<string> BalanceAdjustPartyKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "credit",
        "prepay",
        "creditField",
        "prepayField",
    };

    /// <summary>balance-adjust 银行分支允许的键。</summary>
    private static readonly IReadOnlySet<string> BalanceAdjustBankKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "direction",
        "amountField",
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
        if (effectKey.Equals("inventory-move", StringComparison.OrdinalIgnoreCase)
            && root.TryGetProperty("rowFilter", out var rowFilter))
        {
            issues.AddRange(ValidateRowFilter(rowFilter));
        }
        if (effectKey.Equals("callback-reprice", StringComparison.OrdinalIgnoreCase))
        {
            issues.AddRange(ValidateCallbackTargets(root));
        }
        if (effectKey.Equals("link-stamp", StringComparison.OrdinalIgnoreCase))
        {
            issues.AddRange(ValidateLinkStampTargets(root));
        }
        if (effectKey.Equals("payment-date-calc", StringComparison.OrdinalIgnoreCase))
        {
            issues.AddRange(ValidatePaymentDateParams(root));
        }
        if (effectKey.Equals("client-price-sync", StringComparison.OrdinalIgnoreCase)
            || effectKey.Equals("supplier-price-sync", StringComparison.OrdinalIgnoreCase))
        {
            issues.AddRange(ValidatePriceSyncParams(root));
        }
        if (effectKey.Equals("order-change-apply", StringComparison.OrdinalIgnoreCase)
            || effectKey.Equals("purchase-change-apply", StringComparison.OrdinalIgnoreCase)
            || effectKey.Equals("produce-change-apply", StringComparison.OrdinalIgnoreCase))
        {
            issues.AddRange(ValidateChangeApplyParams(root));
        }
        if (effectKey.Equals("quote-parameter-recalc", StringComparison.OrdinalIgnoreCase))
        {
            issues.AddRange(ValidateQuoteParameterParams(root));
        }
        if (effectKey.Equals("balance-adjust", StringComparison.OrdinalIgnoreCase))
        {
            issues.AddRange(ValidateBalanceAdjustParams(root));
        }
        return issues;
    }

    /// <summary>balance-adjust 分支结构：client/supplier 只认 credit/prepay(+金额列覆盖)，bank 只认 direction/amountField。</summary>
    private static IReadOnlyList<string> ValidateBalanceAdjustParams(JsonElement root)
    {
        var issues = new List<string>();
        foreach (var name in new[] { "client", "supplier", "bank" })
        {
            if (!root.TryGetProperty(name, out var branch))
                continue;
            if (branch.ValueKind != JsonValueKind.Object)
            {
                issues.Add($"balance-adjust.{name} 必须是对象。");
                continue;
            }
            var allowed = name == "bank" ? BalanceAdjustBankKeys : BalanceAdjustPartyKeys;
            foreach (var property in branch.EnumerateObject())
            {
                if (!allowed.Contains(property.Name))
                    issues.Add($"balance-adjust.{name} 含未登记键 '{property.Name}'（允许：{string.Join(",", allowed)}）。");
            }
            var fieldKeys = name == "bank" ? new[] { "amountField" } : new[] { "creditField", "prepayField" };
            foreach (var fieldKey in fieldKeys)
            {
                if (branch.TryGetProperty(fieldKey, out var value)
                    && (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString())))
                {
                    issues.Add($"balance-adjust.{fieldKey} 必须是非空字符串");
                }
            }
        }
        return issues;
    }

    private static IReadOnlyList<string> ValidatePaymentDateParams(JsonElement root)
    {
        var issues = new List<string>();
        foreach (var required in new[] { "targetField", "monthField", "dateField" })
        {
            if (!root.TryGetProperty(required, out var value)
                || value.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(value.GetString()))
            {
                issues.Add($"payment-date-calc.{required} 不能为空");
            }
            else if (!Identifier.IsMatch(value.GetString()!))
            {
                issues.Add($"payment-date-calc.{required} 必须是全大写标识符");
            }
        }
        if (!root.TryGetProperty("paymentDaysFrom", out var source)
            || source.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(source.GetString()))
        {
            issues.Add("payment-date-calc.paymentDaysFrom 不能为空");
        }
        else if (!TableColumnSource.IsMatch(source.GetString()!))
        {
            issues.Add("payment-date-calc.paymentDaysFrom 必须是 'TABLE.COLUMN' 全大写形式");
        }
        return issues;
    }

    private static readonly IReadOnlySet<string> CallbackTargetKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "detail", "master", "typeCol", "noCol", "serialCol",
        "copyFromCallback", "amountTo", "markFromDoc", "markFromCallback",
    };

    private static IReadOnlyList<string> ValidateCallbackTargets(JsonElement root)
    {
        var issues = new List<string>();
        foreach (var group in new[] { "sendTargets", "returnTargets" })
        {
            if (!root.TryGetProperty(group, out var array))
                continue;
            if (array.ValueKind != JsonValueKind.Array)
            {
                issues.Add($"callback-reprice.{group} 必须是数组。");
                continue;
            }
            var index = 0;
            foreach (var item in array.EnumerateArray())
            {
                var where = $"{group}[{index}]";
                if (item.ValueKind != JsonValueKind.Object)
                {
                    issues.Add($"callback-reprice.{where} 必须是对象。");
                }
                else
                {
                    RejectUnknown(item, CallbackTargetKeys, $"callback-reprice.{where}", issues);
                    foreach (var required in new[] { "detail", "master", "typeCol", "noCol", "serialCol" })
                        if (!item.TryGetProperty(required, out var v) || v.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(v.GetString()))
                            issues.Add($"callback-reprice.{where}.{required} 不能为空");
                    foreach (var listKey in new[] { "copyFromCallback", "amountTo", "markFromDoc", "markFromCallback" })
                        if (item.TryGetProperty(listKey, out var arr) && arr.ValueKind != JsonValueKind.Array)
                            issues.Add($"callback-reprice.{where}.{listKey} 必须是数组");
                }
                index++;
            }
        }
        return issues;
    }

    private static IReadOnlyList<string> ValidatePriceSyncParams(JsonElement root)
    {
        var issues = new List<string>();
        foreach (var required in new[] { "master", "detail" })
        {
            if (!root.TryGetProperty(required, out var value) || value.ValueKind != JsonValueKind.String
                || !Identifier.IsMatch(value.GetString() ?? string.Empty))
                issues.Add("price-sync." + required + " 必须是非空大写表名。");
        }
        if (!root.TryGetProperty("quoteRefs", out var refs) || refs.ValueKind != JsonValueKind.Array || refs.GetArrayLength() == 0)
        {
            issues.Add("price-sync.quoteRefs 必须是非空数组。");
        }
        else
        {
            foreach (var item in refs.EnumerateArray())
                if (item.ValueKind != JsonValueKind.String || !Identifier.IsMatch(item.GetString() ?? string.Empty))
                    issues.Add("price-sync.quoteRefs 存在非法字段名。");
        }
        foreach (var flag in new[] { "preserveOld", "overwriteIfNewer" })
        {
            if (root.TryGetProperty(flag, out var value) && value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                issues.Add("price-sync." + flag + " 必须是布尔。");
        }
        return issues;
    }
    private static IReadOnlyList<string> ValidateChangeApplyParams(JsonElement root)
    {
        var issues = new List<string>();
        if (root.TryGetProperty("master", out var master) && master.ValueKind == JsonValueKind.Object)
        {
            if (!master.TryGetProperty("fields", out var fields) || fields.ValueKind != JsonValueKind.Array || fields.GetArrayLength() == 0)
            {
                issues.Add("change-apply.master.fields 必须是非空数组。");
            }
            else
            {
                foreach (var item in fields.EnumerateArray())
                    if (item.ValueKind != JsonValueKind.String || !Identifier.IsMatch(item.GetString() ?? string.Empty))
                        issues.Add("change-apply.master.fields 存在非法字段名。");
            }
        }
        if (root.TryGetProperty("detail", out var detail) && detail.ValueKind == JsonValueKind.Object)
        {
            if (!detail.TryGetProperty("fields", out var fields) || fields.ValueKind != JsonValueKind.Array || fields.GetArrayLength() == 0)
            {
                issues.Add("order-change-apply.detail.fields 必须是非空数组。");
            }
            else
            {
                foreach (var item in fields.EnumerateArray())
                    if (item.ValueKind != JsonValueKind.String || !Identifier.IsMatch(item.GetString() ?? string.Empty))
                        issues.Add("order-change-apply.detail.fields 存在非法字段名。");
            }
        }
        else
        {
            issues.Add("order-change-apply.detail 缺少对象配置。");
        }
        if (root.TryGetProperty("totals", out var totals) && totals.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            issues.Add("order-change-apply.totals 必须是布尔。");
        if (root.TryGetProperty("serialColumn", out var serial) && serial.ValueKind == JsonValueKind.String
            && !Identifier.IsMatch(serial.GetString() ?? string.Empty))
            issues.Add("order-change-apply.serialColumn 必须是大写标识符。");
        if (root.TryGetProperty("projection", out var projection))
        {
            if (projection.ValueKind != JsonValueKind.Object)
            {
                issues.Add("change-apply.projection 必须是对象。");
            }
            else
            {
                if (projection.TryGetProperty("mode", out var mode))
                {
                    if (mode.ValueKind != JsonValueKind.String
                        || !mode.GetString()!.Equals("net-replace", StringComparison.OrdinalIgnoreCase))
                        issues.Add("change-apply.projection.mode 仅支持 net-replace。");
                }
                else
                {
                    issues.Add("change-apply.projection.mode 必填（net-replace）。");
                }
                foreach (var property in projection.EnumerateObject())
                    if (!property.NameEquals("mode"))
                        issues.Add($"change-apply.projection 含未登记键 '{property.Name}'（仅允许 mode）。");
            }
        }
        return issues;
    }
    private static IReadOnlyList<string> ValidateQuoteParameterParams(JsonElement root)
    {
        var issues = new List<string>();
        if (!root.TryGetProperty("targetTable", out var target) || target.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(target.GetString()))
        {
            issues.Add("quote-parameter-recalc.targetTable 不能为空");
        }
        else if (!Identifier.IsMatch(target.GetString()!))
        {
            issues.Add("quote-parameter-recalc.targetTable 必须是全大写标识符");
        }
        if (!root.TryGetProperty("mode", out var mode) || mode.ValueKind != JsonValueKind.String
            || !mode.GetString()!.Equals("recalc-confirmed", StringComparison.OrdinalIgnoreCase))
        {
            issues.Add("quote-parameter-recalc.mode 仅支持 recalc-confirmed。");
        }
        if (!root.TryGetProperty("feeFields", out var feeFields) || feeFields.ValueKind != JsonValueKind.Array
            || feeFields.GetArrayLength() == 0)
        {
            issues.Add("quote-parameter-recalc.feeFields 不能为空。");
        }
        else
        {
            foreach (var item in feeFields.EnumerateArray())
                if (item.ValueKind != JsonValueKind.String || !Identifier.IsMatch(item.GetString() ?? string.Empty))
                    issues.Add("quote-parameter-recalc.feeFields 存在非法字段名。");
        }
        return issues;
    }
    private static IReadOnlyList<string> ValidateLinkStampTargets(JsonElement root)
    {
        var issues = new List<string>();
        if (!root.TryGetProperty("targets", out var array))
            return issues;
        if (array.ValueKind != JsonValueKind.Array)
        {
            issues.Add("link-stamp.targets 必须是数组。");
            return issues;
        }
        issues.AddRange(ValidateLinkStampFields(root));
        var index = 0;
        foreach (var item in array.EnumerateArray())
        {
            var where = $"link-stamp.targets[{index++}]";
            if (item.ValueKind != JsonValueKind.Object)
            {
                issues.Add($"{where} 必须是对象。");
                continue;
            }
            foreach (var property in item.EnumerateObject())
                if (!LinkStampTargetKeys.Contains(property.Name))
                    issues.Add($"{where} 含未登记键 '{property.Name}'");
            if (!item.TryGetProperty("table", out var table)
                || table.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(table.GetString()))
                issues.Add($"{where}.table 不能为空");
            if (item.TryGetProperty("fromDetail", out var flag)
                && flag.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                issues.Add($"{where}.fromDetail 必须是布尔。");
            var fromDetail = item.TryGetProperty("fromDetail", out var detail) && detail.ValueKind == JsonValueKind.True;
            var refs = RefCount(item, "refs", issues) ?? RefCount(item, "ref", issues);
            var sourceRefs = NameCount(item, "sourceRefs", issues);
            if (!fromDetail)
                continue;
            if (refs is null or 0)
                issues.Add($"{where} fromDetail=true 需要非空 refs。");
            if (sourceRefs is null or 0)
                issues.Add($"{where} fromDetail=true 需要非空 sourceRefs。");
            if (refs > 0 && sourceRefs > 0 && refs != sourceRefs)
                issues.Add($"{where} refs 与 sourceRefs 列数必须一致。");
        }
        return issues;
    }

    /// <summary>字段数（字符串记 1，数组记元素数）；类型非法时记问题并返回 -1。</summary>
    private static int? NameCount(JsonElement element, string name, ICollection<string> issues)
    {
        if (!element.TryGetProperty(name, out var value))
            return null;
        if (value.ValueKind == JsonValueKind.String)
            return string.IsNullOrWhiteSpace(value.GetString()) ? 0 : 1;
        if (value.ValueKind != JsonValueKind.Array)
        {
            issues.Add($"link-stamp {name} 必须是字段名或字段名数组。");
            return -1;
        }
        foreach (var entry in value.EnumerateArray())
            if (entry.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(entry.GetString()))
                issues.Add($"link-stamp {name} 存在空字段名。");
        return value.GetArrayLength();
    }

    /// <summary>
    /// Locating keys accept a plain name or a {target[, source]} object so anchors
    /// may be expressed through non-key master columns; the count drives the
    /// positional pairing checks.
    /// </summary>
    private static int? RefCount(JsonElement element, string name, ICollection<string> issues)
    {
        if (!element.TryGetProperty(name, out var value))
            return null;
        if (value.ValueKind == JsonValueKind.String)
            return string.IsNullOrWhiteSpace(value.GetString()) ? 0 : 1;
        if (value.ValueKind != JsonValueKind.Array)
        {
            issues.Add($"link-stamp {name} 必须是字段名或字段名数组。");
            return -1;
        }
        var index = 0;
        foreach (var entry in value.EnumerateArray())
        {
            var where = $"link-stamp {name}[{index++}]";
            if (entry.ValueKind == JsonValueKind.String)
            {
                if (string.IsNullOrWhiteSpace(entry.GetString()))
                    issues.Add($"{where} 存在空字段名。");
                continue;
            }
            if (entry.ValueKind == JsonValueKind.Object)
            {
                if (!entry.TryGetProperty("target", out var target) || target.ValueKind != JsonValueKind.String
                    || string.IsNullOrWhiteSpace(target.GetString()))
                    issues.Add($"{where}.target 不能为空");
                if (entry.TryGetProperty("source", out var source)
                    && (source.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(source.GetString())))
                    issues.Add($"{where}.source 必须是非空字符串");
                foreach (var property in entry.EnumerateObject())
                    if (!property.NameEquals("target") && !property.NameEquals("source"))
                        issues.Add($"{where} 含未登记键 '{property.Name}'");
                continue;
            }
            issues.Add($"{where} 必须是字段名或 {{target,source}} 对象。");
        }
        return value.GetArrayLength();
    }

    private static IReadOnlyList<string> ValidateLinkStampFields(JsonElement root)
    {
        var issues = new List<string>();
        foreach (var name in new[] { "field", "fields" })
        {
            if (!root.TryGetProperty(name, out var value))
                continue;
            if (value.ValueKind == JsonValueKind.String)
            {
                if (string.IsNullOrWhiteSpace(value.GetString()))
                    issues.Add($"link-stamp.{name} 不能为空。");
                continue;
            }
            if (value.ValueKind != JsonValueKind.Array)
            {
                issues.Add($"link-stamp.{name} 必须是字段名或字段名数组。");
                continue;
            }
            var index = 0;
            foreach (var entry in value.EnumerateArray())
            {
                var where = $"link-stamp.{name}[{index++}]";
                if (entry.ValueKind == JsonValueKind.String)
                {
                    if (string.IsNullOrWhiteSpace(entry.GetString()))
                        issues.Add($"{where} 不能为空。");
                }
                else if (entry.ValueKind == JsonValueKind.Object)
                {
                    foreach (var key in new[] { "target", "source" })
                        if (!entry.TryGetProperty(key, out var text)
                            || text.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(text.GetString()))
                            issues.Add($"{where}.{key} 不能为空");
                    foreach (var property in entry.EnumerateObject())
                        if (!property.NameEquals("target") && !property.NameEquals("source"))
                            issues.Add($"{where} 含未登记键 '{property.Name}'");
                }
                else
                {
                    issues.Add($"{where} 必须是字段名或 {{target,source}} 对象。");
                }
            }
        }
        return issues;
    }

    private static void RejectUnknown(JsonElement element, IReadOnlySet<string> allowed, string where, ICollection<string> issues)
    {
        foreach (var property in element.EnumerateObject())
            if (!allowed.Contains(property.Name))
                issues.Add($"callback-reprice {where} 含未登记键 '{property.Name}'");
    }

    private static IReadOnlyList<string> ValidateRowFilter(JsonElement value)
    {
        var issues = new List<string>();
        if (value.ValueKind != JsonValueKind.Object)
        {
            issues.Add("rowFilter 必须是 JSON 对象。");
            return issues;
        }
        foreach (var property in value.EnumerateObject())
            if (!property.NameEquals("anyPositive"))
                issues.Add($"rowFilter 含未登记键 '{property.Name}'（仅允许 anyPositive）。");
        if (!value.TryGetProperty("anyPositive", out var anyPositive)
            || anyPositive.ValueKind != JsonValueKind.Array
            || anyPositive.GetArrayLength() == 0)
        {
            issues.Add("rowFilter.anyPositive 必须是非空字段名数组。");
            return issues;
        }
        foreach (var item in anyPositive.EnumerateArray())
            if (item.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(item.GetString()))
                issues.Add("rowFilter.anyPositive 存在空字段名。");
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
        ValidateConditionItems(items, "参数 condition", issues);
        return issues;
    }

    /// <summary>
    /// Shape validation for a condition item list, shared by the parameter-level check and
    /// the formula-row / action-level conditions. Each item type has one accepted shape;
    /// a bare field name where the compiler expects {scope, field} is rejected here rather
    /// than surfacing as an execution error when a document is approved.
    /// </summary>
    public static void ValidateConditionItems(JsonElement items, string where, ICollection<string> issues)
    {
        if (items.ValueKind != JsonValueKind.Array)
        {
            issues.Add($"{where}.items 必须是数组。");
            return;
        }
        var index = 0;
        foreach (var item in items.EnumerateArray())
        {
            var at = $"{where}.items[{index++}]";
            if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("type", out var type)
                || type.ValueKind != JsonValueKind.String || !ConditionTypes.Contains(type.GetString()!))
            {
                issues.Add($"{at} 存在非法类型。");
                continue;
            }
            var kind = type.GetString()!;
            switch (kind.ToLowerInvariant())
            {
                case "field-compare":
                    if (!item.TryGetProperty("op", out var op) || op.ValueKind != JsonValueKind.String
                        || !CompareOperators.Contains(op.GetString()!))
                        issues.Add($"{at} field-compare 缺少闭式 op。");
                    if (!HasFieldOrTerms(item, "left")) issues.Add($"{at} field-compare 缺少 left 字段引用。");
                    if (!HasFieldOrTerms(item, "right")) issues.Add($"{at} field-compare 缺少 right 字段引用。");
                    break;
                case "value-eq":
                case "value-neq":
                    if (!HasObjectField(item, "field"))
                        issues.Add($"{at} {kind} 的 field 必须是 {{\"scope\":…,\"field\":…}} 对象。");
                    if (!item.TryGetProperty("value", out _))
                        issues.Add($"{at} {kind} 缺少 value。");
                    if (item.TryGetProperty("nullAsMatch", out var nullFlag)
                        && nullFlag.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                        issues.Add($"{at} nullAsMatch 必须是布尔。");
                    break;
                case "not-exists":
                    if (!item.TryGetProperty("targetTable", out var target) || target.ValueKind != JsonValueKind.String)
                        issues.Add($"{at} not-exists 缺少字符串 targetTable。");
                    if (!item.TryGetProperty("condition", out var inner) || inner.ValueKind != JsonValueKind.Object)
                        issues.Add($"{at} not-exists 缺少 condition 对象。");
                    if (!item.TryGetProperty("match", out var match) || match.ValueKind != JsonValueKind.Array)
                        issues.Add($"{at} not-exists 缺少 match 数组。");
                    break;
                case "switch":
                    if (!item.TryGetProperty("key", out var key) || key.ValueKind != JsonValueKind.String)
                        issues.Add($"{at} switch 缺少字符串 key。");
                    if (!item.TryGetProperty("value", out _))
                        issues.Add($"{at} switch 缺少 value。");
                    break;
                case "blank":
                    if (!HasObjectField(item, "field"))
                        issues.Add($"{at} blank 的 field 必须是 {{\"scope\":…,\"field\":…}} 对象。");
                    break;
            }
            if (item.TryGetProperty("negate", out var negate)
                && negate.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                issues.Add($"{at} negate 必须是布尔。");
            }
        }
    }

    /// <summary>True when the member is a field reference object carrying field or terms.</summary>
    private static bool HasFieldOrTerms(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Object
        && (value.TryGetProperty("field", out var field) && field.ValueKind == JsonValueKind.String
            || value.TryGetProperty("terms", out var terms) && terms.ValueKind == JsonValueKind.Array);

    /// <summary>True when the member is {scope, field} with a string field.</summary>
    private static bool HasObjectField(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty("field", out var field)
        && field.ValueKind == JsonValueKind.String
        && !string.IsNullOrWhiteSpace(field.GetString());

    /// <summary>Validates a condition JSON text (formula row / action level); empty when absent.</summary>
    public static IReadOnlyList<string> ValidateConditionJson(string? json, string where)
    {
        var issues = new List<string>();
        if (string.IsNullOrWhiteSpace(json))
            return issues;
        if (!TryParseObject(json!, out var root, out var parseIssue))
        {
            issues.Add($"{where} 不是合法 JSON 对象：{parseIssue}");
            return issues;
        }
        if (!root.TryGetProperty("logic", out var logic) || logic.ValueKind != JsonValueKind.String
            || logic.GetString() is not ("AND" or "OR"))
        {
            issues.Add($"{where} 必须是 {{logic:AND/OR, items:[…]}} 结构化条件。");
            return issues;
        }
        if (!root.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array || items.GetArrayLength() == 0)
        {
            issues.Add($"{where}.items 必须是非空数组。");
            return issues;
        }
        ValidateConditionItems(items, where, issues);
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
                        ValidateDetailEntries(property.Value, issues);
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

    /// <summary>
    /// Row-set entries are either a document column name or a closed constant entry
    /// {column, constant} that fills a slot the document does not carry. Kept as an exact
    /// key set so a stray key can never reach the generated statement.
    /// </summary>
    private static void ValidateDetailEntries(JsonElement array, ICollection<string> issues)
    {
        var index = 0;
        foreach (var item in array.EnumerateArray())
        {
            var where = $"fieldMap.detail[{index++}]";
            if (item.ValueKind == JsonValueKind.String)
            {
                if (!Identifier.IsMatch(item.GetString() ?? string.Empty))
                    issues.Add($"{where} 存在非法字段名 '{item}'。");
                continue;
            }
            if (item.ValueKind != JsonValueKind.Object)
            {
                issues.Add($"{where} 必须是字段名或 {{column, constant}} 对象。");
                continue;
            }
            foreach (var property in item.EnumerateObject())
                if (!property.NameEquals("column") && !property.NameEquals("constant"))
                    issues.Add($"{where} 含未登记键 '{property.Name}'（仅允许 column/constant）。");
            if (!item.TryGetProperty("column", out var column)
                || column.ValueKind != JsonValueKind.String
                || !Identifier.IsMatch(column.GetString() ?? string.Empty))
            {
                issues.Add($"{where}.column 必须是非空字段名。");
            }
            if (!item.TryGetProperty("constant", out var constant))
                issues.Add($"{where} 缺少 constant。");
            else if (constant.ValueKind is not (JsonValueKind.String or JsonValueKind.Number))
                issues.Add($"{where}.constant 必须是字符串或数字。");
        }
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
