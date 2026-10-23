namespace EOS.API.Data.Query;

/// <summary>
/// 分页窗口：页号与页大小的**唯一**归一化处，并把它们折算成 SQL Server <c>OFFSET</c> 的值。
///
/// 为什么必须合一：列表入口有八处各自写 <c>(page - 1) * pageSize</c>（工作台列表、选择器、
/// 报表、高级查找、用户/字段管理、审计日志）。页号普遍只夹下界（<c>Math.Max(1, page)</c>）而不夹上界，
/// 页大小被夹到 [10,100] 之类的小区间，于是 <c>(page-1)*pageSize</c> 这个 <c>int</c> 乘法在页号取到
/// <c>int</c> 上界时溢出成负数，SQL Server 报 "@Offset 附近有语法错误"，表现在接口上是 500。
/// 溢出只在页号极大时才发生，常规的 0 / -1 / 空值都被引擎本身的归一化挡住，因此长期不被察觉。
///
/// 页大小口径同样只在这里声明一次（ADR-036 §8 决定）：默认 <see cref="MinPageSize"/>–<see cref="MaxPageSize"/>。
/// 各入口此前各写一套区间（10–100 与 10–200 两档），同一个"每页几条"在不同页面给出不同上限；
/// 唯一的具名例外是工作台**子表**列表（<see cref="MaxDetailPageSize"/>，一次最多 500 行）——
/// 收窄它会直接切掉明细一次能看的行数，属产品行为，不随本口径统一而动。
/// </summary>
internal static class PageWindow
{
    /// <summary>每页最少条数（小于等于 0 的请求一律抬到该值）。</summary>
    internal const int MinPageSize = 10;

    /// <summary>每页最多条数：全站统一口径。</summary>
    internal const int MaxPageSize = 200;

    /// <summary>工作台子表列表的每页上限（具名例外，见类型注释）。</summary>
    internal const int MaxDetailPageSize = 500;

    /// <summary>页号归一化：下界 1，不设上界（上界由 <see cref="ResolveOffset"/> 的溢出防护兜住）。</summary>
    internal static int NormalizePage(int page) => Math.Max(1, page);

    /// <summary>页大小归一化：夹到全站统一区间。</summary>
    internal static int NormalizePageSize(int pageSize) => Math.Clamp(pageSize, MinPageSize, MaxPageSize);

    /// <summary>工作台子表列表的页大小归一化（上限取 <see cref="MaxDetailPageSize"/>）。</summary>
    internal static int NormalizeDetailPageSize(int pageSize) => Math.Clamp(pageSize, MinPageSize, MaxDetailPageSize);

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
