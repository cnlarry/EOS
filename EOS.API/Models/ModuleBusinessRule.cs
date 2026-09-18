namespace EOS.API.Models;

/// <summary>
/// Business-rule registration for a module: 自动单号配置与（静态登记的）预付冲抵表。
/// 保存期/批核期的业务行为由校验目录与效果目录承载——遗留的存储过程钩子
/// （`MODULES.UPDATE_SP`/`AFTERSAVE_SP`）已从库内物理删除，运行期不再有"按名调用过程"的入口。
/// </summary>
public sealed record ModuleBusinessRule(
    int ModuleId,
    bool AutoBillNo,
    string? BillNoField,
    string? BillTypeField,
    string? PrepayOffsetTable = null);
