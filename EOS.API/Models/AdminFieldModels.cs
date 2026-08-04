namespace EOS.API.Models;

public sealed record AdminFieldDefinition(
    string TableId,
    string FieldId,
    string Description,
    bool Visible,
    bool Virtual,
    string? VirtualExpression,
    bool Cost,
    bool Secrecy,
    string? DataType,
    int? DisplayLength,
    string? DisplayFormat,
    string? HeaderAlign,
    string? ItemAlign);

public sealed record AdminFieldUpdateRequest(
    string TableId,
    string FieldId,
    string Description,
    bool Visible,
    bool Virtual,
    string? VirtualExpression,
    bool Cost,
    bool Secrecy,
    string? DataType,
    int? DisplayLength,
    string? DisplayFormat,
    string? HeaderAlign,
    string? ItemAlign);

public sealed record DefaultColumnRequest(
    string ModuleTable,
    string RelatedTable,
    IReadOnlyList<string> FieldIds);
