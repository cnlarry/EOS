namespace EOS.API.Errors;

/// <summary>
/// 报表过程数据源的结果集列无法按字段级权限过滤时拒绝返回数据（fail-closed）：
/// 列清单是成本/保密/禁止字段三类判据的唯一载体，没有清单就无法判断某一列该不该下发。
/// </summary>
public sealed class ReportColumnWhitelistException(string message) : Exception(message);
