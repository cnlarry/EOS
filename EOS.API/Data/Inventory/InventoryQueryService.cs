using System.Data;
using Microsoft.Data.SqlClient;

namespace EOS.API.Data.Inventory;

/// <summary>
/// 库存余额读取的唯一入口（四键：料号 / 库别 / 库位 / 批次）。
///
/// 收口的范围是**怎么读库存**，不是"哪些业务需要库存"：表名与列名是这里的常量，
/// 键的归一化（库位哨兵 `-`、空批次 `''`）、未指定维度的聚合方式（`SUM(QTY)`）、
/// 库别级字段的取法（一律 `MAX`，绝不 `SUM`）、以及要不要加锁，都在这一处定口径，
/// 调用方只说自己要哪个维度上的数。
///
/// 出口有两类：
/// · 数据级 API（`GetQuantitiesAsync` 等）：结果读进内存返回强类型对象，给"读出来再算"的流程用；
/// · 片段级 API（`InventorySources`）：返回固定的参数化 SQL 片段，给必须把一整条 SQL 交给库跑的
///   宿主（报表聚合、效果链路）用。片段集合是封闭的常量，不接收调用方的 SQL 片段，
///   参数名由片段自己定义。
///
/// 服务不认识调用方的业务表（客户 / 产品 / 单据），也不做任何权限判断——权限门仍在调用方。
/// </summary>
public static class InventoryQueryService
{
    public const string BalanceTable = "INV_PRO_DEPOT";
    public const string LedgerTable = "INV_DEPOT_LOG";

    public const string ProductColumn = "PRO_NO";
    public const string DepotColumn = "DEPOT_ID";
    public const string LocationColumn = "LOCATION_NO";
    public const string BatchColumn = "BATCH_NO";
    public const string QuantityColumn = "QTY";

    /// <summary>库位的哨兵值「未指定位置」：物理行用它占位，比较前必须把空值归一到它。</summary>
    public const string LocationSentinel = "-";

    /// <summary>空批次的归一目标：批次列可空、历史上也存空串，两者语义相同。</summary>
    public const string EmptyBatch = "";

    /// <summary>
    /// 库别级字段：同一 `(PRO_NO, DEPOT_ID)` 的所有行冗余存同一个值，
    /// 因此取值只能任取其一或 `MAX`；`SUM` 会按行数放大成 N 倍。
    /// </summary>
    public static readonly string[] DepotLevelColumns = ["INIT_QTY", "COST_PRICE", "COST_AMOUNT", "LAST_CHECK_DATE"];

    /// <summary>
    /// 读库存时的锁提示。默认不加脏读（余额读要准，避免超发）；
    /// 需要写意图或表级排他时由调用方显式声明（MRP 重算的并发正确性依赖表级排他锁）。
    /// </summary>
    public enum ReadLock
    {
        None = 0,
        UpdateHold = 1,
        ExclusiveTable = 2,
    }

    private static string Hint(ReadLock readLock) => readLock switch
    {
        ReadLock.UpdateHold => " WITH (UPDLOCK, HOLDLOCK)",
        ReadLock.ExclusiveTable => " WITH(TABLOCKX)",
        _ => string.Empty,
    };

    /// <summary>库位键的归一化表达式：空值与哨兵不参与差异——比较原值之前必须过这一层。</summary>
    public static string LocationKey(string alias) =>
        $"ISNULL(LTRIM(RTRIM({alias}.{LocationColumn})), N'{LocationSentinel}')";

    /// <summary>批次键的归一化表达式：NULL 与空串同义。</summary>
    public static string BatchKey(string alias) =>
        $"ISNULL(LTRIM(RTRIM({alias}.{BatchColumn})), N'')";

    /// <summary>库别级字段的取值表达式：`MAX` 而不是 `SUM`（SUM 会按行数翻倍）。</summary>
    public static string DepotLevelValue(string alias, string column) =>
        DepotLevelColumns.Contains(column, StringComparer.OrdinalIgnoreCase)
            ? $"MAX(ISNULL({alias}.{column}, 0))"
            : throw new InvalidOperationException($"{column} 不是库别级字段，不能按库别级口径读取。");

    // ===== 聚合读 =====

    /// <summary>
    /// 聚合请求：给了的维度做等值定位，没给的维度聚合掉；
    /// `GroupBy*` 决定结果的行粒度（未声明的维度一定被 `SUM(QTY)` 合并）。
    /// </summary>
    public sealed record QuantityQuery
    {
        public bool GroupByDepot { get; init; }
        public bool GroupByLocation { get; init; }
        public bool GroupByBatch { get; init; }

        /// <summary>只取参与 MRP 的库别（`DEPOT.MRP=1`）——MRP 重算的口径。</summary>
        public bool MrpDepotsOnly { get; init; }

        public string? DepotId { get; init; }
        public string? ProductNo { get; init; }
        public string? LocationNo { get; init; }
        public string? BatchNo { get; init; }

        /// <summary>只要数量非零的组合（用于"区域内有没有货"这一类判定）。</summary>
        public bool RequireNonZero { get; init; }
    }

    /// <summary>
    /// 聚合结果：被 groupBy 掉的维度为 null；数量可能为 null
    /// （明细全为 NULL 时 SUM 就是 NULL，与原口径一致，不用 0 顶替）。
    /// </summary>
    public sealed record InventoryQuantity(
        string ProductNo, string? DepotId, string? LocationNo, string? BatchNo, double? Quantity);

    /// <summary>
    /// 按维度聚合数量：未出现在 groupBy 里的维度一律 `SUM(QTY)`。
    /// 余额表是千行级（四键唯一），不做任何过滤的整表聚合也在同一量级。
    /// </summary>
    public static async Task<IReadOnlyList<InventoryQuantity>> GetQuantitiesAsync(
        SqlConnection connection,
        SqlTransaction? transaction,
        QuantityQuery query,
        ReadLock readLock,
        CancellationToken token)
    {
        var plan = BuildQuantities(query, readLock);
        await using var command = new SqlCommand(plan.Sql, connection, transaction);
        foreach (var parameter in plan.Parameters)
        {
            command.Parameters.Add(parameter.Name, parameter.Type, parameter.Size).Value = parameter.Value;
        }

        await using var reader = await command.ExecuteReaderAsync(token);
        var rows = new List<InventoryQuantity>();
        while (await reader.ReadAsync(token))
        {
            var index = 0;
            var product = reader.IsDBNull(index) ? string.Empty : reader.GetString(index).Trim();
            index++;
            string? depot = null;
            if (query.GroupByDepot)
            {
                depot = reader.IsDBNull(index) ? string.Empty : reader.GetString(index).Trim();
                index++;
            }
            string? location = null;
            if (query.GroupByLocation)
            {
                location = reader.IsDBNull(index) ? LocationSentinel : reader.GetString(index).Trim();
                index++;
            }
            string? batch = null;
            if (query.GroupByBatch)
            {
                batch = reader.IsDBNull(index) ? EmptyBatch : reader.GetString(index).Trim();
                index++;
            }
            var quantity = reader.IsDBNull(index) ? null : (double?)Convert.ToDouble(reader.GetValue(index));
            rows.Add(new InventoryQuantity(product, depot, location, batch, quantity));
        }
        return rows;
    }

    /// <summary>
    /// 生成聚合语句与其参数。**聚合语义只在这里**：维度未指定就 SUM、指定了就等值定位，
    /// 库位/批次的比较一律走归一化键，过滤值全部参数化（调用方的值不进 SQL 文本）。
    /// </summary>
    internal static (string Sql, IReadOnlyList<BoundParameter> Parameters) BuildQuantities(
        QuantityQuery query, ReadLock readLock)
    {
        var groupBy = new List<string> { ProductColumn };
        if (query.GroupByDepot) groupBy.Add(DepotColumn);
        if (query.GroupByLocation) groupBy.Add(LocationColumn);
        if (query.GroupByBatch) groupBy.Add(BatchColumn);

        var projection = string.Join(", ", groupBy);
        var predicates = new List<string>();
        var parameters = new List<BoundParameter>();
        if (query.MrpDepotsOnly)
        {
            predicates.Add($"{DepotColumn} IN (SELECT {DepotColumn} FROM dbo.DEPOT WHERE MRP=1)");
        }
        if (query.DepotId is { Length: > 0 } depot)
        {
            predicates.Add($"{DepotColumn}=@depot");
            parameters.Add(new BoundParameter("@depot", SqlDbType.NVarChar, 20, depot));
        }
        if (query.ProductNo is { Length: > 0 } product)
        {
            predicates.Add($"{ProductColumn}=@pro");
            parameters.Add(new BoundParameter("@pro", SqlDbType.NVarChar, 60, product));
        }
        if (query.LocationNo is { Length: > 0 } location)
        {
            predicates.Add($"{LocationKey("s")}=@loc");
            parameters.Add(new BoundParameter("@loc", SqlDbType.NVarChar, 60, location));
        }
        if (query.BatchNo is { Length: > 0 } batch)
        {
            predicates.Add($"{BatchKey("s")}=@batch");
            parameters.Add(new BoundParameter("@batch", SqlDbType.NVarChar, 60, batch));
        }
        var where = predicates.Count > 0 ? " WHERE " + string.Join(" AND ", predicates) : string.Empty;
        // RequireNonZero 用 HAVING 而不是把 NULL 兜成 0 再比较：后者会把"谁也没填过量"的行算成零参与判定，
        // 与既有 NULL 语义不等价。
        var having = query.RequireNonZero ? $" HAVING SUM({QuantityColumn}) <> 0" : string.Empty;
        var sql = $"SELECT {projection}, SUM({QuantityColumn}) AS {QuantityColumn} FROM dbo.{BalanceTable} s"
            + Hint(readLock) + where + $" GROUP BY {projection}" + having + ";";
        return (sql, parameters);
    }

    internal sealed record BoundParameter(string Name, SqlDbType Type, int Size, object Value);
}

/// <summary>
/// 库存读取的 SQL 片段：给"必须把一整条 SQL 交给库跑"的宿主（报表聚合、效果链路）用的**封闭集合**。
/// 片段里的表名、列与去重 / 归一化语义属于这里；范围参数名是宿主与本片段之间的绑定契约。
/// </summary>
public static class InventorySources
{
    /// <summary>
    /// 库存日报：区间内有过账面往来的 **(库别, 料号)** 组合。
    /// 四键之后同一组合可能存在多行（库位 / 批次不同），必须 `DISTINCT`——
    /// 否则下游按这两列回关流水时会把期初与本期收发成倍放大。
    /// 绑定参数：`@depot1/@depot2`（库别范围）、`@pro1/@pro2`（料号范围）、
    /// `@sort1/@sort2`（类别范围，走 `PRODUCT.SORT_ID`）。
    /// 空上界的语义是"无界"（不用旧的 `char(255)` 哨兵：它在中文排序规则下并不在最末，会静默截断上界）。
    /// </summary>
    // 缩进刻意按宿主 `WITH PAIR AS (` 的装配位置留 12 个空格：片段本身不以行首为锚，
    // 嵌进去之后的 SQL 仍然对齐，便于直接阅读执行计划里的语句文本。
    public const string DistinctProductDepotPairs = """
                    SELECT DISTINCT i.DEPOT_ID, i.PRO_NO
                    FROM dbo.INV_PRO_DEPOT i
                    JOIN dbo.PRODUCT pr ON pr.PRO_NO = i.PRO_NO
                    WHERE pr.PRO_TYPE = '3'
                      AND (@depot1 IS NULL OR @depot1 = '' OR i.DEPOT_ID >= @depot1)
                      AND (@depot2 IS NULL OR @depot2 = '' OR i.DEPOT_ID <= @depot2)
                      AND (@pro1 IS NULL OR @pro1 = '' OR i.PRO_NO >= @pro1)
                      AND (@pro2 IS NULL OR @pro2 = '' OR i.PRO_NO <= @pro2)
                      AND (@sort1 IS NULL OR @sort1 = '' OR pr.SORT_ID >= @sort1)
                      AND (@sort2 IS NULL OR @sort2 = '' OR pr.SORT_ID <= @sort2)
        """;
}
