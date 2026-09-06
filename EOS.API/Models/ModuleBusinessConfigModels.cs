using System.Text.Json.Serialization;

namespace EOS.API.Models;

/// <summary>
/// 业务动作公式行（MODULE_BUSINESS_ACTION_OP）。
/// 结构化 JSON 字段（sourceTerms / match / condition）按原始 JSON 字符串传输，
/// 保存时由服务端做闭式结构校验；目标/源表与列在保存时做物理存在校验。
/// </summary>
public sealed record BusinessActionOpDto(
    [property: JsonPropertyName("opSeq")] int OpSeq,
    [property: JsonPropertyName("targetTable")] string TargetTable,
    [property: JsonPropertyName("targetField")] string TargetField,
    [property: JsonPropertyName("opCode")] string OpCode,
    [property: JsonPropertyName("sourceScope")] string SourceScope,
    [property: JsonPropertyName("sourceTable")] string? SourceTable = null,
    [property: JsonPropertyName("sourceField")] string? SourceField = null,
    [property: JsonPropertyName("sourceAgg")] string? SourceAgg = null,
    [property: JsonPropertyName("sourceConstant")] string? SourceConstant = null,
    [property: JsonPropertyName("sourceTerms")] string? SourceTerms = null,
    [property: JsonPropertyName("match")] string? Match = null,
    [property: JsonPropertyName("condition")] string? Condition = null,
    [property: JsonPropertyName("remark")] string? Remark = null);

/// <summary>
/// 模块业务动作（MODULE_BUSINESS_ACTION）：同一事件下按 SEQ 执行的一条效果。
/// </summary>
public sealed record BusinessActionDto(
    [property: JsonPropertyName("seq")] int Seq,
    [property: JsonPropertyName("eventCode")] string EventCode,
    [property: JsonPropertyName("effectKey")] string EffectKey,
    [property: JsonPropertyName("effectName")] string? EffectName = null,
    [property: JsonPropertyName("enabled")] bool Enabled = true,
    [property: JsonPropertyName("failMode")] string FailMode = "BLOCK",
    [property: JsonPropertyName("condition")] string? Condition = null,
    [property: JsonPropertyName("params")] string? Params = null,
    [property: JsonPropertyName("reverse")] string? Reverse = null,
    [property: JsonPropertyName("remark")] string? Remark = null,
    [property: JsonPropertyName("sourceRef")] string? SourceRef = null,
    [property: JsonPropertyName("ops")] IReadOnlyList<BusinessActionOpDto>? Ops = null);

/// <summary>模块校验规则（MODULE_VALIDATION_RULE，与业务动作同一编辑区维护）。</summary>
public sealed record ModuleValidationRuleDto(
    [property: JsonPropertyName("seq")] int Seq,
    [property: JsonPropertyName("stage")] string Stage,
    [property: JsonPropertyName("validationKey")] string ValidationKey,
    [property: JsonPropertyName("enabled")] bool Enabled = true,
    [property: JsonPropertyName("params")] string? Params = null,
    [property: JsonPropertyName("message")] string? Message = null,
    [property: JsonPropertyName("remark")] string? Remark = null,
    [property: JsonPropertyName("sourceRef")] string? SourceRef = null);

/// <summary>模块业务配置读回结果（动作链 + 校验规则）。</summary>
public sealed record ModuleBusinessConfigDto(
    [property: JsonPropertyName("moduleId")] int ModuleId,
    [property: JsonPropertyName("actions")] IReadOnlyList<BusinessActionDto> Actions,
    [property: JsonPropertyName("validationRules")] IReadOnlyList<ModuleValidationRuleDto> ValidationRules);

/// <summary>保存模块业务配置（整模块替换语义；动作/公式行/校验规则同事务写入）。</summary>
public sealed record SaveModuleBusinessConfigRequest(
    [property: JsonPropertyName("actions")] IReadOnlyList<BusinessActionDto> Actions,
    [property: JsonPropertyName("validationRules")] IReadOnlyList<ModuleValidationRuleDto> ValidationRules);
