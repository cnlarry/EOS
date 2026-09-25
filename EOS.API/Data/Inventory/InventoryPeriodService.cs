using System.Data;
using EOS.API.Errors;
using Microsoft.Data.SqlClient;

namespace EOS.API.Data.Inventory;

/// <summary>落在某个已关账期间里的身份：报错要点名到"哪一期"。</summary>
public sealed record ClosedPeriod(string MonthType, string MonthNo, DateTime MonthDate);

/// <summary>
/// 会计期间的关账判据（唯一取值点）。
/// </summary>
/// <remarks>
/// **口径**：某一期**已批核**的月结单即代表该期已关账，其 `MONTH_DATE`（该期期末）是这条边界的时点。
/// 一笔业务日期 `d` 落在已关账期，当且仅当存在已批核的月结单满足 `d &lt;= MONTH_DATE`——
/// 取**覆盖它的最早一期**来点名（多期都覆盖时，"它落在哪一期里"指的是最早那一期）。
///
/// **未批核的月结单不是关账**：草稿只是草稿，不参与任何口径——与库存日报期初同一条规矩
/// （`ReportAggregateRegistry` 的 `MONTHROW` 也在选月处就带批核过滤）。
///
/// **半成品账不在本服务的范围**：`HALF_PRO_DEPOT` 与 2603/2604 是否纳入，由月结范围参数决定，
/// 那段工作不在这里；本服务只回答主账的关账边界。
///
/// 这里只读 `INV_PRO_MONTH_M`（月结单头），不读余额 / 流水 / 批次账，因此不构成新的库存读取宿主。
/// </remarks>
public static class InventoryPeriodService
{
    private const string MonthTable = "INV_PRO_MONTH_M";

    /// <summary>该业务日期落在哪一期已关账期间内；没有则返回 null。</summary>
    public static async Task<ClosedPeriod?> FindAsync(
        SqlConnection connection,
        SqlTransaction? transaction,
        DateTime businessDate,
        CancellationToken token)
    {
        await using var command = new SqlCommand(
            $"SELECT TOP 1 MONTH_TYPE, MONTH_NO, MONTH_DATE FROM dbo.{MonthTable} "
            + "WHERE CONFIRM_TAG = 1 AND MONTH_DATE >= @date "
            + "ORDER BY MONTH_DATE ASC, MONTH_NO ASC;",
            connection, transaction);
        command.Parameters.Add("@date", SqlDbType.DateTime).Value = businessDate;

        await using var reader = await command.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token)) return null;
        return new ClosedPeriod(
            reader.IsDBNull(0) ? string.Empty : reader.GetString(0).Trim(),
            reader.IsDBNull(1) ? string.Empty : reader.GetString(1).Trim(),
            reader.GetDateTime(2));
    }

    /// <summary>
    /// **没有流水的账**（半成品账 `HALF_PRO_DEPOT`）的关账守卫：只判"这个业务日期落在不在已关账期"。
    /// </summary>
    /// <remarks>
    /// 与 <see cref="EnsureLedgerWritableAsync"/> 的差别只有一处，但那一处是本质的：半成品账**不写流水**
    /// （`HalfStockMoveHandler` 只动余额表那一行），所以没有"回看这张单已有流水"可言——那条反向记账的
    /// 规矩在这里无从适用，也不该假装适用。判据仍走同一个 <see cref="FindAsync"/>：关账边界只有一处取值点。
    ///
    /// **本方法自己不判"该不该拦"**：半成品是否纳入月结由部署级参数
    /// `MONTH_CLOSE_SCOPE_HALF_STOCK` 决定（ADR-020 §9.3），调用方先问参数、再调这里——
    /// "开才拦、关就不拦"与快照范围同源。
    /// </remarks>
    public static async Task EnsureDateOpenAsync(
        SqlConnection connection,
        SqlTransaction? transaction,
        DateTime businessDate,
        string what,
        CancellationToken token)
    {
        var target = await FindAsync(connection, transaction, businessDate, token);
        if (target is null) return;
        throw new PeriodClosedException(target.MonthType, target.MonthNo, target.MonthDate,
            $"{what}不能改动已关账期间的账（月结单 {target.MonthType}/{target.MonthNo}，期末 {target.MonthDate:yyyy-MM-dd}）："
            + $"业务日期 {businessDate:yyyy-MM-dd} 落在该期之内。请先反结账，或把单据日期改到开账期。");
    }

    /// <summary>
    /// 记账前的关账守卫：把"将要写入的那条流水"的两件事一次问清。
    /// </summary>
    /// <param name="ledgerDate">
    /// 本次将要写入的流水的日期——批核方向是源单的单据日期，非批核方向是当前时间
    /// （与移动引擎算 `MUTUALITY_DATE` 的表达式同源：`LedgerDateExpression()`）。
    /// </param>
    /// <param name="billType">单据单别（用于回看这张单**已有**的流水落点）。</param>
    /// <param name="billNo">单据单号。</param>
    /// <exception cref="PeriodClosedException">
    /// ① 本次流水要落进已关账期；或 ② 这张单已有的流水落在已关账期（其解批 / 反向也一并拒绝——
    /// 否则可以先解批、把单据日期改到开账期、再批核，等于把已关账期的账洗一遍）。
    /// </exception>
    public static async Task EnsureLedgerWritableAsync(
        SqlConnection connection,
        SqlTransaction? transaction,
        DateTime ledgerDate,
        string billType,
        string billNo,
        CancellationToken token)
    {
        var target = await FindAsync(connection, transaction, ledgerDate, token);
        if (target is not null)
        {
            throw new PeriodClosedException(target.MonthType, target.MonthNo, target.MonthDate,
                $"该笔库存变动将记账到已关账期间（月结单 {target.MonthType}/{target.MonthNo}，期末 {target.MonthDate:yyyy-MM-dd}）："
                + $"流水的记账日期 {ledgerDate:yyyy-MM-dd} 落在该期之内。请先反结账，或把单据日期改到开账期。");
        }

        if (billType.Length == 0 || billNo.Length == 0)
        {
            return;
        }

        // ② 反向记账的特殊规矩：解批写的是"现在发生的反向流水"，日期本身不在已关账期，
        // 但**这张单当初记账的那一批流水**可能已经在里面了——那时的账已经进过报表，
        // 反向冲销一样会改动已关账期间的期末。
        var existing = await InventoryQueryService.GetLatestLedgerDateAsync(
            connection, transaction, billType, billNo, token);
        if (existing is not { } latest) return;

        var origin = await FindAsync(connection, transaction, latest, token);
        if (origin is not null)
        {
            throw new PeriodClosedException(origin.MonthType, origin.MonthNo, origin.MonthDate,
                $"该单据已有的库存流水记账到已关账期间（月结单 {origin.MonthType}/{origin.MonthNo}，"
                + $"期末 {origin.MonthDate:yyyy-MM-dd}，流水日期 {latest:yyyy-MM-dd}）：其解批 / 反向冲销会改动已关账期间的结存。"
                + "请先反结账。");
        }
    }
}
