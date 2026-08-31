namespace EOS.API.Models;

public sealed record PrintField(string Key, string Label, string? DisplayFormat = null);

/// <summary>
/// 单据往来单位资料 + 客户级页头默认（P6 数据集：CLIENT/SUPPLIER 主档上的地址/联系人/单价配置）。
/// 语义（2026-08-30 澄清）：单据页头 Title = 开单方主体（页头字典引用），不是往来单位名称；
/// 客户/厂商的公司名（FULL_NAME_CN/EN）与地址/联系人只进正文。
/// HEADER_ID 是该往来单位默认使用哪个开单方页头，ADR-009 §9.4.2 优先级：
/// CLIENT.HEADER_ID → SYSQR.HEADER_ID → REPORT.HEADER_ID。
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
    string? HeaderCompanyEn,
    string? HeaderText,
    string? FooterText,
    string? LogoPath,
    string? TailText,
    IReadOnlyList<PrintField> MasterFields,
    IReadOnlyList<PrintField> DetailFields,
    IReadOnlyDictionary<string,object?> Master,
    IReadOnlyList<IReadOnlyDictionary<string,object?>> Details,
    ClientPrintProfile? ClientProfile = null);
