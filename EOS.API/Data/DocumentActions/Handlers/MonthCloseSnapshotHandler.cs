using System.Data;
using EOS.API.Features.Inventory;
using Microsoft.Data.SqlClient;

namespace EOS.API.Data.DocumentActions.Handlers;

/// <summary>
/// 月结单（料件每月统计）上的「生成快照」按钮：按本单的期末日期反算该时点结存，写成快照明细。
/// </summary>
/// <remarks>
/// 语义与边界都压在 <see cref="MonthCloseSnapshotService"/> 里，本处理器只做三件事：
/// 读本单的期末日期、拒绝已批核的单、把结果说成人话。
///
/// - **已批核拒绝**：批核过的快照是报表期初的依据，不能就地改——要重做必须先反结账（另一段工作）。
/// - **重写而非追加**：未批核时重复点击是重算（先删本级明细再写），不会出现两套行。
/// - **探路不写**：`CONFIRM_TAG=1` 的操作在 `confirm=false` 时只报"将会写多少行"。
/// </remarks>
internal sealed class MonthCloseSnapshotHandler : IDocumentUserAction
{
    public const string ActionKey = "month-close-snapshot";

    private const string MonthTypeField = "MONTH_TYPE";
    private const string MonthNoField = "MONTH_NO";
    private const string MonthDateField = "MONTH_DATE";

    private readonly MonthCloseSnapshotService _snapshots;

    public MonthCloseSnapshotHandler(MonthCloseSnapshotService snapshots) => _snapshots = snapshots;

    public string Key => ActionKey;

    public string Label => "生成快照";

    public async Task<DocumentActionResult> ExecuteAsync(DocumentActionContext context, CancellationToken token)
    {
        var header = await ReadHeaderAsync(context, token);
        if (header is null)
        {
            throw new InvalidOperationException($"找不到月结单 {context.RecordKey}，无法生成快照。");
        }

        // 「已批核拒绝」的判据不在这里：它压在服务里（唯一一份），处理器只管把日期取出来。
        var result = await _snapshots.GenerateAsync(
            context.Connection,
            context.Transaction,
            new MonthCloseSnapshotRequest(header.MonthType, header.MonthNo, header.MonthDate, context.Preview),
            token);

        var dimensions = new List<string> { "料号", "库别" };
        if (result.ByLocation) dimensions.Add("库位");
        if (result.ByBatch) dimensions.Add("批次");
        var scope = string.Join('+', dimensions);

        if (context.Preview)
        {
            return new DocumentActionResult(DocumentActionOutcome.Message,
                $"将按 {scope} 反算 {header.MonthDate:yyyy-MM-dd} 的期末结存，写出快照 {result.RowCount} 行，期末数量合计 {Trim(result.EndingQuantity)}。本次未改动任何数据。");
        }

        return new DocumentActionResult(DocumentActionOutcome.Refreshed,
            $"已按 {scope} 生成快照 {result.RowCount} 行，期末数量合计 {Trim(result.EndingQuantity)}。");
    }

    /// <summary>读本单的表头：键列与键值都由框架给出（列名来自定义、键值来自主键），没有调用方拼进来的文本。</summary>
    private static async Task<Header?> ReadHeaderAsync(DocumentActionContext context, CancellationToken token)
    {
        var keyColumns = context.MasterPkOrder;
        if (keyColumns.Count == 0 || keyColumns.Count != context.KeyValues.Count)
        {
            throw new InvalidOperationException("月结单的主键列未被定义，无法定位单据。");
        }

        var where = string.Join(" AND ", keyColumns.Select((column, index) => $"[{column}]=@k{index}"));
        await using var command = new SqlCommand(
            $"SELECT {MonthTypeField}, {MonthNoField}, {MonthDateField} FROM dbo.{context.Definition.MasterTable} WHERE {where};",
            context.Connection,
            context.Transaction);
        for (var index = 0; index < keyColumns.Count; index++)
        {
            command.Parameters.Add($"@k{index}", SqlDbType.NVarChar, 60).Value = context.KeyValues[index];
        }

        await using var reader = await command.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token)) return null;
        return new Header(
            reader.IsDBNull(0) ? string.Empty : reader.GetString(0).Trim(),
            reader.IsDBNull(1) ? string.Empty : reader.GetString(1).Trim(),
            reader.IsDBNull(2) ? default : reader.GetDateTime(2));
    }

    /// <summary>数量去掉无意义的小数尾巴（期末合计是给人看的，不是给机器比的）。</summary>
    private static string Trim(double value) => value.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture);

    private sealed record Header(string MonthType, string MonthNo, DateTime MonthDate);
}
