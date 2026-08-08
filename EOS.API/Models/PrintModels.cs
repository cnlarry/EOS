namespace EOS.API.Models;

public sealed record PrintField(string Key, string Label);

public sealed record PrintData(
    int ModuleId,
    string Title,
    string? HeaderCompany,
    string? HeaderText,
    string? FooterText,
    IReadOnlyList<PrintField> MasterFields,
    IReadOnlyList<PrintField> DetailFields,
    IReadOnlyDictionary<string,object?> Master,
    IReadOnlyList<IReadOnlyDictionary<string,object?>> Details);
