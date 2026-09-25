namespace EOS.API.Errors;

/// <summary>
/// 单据的业务日期落在**已关账的会计期间**内，记账被拒绝。
/// </summary>
/// <remarks>
/// 关账之后禁止再往该期间写入库存变动：期间报表的期初来自已批核的快照，一旦事后补记，
/// 报表的"期初 + 本期收发 = 期末"当场不成立，而且事后无从分辨哪一笔是补的。
/// 因此拒绝要**点名期间**（月结单别 / 单号 / 期末日期），否则用户只看到"不行"而不知道该反结哪一期。
/// </remarks>
public sealed class PeriodClosedException(string monthType, string monthNo, DateTime monthDate, string message)
    : Exception(message)
{
    public string MonthType { get; } = monthType;

    public string MonthNo { get; } = monthNo;

    public DateTime MonthDate { get; } = monthDate;
}
