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

/// <summary>配置编辑目录（2301 下拉来源；与 BusinessActionCatalog 闭式集合一致）。</summary>
public sealed record BusinessConfigCatalogDto(
    [property: JsonPropertyName("events")] IReadOnlyList<string> Events,
    [property: JsonPropertyName("failModes")] IReadOnlyList<string> FailModes,
    [property: JsonPropertyName("effectKeys")] IReadOnlyList<string> EffectKeys,
    [property: JsonPropertyName("opCodes")] IReadOnlyList<string> OpCodes,
    [property: JsonPropertyName("sourceScopes")] IReadOnlyList<string> SourceScopes,
    [property: JsonPropertyName("sourceAggregates")] IReadOnlyList<string> SourceAggregates,
    [property: JsonPropertyName("validationStages")] IReadOnlyList<string> ValidationStages,
    [property: JsonPropertyName("validationKeys")] IReadOnlyList<string> ValidationKeys);

/// <summary>效果参数 Schema 元数据（根键白名单；供 2301 按 Schema 渲染参数编辑器）。</summary>
public sealed record EffectParamSchemaDto(
    [property: JsonPropertyName("effectKey")] string EffectKey,
    [property: JsonPropertyName("rootKeys")] IReadOnlyList<string> RootKeys);

/// <summary>配置编辑 Schema 目录（效果参数根键 + 反向 kind 枚举）。</summary>
public sealed record BusinessConfigSchemasDto(
    [property: JsonPropertyName("effects")] IReadOnlyList<EffectParamSchemaDto> Effects,
    [property: JsonPropertyName("reverseKinds")] IReadOnlyList<string> ReverseKinds);
