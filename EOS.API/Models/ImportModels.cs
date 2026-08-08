namespace EOS.API.Models;

public sealed record ImportTable(string TableId, string TableDesc, IReadOnlyList<string> PrimaryKeys);

public sealed record ImportField(string Key, string Label, string DataType, bool IsRequired, bool IsPrimaryKey);

public sealed record ImportDefinition(
    string Table,
    IReadOnlyList<ImportField> Fields,
    IReadOnlyList<string> PrimaryKeys);

public sealed record ImportColumn(string Name, string? MappedField);

public sealed record ImportPreview(
    IReadOnlyList<string> Columns,
    IReadOnlyList<IReadOnlyList<string>> SampleRows,
    int TotalRows);

public sealed record ImportExecuteRequest(
    string Table,
    IReadOnlyList<ImportColumn> Mapping,
    IReadOnlyList<IReadOnlyList<string>> Rows);

public sealed record ImportRowError(int RowNumber, string Message);

public sealed record ImportExecuteResult(int Inserted, int Failed, IReadOnlyList<ImportRowError> Errors);
