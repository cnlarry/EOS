using System.Text.Json.Serialization;

namespace EOS.API.Models;

/// <summary>
/// MODULES 菜单节点（对应旧 可编辑字段全集）。
/// JSON 字段名与数据库/完全一致（M_IDX / MASTER_TABLE / GROUP1..5 等）。
/// M_P_IDX 为 0 或 null 表示根节点。
/// </summary>
public sealed record MenuAdminModule(
    [property: JsonPropertyName("M_IDX")] int M_IDX,
    [property: JsonPropertyName("M_ALIAS")] string? M_ALIAS,
    [property: JsonPropertyName("M_DESC")] string M_DESC,
    /// <summary>
    /// 承载页（唯一路由字段）：空 = 目录节点/未声明（单据模块由主表判定，默认走统一工作台）；
    /// `/workbench` = 统一工作台；精确路径 = 自定义承载页。见迁移 321 与 <see cref="ModuleRouteValidator"/>。
    /// </summary>
    [property: JsonPropertyName("M_URL")] string? M_URL,
    [property: JsonPropertyName("DETAIL_NO_FIELDS")] string? DETAIL_NO_FIELDS,
    [property: JsonPropertyName("DETAIL_NO_SAVE")] bool DETAIL_NO_SAVE,
    [property: JsonPropertyName("SEARCH_1")] bool SEARCH_1,
    [property: JsonPropertyName("SEARCH_2")] bool SEARCH_2,
    [property: JsonPropertyName("M_P_IDX")] int? M_P_IDX,
    [property: JsonPropertyName("SORT_IDX")] int SORT_IDX,
    [property: JsonPropertyName("M_TAG")] bool M_TAG,
    [property: JsonPropertyName("AUTO_APPROVE")] bool AUTO_APPROVE,
    [property: JsonPropertyName("IF_COPY")] bool IF_COPY,
    [property: JsonPropertyName("ERROR_NO_SAVE")] bool ERROR_NO_SAVE,
    [property: JsonPropertyName("SORT_FIELDS")] string? SORT_FIELDS,
    [property: JsonPropertyName("MASTER_TABLE")] string? MASTER_TABLE,
    [property: JsonPropertyName("FILTER")] string? FILTER,
    [property: JsonPropertyName("DETAIL_TABLE")] string? DETAIL_TABLE,
    [property: JsonPropertyName("NOT_BACK_FIELDS_M")] string? NOT_BACK_FIELDS_M,
    [property: JsonPropertyName("NOT_BACK_FIELDS")] string? NOT_BACK_FIELDS,
    [property: JsonPropertyName("GROUP1")] bool GROUP1,
    [property: JsonPropertyName("GROUP_EXP1")] string? GROUP_EXP1,
    [property: JsonPropertyName("GROUP_DESC1")] string? GROUP_DESC1,
    [property: JsonPropertyName("GROUP2")] bool GROUP2,
    [property: JsonPropertyName("GROUP_EXP2")] string? GROUP_EXP2,
    [property: JsonPropertyName("GROUP_DESC2")] string? GROUP_DESC2,
    [property: JsonPropertyName("GROUP3")] bool GROUP3,
    [property: JsonPropertyName("GROUP_EXP3")] string? GROUP_EXP3,
    [property: JsonPropertyName("GROUP_DESC3")] string? GROUP_DESC3,
    [property: JsonPropertyName("GROUP4")] bool GROUP4,
    [property: JsonPropertyName("GROUP_EXP4")] string? GROUP_EXP4,
    [property: JsonPropertyName("GROUP_DESC4")] string? GROUP_DESC4,
    [property: JsonPropertyName("GROUP5")] bool GROUP5,
    [property: JsonPropertyName("GROUP_EXP5")] string? GROUP_EXP5,
    [property: JsonPropertyName("GROUP_DESC5")] string? GROUP_DESC5,
    [property: JsonPropertyName("LAST_UPDATE_BY")] string? LAST_UPDATE_BY,
    [property: JsonPropertyName("LAST_UPDATE_DATE")] DateTime? LAST_UPDATE_DATE,
    [property: JsonPropertyName("FORM_TABS")] string? FORM_TABS = null,
    [property: JsonPropertyName("FORM_COLUMNS")] int? FORM_COLUMNS = null,
    // 内置动作「受控注册码」字段（FORM_BUTTONS）于迁移 320 退役并物理删列：
    // 工具栏按钮由能力 + 权限决定，自定义按钮走 MODULE_BUSINESS_ACTION（另一条通路）。
    /// <summary>用户自选图标名（留空时按 NavigationIcons/根名关键字规则解析；仅一级菜单显示）。</summary>
    [property: JsonPropertyName("M_ICON")] string? M_ICON = null,
    /// <summary>只读展示字段：所在根菜单的侧栏图标名（服务端解析，前端保存时忽略）。</summary>
    [property: JsonPropertyName("Icon")] string? Icon = null,
    /// <summary>模块级效果引擎开关（发布后写入 Definition effectEngine.enabled）。</summary>
    [property: JsonPropertyName("EFFECT_ENGINE_TAG")] bool EffectEngineTag = false,
    /// <summary>只读展示字段：操作主表描述（TABLES.T_DESC，服务端解析，保存时忽略）。</summary>
    [property: JsonPropertyName("MASTER_TABLE_DESC")] string? MasterTableDesc = null,
    /// <summary>只读展示字段：操作副表描述（TABLES.T_DESC，服务端解析，保存时忽略）。</summary>
    [property: JsonPropertyName("DETAIL_TABLE_DESC")] string? DetailTableDesc = null,
    /// <summary>只读状态：已有保存但未发布的改动（WORKBENCH_MODULE_DIRTY）。</summary>
    [property: JsonPropertyName("DIRTY_TAG")] bool DirtyTag = false,
    /// <summary>只读状态：当前生效的 Definition 快照版本（无则未发布）。</summary>
    [property: JsonPropertyName("PUBLISH_VERSION")] int? PublishVersion = null,
    /// <summary>只读状态：当前生效版本的发布时间。</summary>
    [property: JsonPropertyName("PUBLISHED_AT")] DateTime? PublishedAt = null,
    /// <summary>
    /// 模块备注：写"这个模块是干什么的"、口径约定等附加信息，给后来接手的人看。
    /// 2301「基础」页签可编辑（2026-10-06 用户要求把它放出来）；空串按 NULL 落库。
    /// </summary>
    [property: JsonPropertyName("REMARK")] string? REMARK = null,
    /// <summary>
    /// **只读**形态判定（服务端算，保存时忽略）：`WORKBENCH` 统一工作台模块 / `CUSTOMPAGE` 自定义承载页 /
    /// `DIRECTORY` 目录节点。与 SQL 视图 `dbo.V_MODULE_NODE` 同源，2301 据此决定哪些配置项该出现
    /// （目录没有可配项；自定义承载页只留基础与数据源锚点；工作台模块全配）。
    /// </summary>
    [property: JsonPropertyName("NODE_KIND")] string? NODE_KIND = null);
    // 表单呈现配置（打开方式 / 弹窗宽高）不在这张只读投影里：模块管理只管模块自身的字段，
    // 呈现配置与页签级一行几列都在——且只在——表单设计器里配（`FormLayoutRepository` 读写
    // `MODULES.FORM_OPEN_MODE` 等三列与 `MODULE_FORM_TAB.LAYOUT_COLUMNS`）。
    // 此前这三个字段只是 2301「统一表单」页签的只读摘要，该页签已删，投影随之收口。
    // 迁移 324 把 MODULES 的八列旧系统遗产删了（CONFIRM_TAG/OWNER/OWNER_G/CONFIRM_DATE/
    // CONFIRM_PERSON/CI/CREATE_PERSON/CREATE_DATE），REMARK 是那批里唯一留下的（用来写备注）。

/// <summary>
/// 菜单保存载荷：模块行 + 可选的行为动作/校验规则 + 可选的默认查询列，三者在同一事务内落库。
/// BusinessConfig / DefaultColumns 传 null 或空表示该部分保持不动（仅保存模块行）。
/// </summary>
public sealed record SaveMenuModuleRequest(
    [property: JsonPropertyName("module")] MenuAdminModule Module,
    [property: JsonPropertyName("businessConfig")] SaveModuleBusinessConfigRequest? BusinessConfig = null,
    [property: JsonPropertyName("defaultColumns")] IReadOnlyList<SaveMenuDefaultColumns>? DefaultColumns = null);

public sealed record MenuRenameRequest([property: JsonPropertyName("description")] string Description);

/// <summary>模块定义快照的历史版本（只读）。</summary>
public sealed record MenuModuleVersion(
    [property: JsonPropertyName("version")] int Version,
    [property: JsonPropertyName("definitionVersion")] string DefinitionVersion,
    [property: JsonPropertyName("publishedBy")] string? PublishedBy,
    [property: JsonPropertyName("publishedAt")] DateTime? PublishedAt,
    [property: JsonPropertyName("validationStatus")] string ValidationStatus,
    [property: JsonPropertyName("isCurrent")] bool IsCurrent);

public sealed record MenuEnabledRequest([property: JsonPropertyName("enabled")] bool Enabled);

public sealed record MenuAdminList(int Total, IReadOnlyList<MenuAdminModule> Modules);

/// <summary>菜单管理表选择器候选（TABLES 元数据，供操作主表/副表选择）。</summary>
public sealed record MenuAdminTableInfo(
    [property: JsonPropertyName("T_ID")] string TId,
    [property: JsonPropertyName("T_DESC")] string TDesc,
    [property: JsonPropertyName("T_KIND")] string? TKind,
    [property: JsonPropertyName("T_TYPE")] string? TType);

/// <summary>菜单管理字段选择器候选（FIELDS 元数据，供排序字段/必需字段/不可解批字段/过滤条件选择）。</summary>
public sealed record MenuAdminFieldInfo(
    [property: JsonPropertyName("F_ID")] string FieldId,
    [property: JsonPropertyName("F_DESC")] string FieldDesc,
    [property: JsonPropertyName("F_TYPE")] string DataType,
    [property: JsonPropertyName("IS_VISIBLE")] bool IsVisible,
    [property: JsonPropertyName("IS_VIRTUAL")] bool IsVirtual,
    [property: JsonPropertyName("IS_QUERY")] bool IsQuery);

/// <summary>菜单默认查询列（SYSQL_DEFAULT）候选字段。Table 为 master/detail 之一。</summary>
public sealed record MenuDefaultColumn(string Key, string Label, bool IsSelected, int Order);

public sealed record MenuDefaultColumns(string Table, string TableKind, IReadOnlyList<MenuDefaultColumn> Fields);

public sealed record SaveMenuDefaultColumns(string Table, IReadOnlyList<string> FieldIds);

/// <summary>
/// 菜单同级排序动作：top=移到同级顶部、up=向上一位、down=向下一位、bottom=移到同级底部。
/// 排序只调整同级节点（M_P_IDX 相同）的 SORT_IDX，
/// </summary>
public sealed record MenuReorderRequest(string Action);

/// <summary>
/// 菜单拖拽移动：parentId=目标父节点（null/0 表示根级），beforeId=插入到该同级节点之前
/// （null 表示追加到同级末尾）。服务端重写相关同级 SORT_IDX 并同步 M_P_IDX/M_ROOT_IDX。
/// </summary>
public sealed record MenuMoveRequest(int? ParentId, int? BeforeId);
