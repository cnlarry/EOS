using System.Text.Json.Serialization;
using System.Text.Json;

namespace EOS.API.Models;

/// <summary>Field projection for workbench lists and forms.</summary>
public sealed record WorkbenchField(string Key, string Label, string DataType, int Width, string? Align, bool IsPrimaryKey, bool IsVisible = true, bool IsQueryable = true, string HeaderAlign = "center", string? Format = null, string? BrowseUrl = null, int? BrowseModuleId = null, bool IsVirtual = false, [property: JsonIgnore] string? VirtualExpression = null, [property: JsonIgnore] string? ConvertFunction = null, IReadOnlyList<string>? BrowseKeyFields = null);
public sealed record WorkbenchColumn(string Key, string Label, bool IsVisible, int Order, bool IsVirtual = false);
public sealed record WorkbenchColumnSettings(IReadOnlyList<WorkbenchColumn> Master, IReadOnlyList<WorkbenchColumn> Detail);
public sealed record SaveWorkbenchColumns(IReadOnlyList<string> Master, IReadOnlyList<string> Detail);
public sealed record UpdateColumnWidthsRequest(IReadOnlyDictionary<string, int> Master, IReadOnlyDictionary<string, int>? Detail);
public sealed record WorkbenchFieldSummary(string Key, string Label, bool IsVisible, bool IsDefault, bool IsQueryable, bool IsReadonly, bool IsCost, bool IsSecrecy, bool IsVirtual);

/// <summary>
/// Chooser data source bound to a field. Filter carries the FILTER_STRUCT JSON and
/// ReturnMapping the RETURN_ITEMS JSON; the runtime source definition is resolved by the
/// form-chooser endpoint from FIELD_DATASOURCE by SerialNo.
/// </summary>
public sealed record FieldChooserSource(
    bool Active,
    string? Table,
    string? Description,
    int? ModuleId,
    string? Filter,
    string? ReturnMapping,
    int? SerialNo = null,
    /// <summary>
    /// 服务端注册数据源的键（如 <c>inventory.batches</c>）：非空时前端走统一选择器的 sourceKey 分支，
    /// 列与排序由服务端注册表给出（比"按表名查"更精确）。为空则走 formField 分支。
    /// </summary>
    string? SourceKey = null);

// 内置动作「受控注册码」字段（FORM_BUTTONS）与它的 WorkbenchButton 契约已于迁移 320 退役：
// 工具栏按钮改由「能力 + 权限」决定（批核/解批/结案/附件/打印…），不再有白名单开关列；
// 自定义按钮走 MODULE_BUSINESS_ACTION（EVENT_CODE='MANUAL'），是另一条独立通路。
public sealed record WorkbenchFieldMetadata(string Key, string Label, string DataType, int Width, string? Align, string HeaderAlign, string? Format, bool IsVisible, bool IsDefault, bool IsQueryable, bool IsReadonly, bool IsRequired, bool IsCost, bool IsSecrecy, string? DefaultValue, int? VerifyIndex, string? Regex, string? Remark, string? BrowseUrl, int? BrowseModuleId, bool OnlyChoose, bool ChooseMultiple, string? ChoosePage, IReadOnlyList<FieldChooserSource> Choosers, bool IsVirtual, string? VirtualExpression, bool CanCopy, bool IsAutoIncrement, string? ConvertFunction, string? LastUpdatedBy, DateTime? LastUpdatedAt, string? FormOptions = null);
public sealed record UpdateWorkbenchFieldMetadata(string Label, string DataType, int Width, string Align, string HeaderAlign, string? Format, bool IsVisible, bool IsDefault, bool IsQueryable, bool IsReadonly, bool IsRequired, bool IsCost, bool IsSecrecy, string? DefaultValue, int? VerifyIndex, string? Regex, string? Remark, string? BrowseUrl, int? BrowseModuleId, bool OnlyChoose, bool ChooseMultiple, string? ChoosePage, IReadOnlyList<FieldChooserSource> Choosers, bool CanCopy, WorkbenchFieldMetadata? Original, string? FormOptions = null);

/// <summary>Runtime workbench definition for one module (fields, permissions and workflow flags).</summary>
public sealed record WorkbenchDefinition(
    int ModuleId,
    string Title,
    string MasterTable,
    string? DetailTable,
    IReadOnlyList<WorkbenchField> MasterFields,
    IReadOnlyList<WorkbenchField> DetailFields,
    string? DefaultSort,
    bool HasAdd,
    bool HasEdit,
    bool DetailNoSave,
    IReadOnlyList<string> MasterPkOrder,
    string DetailNoFields,
    bool HasWorkflow,
    string? ModuleFilter = null,
    IReadOnlySet<string>? FilterFieldKeys = null,
    string UserId = "",
    string? ExecTag = null,
    ModuleBusinessRule? BusinessRule = null,
    bool AutoApprove = false,
    // 分组表达式按 GROUP_ID 索引（配置见 MODULE_GROUPS / 2315 模块分组）。**不进快照**：
    // 分组是"保存即生效"的配置，每次装配定义时实时读取，故与 FormLayout 那类随快照冻结的段不同。
    [property: JsonIgnore] IReadOnlyDictionary<int, string>? GroupExpressions = null,
    string? FormTabs = null,
    int? FormColumns = null,
    bool IfCopy = false,
    bool SearchMaster = false,
    bool SearchDetail = false,
    // 新增/修改/帮助三条路由字段（NEW_URL / MODI_URL / HELP_URL）已于迁移 321 物理删除：
    // 能不能新增/编辑由统一表单名单折叠（`WorkbenchAccessPolicy.FoldRoutes`），
    // 页面落点只有 `M_URL` 一个字段（`ModuleRouteValidator`）。
    bool CanDelete = false,
    string? DefinitionVersion = null,
    [property: JsonPropertyName("businessActions")] JsonElement? BusinessActions = null,
    [property: JsonPropertyName("validationRules")] JsonElement? ValidationRules = null,
    [property: JsonPropertyName("effectEngine")] JsonElement? EffectEngine = null,
    // 模块级表单版式段（随快照下发）。放在参数表末尾，让快照 JSON 的新增段落在尾部：
    // 逐字比对时"首个差异位置"出现在末尾，历史快照一眼能看出只多了这一段。
    FormLayoutDefinition? FormLayout = null,
    // 表单打开方式（`MODULES.FORM_OPEN_MODE` + 弹窗宽高），与版式同属"模块级表单事实"。
    // 取值见 `FormOpenModes`；未配置时为 null。**栅格列数不在这里**：它是页签级事实
    // （`FormLayout.Tabs[].Columns`，库内 MODULE_FORM_TAB.LAYOUT_COLUMNS），模块级那层自迁移 319 起不存在。
    string? FormOpenMode = null,
    int? FormDialogWidth = null,
    int? FormDialogHeight = null)
{
    /// <summary>Effect-engine gate read from the published snapshot (effectEngine.enabled).</summary>
    public bool EffectEngineEnabled =>
        EffectEngine is { ValueKind: JsonValueKind.Object } section
        && section.TryGetProperty("enabled", out var enabled)
        && enabled.ValueKind == JsonValueKind.True;
}

/// <summary>
/// 按钮元数据：随表单/工作台定义下发给前端（`userActions[]`），前端只按它渲染按钮，
/// 因此未获授权的操作根本不会出现在界面上；服务端对每次请求仍独立鉴权。
/// </summary>
public sealed record DocumentActionMetadata(
    [property: JsonPropertyName("key")] string Key,
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("confirmTag")] bool ConfirmTag,
    [property: JsonPropertyName("failMode")] string FailMode,
    [property: JsonPropertyName("params")] JsonElement? Params = null,
    [property: JsonPropertyName("placement")] string Placement = DocumentActionPlacements.Master);

/// <summary>按钮的渲染落点：明细级占子表标题栏，单据级贴浏览态工具条尾部。</summary>
public static class DocumentActionPlacements
{
    /// <summary>单据级：作用于整单。</summary>
    public const string Master = "master";

    /// <summary>明细级：作用于子表（重算明细、按差异生成下游单据等）。</summary>
    public const string Detail = "detail";
}

/// <summary>
/// Form tab definition (页签只来自 MODULE_FORM_TAB；此处为统一模型)。
/// <paramref name="Columns"/> = 该页签的布局列数（1..4，库内 NOT NULL DEFAULT 4）——
/// 同一表单的不同页签可以一行几列各不相同。**可空只是为兼容历史快照**（那个年代取不到值），
/// 读取/渲染/夹取一律经 <see cref="Forms.FormLayoutDerivation.ResolveTabColumns"/> 兜到 4 列。
/// </summary>
public sealed record FormTabDefinition(int No, string Title, int? Columns = null);

/// <summary>
/// Form dropdown option (parsed from FIELDS.FORM_OPTIONS). <paramref name="Disabled"/> means the
/// option is shown but cannot be picked (a tier that is not implemented yet).
/// </summary>
public sealed record FormOptionItem(string Value, string Label, bool Disabled = false);
public sealed record FormDefinition(int ModuleId, string Title, string MasterTable, string? DetailTable, bool HasAdd, bool HasEdit, string Mode, IReadOnlyList<FormFieldDefinition> MasterFields, IReadOnlyList<FormFieldDefinition> DetailFields, IReadOnlyList<string> MasterPkOrder, string DetailNoFields, string DetailDfVerify, IReadOnlyList<FormTabDefinition> Tabs = default!,
    // 栅格列数**只有页签级一处真源**（`Tabs[].Columns`）：原先这里还有一个模块级兜底 `Columns`，
    // 自迁移 319 起就不存在——历史快照缺页签列数时，两侧统一按 4 列兜底（`FormLayoutDerivation.DefaultColumns`）。
    IReadOnlyDictionary<string, string> DefaultValues = default!, bool HasWorkflow = false, bool IfCopy = false, bool SearchMaster = false, bool SearchDetail = false, bool CanDelete = false, bool CanApprove = false, bool CanDeapprove = false, bool CanEndCase = false, bool CanUnEndCase = false, bool CanFileView = false, bool CanFileUpda = false, bool CanFileEdit = false, bool CanFileDele = false, bool CanAddNew = false, bool CanEdit = false, bool CanSetup = false, bool HasStatelessApprove = false, bool HasApproveCapability = false,
    [property: JsonPropertyName("userActions")] IReadOnlyList<DocumentActionMetadata>? UserActions = null,
    // 表单设计权（供前端决定是否渲染设计入口）。服务端写端点独立鉴权，此处只影响界面。
    bool CanFormDesign = false,
    // 打开方式（`FormOpenModes` 的取值）与弹窗尺寸（px）：只有 dialog 方式带尺寸，其余为 null。
    string OpenMode = FormOpenModes.Tab, int? DialogWidth = null, int? DialogHeight = null);
public sealed record FormFieldDefinition(string Key, string Label, string DataType, int DisplayLength, string? DisplayFormat, bool IsRequired, int? VerifyIndex, string? Regex, string? DefaultValue, bool IsReadonly, bool IsVisible, bool OnlyChoose, bool ChooseMultiple, string? ChoosePage, IReadOnlyList<FieldChooserSource> Choosers, bool IsPrimaryKey, bool IsAutoIncrement, bool IsVirtual, bool IsCost, bool IsSecrecy, bool ServerFilled, int? MaxLength, int TabNo = 1, int? FormOrder = null, int Span = 1, bool NewLine = false, string? CellGroup = null, int CellRole = 0, IReadOnlyList<FormOptionItem>? Options = null, bool DisplayOnly = false, bool CanCopy = true,
    int? Precision = null, int? Scale = null, int RowSpan = 1, string? SectionId = null);
public sealed record WorkbenchData(IReadOnlyList<Dictionary<string, object?>> Rows, int Total, int Page, int PageSize);
public sealed record WorkbenchQueryCondition(string Field, string Operator, string? Value, string? ValueTo, IReadOnlyList<string>? Values, string Logic = "and");
public sealed record WorkbenchQuery(IReadOnlyList<WorkbenchQueryCondition> Conditions);
public sealed record ExportSelectedRequest(IReadOnlyList<IReadOnlyList<string>> Keys);
public sealed record FieldSetupLookup(string Value, string Label);
public sealed record SystemKnowledgeModule(int Id, string Title);
public sealed record SystemKnowledgeField(string Table, string Field, string Description, string? DataType);
public sealed record SystemKnowledgeResult(IReadOnlyList<SystemKnowledgeModule> Modules, IReadOnlyList<SystemKnowledgeField> Fields);
public sealed record SystemModuleList(int Total, IReadOnlyList<SystemKnowledgeModule> Modules);
