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
    int? SerialNo = null);

/// <summary>Workbench/form action button (parsed from MODULES.FORM_BUTTONS).</summary>
public sealed record WorkbenchButton(string Action);
public sealed record WorkbenchFieldMetadata(string Key, string Label, string DataType, int Width, string? Align, string HeaderAlign, string? Format, bool IsVisible, bool IsDefault, bool IsQueryable, bool IsReadonly, bool IsRequired, bool IsCost, bool IsSecrecy, string? DefaultValue, int? VerifyIndex, string? Regex, string? Remark, string? BrowseUrl, int? BrowseModuleId, bool OnlyChoose, bool ChooseMultiple, string? ChoosePage, IReadOnlyList<FieldChooserSource> Choosers, bool IsVirtual, string? VirtualExpression, bool CanCopy, bool IsAutoIncrement, string? ConvertFunction, string? DataSourceSql, string? LastUpdatedBy, DateTime? LastUpdatedAt, int TabNo = 1, int? FormOrder = null, int Span = 1, bool NewLine = false, string? CellGroup = null, int CellRole = 0, string? FormOptions = null);
public sealed record UpdateWorkbenchFieldMetadata(string Label, string DataType, int Width, string Align, string HeaderAlign, string? Format, bool IsVisible, bool IsDefault, bool IsQueryable, bool IsReadonly, bool IsRequired, bool IsCost, bool IsSecrecy, string? DefaultValue, int? VerifyIndex, string? Regex, string? Remark, string? BrowseUrl, int? BrowseModuleId, bool OnlyChoose, bool ChooseMultiple, string? ChoosePage, IReadOnlyList<FieldChooserSource> Choosers, bool CanCopy, WorkbenchFieldMetadata? Original, int TabNo = 1, int? FormOrder = null, int Span = 1, bool NewLine = false, string? CellGroup = null, int CellRole = 0, string? FormOptions = null);

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
    bool HasOwnerColumn = true,
    bool HasOwnerGroupColumn = true,
    ModuleBusinessRule? BusinessRule = null,
    bool AutoApprove = false,
    [property: JsonIgnore] IReadOnlyList<string> GroupExpressions = default!,
    string? FormTabs = null,
    int? FormColumns = null,
    [property: JsonPropertyName("buttons")]
    IReadOnlyList<WorkbenchButton>? FormButtons = null,
    bool IfCopy = false,
    bool SearchMaster = false,
    bool SearchDetail = false,
    string? NewUrl = null,
    string? ModiUrl = null,
    string? HelpUrl = null,
    bool CanDelete = false,
    string? DefinitionVersion = null,
    [property: JsonPropertyName("businessActions")] JsonElement? BusinessActions = null,
    [property: JsonPropertyName("validationRules")] JsonElement? ValidationRules = null,
    [property: JsonPropertyName("effectEngine")] JsonElement? EffectEngine = null)
{
    /// <summary>Effect-engine gate read from the published snapshot (effectEngine.enabled).</summary>
    public bool EffectEngineEnabled =>
        EffectEngine is { ValueKind: JsonValueKind.Object } section
        && section.TryGetProperty("enabled", out var enabled)
        && enabled.ValueKind == JsonValueKind.True;
}

/// <summary>Form tab definition (parsed from MODULES.FORM_TABS).</summary>
public sealed record FormTabDefinition(int No, string Title);

/// <summary>Form dropdown option (parsed from FIELDS.FORM_OPTIONS).</summary>
public sealed record FormOptionItem(string Value, string Label);
public sealed record FormDefinition(int ModuleId, string Title, string MasterTable, string? DetailTable, bool HasAdd, bool HasEdit, string Mode, IReadOnlyList<FormFieldDefinition> MasterFields, IReadOnlyList<FormFieldDefinition> DetailFields, IReadOnlyList<string> MasterPkOrder, string DetailNoFields, string DetailDfVerify, IReadOnlyList<FormTabDefinition> Tabs = default!, int Columns = 2, IReadOnlyList<WorkbenchButton>? Buttons = null, IReadOnlyDictionary<string, string> DefaultValues = default!, bool HasWorkflow = false, bool IfCopy = false, bool SearchMaster = false, bool SearchDetail = false, bool CanDelete = false, bool CanApprove = false, bool CanDeapprove = false, bool CanEndCase = false, bool CanUnEndCase = false, bool CanFileView = false, bool CanFileUpda = false, bool CanFileEdit = false, bool CanFileDele = false, bool CanAddNew = false, bool CanEdit = false, string? HelpUrl = null, bool CanSetup = false, bool HasStatelessApprove = false);
public sealed record FormFieldDefinition(string Key, string Label, string DataType, int DisplayLength, string? DisplayFormat, bool IsRequired, int? VerifyIndex, string? Regex, string? DefaultValue, bool IsReadonly, bool IsVisible, bool OnlyChoose, bool ChooseMultiple, string? ChoosePage, IReadOnlyList<FieldChooserSource> Choosers, bool IsPrimaryKey, bool IsAutoIncrement, bool IsVirtual, bool IsCost, bool IsSecrecy, bool ServerFilled, int? MaxLength, int TabNo = 1, int? FormOrder = null, int Span = 1, bool NewLine = false, string? CellGroup = null, int CellRole = 0, IReadOnlyList<FormOptionItem>? Options = null, bool DisplayOnly = false, bool CanCopy = true,
    int? Precision = null, int? Scale = null);
public sealed record WorkbenchData(IReadOnlyList<Dictionary<string, object?>> Rows, int Total, int Page, int PageSize);
public sealed record WorkbenchQueryCondition(string Field, string Operator, string? Value, string? ValueTo, IReadOnlyList<string>? Values, string Logic = "and");
public sealed record WorkbenchQuery(IReadOnlyList<WorkbenchQueryCondition> Conditions);
public sealed record ExportSelectedRequest(IReadOnlyList<IReadOnlyList<string>> Keys);
public sealed record FieldSetupLookup(string Value, string Label);
public sealed record SystemKnowledgeModule(int Id, string Title);
public sealed record SystemKnowledgeField(string Table, string Field, string Description, string? DataType);
public sealed record SystemKnowledgeResult(IReadOnlyList<SystemKnowledgeModule> Modules, IReadOnlyList<SystemKnowledgeField> Fields);
public sealed record SystemModuleList(int Total, IReadOnlyList<SystemKnowledgeModule> Modules);
