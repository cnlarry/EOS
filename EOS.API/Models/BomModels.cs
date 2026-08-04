namespace EOS.API.Models;

public sealed record BomMasterRow(
    string ProNo,
    string ProName,
    string ProSpec,
    string Attention,
    bool Confirmed,
    string ConfirmPerson,
    DateTime? ConfirmDate,
    string LastUpdatedBy,
    DateTime? LastUpdatedAt,
    double ParameterDifference,
    string Remark);

public sealed record BomDetailRow(
    short SerialNo,
    string ElementProNo,
    string ElementProName,
    string ElementProSpec,
    double? ElementQty,
    double? LostRate,
    double? DepotQty,
    double? MrpQty,
    string UnitId,
    string StuffId,
    string Remark);

public sealed record BomSearchResult(
    IReadOnlyList<BomMasterRow> Items,
    int Count,
    int Limit,
    string SearchField,
    string Keyword);

public sealed record DevelopmentIdentity(string UserId, bool LoginBypassed, int ModuleId);
