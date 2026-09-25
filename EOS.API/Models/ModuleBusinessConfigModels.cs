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
    [property: JsonPropertyName("ops")] IReadOnlyList<BusinessActionOpDto>? Ops = null,
    // 按钮标题（仅 EVENT_CODE='MANUAL' 使用；EFFECT_NAME 是整类效果的命名，不足以当按钮文案）。
    [property: JsonPropertyName("label")] string? Label = null,
    // 点击前是否先返回"将会发生什么"让用户确认（仅 MANUAL 使用）。
    [property: JsonPropertyName("confirmTag")] bool ConfirmTag = false);

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
    [property: JsonPropertyName("validationKeys")] IReadOnlyList<string> ValidationKeys,
    [property: JsonPropertyName("labels")] BusinessConfigLabelsDto? Labels = null,
    // 可配置的自定义按钮键（EVENT_CODE='MANUAL' 行只能从这里挑；未登记实现即发布不出去）。
    [property: JsonPropertyName("documentActions")] IReadOnlyList<DocumentActionCatalogEntryDto>? DocumentActions = null);

/// <summary>一个可配置的自定义按钮（2301 下拉项）。</summary>
public sealed record DocumentActionCatalogEntryDto(
    [property: JsonPropertyName("key")] string Key,
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("placement")] string Placement);

/// <summary>自定义按钮的授权镜子（每个按钮当前多少用户 / 多少组可用）。</summary>
public sealed record DocumentActionAuthorizationMirrorDto(
    [property: JsonPropertyName("buttons")] IReadOnlyList<DocumentActionAuthorizationEntryDto> Buttons);

public sealed record DocumentActionAuthorizationEntryDto(
    [property: JsonPropertyName("seq")] int Seq,
    [property: JsonPropertyName("key")] string Key,
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("users")] int Users,
    [property: JsonPropertyName("groups")] int Groups);

/// <summary>
/// 目录值的中文显示名（与目录集合同源，缺标签即在界面上露出英文码）。
/// 只影响展示，不参与保存校验。
/// </summary>
public sealed record BusinessConfigLabelsDto(
    [property: JsonPropertyName("events")] IReadOnlyDictionary<string, string> Events,
    [property: JsonPropertyName("failModes")] IReadOnlyDictionary<string, string> FailModes,
    [property: JsonPropertyName("effectKeys")] IReadOnlyDictionary<string, string> EffectKeys,
    [property: JsonPropertyName("opCodes")] IReadOnlyDictionary<string, string> OpCodes,
    [property: JsonPropertyName("sourceScopes")] IReadOnlyDictionary<string, string> SourceScopes,
    [property: JsonPropertyName("sourceAggregates")] IReadOnlyDictionary<string, string> SourceAggregates,
    [property: JsonPropertyName("validationStages")] IReadOnlyDictionary<string, string> ValidationStages,
    [property: JsonPropertyName("validationKeys")] IReadOnlyDictionary<string, string> ValidationKeys,
    [property: JsonPropertyName("effectKeyDescriptions")] IReadOnlyDictionary<string, string>? EffectKeyDescriptions = null,
    [property: JsonPropertyName("reverseKindDescriptions")] IReadOnlyDictionary<string, string>? ReverseKindDescriptions = null);

/// <summary>
/// 定位键候选：FIELD_RELATION 里一条已登记的效果关系边组（同一关系的主键列集合）。
/// 2301 选一条边组即可生成整段 MATCH_STRUCT，替代手写 JSON。
/// </summary>
public sealed record BusinessRelationGroupDto(
    [property: JsonPropertyName("relationId")] long RelationId,
    [property: JsonPropertyName("relationName")] string? RelationName,
    [property: JsonPropertyName("sourceScope")] string? SourceScope,
    [property: JsonPropertyName("keys")] IReadOnlyList<BusinessRelationKeyDto> Keys);

/// <summary>定位键候选的一段键映射：目标列 ← 来源表.来源列。</summary>
public sealed record BusinessRelationKeyDto(
    [property: JsonPropertyName("fromTable")] string FromTable,
    [property: JsonPropertyName("fromColumn")] string FromColumn,
    [property: JsonPropertyName("toTable")] string ToTable,
    [property: JsonPropertyName("toColumn")] string ToColumn);

/// <summary>
/// 本模块涉及的表/字段中文名（表键 `T_ID`，字段键 `T_ID.F_ID`）。
/// 供 2301 把配置渲染成人话（"收料数量（RECEIVE_QTY）"）；缺元数据的键回落显示列名本身。
/// </summary>
public sealed record BusinessFieldLabelsDto(
    [property: JsonPropertyName("tables")] IReadOnlyDictionary<string, string> Tables,
    [property: JsonPropertyName("fields")] IReadOnlyDictionary<string, string> Fields);

/// <summary>效果参数 Schema 元数据（根键白名单；供 2301 按 Schema 渲染参数编辑器）。</summary>
public sealed record EffectParamSchemaDto(
    [property: JsonPropertyName("effectKey")] string EffectKey,
    [property: JsonPropertyName("rootKeys")] IReadOnlyList<string> RootKeys);

/// <summary>校验模板参数 Schema 元数据（根键白名单；结构与效果参数同形，界面共用同一编辑器）。</summary>
public sealed record ValidationParamSchemaDto(
    [property: JsonPropertyName("validationKey")] string ValidationKey,
    [property: JsonPropertyName("rootKeys")] IReadOnlyList<string> RootKeys);

/// <summary>配置编辑 Schema 目录（效果参数根键 + 校验模板参数根键 + 反向 kind 枚举与中文名）。</summary>
public sealed record BusinessConfigSchemasDto(
    [property: JsonPropertyName("effects")] IReadOnlyList<EffectParamSchemaDto> Effects,
    [property: JsonPropertyName("reverseKinds")] IReadOnlyList<string> ReverseKinds,
    [property: JsonPropertyName("reverseKindLabels")] IReadOnlyDictionary<string, string>? ReverseKindLabels = null,
    [property: JsonPropertyName("validationParams")] IReadOnlyList<ValidationParamSchemaDto>? ValidationParams = null,
    [property: JsonPropertyName("paramFields")] IReadOnlyList<EffectParamFieldsDto>? ParamFields = null,
    [property: JsonPropertyName("reverseKindsByEffect")] IReadOnlyDictionary<string, IReadOnlyList<string>>? ReverseKindsByEffect = null);

/// <summary>一个效果参数的深 Schema 描述（名称/类型/是否必填/枚举/默认值/说明/示例）。</summary>
public sealed record EffectParamFieldDto(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("required")] bool Required,
    [property: JsonPropertyName("enumValues")] IReadOnlyList<string>? EnumValues = null,
    [property: JsonPropertyName("default")] string? Default = null,
    [property: JsonPropertyName("description")] string? Description = null,
    [property: JsonPropertyName("example")] string? Example = null);

/// <summary>一个效果键的参数描述集合。</summary>
public sealed record EffectParamFieldsDto(
    [property: JsonPropertyName("effectKey")] string EffectKey,
    [property: JsonPropertyName("fields")] IReadOnlyList<EffectParamFieldDto> Fields);

/// <summary>预演请求：事件（闭集）+ 单据主键。</summary>
public sealed record EffectSimulationRequest(
    [property: JsonPropertyName("event")] string Event,
    [property: JsonPropertyName("key")] IReadOnlyList<string> Key);

/// <summary>预演报告里的一道闸（前置守卫 / 校验闸）。</summary>
public sealed record EffectSimulationGateDto(
    [property: JsonPropertyName("passed")] bool Passed,
    [property: JsonPropertyName("code")] string? Code = null,
    [property: JsonPropertyName("message")] string? Message = null);

/// <summary>预演报告里一列值的变化。</summary>
public sealed record EffectSimulationColumnChangeDto(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("before")] string? Before,
    [property: JsonPropertyName("after")] string? After);

/// <summary>预演报告里一行目标记录的标识与变化列。</summary>
public sealed record EffectSimulationRowChangeDto(
    [property: JsonPropertyName("identity")] string Identity,
    [property: JsonPropertyName("columns")] IReadOnlyList<EffectSimulationColumnChangeDto> Columns);

/// <summary>预演报告里一条公式行的执行轨迹。</summary>
public sealed record EffectSimulationOpDto(
    [property: JsonPropertyName("opSeq")] int OpSeq,
    [property: JsonPropertyName("targetTable")] string TargetTable,
    [property: JsonPropertyName("targetField")] string TargetField,
    [property: JsonPropertyName("opCode")] string OpCode,
    [property: JsonPropertyName("rowsAffected")] int RowsAffected,
    [property: JsonPropertyName("changes")] IReadOnlyList<EffectSimulationRowChangeDto> Changes);

/// <summary>预演报告里的一个效果步骤。</summary>
public sealed record EffectSimulationStepDto(
    [property: JsonPropertyName("seq")] int Seq,
    [property: JsonPropertyName("effectKey")] string EffectKey,
    [property: JsonPropertyName("effectName")] string? EffectName,
    [property: JsonPropertyName("enabled")] bool Enabled,
    [property: JsonPropertyName("failMode")] string FailMode,
    [property: JsonPropertyName("outcome")] string Outcome,
    [property: JsonPropertyName("conditionMatched")] bool ConditionMatched,
    [property: JsonPropertyName("rowsAffected")] int RowsAffected,
    [property: JsonPropertyName("ops")] IReadOnlyList<EffectSimulationOpDto> Ops,
    [property: JsonPropertyName("condition")] string? Condition = null,
    [property: JsonPropertyName("skipReason")] string? SkipReason = null,
    [property: JsonPropertyName("message")] string? Message = null);

/// <summary>预演报告的步骤计数。</summary>
public sealed record EffectSimulationCountsDto(
    [property: JsonPropertyName("total")] int Total,
    [property: JsonPropertyName("ran")] int Ran,
    [property: JsonPropertyName("skipped")] int Skipped,
    [property: JsonPropertyName("failed")] int Failed);

/// <summary>
/// 效果链预演报告：对一张真实单据在同一事务内跑完整条生效链（含状态翻转），随后无条件回滚。
/// </summary>
public sealed record EffectSimulationReportDto(
    [property: JsonPropertyName("moduleId")] int ModuleId,
    [property: JsonPropertyName("event")] string Event,
    [property: JsonPropertyName("definitionVersion")] string? DefinitionVersion,
    [property: JsonPropertyName("recordKey")] IReadOnlyList<string> RecordKey,
    [property: JsonPropertyName("durationMs")] int DurationMs,
    [property: JsonPropertyName("rolledBack")] bool RolledBack,
    [property: JsonPropertyName("precondition")] EffectSimulationGateDto Precondition,
    [property: JsonPropertyName("validation")] EffectSimulationGateDto Validation,
    [property: JsonPropertyName("effects")] IReadOnlyList<EffectSimulationStepDto> Effects,
    [property: JsonPropertyName("counts")] EffectSimulationCountsDto Counts,
    [property: JsonPropertyName("warnings")] IReadOnlyList<string> Warnings);
