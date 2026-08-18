namespace EOS.API.Models;

public sealed record SearchableModule(
    int ModuleId,
    string Title,
    string MasterTable,
    string? DetailTable,
    bool SearchMaster,
    bool SearchDetail);

public sealed record SearchField(string Key, string Label, string DataType, string? DisplayFormat = null);

public sealed record SearchDefinition(
    int ModuleId,
    string Title,
    string Table,
    IReadOnlyList<SearchField> Fields,
    IReadOnlyList<SearchField> Columns,
    IReadOnlyList<string> PkOrder);

public sealed record SearchQueryRequest(string Table, string? Field, string? Value, string? Keyword);

public sealed record SearchQueryResult(
    IReadOnlyList<Dictionary<string, object?>> Rows,
    int Total,
    int Page,
    int PageSize);
