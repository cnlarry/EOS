namespace EOS.API.Models;

public sealed record PrintField(string Key, string Label, string? DisplayFormat = null);

public sealed record PrintData(
    int ModuleId,
    string Title,
    string? HeaderCompany,
    string? HeaderText,
    string? FooterText,
    string? LogoPath,
    string? TailText,
    IReadOnlyList<PrintField> MasterFields,
    IReadOnlyList<PrintField> DetailFields,
    IReadOnlyDictionary<string,object?> Master,
    IReadOnlyList<IReadOnlyDictionary<string,object?>> Details);
