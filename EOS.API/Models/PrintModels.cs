namespace EOS.API.Models;

public sealed record PrintField(string Key, string Label, string? DisplayFormat = null);

/// <summary>
/// 客户的抬头配置（P6 数据集：CLIENT 主档上已有的抬头/地址/单价配置）。
/// ADR-009 §9.4.2 抬头取值优先级：CLIENT.HEADER_ID → SYSQR.HEADER_ID → REPORT.HEADER_ID。
/// CLIENT.PRINT_PRICE 启用前需审计取值分布（338 家 0 / 1 家 1，不得假设存量可信）。
/// </summary>
public sealed record ClientPrintProfile(
    string? ClientName,
    string? FullNameCn,
    string? FullNameEn,
    string? DeliAddrCn,
    string? DeliAddrEn,
    string? Tel,
    string? Fax,
    string? Linkman,
    string? HeaderId,
    bool PrintPrice);

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
    IReadOnlyList<IReadOnlyDictionary<string,object?>> Details,
    ClientPrintProfile? ClientProfile = null);
