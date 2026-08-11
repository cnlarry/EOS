using System.Text.Json.Serialization;

namespace EOS.API.Models;

/// <summary>
/// MODULES 菜单节点（对应旧 Admin/MenuBuilder.aspx 可编辑字段全集）。
/// JSON 字段名与数据库/旧系统完全一致（M_IDX / MASTER_TABLE / GROUP1..5 等）。
/// M_P_IDX 为 0 或 null 表示根节点。
/// </summary>
public sealed record MenuAdminModule(
    [property: JsonPropertyName("M_IDX")] int M_IDX,
    [property: JsonPropertyName("M_ALIAS")] string? M_ALIAS,
    [property: JsonPropertyName("M_DESC")] string M_DESC,
    [property: JsonPropertyName("M_URL")] string? M_URL,
    [property: JsonPropertyName("NEW_URL")] string? NEW_URL,
    [property: JsonPropertyName("MODI_URL")] string? MODI_URL,
    [property: JsonPropertyName("HELP_URL")] string? HELP_URL,
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
    [property: JsonPropertyName("UPDATE_SP")] string? UPDATE_SP,
    [property: JsonPropertyName("AFTERSAVE_SP")] string? AFTERSAVE_SP,
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
    [property: JsonPropertyName("FORM_BUTTONS")] string? FORM_BUTTONS = null);

public sealed record MenuAdminList(int Total, IReadOnlyList<MenuAdminModule> Modules);

/// <summary>菜单默认查询列（SYSQL_DEFAULT）候选字段。Table 为 master/detail 之一。</summary>
public sealed record MenuDefaultColumn(string Key, string Label, bool IsSelected, int Order);

public sealed record MenuDefaultColumns(string Table, string TableKind, IReadOnlyList<MenuDefaultColumn> Fields);

public sealed record SaveMenuDefaultColumns(string Table, IReadOnlyList<string> FieldIds);
