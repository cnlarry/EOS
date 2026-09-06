using System.Text.Json;

namespace EOS.API.Data.ValidationRules;

/// <summary>模块校验规则（Definition JSON.validationRules 中的一条；零 moduleId 语义）。</summary>
public sealed record ValidationRuleConfig(
    string RuleId,
    string ValidationKey,
    string Stage,
    bool Enabled,
    string? Message,
    JsonElement? Params);

/// <summary>校验规则定义加载结果。</summary>
public sealed record ModuleValidationLoadResult(
    bool Success,
    IReadOnlyList<ValidationRuleConfig> Rules,
    IReadOnlyList<string> Issues);
