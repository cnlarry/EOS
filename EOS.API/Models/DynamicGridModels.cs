namespace EOS.API.Models;

public sealed record DynamicGridResult(
    IReadOnlyList<FieldDefinition> Columns,
    IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows,
    int Count);
