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

/// <summary>
/// 行为配置面**系统级总览**的一行：某个模块的某条动作行 + 它属于哪个模块。
///
/// 配置面拆开后若只支持"先选模块"，打开页面就是一片空态——使用者无从知道系统里到底配了什么。
/// 总览回答的正是这个问题；它只列**事实**（哪一行、配在哪、开没开），不替调用者做可用性判定。
/// </summary>
public sealed record ModuleBusinessActionOverviewDto(
    [property: JsonPropertyName("moduleId")] int ModuleId,
    [property: JsonPropertyName("moduleDesc")] string ModuleDesc,
    [property: JsonPropertyName("eventCode")] string EventCode,
    [property: JsonPropertyName("seq")] int Seq,
    [property: JsonPropertyName("effectKey")] string EffectKey,
    [property: JsonPropertyName("effectName")] string? EffectName,
    [property: JsonPropertyName("enabled")] bool Enabled,
    [property: JsonPropertyName("failMode")] string FailMode,
    [property: JsonPropertyName("label")] string? Label,
    [property: JsonPropertyName("remark")] string? Remark);

/// <summary>行为配置面系统级总览的一行校验规则（同为事实行，不带可用性判定）。</summary>
public sealed record ModuleValidationRuleOverviewDto(
    [property: JsonPropertyName("moduleId")] int ModuleId,
    [property: JsonPropertyName("moduleDesc")] string ModuleDesc,
    [property: JsonPropertyName("seq")] int Seq,
    [property: JsonPropertyName("stage")] string Stage,
    [property: JsonPropertyName("validationKey")] string ValidationKey,
    [property: JsonPropertyName("enabled")] bool Enabled,
    [property: JsonPropertyName("message")] string? Message);

/// <summary>
/// 配置面的**可选目标模块**：候选集与保存时的拒存规则同源——只有统一工作台模块（有主表）才有
/// 动作 / 按钮 / 分组的消费方。`Configured` 是该模块当前的配置条数，用于把"已配过的"排在前面。
///
/// 为什么放进总览响应而不是让界面调 `/admin/menus`：那扇门属于模块定义面（230101），
/// 配置面按自己的门取自己的候选集。两侧同源由「同一个视图 + 同一张主表条件」保证。
/// </summary>
public sealed record ModuleConfigTargetOptionDto(
    [property: JsonPropertyName("moduleId")] int ModuleId,
    [property: JsonPropertyName("moduleDesc")] string ModuleDesc,
    [property: JsonPropertyName("masterTable")] string MasterTable,
    [property: JsonPropertyName("masterTableDesc")] string MasterTableDesc,
    [property: JsonPropertyName("detailTable")] string DetailTable,
    [property: JsonPropertyName("detailTableDesc")] string DetailTableDesc,
    [property: JsonPropertyName("configured")] int Configured);

/// <summary>
/// 行为配置面的系统级总览。230103（自定义按钮）用 <see cref="Actions"/> = 全库按钮行；
/// 230104（行为动作）用 <see cref="Actions"/> = 全库效果链动作 + <see cref="ValidationRules"/>。
/// <see cref="Targets"/> 是界面左栏的模块清单（含每个模块已配条数）。
/// </summary>
public sealed record ModuleBusinessConfigOverviewDto(
    [property: JsonPropertyName("targets")] IReadOnlyList<ModuleConfigTargetOptionDto> Targets,
    [property: JsonPropertyName("actions")] IReadOnlyList<ModuleBusinessActionOverviewDto> Actions,
    [property: JsonPropertyName("validationRules")] IReadOnlyList<ModuleValidationRuleOverviewDto> ValidationRules,
    /// <summary>调用者在本配置面上是否有写权。界面据此决定"能编辑 / 只读"，而不是把控件灰掉让人猜。</summary>
    [property: JsonPropertyName("canWrite")] bool CanWrite = false,
    /// <summary>
    /// 全库按钮行的**授权镜子**（只有 230103 的总览填充）。跨模块的扁平列表要在行上回答
    /// "这个按钮配了有没有人能用"，就必须随总览一起下发——否则界面只能逐模块再查一次。
    /// </summary>
    [property: JsonPropertyName("buttonAuthorization")]
    IReadOnlyList<ModuleButtonAuthorizationMirrorDto>? ButtonAuthorization = null);

/// <summary>
/// 配置面的**目标模块形状**：配置面选中一个模块后要显示它的主表 / 副表（含表描述）。
/// 与 `/admin/menus` 的模块列表是同一组事实，但那个端点的门在 230101（模块定义面），
/// 配置面不该借用它——故由配置面自己按自己的门取这一份（字段名与菜单侧一致，前端可直接用）。
/// </summary>
public sealed record ModuleConfigTargetDto(
    [property: JsonPropertyName("M_IDX")] int ModuleId,
    [property: JsonPropertyName("M_DESC")] string Description,
    [property: JsonPropertyName("MASTER_TABLE")] string MasterTable,
    [property: JsonPropertyName("DETAIL_TABLE")] string DetailTable,
    [property: JsonPropertyName("MASTER_TABLE_DESC")] string MasterTableDesc,
    [property: JsonPropertyName("DETAIL_TABLE_DESC")] string DetailTableDesc);

/// <summary>模块业务配置读回结果（动作链 + 校验规则）。</summary>
public sealed record ModuleBusinessConfigDto(
    [property: JsonPropertyName("moduleId")] int ModuleId,
    [property: JsonPropertyName("actions")] IReadOnlyList<BusinessActionDto> Actions,
    [property: JsonPropertyName("validationRules")] IReadOnlyList<ModuleValidationRuleDto> ValidationRules);

/// <summary>保存模块业务配置（整模块替换语义；动作/公式行/校验规则同事务写入）。</summary>
public sealed record SaveModuleBusinessConfigRequest(
    [property: JsonPropertyName("actions")] IReadOnlyList<BusinessActionDto> Actions,
    [property: JsonPropertyName("validationRules")] IReadOnlyList<ModuleValidationRuleDto> ValidationRules);

/// <summary>配置编辑目录（230101 下拉来源；与 BusinessActionCatalog 闭式集合一致）。</summary>
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
    [property: JsonPropertyName("documentActions")] IReadOnlyList<DocumentActionCatalogEntryDto>? DocumentActions = null,
    // 当前接不到效果链的事件（可配置、但配了不会跑）：界面必须如实标注。
    [property: JsonPropertyName("inertEvents")] IReadOnlyList<string>? InertEvents = null,
    // 事件在库内的使用次数（全库）：界面据此决定"库内 0 行且接不到效果链"的事件不可选（暂未启用），
    // 而"已有行"的事件保持可选但必须标注——隐藏会让既有配置无法编辑。
    [property: JsonPropertyName("eventUsage")] IReadOnlyDictionary<string, int>? EventUsage = null);

/// <summary>一个可配置的自定义按钮（230101 下拉项）。</summary>
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
/// 授权镜子**按模块**的一层分组：授权粒度是「模块 + 按钮键」，同一个键配在不同模块上互不相干，
/// 全库列表因此必须按模块分开取数，不能合成一份。
/// </summary>
public sealed record ModuleButtonAuthorizationMirrorDto(
    [property: JsonPropertyName("moduleId")] int ModuleId,
    [property: JsonPropertyName("buttons")] IReadOnlyList<DocumentActionAuthorizationEntryDto> Buttons);

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
/// 230101 选一条边组即可生成整段 MATCH_STRUCT，替代手写 JSON。
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
/// 供 230101 把配置渲染成人话（"收料数量（RECEIVE_QTY）"）；缺元数据的键回落显示列名本身。
/// </summary>
public sealed record BusinessFieldLabelsDto(
    [property: JsonPropertyName("tables")] IReadOnlyDictionary<string, string> Tables,
    [property: JsonPropertyName("fields")] IReadOnlyDictionary<string, string> Fields);

/// <summary>效果参数 Schema 元数据（根键白名单；供 230101 按 Schema 渲染参数编辑器）。</summary>
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
    [property: JsonPropertyName("reverseKindsByEffect")] IReadOnlyDictionary<string, IReadOnlyList<string>>? ReverseKindsByEffect = null,
    // 效果配方目录（配置面的默认视图；配方只做预填与收敛，专家模式仍可逐字段编辑）。
    [property: JsonPropertyName("recipes")] IReadOnlyList<EffectRecipeDto>? Recipes = null);

/// <summary>
/// 一个效果配方（业务概念 → 实现键的映射）。字段刻意少：凡界面能从 `paramFields` /
/// `reverseKindsByEffect` 读出来的都不在这里重复下发。
/// </summary>
public sealed record EffectRecipeDto(
    [property: JsonPropertyName("key")] string Key,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("summary")] string Summary,
    [property: JsonPropertyName("eventCodes")] IReadOnlyList<string> EventCodes,
    [property: JsonPropertyName("effectKeys")] IReadOnlyList<string> EffectKeys,
    [property: JsonPropertyName("formulaMode")] bool FormulaMode,
    [property: JsonPropertyName("requiresRelation")] bool RequiresRelation,
    [property: JsonPropertyName("reversePreset")] string ReversePreset,
    [property: JsonPropertyName("paramsHint")] string ParamsHint,
    [property: JsonPropertyName("note")] string? Note = null);

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

/// <summary>
/// 预演请求：事件（闭集）+ 单据主键 + 可选的**未保存草稿**。
///
/// 带 `Draft` 时，预演按草稿里的动作链/校验规则跑（其余部分仍取已发布快照）——
/// 配置者据此回答"我这次改完会发生什么"；不带则按已发布配置跑。
/// </summary>
public sealed record EffectSimulationRequest(
    [property: JsonPropertyName("event")] string Event,
    [property: JsonPropertyName("key")] IReadOnlyList<string> Key,
    [property: JsonPropertyName("draft")] SaveModuleBusinessConfigRequest? Draft = null);

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
    [property: JsonPropertyName("warnings")] IReadOnlyList<string> Warnings,
    // 这次预演跑的配置来自哪一处：published（已发布快照）/ draft（本次未保存草稿）。
    // 报告必须自证来源——同一份单据在两种配置下结果可能不同。
    [property: JsonPropertyName("configSource")] string ConfigSource = "published");

/// <summary>
/// 「停用本模块全部动作」的结果（ADR-035 §2.4）：只停不启，故回传实际被置为停用的行数，
/// 让界面能如实说"停了 0 条（本来就没有启用的动作）"而不是假装成功。
/// `Publish` 与保存路径同语义（ADR-033 §7.3 方案 B）：写完后自动发布一次，
/// 使这个批量动作与页面上其它写操作一样"点完即生效"，不必再回 230101 单独发布。
/// </summary>
public sealed record DisableAllActionsResultDto(
    [property: JsonPropertyName("moduleId")] int ModuleId,
    [property: JsonPropertyName("disabled")] int Disabled,
    [property: JsonPropertyName("publish")] WorkbenchPublishResult? Publish = null);

/// <summary>
/// 业务配置保存的**显式作用域**（[ADR-033](../../docs/decisions/ADR-033-系统功能目录与模块配置面拆分.md) §6）。
///
/// 自定义按钮与行为动作**同表**（`MODULE_BUSINESS_ACTION`，按钮是 `EVENT_CODE='MANUAL'` 的行），
/// 而写路径是"整模块全删全插"。配置面一旦拆开，两个页面各自保存若都发全量请求，保存按钮就会删光
/// 行为动作（反之亦然）——**静默数据损失**，而且两边都会标脏、都会写审计"保存成功"。
///
/// 所以保存必须显式声明作用域：`Manual` 只替换按钮行；`Actions` 只替换非按钮动作行 + 全部校验规则；
/// `All` 保留今天的全量替换语义（供"模块 + 配置"同事务合成保存）。**显式优于推导**：
/// 空列表与"未提交此域"在 JSON 上是同一件事，服务端不可能从负载里推出来。
/// </summary>
public enum BusinessConfigScope
{
    All,
    Actions,
    Manual,
}

/// <summary>作用域的解析（HTTP 查询参数 / 请求体字段共用一处，避免两处口径分叉）。</summary>
public static class BusinessConfigScopes
{
    /// <summary>
    /// 解析作用域字面量：`all`（**默认**）/`actions`/`manual`，大小写不敏感。
    /// 其它值返回 false，由调用方回 400——**不静默回退到 all**：那会让一个拼错的 scope 变成"全量替换"，
    /// 正是这套机制要防的静默损失。
    /// </summary>
    public static bool TryParse(string? value, out BusinessConfigScope scope)
    {
        switch (value?.Trim().ToLowerInvariant())
        {
            case null or "" or "all":
                scope = BusinessConfigScope.All;
                return true;
            case "actions":
                scope = BusinessConfigScope.Actions;
                return true;
            case "manual":
                scope = BusinessConfigScope.Manual;
                return true;
            default:
                scope = BusinessConfigScope.All;
                return false;
        }
    }
}
