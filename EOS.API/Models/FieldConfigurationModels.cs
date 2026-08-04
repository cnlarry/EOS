namespace EOS.API.Models;

public sealed record FieldDefinition(
    string Id,
    string SourceTable,
    string SourceField,
    string Caption,
    string DataType,
    bool IsVirtual,
    string? VirtualExpression,
    bool Visible,
    int Position,
    int? DisplayLength,
    string? DisplayFormat,
    string? HeaderAlign,
    string? ItemAlign,
    bool IsCost,
    bool IsSecrecy);

public sealed record FieldConfigurationResult(
    int ModuleId,
    string Scope,
    string UserId,
    bool UsesUserConfiguration,
    IReadOnlyList<FieldDefinition> AvailableFields,
    IReadOnlyList<FieldDefinition> SelectedFields);

public sealed record SaveFieldConfigurationRequest(
    string Scope,
    IReadOnlyList<string> FieldIds);
