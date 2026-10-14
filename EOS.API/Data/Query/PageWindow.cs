namespace EOS.API.Data.Query;

/// <summary>
/// 分页窗口：把页号与页大小折算成 SQL Server <c>OFFSET</c> 的值。
///
/// 为什么必须合一：列表入口有八处各自写 <c>(page - 1) * pageSize</c>（工作台列表、选择器、
/// 报表、高级查找、用户/字段管理、审计日志）。页号普遍只夹下界（<c>Math.Max(1, page)</c>）而不夹上界，
/// 页大小被夹到 [10,100] 之类的小区间，于是 <c>(page-1)*pageSize</c> 这个 <c>int</c> 乘法在页号取到
/// <c>int</c> 上界时溢出成负数，SQL Server 报 "@Offset 附近有语法错误"，表现在接口上是 500。
/// 溢出只在页号极大时才发生，常规的 0 / -1 / 空值都被引擎本身的归一化挡住，因此长期不被察觉。
///
/// 这里只做溢出防护，不替调用方决定页大小口径——页大小仍由各入口按自己的区间夹取
/// （有的允许到 200，导出走全量不分页），页大小传 0 或负值时按下界 0 处理。
/// </summary>
internal static class PageWindow
{
    /// <summary>
    /// SQL Server <c>OFFSET</c> 接受的最大正值。超出整机不可能一次跳过这么多行，
    /// 语义等价于"翻过了最后一页"，返回空结果集而不是报错。
    /// </summary>
    private const int MaxOffset = int.MaxValue;

    /// <summary>
    /// 计算行偏移量。页号先按 1 归一化（调用方若已归一化，结果一致），再用 <see cref="long"/> 做乘法以避开
    /// <see cref="int"/> 溢出，最后夹到 <see cref="MaxOffset"/>。
    /// </summary>
    internal static int ResolveOffset(int page, int pageSize)
    {
        var normalizedPage = Math.Max(1, page);
        var size = Math.Max(0, pageSize);
        var offset = (long)(normalizedPage - 1) * size;
        return offset >= MaxOffset ? MaxOffset : (int)offset;
    }
}
