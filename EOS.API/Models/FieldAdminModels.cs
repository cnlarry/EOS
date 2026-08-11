namespace EOS.API.Models;

public sealed record FieldAdminTable(string TableId, string Description, string? Kind, string? Type);

public sealed record FieldAdminModule(int Id, string Label);

public sealed record FieldAdminFieldSummary(
    string TableId,
    string FieldId,
    string Description,
    string DataType,
    bool IsVirtual,
    bool IsVisible,
    bool IsDefault,
    bool IsQueryable,
    bool IsReadonly,
    bool IsCost,
    bool IsSecrecy);

public sealed record FieldAdminChooser(
    bool Active,
    string? Table,
    string? Description,
    int? ModuleId,
    string? Filter,
    string? ReturnMapping);

public sealed record FieldAdminInput(
    string Label,
    string DataType,
    int Width,
    string Align,
    string HeaderAlign,
    string? Format,
    bool IsVisible,
    bool IsDefault,
    bool IsQueryable,
    bool IsReadonly,
    bool IsRequired,
    bool IsCost,
    bool IsSecrecy,
    string? DefaultValue,
    int? VerifyIndex,
    string? Regex,
    string? Remark,
    string? BrowseUrl,
    int? BrowseModuleId,
    bool OnlyChoose,
    bool ChooseMultiple,
    string? ChoosePage,
    IReadOnlyList<FieldAdminChooser> Choosers,
    bool CanCopy,
    int TabNo = 1,
    int? FormOrder = null,
    int Span = 1,
    bool NewLine = false,
    string? CellGroup = null,
    int CellRole = 0,
    string? Options = null);

public sealed record FieldAdminMetadata(
    string TableId,
    string FieldId,
    FieldAdminInput Field,
    bool IsVirtual,
    string? VirtualExpression,
    bool IsAutoIncrement,
    string? ConvertFunction,
    string? DataSourceSql,
    string? LastUpdatedBy,
    DateTime? LastUpdatedAt);

public sealed record CreateFieldAdminRequest(string TableId, string FieldId, FieldAdminInput Field);

public sealed record UpdateFieldAdminRequest(string TableId, string FieldId, FieldAdminInput Field, FieldAdminInput? Original);

public sealed record FieldAdminPageResult(IReadOnlyList<FieldAdminFieldSummary> Items, int Total, int Page, int PageSize);
