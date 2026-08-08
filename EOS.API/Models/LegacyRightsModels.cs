namespace EOS.API.Models;

public sealed record LegacyModuleRights(
    bool CanBrowse,
    bool CanViewCost,
    bool CanViewSecrecy,
    bool CanSetup,
    IReadOnlySet<string> DeniedMasterFields,
    IReadOnlySet<string> DeniedDetailFields,
    bool CanAddNew,
    bool CanEdit,
    bool CanDelete,
    IReadOnlySet<string> DenyNewMasterFields,
    IReadOnlySet<string> DenyNewDetailFields,
    IReadOnlySet<string> DenyModiMasterFields,
    IReadOnlySet<string> DenyModiDetailFields,
    string DataFilter,
    string ExecuteTag);
