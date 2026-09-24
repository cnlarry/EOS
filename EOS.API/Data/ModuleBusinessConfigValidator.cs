using System.Text.Json;
using EOS.API.Data.DocumentActions;
using EOS.API.Data.Effects;
using EOS.API.Data.ValidationRules;
using EOS.API.Models;
using EOS.API.Validation;

namespace EOS.API.Data;

/// <summary>
/// 模块业务动作/校验配置的保存即校验（结构层）。
/// 只做与数据库无关的闭式结构校验：目录值、编号连续性、JSON 可解析与组合约束；
/// 表/列物理存在性与模块形态由仓储在事务内校验。不含执行语义。
/// </summary>
public static class ModuleBusinessConfigValidator
{
    /// <summary>
    /// Translation-phase placeholder row on a service-style effect (empty op code and
    /// target identifiers): the loader, the save lint and the physical column check all
    /// skip such rows, keeping one shared predicate instead of three divergent copies.
    /// </summary>
    public static bool IsPlaceholderOp(string? opCode, string? targetTable, string? targetField) =>
        string.IsNullOrWhiteSpace(opCode)
        && string.IsNullOrWhiteSpace(targetTable)
        && string.IsNullOrWhiteSpace(targetField);

    /// <summary>
    /// Same-event chain prerequisites: an effect may require another effect earlier in
    /// the same event chain because it consumes rows/amounts produced there (e.g. the
    /// completion decision must run after quantity write-back, inventory after
    /// write-back). The dependency is registered per effect key, never per module.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, IReadOnlySet<string>> ChainPrerequisites =
        new Dictionary<string, IReadOnlySet<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["completion-close"] = Set("field-accumulate"),
            ["inventory-move"] = Set("field-accumulate"),
        };

    /// <param name="documentActionKeys">
    /// 单据操作注册表的键集（用户点击类动作的封闭目录）。MANUAL 行的键必须在此集合内——
    /// 它的实现来自注册的处理器，不在效果目录里，因此不能拿效果键集合去判它。
    /// </param>
    public static ValidationResult Validate(
        SaveModuleBusinessConfigRequest request,
        IReadOnlySet<string>? documentActionKeys = null)
    {
        var issues = new List<string>();
        if (request.Actions.Count > 300)
            issues.Add("业务动作数量超过上限（300）。");
        if (request.ValidationRules.Count > 100)
            issues.Add("校验规则数量超过上限（100）。");

        var actionKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var actionsByEvent = request.Actions
            .Where(action => !string.IsNullOrWhiteSpace(action.EventCode))
            .GroupBy(action => action.EventCode!.Trim().ToUpperInvariant(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.OrderBy(item => item.Seq).ToList(), StringComparer.OrdinalIgnoreCase);
        foreach (var action in request.Actions.OrderBy(item => item.Seq))
        {
            ValidateAction(action, actionKeys, issues, documentActionKeys);
            ValidateChainPrerequisite(action, actionsByEvent, issues);
        }

        var ruleKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rule in request.ValidationRules)
            ValidateValidationRule(rule, ruleKeys, issues);

        return ValidationResult.FromMessages(issues);
    }

    private static void ValidateChainPrerequisite(
        BusinessActionDto action,
        IReadOnlyDictionary<string, List<BusinessActionDto>> actionsByEvent,
        ICollection<string> issues)
    {
        var eventCode = action.EventCode?.Trim().ToUpperInvariant() ?? string.Empty;
        if (eventCode.Length == 0)
        {
            return;
        }
        if (ChainPrerequisites.TryGetValue(action.EffectKey, out var prerequisites))
        {
            foreach (var prerequisite in prerequisites)
            {
                // The prerequisite is required only when the chain actually contains it:
                // a pure inventory/state chain without a write-back step is legal.
                if (!actionsByEvent.TryGetValue(eventCode, out var chain))
                {
                    continue;
                }
                var hasPrerequisiteBefore = chain.Any(item =>
                    item.Seq < action.Seq
                    && item.EffectKey.Equals(prerequisite, StringComparison.OrdinalIgnoreCase));
                var hasPrerequisiteAnywhere = chain.Any(item =>
                    item.EffectKey.Equals(prerequisite, StringComparison.OrdinalIgnoreCase));
                if (hasPrerequisiteAnywhere && !hasPrerequisiteBefore)
                {
                    issues.Add($"动作 SEQ={action.Seq}：效果 '{action.EffectKey}' 依赖同链前置效果 '{prerequisite}'（顺序 lint：依赖效果须先于本动作执行）。");
                }
            }
        }
    }

    private static HashSet<string> Set(params string[] values) =>
        new(values, StringComparer.OrdinalIgnoreCase);

    private static void ValidateAction(
        BusinessActionDto action,
        ISet<string> keys,
        ICollection<string> issues,
        IReadOnlySet<string>? documentActionKeys)
    {
        if (action.Seq < 1)
        {
            issues.Add($"动作缺少有效顺序号（SEQ={action.Seq}）。");
            return;
        }
        if (string.IsNullOrWhiteSpace(action.EventCode))
        {
            issues.Add($"动作 SEQ={action.Seq}：缺少事件。");
            return;
        }
        var trimmedEvent = action.EventCode.Trim().ToUpperInvariant();
        if (!BusinessActionCatalog.IsKnownEvent(trimmedEvent))
        {
            issues.Add($"动作 SEQ={action.Seq}：未知事件 '{action.EventCode}'。");
            return;
        }
        if (!keys.Add(trimmedEvent + "|" + action.Seq))
            issues.Add($"动作 SEQ={action.Seq}：事件 {trimmedEvent} 内顺序号重复。");
        if (!BusinessActionCatalog.FailModes.Contains(action.FailMode))
            issues.Add($"动作 SEQ={action.Seq}：失败模式仅支持 BLOCK / WARN。");
        if (TooLong(action.EffectName, 200)) issues.Add($"动作 SEQ={action.Seq}：效果名称超过 200 字符。");
        if (TooLong(action.Remark, 500)) issues.Add($"动作 SEQ={action.Seq}：说明超过 500 字符。");
        if (TooLong(action.SourceRef, 100)) issues.Add($"动作 SEQ={action.Seq}：溯源超过 100 字符。");
        ValidateJson(action.Condition, $"动作 SEQ={action.Seq} 条件", issues);
        foreach (var conditionIssue in EffectStructSchemas.ValidateConditionJson(action.Condition, $"动作 SEQ={action.Seq} 条件"))
            issues.Add(conditionIssue);

        // 用户点击类动作到此为止：它的键来自操作注册表、参数是入参声明，
        // 效果键目录 / 参数 Schema / 公式行 / 反向结构都不适用于它。
        if (BusinessActionCatalog.IsManualEvent(trimmedEvent))
        {
            ValidateManualAction(action, documentActionKeys, issues);
            return;
        }

        if (!BusinessActionCatalog.IsKnownEffectKey(action.EffectKey))
        {
            issues.Add($"动作 SEQ={action.Seq}：未知效果键 '{action.EffectKey}'。");
        }
        else if (EffectRegistry.Keys.TryGetValue(action.EffectKey, out var status)
                 && status == EffectRegistry.Status.Formula
                 && !(action.Ops ?? Array.Empty<BusinessActionOpDto>())
                     .Any(op => !IsPlaceholderOp(op.OpCode, op.TargetTable, op.TargetField)))
        {
            // A formula effect without expanded rows cannot run: the loader skips
            // translation-phase placeholder rows, leaving an empty op list that the
            // pipeline would treat as a missing service handler.
            issues.Add($"动作 SEQ={action.Seq}：公式型效果 '{action.EffectKey}' 无公式行展开（占位行仅限服务型效果），引擎无法执行。");
        }
        ValidateJson(action.Params, $"动作 SEQ={action.Seq} 参数", issues);
        ValidateJson(action.Reverse, $"动作 SEQ={action.Seq} 反向", issues);
        foreach (var structIssue in EffectStructSchemas.ValidateActionStructs(action.EffectKey, action.Params, action.Reverse))
            issues.Add($"动作 SEQ={action.Seq}：{structIssue}");

        var ops = action.Ops ?? Array.Empty<BusinessActionOpDto>();
        if (ops.Count > 300)
        {
            issues.Add($"动作 SEQ={action.Seq}：公式行数量超过上限（300）。");
            return;
        }
        var opSeqs = new HashSet<int>();
        foreach (var op in ops)
            ValidateOp(action.Seq, op, opSeqs, issues);
    }

    /// <summary>
    /// 用户点击类动作（EVENT_CODE='MANUAL'）的专属校验：键取单据操作注册表；参数取操作入参声明的闭式结构；
    /// 公式行与反向结构必须为空——它们配了也不会被执行，留着只会让配置人员以为会生效。
    /// </summary>
    private static void ValidateManualAction(
        BusinessActionDto action,
        IReadOnlySet<string>? documentActionKeys,
        ICollection<string> issues)
    {
        if (string.IsNullOrWhiteSpace(action.EffectKey)
            || documentActionKeys is null
            || !documentActionKeys.Contains(action.EffectKey.Trim()))
        {
            issues.Add($"动作 SEQ={action.Seq}：未知自定义按钮键 '{action.EffectKey}'（须在单据操作注册表中登记实现）。");
        }
        foreach (var parameterIssue in DocumentActionParams.Validate(action.Params))
        {
            issues.Add($"动作 SEQ={action.Seq}：{parameterIssue}");
        }
        if (!string.IsNullOrWhiteSpace(action.Reverse))
        {
            issues.Add($"动作 SEQ={action.Seq}：用户点击类动作不支持反向结构（REVERSE_STRUCT）。");
        }
        if ((action.Ops ?? Array.Empty<BusinessActionOpDto>())
            .Any(op => !IsPlaceholderOp(op.OpCode, op.TargetTable, op.TargetField)))
        {
            issues.Add($"动作 SEQ={action.Seq}：用户点击类动作不支持公式行（OPS）。");
        }
    }

    private static void ValidateOp(
        int actionSeq,
        BusinessActionOpDto op,
        ISet<int> opSeqs,
        ICollection<string> issues)
    {
        var where = $"动作 SEQ={actionSeq} 公式行 OP_SEQ={op.OpSeq}";
        if (IsPlaceholderOp(op.OpCode, op.TargetTable, op.TargetField))
            return;
        if (op.OpSeq < 1 || !opSeqs.Add(op.OpSeq))
        {
            issues.Add($"{where}：顺序号缺失或重复。");
            return;
        }
        if (!WorkbenchSql.Identifier.IsMatch(op.TargetTable))
            issues.Add($"{where}：目标表名无效 '{op.TargetTable}'。");
        if (!WorkbenchSql.Identifier.IsMatch(op.TargetField))
            issues.Add($"{where}：目标字段名无效 '{op.TargetField}'。");
        if (!BusinessActionCatalog.IsKnownOpCode(op.OpCode))
            issues.Add($"{where}：未知运算 '{op.OpCode}'。");

        if (string.IsNullOrWhiteSpace(op.SourceScope))
        {
            issues.Add($"{where}：缺少源范围。");
            return;
        }
        var scope = op.SourceScope.Trim().ToUpperInvariant();
        if (!BusinessActionCatalog.IsKnownSourceScope(scope))
        {
            issues.Add($"{where}：未知源范围 '{op.SourceScope}'。");
            return;
        }
        if (op.SourceAgg is { } agg && !string.IsNullOrWhiteSpace(agg) && !BusinessActionCatalog.SourceAggregates.Contains(agg))
            issues.Add($"{where}：未知源聚合 '{agg}'。");

        var hasField = !string.IsNullOrWhiteSpace(op.SourceField);
        var hasTerms = !string.IsNullOrWhiteSpace(op.SourceTerms);
        var hasConstant = !string.IsNullOrWhiteSpace(op.SourceConstant);
        var hasSourceTable = !string.IsNullOrWhiteSpace(op.SourceTable);

        if (hasField && hasTerms)
            issues.Add($"{where}：源字段与源加减项只能二选一。");
        if (hasField && !WorkbenchSql.Identifier.IsMatch(op.SourceField!))
            issues.Add($"{where}：源字段名无效 '{op.SourceField}'。");
        if (hasSourceTable && !WorkbenchSql.Identifier.IsMatch(op.SourceTable!))
            issues.Add($"{where}：源表名无效 '{op.SourceTable}'。");

        switch (scope)
        {
            case "CONSTANT":
                if (op.SourceConstant is null)
                    issues.Add($"{where}：源范围为 CONSTANT 时必须提供 sourceConstant。");
                if (hasField || hasTerms || hasSourceTable)
                    issues.Add($"{where}：源范围为 CONSTANT 时不得再提供字段/加减项/源表。");
                break;
            case "TABLE":
                if (!hasSourceTable)
                    issues.Add($"{where}：源范围为 TABLE 时必须提供 sourceTable。");
                if (!hasField && !hasTerms)
                    issues.Add($"{where}：源范围为 TABLE 时需要 sourceField 或 sourceTerms 之一。");
                break;
            case "MASTER":
            case "DETAIL":
                if (!hasField && !hasTerms)
                    issues.Add($"{where}：源范围为 {scope} 时需要 sourceField 或 sourceTerms 之一。");
                if (hasSourceTable || hasConstant)
                    issues.Add($"{where}：源范围为 {scope} 时不得提供 sourceTable/sourceConstant。");
                break;
        }

        if (hasTerms)
            ValidateTerms(op.SourceTerms!, $"{where} 源加减项", issues);
        ValidateJson(op.Match, $"{where} 定位键", issues);
        ValidateJson(op.Condition, $"{where} 条件", issues);
        foreach (var conditionIssue in EffectStructSchemas.ValidateConditionJson(op.Condition, $"{where} 条件"))
            issues.Add(conditionIssue);
        if (TooLong(op.Remark, 200)) issues.Add($"{where}：说明超过 200 字符。");
    }

    private static void ValidateValidationRule(
        ModuleValidationRuleDto rule,
        ISet<string> keys,
        List<string> issues)
    {
        if (rule.Seq < 1)
        {
            issues.Add("校验规则缺少有效顺序号。");
            return;
        }
        if (string.IsNullOrWhiteSpace(rule.ValidationKey))
        {
            issues.Add($"校验规则 SEQ={rule.Seq}：缺少校验模板键。");
            return;
        }
        if (string.IsNullOrWhiteSpace(rule.Stage))
        {
            issues.Add($"校验规则 SEQ={rule.Seq}：缺少阶段。");
            return;
        }
        var stage = rule.Stage.Trim().ToUpperInvariant();
        if (!BusinessActionCatalog.IsKnownValidationStage(stage))
        {
            issues.Add($"校验规则 SEQ={rule.Seq}：未知阶段 '{rule.Stage}'。");
            return;
        }
        if (!keys.Add(stage + "|" + rule.Seq))
            issues.Add($"校验规则 SEQ={rule.Seq}：阶段 {stage} 内顺序号重复。");
        if (TooLong(rule.Message, 500)) issues.Add($"校验规则 SEQ={rule.Seq}：消息超过 500 字符。");
        if (TooLong(rule.Remark, 500)) issues.Add($"校验规则 SEQ={rule.Seq}：说明超过 500 字符。");
        if (TooLong(rule.SourceRef, 100)) issues.Add($"校验规则 SEQ={rule.Seq}：溯源超过 100 字符。");

        if (string.IsNullOrWhiteSpace(rule.Params))
        {
            issues.Add($"校验规则 SEQ={rule.Seq}：params 必填（闭式 JSON 对象）。");
            return;
        }
        try
        {
            using var doc = JsonDocument.Parse(rule.Params);
            var config = new ValidationRuleConfig(
                $"module-validation-{rule.Seq}", rule.ValidationKey, stage, rule.Enabled,
                rule.Message, doc.RootElement.Clone());
            ValidationRuleRegistry.Validate(config, issues);
        }
        catch (JsonException)
        {
            issues.Add($"校验规则 SEQ={rule.Seq}：params 不是合法 JSON。");
        }
    }

    private static void ValidateTerms(string json, string label, ICollection<string> issues)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() == 0)
            {
                issues.Add($"{label} 必须是 JSON 数组。");
                return;
            }
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                var field = item.ValueKind == JsonValueKind.Object
                    && item.TryGetProperty("field", out var f) && f.ValueKind == JsonValueKind.String
                        ? f.GetString()
                        : null;
                var coef = item.ValueKind == JsonValueKind.Object
                    && item.TryGetProperty("coef", out var c) && c.ValueKind == JsonValueKind.Number
                    && c.TryGetInt32(out var n)
                        ? n
                        : (int?)null;
                if (string.IsNullOrWhiteSpace(field))
                    issues.Add($"{label} 存在缺 field 的项。");
                if (coef is not (1 or -1))
                    issues.Add($"{label} 存在非法 coef 的项（仅允许 1 / -1）。");
            }
        }
        catch (JsonException)
        {
            issues.Add($"{label} 不是合法 JSON。");
        }
    }

    private static void ValidateJson(string? value, string label, ICollection<string> issues)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        try
        {
            _ = JsonDocument.Parse(value);
        }
        catch (JsonException)
        {
            issues.Add($"{label} 不是合法 JSON。");
        }
    }

    private static bool TooLong(string? value, int max) =>
        value is not null && value.Length > max;
}
