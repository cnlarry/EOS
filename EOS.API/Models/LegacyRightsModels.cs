namespace EOS.API.Models;

public sealed record LegacyModuleRights(
    bool CanBrowse,
    bool CanViewCost,
    bool CanViewSecrecy,
    bool CanSetup,
    IReadOnlySet<string> DeniedMasterFields,
    IReadOnlySet<string> DeniedDetailFields);
