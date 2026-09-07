using System.Text.Json;
using EOS.API.Data.ValidationRules;
using EOS.API.Models;

namespace EOS.API.Data.Effects;

/// <summary>
/// Parses the businessActions / validationRules sections of a published workbench
/// definition into an executable effect plan. Config data is fail-closed: any unknown
/// event, effect key, op code or malformed structure aborts loading (config errors are
/// save-time issues, never runtime warnings). Parsing happens per call: the workspace
/// definition can change without a version bump, so caching on definitionVersion alone
/// would risk executing stale config.
/// </summary>
public sealed class EffectPlanLoader
{
    private static readonly IReadOnlySet<string> OpCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "ACCUM", "DEACCUM", "ASSIGN", "ASSIGN_MAX", "ASSIGN_MIN", "APPEND_UNIQ", "SET_WHEN",
    };

    private static readonly IReadOnlySet<string> SourceScopes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "MASTER", "DETAIL", "TABLE", "CONSTANT", "TARGET",
    };

    private static readonly IReadOnlySet<string> Aggregations = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "NONE", "SUM", "MAX", "MIN", "DISTINCT",
    };

    public ModuleEffectPlan Load(WorkbenchDefinition definition)
    {
        var actions = new List<EffectActionPlan>();
        if (definition.BusinessActions is { ValueKind: JsonValueKind.Array } actionsJson)
            foreach (var element in actionsJson.EnumerateArray())
                actions.Add(ParseAction(element, definition));

        var rules = new List<EffectValidationPlan>();
        if (definition.ValidationRules is { ValueKind: JsonValueKind.Array } rulesJson)
            foreach (var element in rulesJson.EnumerateArray())
                rules.Add(ParseValidationRule(element));

        return new ModuleEffectPlan(
            definition.ModuleId,
            definition.MasterTable,
            definition.DetailTable,
            definition.DefinitionVersion,
            definition.MasterPkOrder,
            actions.OrderBy(item => item.Seq).ToList(),
            rules.OrderBy(item => item.Seq).ToList());
    }

    private static EffectActionPlan ParseAction(JsonElement element, WorkbenchDefinition definition)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw new EffectConfigException("动作配置项必须是对象。");

        var effectKey = RequiredString(element, "effectKey");
        if (!BusinessActionCatalog.EffectKeys.Contains(effectKey))
            throw new EffectConfigException($"动作效果键 '{effectKey}' 不在效果目录 v0.2 内。");

        var eventCode = RequiredString(element, "eventCode");
        if (!BusinessActionCatalog.Events.Contains(eventCode))
            throw new EffectConfigException($"动作触发事件 '{eventCode}' 不在封闭事件集内。");

        var failMode = OptionalString(element, "failMode") ?? "BLOCK";
        if (!BusinessActionCatalog.FailModes.Contains(failMode))
            throw new EffectConfigException($"动作失败模式 '{failMode}' 不在 BLOCK/WARN 内。");

        var ops = new List<EffectOpPlan>();
        if (element.TryGetProperty("ops", out var opsJson) && opsJson.ValueKind == JsonValueKind.Array)
            foreach (var op in opsJson.EnumerateArray())
                if (ParseOp(op) is { } parsed)
                    ops.Add(parsed);

        if (ops.Count == 0 && element.TryGetProperty("opCount", out var opCount)
            && opCount.ValueKind == JsonValueKind.Number && opCount.GetInt32() > 0)
        {
            // Formula effects must carry at least one formula row; the editor may embed
            // only a count when ops are streamed separately, so require them explicitly.
            throw new EffectConfigException($"公式型效果 '{effectKey}' 缺少公式行数据。");
        }

        var paramsJson = OptionalElement(element, "params");
        var reverseJson = OptionalElement(element, "reverse");
        var structIssues = EffectStructSchemas.ValidateActionStructs(
            effectKey,
            paramsJson?.GetRawText(),
            reverseJson?.GetRawText());
        if (structIssues.Count > 0)
            throw new EffectConfigException($"效果 '{effectKey}' 参数/反向结构校验未通过：{string.Join("；", structIssues)}");

        return new EffectActionPlan(
            RequiredInt(element, "seq"),
            eventCode,
            effectKey,
            OptionalString(element, "effectName"),
            OptionalBool(element, "enabled") ?? true,
            failMode,
            OptionalElement(element, "condition"),
            paramsJson,
            reverseJson,
            ops.OrderBy(item => item.OpSeq).ToList());
    }

    /// <summary>
    /// Row-location semantics (v1.0 gray-release): formula rows consume MATCH_STRUCT
    /// directly (column-name pairs); the FIELD_RELATION effect edges registered in the
    /// configuration phase are validated for consistency at save time and re-checked
    /// here only as identifiers, not resolved back to relation ids. Placeholder rows
    /// written by the translation phase (empty op code; semantics carried by
    /// PARAM_STRUCT of a service-style key) are skipped, never executed.
    /// </summary>
    private static EffectOpPlan? ParseOp(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw new EffectConfigException("公式行配置项必须是对象。");

        var opCode = OptionalString(element, "opCode");
        if (string.IsNullOrWhiteSpace(opCode)
            && string.IsNullOrWhiteSpace(OptionalString(element, "targetTable"))
            && string.IsNullOrWhiteSpace(OptionalString(element, "targetField")))
            return null; // translation-phase placeholder row; service key params carry the semantics

        opCode = RequiredString(element, "opCode");
        if (!OpCodes.Contains(opCode))
            throw new EffectConfigException($"公式行算子 '{opCode}' 不在封闭算子集内。");

        var source = ParseSource(element);
        var terms = ParseTerms(element);
        if (source.Scope == "CONSTANT" == false
            && source.Field is null && terms is null && source.Constant is null)
            throw new EffectConfigException($"公式行 OP_SEQ={OptionalInt(element, "opSeq")} 缺少来源字段或加减项。");

        var agg = OptionalString(element, "sourceAgg");
        if (agg is not null && !Aggregations.Contains(agg))
            throw new EffectConfigException($"公式行聚合 '{agg}' 不在封闭聚合集内。");

        return new EffectOpPlan(
            RequiredInt(element, "opSeq"),
            RequiredString(element, "targetTable"),
            RequiredString(element, "targetField"),
            opCode,
            source,
            agg,
            terms,
            ParseMatch(element),
            OptionalElement(element, "condition"),
            OptionalString(element, "remark"));
    }

    private static EffectSourceRef ParseSource(JsonElement element)
    {
        var scope = OptionalString(element, "sourceScope") ?? "MASTER";
        if (!SourceScopes.Contains(scope))
            throw new EffectConfigException($"公式行来源域 '{scope}' 不在 MASTER/DETAIL/TABLE/TARGET/CONSTANT 内。");
        return new EffectSourceRef(
            scope,
            OptionalString(element, "sourceTable"),
            OptionalString(element, "sourceField"),
            OptionalString(element, "sourceConstant"));
    }

    private static IReadOnlyList<EffectTerm>? ParseTerms(JsonElement element)
    {
        var parsed = ParseEmbeddedArray(element, "sourceTerms");
        if (parsed is not { } terms)
        {
            return null;
        }
        var result = new List<EffectTerm>();
        foreach (var term in terms.EnumerateArray())
        {
            if (term.ValueKind != JsonValueKind.Object
                || !term.TryGetProperty("field", out var field) || field.ValueKind != JsonValueKind.String)
                throw new EffectConfigException("加减项缺少字符串 field。");
            var coef = term.TryGetProperty("coef", out var c) && c.ValueKind == JsonValueKind.Number && c.TryGetInt32(out var n)
                ? n
                : 1;
            if (coef is not (1 or -1))
                throw new EffectConfigException($"加减项 '{field.GetString()}' 的 coef 仅允许 1 / -1。");
            result.Add(new EffectTerm(field.GetString()!, coef));
        }
        return result;
    }

    private static IReadOnlyList<EffectMatchItem>? ParseMatch(JsonElement element)
    {
        var parsed = ParseEmbeddedArray(element, "match");
        if (parsed is not { } match)
        {
            return null;
        }
        var items = new List<EffectMatchItem>();
        foreach (var item in match.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object
                || !item.TryGetProperty("target", out var target) || target.ValueKind != JsonValueKind.String
                || !item.TryGetProperty("source", out var source) || source.ValueKind != JsonValueKind.Object)
                throw new EffectConfigException("定位键必须是 {target, source:{scope, field}} 结构。");
            var scope = source.TryGetProperty("scope", out var s) && s.ValueKind == JsonValueKind.String
                ? s.GetString()!.Trim().ToUpperInvariant()
                : string.Empty;
            if (!SourceScopes.Contains(scope))
                throw new EffectConfigException($"定位键来源域 '{scope}' 不在封闭来源域内。");
            var field = source.TryGetProperty("field", out var f) && f.ValueKind == JsonValueKind.String
                ? f.GetString()!.Trim()
                : null;
            var table = source.TryGetProperty("table", out var t) && t.ValueKind == JsonValueKind.String
                ? t.GetString()!.Trim()
                : null;
            items.Add(new EffectMatchItem(target.GetString()!.Trim(), new EffectSourceRef(scope, table, field, null)));
        }
        return items;
    }

    /// <summary>Reads a JSON array property, accepting either an inline array or a JSON text string.</summary>
    private static JsonElement? ParseEmbeddedArray(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value)
            || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }
        if (value.ValueKind == JsonValueKind.String)
        {
            var text = value.GetString()?.Trim();
            if (string.IsNullOrEmpty(text))
            {
                return null;
            }
            try
            {
                value = JsonDocument.Parse(text).RootElement.Clone();
            }
            catch (JsonException exception)
            {
                throw new EffectConfigException($"配置项 '{name}' 内嵌 JSON 解析失败：{exception.Message}");
            }
        }
        return value.ValueKind == JsonValueKind.Array ? value : null;
    }

    private static EffectValidationPlan ParseValidationRule(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw new EffectConfigException("校验规则配置项必须是对象。");
        var stage = RequiredString(element, "stage");
        if (stage is not ("SAVE" or "APPROVE" or "DEAPPROVE"))
            throw new EffectConfigException($"校验规则阶段 '{stage}' 不在 SAVE/APPROVE/DEAPPROVE 内。");
        var validationKey = RequiredString(element, "validationKey");
        if (!ValidationRuleRegistry.IsKnownKey(validationKey))
            throw new EffectConfigException($"校验规则键 '{validationKey}' 不在封闭校验模板目录内。");
        var paramsJson = OptionalElement(element, "params") ?? JsonSerializer.SerializeToElement(new { });
        var issues = new List<string>();
        ValidationRuleRegistry.Validate(new ValidationRuleConfig(
            OptionalString(element, "ruleId") ?? $"module-validation-{RequiredInt(element, "seq")}",
            validationKey,
            stage,
            OptionalBool(element, "enabled") ?? true,
            OptionalString(element, "message"),
            paramsJson), issues);
        if (issues.Count > 0)
            throw new EffectConfigException($"校验规则 '{validationKey}' 结构校验未通过：{string.Join("；", issues)}");
        return new EffectValidationPlan(
            RequiredInt(element, "seq"),
            stage,
            validationKey,
            OptionalBool(element, "enabled") ?? true,
            paramsJson,
            OptionalString(element, "message"));
    }

    private static string RequiredString(JsonElement element, string name) =>
        OptionalString(element, name)
        ?? throw new EffectConfigException($"配置项缺少必填字段 '{name}'。");

    private static int RequiredInt(JsonElement element, string name) =>
        OptionalInt(element, name)
        ?? throw new EffectConfigException($"配置项缺少必填整数 '{name}'。");

    private static string? OptionalString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()!.Trim()
            : null;

    private static int? OptionalInt(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var n)
            ? n
            : null;

    private static bool? OptionalBool(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True
            ? true
            : element.TryGetProperty(name, out var falseValue) && falseValue.ValueKind == JsonValueKind.False
                ? false
                : null;

    private static JsonElement? OptionalElement(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value)
            || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }
        if (value.ValueKind == JsonValueKind.String)
        {
            // Published snapshots carry structured JSON columns (PARAM_STRUCT,
            // REVERSE_STRUCT, MATCH_STRUCT, ...) as text; parse them back into the
            // element shape the compiler consumes.
            var text = value.GetString()?.Trim();
            if (string.IsNullOrEmpty(text))
            {
                return null;
            }
            try
            {
                return JsonDocument.Parse(text).RootElement.Clone();
            }
            catch (JsonException exception)
            {
                throw new EffectConfigException($"配置项 '{name}' 内嵌 JSON 解析失败：{exception.Message}");
            }
        }
        return value.Clone();
    }
}

/// <summary>Effect plan rejected the configuration; treated as a hard config error.</summary>
public sealed class EffectConfigException(string message) : Exception(message);
