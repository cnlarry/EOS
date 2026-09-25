using System.Data;
using EOS.API.Data;
using EOS.API.Data.Inventory;
using Microsoft.Data.SqlClient;

namespace EOS.API.Features.Inventory;

/// <summary>生成输入的来源：单据身份、期末时点、是否只探路。</summary>
public sealed record MonthCloseSnapshotRequest(string MonthType, string MonthNo, DateTime MonthDate, bool Preview);

/// <summary>生成结果：写了多少行、按哪些维度展开、期末数量合计、其中半成品账多少行
/// （探路时是"将会写多少"）。</summary>
public sealed record MonthCloseSnapshotResult(
    int RowCount, bool ByLocation, bool ByBatch, double EndingQuantity,
    /// <summary>其中半成品账（按制程）的行数：0 表示这一期没把半成品账纳进来。</summary>
    int HalfStockRowCount = 0);

/// <summary>
/// 月结快照生成：把某张月结单的期末结存写成快照明细行。
/// </summary>
/// <remarks>
/// **按期末时点反算，不读当前余额**：
/// <code>
/// 期末(该时点) = 上一期已批核快照(时点严格早于本期末) + 该时点之后到本期末的流水净额
/// </code>
/// 这与"读当前余额"是两个不同的数——补结一个已经过去的期间时，要的是**那一期的期末值**，
/// 不是今天的余额。差别不是小数点级：只要期间之后发生过任何收发，两个数就不相等。
///
/// **粒度由部署级策略参数决定**（`MONTH_CLOSE_BY_LOCATION` / `MONTH_CLOSE_BY_BATCH`，一律取部署级那一行；
/// 求值口径只由 `DepotStockPolicyService` 提供）：关掉一个维度就是把该维度
/// `SUM` 掉并落哨兵值。快照表的唯一键已经是七列，所以"改参数"只影响行数，不影响能否存下。
///
/// **本服务不碰余额表与流水表的写入**，流水只经 <see cref="InventoryQueryService"/> 读；
/// 写出的行也只落 `INV_PRO_MONTH_D`（快照明细），不产生任何库存台账流水。
///
/// **半成品账（按制程分账）纳不纳入，由部署级参数 `MONTH_CLOSE_SCOPE_HALF_STOCK` 决定，
/// 且与关账拦截同源**（ADR-020 §9.3）：同一个参数决定两侧，关时既不拦也不快照。
/// 它**不写流水**，所以算法与主账不同——主账是"上一期快照 + 区间流水净额"，半成品只能
/// **直取余额**（经 <see cref="InventoryQueryService.GetHalfStockBalancesAsync"/>），
/// 于是只有"期末 == 生成当天"时那份余额才是那一期的期末；有结存而要补结过去的期间，
/// 本服务**拒绝**而不是写一个看着像真的的数（口径与理由见 <c>GenerateAsync</c> 内注释）。
/// 制程维度对主账行落哨兵空串——它区分的是"哪本账"，不是"细分到多细"。
/// </remarks>
/// </remarks>
public sealed class MonthCloseSnapshotService
{
    private const string MasterTable = "INV_PRO_MONTH_M";
    private const string DetailTable = "INV_PRO_MONTH_D";

    private readonly DepotStockPolicyService _policy;

    public MonthCloseSnapshotService(DepotStockPolicyService policy) => _policy = policy;

    /// <summary>
    /// 生成（或重写）该期快照。调用方负责"该期是否已批核"的拒绝与事务边界。
    /// </summary>
    public async Task<MonthCloseSnapshotResult> GenerateAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        MonthCloseSnapshotRequest request,
        CancellationToken token)
    {
        if (request.MonthDate == default)
            throw new InvalidOperationException("该月结单没有期末日期，无法反算该时点的结存。");

        // 批核位在**写之前**再读一次（调用方也要读表头拿日期，但规则只能有一份）：
        // 已批核的快照是报表期初的依据，就地重写会把历史报表的口径改掉——要重做必须先反结账。
        var confirmed = await ReadConfirmTagAsync(connection, transaction, request, token);
        if (confirmed is null)
            throw new InvalidOperationException($"找不到月结单 {request.MonthType.Trim()}/{request.MonthNo.Trim()}，无法生成快照。");
        if (confirmed.Value)
            throw new InvalidOperationException("该月结单已批核，快照不能再生成（要先反结账）。");

        // 粒度取部署级策略（depotId 传空即部署级那一行）：月结是全库口径，不按库别分别定粒度。
        var policy = await _policy.ResolveAsync(null, connection, transaction, token);
        var byLocation = policy.MonthCloseByLocation;
        var byBatch = policy.MonthCloseByBatch;

        var previous = await ReadPreviousPeriodAsync(connection, transaction, request, token);
        var baseCells = await ReadPreviousSnapshotAsync(connection, transaction, previous, byLocation, byBatch, token);

        var movements = await InventoryQueryService.GetNetMovementsAsync(
            connection,
            transaction,
            new InventoryQueryService.LedgerNetQuery
            {
                After = previous?.MonthDate,
                UpTo = request.MonthDate,
                GroupByLocation = byLocation,
                GroupByBatch = byBatch,
            },
            InventoryQueryService.ReadLock.None,
            token);

        var cells = new Dictionary<string, SnapshotCell>(StringComparer.Ordinal);
        foreach (var row in baseCells)
        {
            // 上一期里**制程非哨兵**的行属于半成品账：它没有流水可累加，本期结存只能"直取余额"
            // （参数开）或不进快照（参数关），两种情形都不该把它当主账格子接着滚。
            if (row.ProcedureTypeId.Length > 0)
            {
                continue;
            }
            cells[row.Key] = new SnapshotCell(row.ProductNo, row.DepotId, row.LocationNo, row.BatchNo, row.Quantity, row.Amount);
        }
        foreach (var movement in movements)
        {
            // 流水是**主账**的流水 ⇒ 制程落哨兵；半成品账不写流水，不可能出现在这里。
            var key = SnapshotCell.BuildKey(
                movement.ProductNo, movement.DepotId, movement.LocationNo, movement.BatchNo);
            var cell = cells.TryGetValue(key, out var existing)
                ? existing
                : new SnapshotCell(movement.ProductNo, movement.DepotId, movement.LocationNo, movement.BatchNo, 0, 0);
            cell.Quantity += movement.NetQuantity;
            cell.InQuantity += movement.InQuantity;
            cell.Amount += movement.InAmount;
            cells[key] = cell;
        }

        // 半成品账（按制程分账）：**与关账拦截同源**——同一个部署级参数决定两侧（ADR-020 §9.3）。
        if (policy.MonthCloseScopeHalfStock)
        {
            var halfStock = await InventoryQueryService.GetHalfStockBalancesAsync(connection, transaction, token);
            var holding = halfStock.Where(row => row.Quantity != 0).ToList();
            // 半成品账不写流水 ⇒ 这份余额只代表"现在"。期末不是生成当天的期间，**补不出来**：
            // 写下去就是拿今天的余额冒充那一期的期末（比"没有快照"更糟，因为它看着像真的）。
            if (holding.Count > 0 && request.MonthDate.Date != DateTime.Today)
            {
                throw new InvalidOperationException(
                    $"半成品账有 {holding.Count} 个键的结存，但它没有库存流水、无法回溯："
                    + $"本次期末是 {request.MonthDate:yyyy-MM-dd}，而生成发生在 {DateTime.Today:yyyy-MM-dd}。"
                    + "半成品账的快照只能在期末当天生成；要么当天生成，要么把月结范围参数关掉（关掉即半成品账不进月结）。");
            }
            foreach (var row in holding)
            {
                // 位置/批次对半成品账没有意义 ⇒ 落哨兵；金额按成本价折算，使单价正好等于成本价
                // （期初量 = 结存、本期入库 0 ⇒ 移动加权分母就是它自己）。
                var cell = new SnapshotCell(row.ProductNo, row.DepotId, InventoryQueryService.LocationSentinel,
                    InventoryQueryService.EmptyBatch, row.Quantity, row.Quantity * row.CostPrice, row.ProcedureTypeId);
                cells[cell.Key] = cell;
            }
        }

        // 零数量的键不落行：它对任何口径都贡献 0，落下去只会把快照撑大。
        var rows = cells.Values
            .Where(cell => cell.Quantity != 0)
            .OrderBy(cell => cell.ProductNo, StringComparer.Ordinal)
            .ThenBy(cell => cell.DepotId, StringComparer.Ordinal)
            .ThenBy(cell => cell.LocationNo, StringComparer.Ordinal)
            .ThenBy(cell => cell.BatchNo, StringComparer.Ordinal)
            .ThenBy(cell => cell.ProcedureTypeId, StringComparer.Ordinal)
            .ToList();

        var result = new MonthCloseSnapshotResult(
            rows.Count, byLocation, byBatch, rows.Sum(row => row.Quantity),
            rows.Count(row => row.ProcedureTypeId.Length > 0));

        if (request.Preview)
        {
            return result;
        }

        await using (var delete = new SqlCommand(
            $"DELETE FROM dbo.{DetailTable} WHERE MONTH_TYPE=@type AND MONTH_NO=@no;", connection, transaction))
        {
            delete.Parameters.Add("@type", SqlDbType.NVarChar, 20).Value = request.MonthType.Trim();
            delete.Parameters.Add("@no", SqlDbType.NVarChar, 40).Value = request.MonthNo.Trim();
            await delete.ExecuteNonQueryAsync(token);
        }

        const int chunkSize = 100;
        for (var offset = 0; offset < rows.Count; offset += chunkSize)
        {
            var chunk = rows.Skip(offset).Take(chunkSize).ToList();
            var values = new List<string>(chunk.Count);
            await using var insert = new SqlCommand { Connection = connection, Transaction = transaction };
            insert.Parameters.Add("@type", SqlDbType.NVarChar, 20).Value = request.MonthType.Trim();
            insert.Parameters.Add("@no", SqlDbType.NVarChar, 40).Value = request.MonthNo.Trim();
            for (var index = 0; index < chunk.Count; index++)
            {
                var row = chunk[index];
                values.Add($"(@type, @no, @s{index}, @pro{index}, @depot{index}, @loc{index}, @batch{index}, @proc{index}, @qty{index}, @price{index})");
                insert.Parameters.Add($"@s{index}", SqlDbType.Int).Value = offset + index + 1;
                insert.Parameters.Add($"@pro{index}", SqlDbType.NVarChar, 60).Value = row.ProductNo;
                insert.Parameters.Add($"@depot{index}", SqlDbType.NVarChar, 20).Value = row.DepotId;
                // 维度未启用时落哨兵值而不是 NULL：唯一键把 NULL 视为彼此相等，多行 NULL 一样冲突，
                // 表达不了"该维度不细分"。
                insert.Parameters.Add($"@loc{index}", SqlDbType.NVarChar, 60).Value = row.LocationNo;
                insert.Parameters.Add($"@batch{index}", SqlDbType.NVarChar, 60).Value = row.BatchNo;
                // 制程：主账行落哨兵空串，半成品行落真制程（靠它区分"两本账"）。
                insert.Parameters.Add($"@proc{index}", SqlDbType.NVarChar, 20).Value = row.ProcedureTypeId;
                insert.Parameters.Add($"@qty{index}", SqlDbType.Float).Value = row.Quantity;
                insert.Parameters.Add($"@price{index}", SqlDbType.Float).Value = row.UnitPrice;
            }
            insert.CommandText = $"INSERT INTO dbo.{DetailTable} "
                + "(MONTH_TYPE, MONTH_NO, SERIAL_NO, PRO_NO, DEPOT_ID, LOCATION_NO, BATCH_NO, PROCEDURE_TYPE_ID, QTY, PRICE) VALUES "
                + string.Join(", ", values) + ";";
            await insert.ExecuteNonQueryAsync(token);
        }

        return result;
    }

    /// <summary>本期单据的批核位：单据不存在返回 null（与"批核位为 0"必须区分开）。</summary>
    private static async Task<bool?> ReadConfirmTagAsync(
        SqlConnection connection, SqlTransaction transaction, MonthCloseSnapshotRequest request, CancellationToken token)
    {
        await using var command = new SqlCommand(
            $"SELECT CONFIRM_TAG FROM dbo.{MasterTable} WITH (UPDLOCK, HOLDLOCK) "
            + "WHERE MONTH_TYPE=@type AND MONTH_NO=@no;", connection, transaction);
        command.Parameters.Add("@type", SqlDbType.NVarChar, 20).Value = request.MonthType.Trim();
        command.Parameters.Add("@no", SqlDbType.NVarChar, 40).Value = request.MonthNo.Trim();
        var value = await command.ExecuteScalarAsync(token);
        if (value is null or DBNull) return null;
        return Convert.ToInt32(value) == 1;
    }

    /// <summary>上一期：时点**严格早于**本期末的、最近一期**已批核**的月结单。未批核的是草稿，不参与。</summary>
    private static async Task<PreviousPeriod?> ReadPreviousPeriodAsync(
        SqlConnection connection, SqlTransaction transaction, MonthCloseSnapshotRequest request, CancellationToken token)
    {
        await using var command = new SqlCommand(
            $"SELECT TOP 1 MONTH_TYPE, MONTH_NO, MONTH_DATE FROM dbo.{MasterTable} "
            + "WHERE MONTH_DATE < @monthDate AND CONFIRM_TAG = 1 "
            + "ORDER BY MONTH_DATE DESC, MONTH_NO DESC;", connection, transaction);
        command.Parameters.Add("@monthDate", SqlDbType.DateTime).Value = request.MonthDate;
        await using var reader = await command.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token)) return null;
        return new PreviousPeriod(
            reader.IsDBNull(0) ? string.Empty : reader.GetString(0).Trim(),
            reader.IsDBNull(1) ? string.Empty : reader.GetString(1).Trim(),
            reader.GetDateTime(2));
    }

    /// <summary>上一期快照按当期粒度聚合：维度关掉时用 `SUM` 合并、键落哨兵。</summary>
    private static async Task<IReadOnlyList<SnapshotCell>> ReadPreviousSnapshotAsync(
        SqlConnection connection, SqlTransaction transaction, PreviousPeriod? previous, bool byLocation, bool byBatch,
        CancellationToken token)
    {
        if (previous is null) return [];

        var groupBy = new List<string> { "d.PRO_NO", "d.DEPOT_ID" };
        if (byLocation) groupBy.Add($"ISNULL(LTRIM(RTRIM(d.{InventoryQueryService.LocationColumn})), N'{InventoryQueryService.LocationSentinel}')");
        if (byBatch) groupBy.Add($"ISNULL(LTRIM(RTRIM(d.{InventoryQueryService.BatchColumn})), N'')");
        // 制程**永远**参与分组（不受粒度参数影响）：它区分"一本账"而不是"一层细分"——
        // 主账行落哨兵空串、半成品行落真制程。混在一格里，半成品的量就会被当主账的量接着滚。
        groupBy.Add($"ISNULL(LTRIM(RTRIM(d.{InventoryQueryService.ProcedureColumn})), N'')");
        var grouping = string.Join(", ", groupBy);
        // 列序 = 维度（含制程）在前、量与金额在末两位：制程是**维度**，跟着维度走，
        // 读的人按"先吃维度、再吃末尾两列"的顺序取，不必为每种维度组合各算一次下标。
        var projection = grouping + ", SUM(ISNULL(d.QTY,0)) AS QTY, SUM(ISNULL(d.QTY,0) * ISNULL(d.PRICE,0)) AS AMOUNT";

        await using var command = new SqlCommand(
            $"SELECT {projection} FROM dbo.{DetailTable} d "
            + $"WHERE d.MONTH_TYPE=@type AND d.MONTH_NO=@no GROUP BY {grouping};",
            connection, transaction);
        command.Parameters.Add("@type", SqlDbType.NVarChar, 20).Value = previous.MonthType;
        command.Parameters.Add("@no", SqlDbType.NVarChar, 40).Value = previous.MonthNo;

        await using var reader = await command.ExecuteReaderAsync(token);
        var rows = new List<SnapshotCell>();
        while (await reader.ReadAsync(token))
        {
            var index = 0;
            var product = reader.IsDBNull(index) ? string.Empty : reader.GetString(index).Trim();
            index++;
            var depot = reader.IsDBNull(index) ? string.Empty : reader.GetString(index).Trim();
            index++;
            var location = InventoryQueryService.LocationSentinel;
            if (byLocation)
            {
                location = reader.IsDBNull(index) || reader.GetString(index).Trim().Length == 0
                    ? InventoryQueryService.LocationSentinel
                    : reader.GetString(index).Trim();
                index++;
            }
            var batch = InventoryQueryService.EmptyBatch;
            if (byBatch)
            {
                batch = reader.IsDBNull(index) ? InventoryQueryService.EmptyBatch : reader.GetString(index).Trim();
                index++;
            }
            // 制程紧跟在最后一位维度之后（它本身就是维度），量与金额固定是末两位。
            var procedure = reader.IsDBNull(index) ? string.Empty : reader.GetString(index).Trim();
            var quantity = reader.IsDBNull(index + 1) ? 0 : Convert.ToDouble(reader.GetValue(index + 1));
            var amount = reader.IsDBNull(index + 2) ? 0 : Convert.ToDouble(reader.GetValue(index + 2));
            rows.Add(new SnapshotCell(product, depot, location, batch, quantity, amount, procedure));
        }
        return rows;
    }

    private sealed record PreviousPeriod(string MonthType, string MonthNo, DateTime MonthDate);

    /// <summary>快照里的一个格子（一条明细行）：期末量与移动加权单价由累计量与累计入库金额推出。</summary>
    private sealed class SnapshotCell(
        string productNo, string depotId, string locationNo, string batchNo, double quantity, double amount,
        string procedureTypeId = "")
    {
        /// <summary>字段分隔符：料号/库别/库位/批次都可能含逗号与短横，拼键必须用一个不会出现的字符。</summary>
        private const char KeySeparator = '\u001f';

        public string ProductNo { get; } = productNo;
        public string DepotId { get; } = depotId;
        public string LocationNo { get; } = locationNo;
        public string BatchNo { get; } = batchNo;

        /// <summary>
        /// 制程（半成品账按制程分账）。**主账行落哨兵空串**：那表示"该维度不细分"，
        /// 与库位/批次两维的哨兵是同一个道理——唯一键把 NULL 视为彼此相等，表达不了"不细分"。
        /// </summary>
        public string ProcedureTypeId { get; } = procedureTypeId;

        /// <summary>期初数量（上一期已批核快照）。</summary>
        public double OpeningQuantity { get; } = quantity;

        /// <summary>期末数量：期初量 + 本期净动账。</summary>
        public double Quantity { get; set; } = quantity;

        /// <summary>本期入库数量：移动加权成本的分母用它，出库不改均价。</summary>
        public double InQuantity { get; set; }

        /// <summary>期初金额 + 本期入库金额。</summary>
        public double Amount { get; set; } = amount;

        /// <summary>
        /// 移动加权单价：分母取"期初量 + 本期入库量"，**不取期末量**——期末量含出库，拿它当分母会把均价拉偏。
        /// 分母不为正时落 0 而不抛异常：空账、退货多于入库这类形态也要能生成出来。
        /// </summary>
        public double UnitPrice
        {
            get
            {
                var denominator = OpeningQuantity + InQuantity;
                return denominator > 0 ? Amount / denominator : 0;
            }
        }

        public string Key => string.Join(KeySeparator, ProductNo, DepotId, LocationNo, BatchNo, ProcedureTypeId);

        public static string BuildKey(string productNo, string depotId, string locationNo, string batchNo,
            string procedureTypeId = "")
            => string.Join(KeySeparator, productNo, depotId, locationNo, batchNo, procedureTypeId);
    }
}
