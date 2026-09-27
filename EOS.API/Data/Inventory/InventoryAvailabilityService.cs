using System.Data;
using Microsoft.Data.SqlClient;

namespace EOS.API.Data.Inventory;

/// <summary>
/// 可用量的**唯一口径出口**（给定口径， D7 落实）：
/// <code>
/// 可用量 = 库存数量 − 质量冻结 − 客诉/召回冻结 − 订单预留 − 制令预留 − 待检不合格
/// </code>
/// </summary>
/// <remarks>
/// **为什么要有一个服务**（而不是各处自己 SUM）：口径分头实现必然分叉——本项目已经栽过
/// 一次（`check-inventory-read-hosts` 那条门禁就是这么来的）。三个消费方（出库校验**拦**、
/// 报表/查询下发、选择器显示）都必须经这里取数，**禁止自行聚合**。
///
/// **哪些冻结/预留计入**（"有效性"判据，写在读取时——这就是 D7-⑤ 的**惰性判定**：
/// 不靠定时作业去清理，而是每次读的时候按证据判）：
/// <list type="number">
/// <item><c>STATUS = 'A'</c> 才计入；<c>'C'</c>（已取消/已释放）不计入。</item>
/// <item>**来源单据已结案的不计入**：按 <c>SOURCE_TYPE</c> 解析到源表后，源表若带结案列
/// （`FINISHED_TAG`）且该单据 `FINISHED_TAG = 1`，这笔占用按"已经不该再占"处理。
/// 于是"释放钩子没跑到"不会变成**假性缺料**——这正是惰性判定的价值所在。</item>
/// <item>**判不出来的一律计入**（保守方向）：来源类型解析不到、源表没有结案列、单据查不到、
/// 脏数据——统统仍然算占用，并把这些情形作为 <see cref="Availability.Notes"/> 报出来。
/// 理由：可用量算大是**危险方向**（会让不该出的货出得去），算小只是保守；判不出来就报出来，
/// 但不拿它去放宽出库。</item>
/// </list>
///
/// **写入约定**（WS-20 定义，WS-21 冻结入口 / WS-22 预留与释放照此写入）：
/// <c>INV_FREEZE</c> / <c>INV_RESERVE</c> 的 <c>SOURCE_TYPE</c> = **模块号**（字符串形式的 `M_IDX`），
/// <c>SOURCE_NO</c> = 该模块主表**单号列**的取值；"单号列"从主表物理主键里认——
/// 以 `_NO` 结尾且不以 `_TYPE` 结尾的唯一那一列。认不出唯一单号列（例如主键里有两个 `_NO` 的
/// 主档表）就按上面第 3 条**保守计入**并报出来。
///
/// **本服务只读**：不写余额表、不写冻结/预留表（维护 `USEABLE_QTY` 列是写入路径的事，
/// 逐键对账是 `check-inventory-availability.ps1` 的事）。
/// </remarks>
public static class InventoryAvailabilityService
{
    /// <summary>
    /// 从主键列里认**来源单号列**：以 `_NO` 结尾、且不以 `_TYPE` 结尾的**唯一**那一列；
    /// 不唯一（例如主键里有两个 `_NO` 的主档表）返回 null——调用方按"认不出就不动"处理
    /// （不猜着释放、也不猜着判过期）。
    /// </summary>
    public static string? PickSourceNumberColumn(IReadOnlyList<string> pkColumns)
    {
        var candidates = pkColumns
            .Where(column => column.EndsWith("_NO", StringComparison.OrdinalIgnoreCase)
                             && !column.EndsWith("_TYPE", StringComparison.OrdinalIgnoreCase))
            .ToList();
        return candidates.Count == 1 ? candidates[0] : null;
    }

    /// <summary>库存行的一格（四键）。与冻结/预留表的维度**逐列同宽同义**。</summary>
    public sealed record SlotKey(string ProductNo, string DepotId, string LocationNo, string BatchNo)
    {
        public SlotKey Trimmed() => new(
            ProductNo?.Trim() ?? string.Empty,
            DepotId?.Trim() ?? string.Empty,
            string.IsNullOrWhiteSpace(LocationNo) ? InventoryQueryService.LocationSentinel : LocationNo.Trim(),
            BatchNo?.Trim() ?? InventoryQueryService.EmptyBatch);
    }

    /// <summary>
    /// 一格的可用量。<paramref name="Quantity"/> 是库存数量，<paramref name="Frozen"/> /
    /// <paramref name="Reserved"/> 是**计入**的冻结与预留（已按有效性过滤），
    /// <paramref name="Available"/> = 三者相减。
    /// <paramref name="DroppedFrozen"/> / <paramref name="DroppedReserved"/> 是**被判掉**的量
    /// （已取消、或来源已结案）——它们不影响可用量，但要看得到，否则"为什么这笔不算"无从追。
    /// </summary>
    public sealed record Availability(
        double Quantity, double Frozen, double Reserved, double Available,
        double DroppedFrozen, double DroppedReserved, IReadOnlyList<string> Notes)
    {
        public static Availability Empty { get; } =
            new(0, 0, 0, 0, 0, 0, Array.Empty<string>());

        internal Availability With(double quantity) =>
            this with { Quantity = quantity, Available = quantity - Frozen - Reserved };
    }

    /// <summary>按**行格**（料号 + 库别 + 库位 + 批次）取可用量：出库校验与选择器用它。</summary>
    public static async Task<IReadOnlyDictionary<SlotKey, Availability>> ForSlotsAsync(
        SqlConnection connection,
        SqlTransaction? transaction,
        IReadOnlyCollection<SlotKey> slots,
        CancellationToken token)
    {
        var wanted = slots.Select(slot => slot.Trimmed()).Distinct().ToList();
        var result = wanted.ToDictionary(slot => slot, _ => Availability.Empty);
        if (wanted.Count == 0)
        {
            return result;
        }

        var products = wanted.Select(slot => slot.ProductNo).Distinct().ToArray();
        var depots = wanted.Select(slot => slot.DepotId).Distinct().ToArray();

        foreach (var row in await ReadBalanceAsync(connection, transaction, products, depots, token))
        {
            var key = new SlotKey(row.ProductNo, row.DepotId, row.LocationNo, row.BatchNo);
            if (result.TryGetValue(key, out var current))
            {
                result[key] = current.With(current.Quantity + row.Quantity);
            }
        }

        var occupancy = await ReadOccupancyAsync(connection, transaction, products, depots, token);
        foreach (var slot in wanted)
        {
            var current = result[slot];
            var frozen = occupancy.Counted.Where(row => row.Matches(slot) && row.IsFreeze).Sum(row => row.Quantity);
            var reserved = occupancy.Counted.Where(row => row.Matches(slot) && !row.IsFreeze).Sum(row => row.Quantity);
            var droppedFrozen = occupancy.Dropped.Where(row => row.Matches(slot) && row.IsFreeze).Sum(row => row.Quantity);
            var droppedReserved = occupancy.Dropped.Where(row => row.Matches(slot) && !row.IsFreeze).Sum(row => row.Quantity);
            result[slot] = new Availability(
                current.Quantity, frozen, reserved, current.Quantity - frozen - reserved,
                droppedFrozen, droppedReserved, occupancy.Notes);
        }
        return result;
    }

    /// <summary>按**料号 + 库别**取可用量（行格按四键汇总）：报表与查询下发用它。</summary>
    public static async Task<IReadOnlyDictionary<(string ProductNo, string DepotId), Availability>> ForPairsAsync(
        SqlConnection connection,
        SqlTransaction? transaction,
        IReadOnlyCollection<(string ProductNo, string DepotId)> pairs,
        CancellationToken token)
    {
        var wanted = pairs.Select(pair => (ProductNo: pair.ProductNo?.Trim() ?? string.Empty,
                DepotId: pair.DepotId?.Trim() ?? string.Empty))
            .Distinct().ToList();
        if (wanted.Count == 0)
        {
            return new Dictionary<(string, string), Availability>();
        }

        var products = wanted.Select(pair => pair.ProductNo).Distinct().ToArray();
        var depots = wanted.Select(pair => pair.DepotId).Distinct().ToArray();

        var totals = wanted.ToDictionary(pair => pair, _ => Availability.Empty);
        foreach (var row in await ReadBalanceAsync(connection, transaction, products, depots, token))
        {
            var key = (row.ProductNo, row.DepotId);
            if (totals.TryGetValue(key, out var current))
            {
                totals[key] = current.With(current.Quantity + row.Quantity);
            }
        }

        var occupancy = await ReadOccupancyAsync(connection, transaction, products, depots, token);
        foreach (var pair in wanted)
        {
            var frozen = occupancy.Counted.Where(row => row.ProductNo == pair.ProductNo && row.DepotId == pair.DepotId && row.IsFreeze).Sum(row => row.Quantity);
            var reserved = occupancy.Counted.Where(row => row.ProductNo == pair.ProductNo && row.DepotId == pair.DepotId && !row.IsFreeze).Sum(row => row.Quantity);
            var droppedFrozen = occupancy.Dropped.Where(row => row.ProductNo == pair.ProductNo && row.DepotId == pair.DepotId && row.IsFreeze).Sum(row => row.Quantity);
            var droppedReserved = occupancy.Dropped.Where(row => row.ProductNo == pair.ProductNo && row.DepotId == pair.DepotId && !row.IsFreeze).Sum(row => row.Quantity);
            var current = totals[pair];
            totals[pair] = new Availability(
                current.Quantity, frozen, reserved, current.Quantity - frozen - reserved,
                droppedFrozen, droppedReserved, occupancy.Notes);
        }
        return totals;
    }

    /// <summary>
    /// **可用量的唯一维护出口**：把给定格子的 `USEABLE_QTY` 按口径重算并落列，返回写入的行数。
    /// </summary>
    /// <remarks>
    /// 为什么要收在一处：这条列是**存列**（D7-②），任何"数量动过或占用动过"的写入路径都得把它
    /// 重算一遍——进出账（移动引擎）、冻结/解冻、预留/释放、来源结案钩子。若各自写一份减法，
    /// 迟早有一处忘了判有效性（来源已结案的不计入），于是列与账悄悄分叉。
    /// 于是：**口径与落列都在这里**，调用方只报"我动了哪些格子"。
    ///
    /// 只写请求到的格子（不做全表扫描）：写入路径自己知道它动过谁，越界去改别人只会制造并发面。
    /// </remarks>
    public static async Task<int> SyncSlotsAsync(
        SqlConnection connection,
        SqlTransaction? transaction,
        IReadOnlyCollection<SlotKey> slots,
        CancellationToken token)
    {
        var wanted = slots.Select(slot => slot.Trimmed()).Distinct().ToList();
        if (wanted.Count == 0)
        {
            return 0;
        }

        var available = await ForSlotsAsync(connection, transaction, wanted, token);
        var written = 0;
        foreach (var (slot, value) in available)
        {
            await using var command = new SqlCommand(
                $"UPDATE dbo.{InventoryQueryService.BalanceTable} SET USEABLE_QTY = @available "
                + $"WHERE {InventoryQueryService.ProductColumn} = @pro AND {InventoryQueryService.DepotColumn} = @depot "
                + $"AND {InventoryQueryService.LocationColumn} = @location AND {InventoryQueryService.BatchColumn} = @batch;",
                connection, transaction);
            command.Parameters.AddWithValue("@available", value.Available);
            command.Parameters.AddWithValue("@pro", slot.ProductNo);
            command.Parameters.AddWithValue("@depot", slot.DepotId);
            command.Parameters.AddWithValue("@location", slot.LocationNo);
            command.Parameters.AddWithValue("@batch", slot.BatchNo);
            written += await command.ExecuteNonQueryAsync(token);
        }
        return written;
    }

    // ===== 库存数量 =====

    private sealed record BalanceRow(string ProductNo, string DepotId, string LocationNo, string BatchNo, double Quantity);

    /// <summary>
    /// 读余额行（收口在本文件内的唯一读取点，与 <see cref="InventoryQueryService"/> 同一套常量）。
    /// 按 (料号, 库别) 收窄再在内存里对齐四键：维度未启用时余额行落哨兵，与本服务的空格语义一致。
    /// </summary>
    private static async Task<IReadOnlyList<BalanceRow>> ReadBalanceAsync(
        SqlConnection connection, SqlTransaction? transaction,
        IReadOnlyList<string> products, IReadOnlyList<string> depots, CancellationToken token)
    {
        await using var command = new SqlCommand(
            $"SELECT LTRIM(RTRIM({InventoryQueryService.ProductColumn})), LTRIM(RTRIM({InventoryQueryService.DepotColumn})), "
            + $"ISNULL({InventoryQueryService.LocationColumn}, N'{InventoryQueryService.LocationSentinel}'), "
            + $"ISNULL({InventoryQueryService.BatchColumn}, N''), ISNULL({InventoryQueryService.QuantityColumn},0) "
            + $"FROM dbo.{InventoryQueryService.BalanceTable} "
            + $"WHERE {InventoryQueryService.ProductColumn} IN ({Placeholders("@p", products.Count)}) "
            + $"AND {InventoryQueryService.DepotColumn} IN ({Placeholders("@d", depots.Count)});",
            connection, transaction);
        AddParameters(command, "@p", products);
        AddParameters(command, "@d", depots);

        var rows = new List<BalanceRow>();
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            rows.Add(new BalanceRow(
                reader.GetString(0), reader.GetString(1),
                string.IsNullOrWhiteSpace(reader.GetString(2)) ? InventoryQueryService.LocationSentinel : reader.GetString(2).Trim(),
                reader.GetString(3).Trim(),
                reader.IsDBNull(4) ? 0 : Convert.ToDouble(reader.GetValue(4))));
        }
        return rows;
    }

    // ===== 冻结 / 预留 =====

    private sealed record OccupancyRow(
        bool IsFreeze, string ProductNo, string DepotId, string LocationNo, string BatchNo,
        double Quantity, string SourceType, string SourceNo, string Status)
    {
        public bool Matches(SlotKey slot) =>
            string.Equals(ProductNo, slot.ProductNo, StringComparison.Ordinal)
            && string.Equals(DepotId, slot.DepotId, StringComparison.Ordinal)
            && string.Equals(LocationNo, slot.LocationNo, StringComparison.Ordinal)
            && string.Equals(BatchNo, slot.BatchNo, StringComparison.Ordinal);
    }

    private sealed record Occupancy(IReadOnlyList<OccupancyRow> Counted, IReadOnlyList<OccupancyRow> Dropped, IReadOnlyList<string> Notes);

    /// <summary>
    /// 读两本占用表，并按**有效性**分成"计入"与"判掉"两堆。判据见类文档：状态位 + 来源结案；
    /// 判不出来的一律计入（保守方向）并记一条 note。
    /// </summary>
    private static async Task<Occupancy> ReadOccupancyAsync(
        SqlConnection connection, SqlTransaction? transaction,
        IReadOnlyList<string> products, IReadOnlyList<string> depots, CancellationToken token)
    {
        var rows = new List<OccupancyRow>();
        foreach (var (table, column, isFreeze) in new[]
                 {
                     ("INV_FREEZE", "FREEZE_QTY", true),
                     ("INV_RESERVE", "RESERVE_QTY", false),
                 })
        {
            await using var command = new SqlCommand(
                $"SELECT LTRIM(RTRIM(PRO_NO)), LTRIM(RTRIM(DEPOT_ID)), ISNULL(LOCATION_NO, N'-'), ISNULL(BATCH_NO, N''), "
                + $"ISNULL({column},0), LTRIM(RTRIM(ISNULL(SOURCE_TYPE, N''))), LTRIM(RTRIM(ISNULL(SOURCE_NO, N''))), "
                + $"LTRIM(RTRIM(ISNULL(STATUS, N'C'))) "
                + $"FROM dbo.{table} "
                + "WHERE PRO_NO IN (" + Placeholders("@p", products.Count) + ") "
                + "AND DEPOT_ID IN (" + Placeholders("@d", depots.Count) + ");",
                connection, transaction);
            AddParameters(command, "@p", products);
            AddParameters(command, "@d", depots);

            await using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
            {
                rows.Add(new OccupancyRow(
                    isFreeze, reader.GetString(0), reader.GetString(1),
                    string.IsNullOrWhiteSpace(reader.GetString(2)) ? InventoryQueryService.LocationSentinel : reader.GetString(2).Trim(),
                    reader.GetString(3).Trim(),
                    reader.IsDBNull(4) ? 0 : Convert.ToDouble(reader.GetValue(4)),
                    reader.GetString(5), reader.GetString(6), reader.GetString(7)));
            }
        }

        // 已取消/已释放的行直接判掉，不必去看来源单据。
        var cancelled = rows.Where(row => !row.Status.Equals("A", StringComparison.OrdinalIgnoreCase)).ToList();
        var pending = rows.Where(row => row.Status.Equals("A", StringComparison.OrdinalIgnoreCase)).ToList();
        var notes = new List<string>();
        var dropped = new List<OccupancyRow>(cancelled);
        if (cancelled.Count > 0)
        {
            notes.Add($"已取消/已释放（STATUS<>'A'）的占用 {cancelled.Count} 笔未计入");
        }

        // 惰性判定：来源单据已结案的不计入（"释放钩子没跑到"不该变成假性缺料）。
        var closed = await ReadClosedSourceKeysAsync(connection, transaction, pending, notes, token);
        foreach (var row in pending)
        {
            var key = (row.SourceType, row.SourceNo);
            if (row.SourceType.Length > 0 && row.SourceNo.Length > 0 && closed.Contains(key))
            {
                dropped.Add(row);
                continue;
            }
            // 判不出来的（来源为空/源表无结案列/单据查不到）按保守方向**计入**；
            // 这一类已经被 ReadClosedSourceKeysAsync 记进 notes，这里不再重复。
        }

        return new Occupancy(pending.Except(dropped).ToList(), dropped, notes);
    }

    /// <summary>
    /// 找出"来源单据已结案"的 (来源类型, 来源单号) 集合。为此需要三件事，缺一不可：
    /// 来源类型能解析到模块主表、主表有唯一的单号列、主表带结案列（`FINISHED_TAG`）。
    /// 缺哪一件就把那一类记进 <paramref name="notes"/>（**判不出来就报出来**），返回值里自然不含它。
    /// </summary>
    private static async Task<IReadOnlySet<(string SourceType, string SourceNo)>> ReadClosedSourceKeysAsync(
        SqlConnection connection, SqlTransaction? transaction,
        IReadOnlyList<OccupancyRow> rows, List<string> notes, CancellationToken token)
    {
        var closed = new HashSet<(string, string)>();
        var byType = rows.Where(row => row.SourceType.Length > 0 && row.SourceNo.Length > 0)
            .GroupBy(row => row.SourceType, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Select(row => row.SourceNo).Distinct().ToArray(),
                StringComparer.Ordinal);
        if (byType.Count == 0)
        {
            notes.Add("有占用没有来源单别/单号：来源判不出来，已按保守方向计入");
            return closed;
        }

        // ① 来源类型（模块号）→ 主表
        var tables = new Dictionary<string, string>(StringComparer.Ordinal);
        await using (var command = new SqlCommand(
            "SELECT CONVERT(nvarchar(20), M_IDX), LTRIM(RTRIM(ISNULL(MASTER_TABLE, N''))) FROM dbo.MODULES "
            + $"WHERE CONVERT(nvarchar(20), M_IDX) IN ({Placeholders("@t", byType.Count)});", connection, transaction))
        {
            AddParameters(command, "@t", byType.Keys.ToArray());
            await using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
            {
                tables[reader.GetString(0)] = reader.GetString(1);
            }
        }

        foreach (var (sourceType, numbers) in byType)
        {
            if (!tables.TryGetValue(sourceType, out var table) || table.Length == 0)
            {
                notes.Add($"来源类型 {sourceType} 解析不到模块主表：这类占用已按保守方向计入");
                continue;
            }

            var (noColumn, hasFinished) = await ReadSourceShapeAsync(connection, transaction, table, token);
            if (noColumn is null)
            {
                notes.Add($"来源表 {table} 认不出唯一单号列（主键里 `_NO` 列不唯一）：这类占用已按保守方向计入");
                continue;
            }
            if (!hasFinished)
            {
                notes.Add($"来源表 {table} 没有结案列：这类占用的过期只能靠释放钩子（STATUS 置 C）");
                continue;
            }

            await using var command = new SqlCommand(
                $"SELECT LTRIM(RTRIM([{noColumn}])) FROM dbo.[{table}] "
                + $"WHERE [{noColumn}] IN ({Placeholders("@n", numbers.Length)}) AND ISNULL(FINISHED_TAG,0) = 1;",
                connection, transaction);
            AddParameters(command, "@n", numbers);
            await using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
            {
                closed.Add((sourceType, reader.GetString(0)));
            }
        }
        return closed;
    }

    /// <summary>
    /// 认源表的形状：物理主键里以 `_NO` 结尾、且不以 `_TYPE` 结尾的**唯一**那一列即单号列；
    /// 不唯一就返回 null（宁可判不出来——见类文档的保守方向）。同时报出该表有没有结案列。
    /// </summary>
    private static async Task<(string? NoColumn, bool HasFinished)> ReadSourceShapeAsync(
        SqlConnection connection, SqlTransaction? transaction, string table, CancellationToken token)
    {
        var pkColumns = new List<string>();
        await using (var command = new SqlCommand("""
            SELECT c.name
              FROM sys.indexes i
              JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
              JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
             WHERE i.object_id = OBJECT_ID(N'dbo.' + @table) AND i.is_primary_key = 1
             ORDER BY ic.key_ordinal;
            """, connection, transaction))
        {
            command.Parameters.AddWithValue("@table", table);
            await using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
            {
                pkColumns.Add(reader.GetString(0));
            }
        }

        var noColumn = PickSourceNumberColumn(pkColumns);

        var hasFinished = false;
        await using (var command = new SqlCommand(
            "SELECT CASE WHEN COL_LENGTH(N'dbo.' + @table, N'FINISHED_TAG') IS NULL THEN 0 ELSE 1 END;",
            connection, transaction))
        {
            command.Parameters.AddWithValue("@table", table);
            hasFinished = Convert.ToInt32(await command.ExecuteScalarAsync(token)) == 1;
        }

        return (noColumn, hasFinished);
    }

    private static string Placeholders(string prefix, int count) =>
        string.Join(", ", Enumerable.Range(0, count).Select(index => $"{prefix}{index}"));

    private static void AddParameters(SqlCommand command, string prefix, IReadOnlyList<string> values)
    {
        for (var index = 0; index < values.Count; index++)
        {
            command.Parameters.AddWithValue($"{prefix}{index}", values[index]);
        }
    }
}
