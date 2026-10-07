using System.Data;
using Microsoft.Data.SqlClient;

namespace EOS.API.Data.Workbench;

/// <summary>
/// 单据上下游的状态守卫：**批核前查上游、解批前查下游**。
///
/// **依据只有一处**：`FIELD_RELATION` 里 `RELATION_KIND = 'EFFECT'` 的登记边 —— 它由业务动作的定位键
/// 派生，并有 `scripts/verify-field-relation-edges.ps1` 全量核验。表里每行是一个**键列对**：
/// `FROM_TABLE.FROM_COLUMN`（下游）↔ `TO_TABLE.TO_COLUMN`（上游），`KEY_ORDINAL` 是复合键位序；
/// 同一个 `(FROM_TABLE, TO_TABLE)` 下的若干行合起来才是一条边（如同类型 + 同单号两个键列）。
/// 方向以"**被引用方是上游**"为准（`BusinessFlowRepository` 里用两个独立数据源互证过这个方向）。
///
/// 口径（2026-10-07 用户拍板，见 docs/guide/48-生命周期与审批流.md）：
/// <list type="bullet">
/// <item>**批核**：上游未批核时本单没有来源，批核要被拦住（B 未批核则批核 C 应被拒）。</item>
/// <item>**解批**：下游已批核时不许解批（引用链逐级解开：A→B→C，解批 A 要求 B 未批核）。</item>
/// <item>**查不到就放行**（宁可漏拦、不可误拦）：对面表没有 `CONFIRM_TAG` 列、键列读不到值、
/// 边没登记在 <see cref="AllowedEdges"/> 里 —— 一律视为"这条链看不全"，不拦。</item>
/// </list>
///
/// 解批**不删除任何下游单据**；这里只拒绝"下游还站着"的情形。
/// </summary>
internal static class DocumentChainGuard
{
    internal const string DownstreamConfirmedCode = "DOWNSTREAM_CONFIRMED";
    internal const string UpstreamNotConfirmedCode = "UPSTREAM_NOT_CONFIRMED";

    internal sealed record ChainBlock(string Code, string Message);

    /// <summary>
    /// 允许参与校验的边（**下游表 → 上游表**）。
    ///
    /// **为什么必须有这份清单**：元数据里**没有"单据 / 主档"的标记**（`MODULES` 只有
    /// `DETAIL_TABLE` / `AUTO_APPROVE` / `EFFECT_ENGINE_TAG` 可看），而 `FIELD_RELATION` 的 EFFECT 边
    /// 把两类引用混在了一起：真正该管的"单据链"（订单变更单 → 订单），和"引用基础资料"
    /// （制令单 → 产品、发货单 → 客户、采购单 → 厂商）。后者一旦纳入就是**大规模误拦**——
    /// 实测 `CLIENT` 375 行里 242 行未批核、`PRODUCT` 1917 行里 599 行未批核、`SUPPLIER` 170/433，
    /// 凡引用了这些主档的单据都会批核不了。
    ///
    /// 因此这里**只认显式登记的边**：没登记就当作"这条链看不全"，放行（与"查不到就放行"同一条口径）。
    /// 往清单里加一条 = 确认那一对确实是"单据 → 单据"。**宁可先窄后宽，不要先宽后收**。
    /// </summary>
    private static readonly HashSet<string> AllowedEdges = new(StringComparer.OrdinalIgnoreCase)
    {
        "COP_ORDER_CHANGE_M|COP_ORDER_M",        // 销售订单变更单 → 销售订单
        "MOC_PRODUCE_CHANGE_M|MOC_PRODUCE_M",    // 制令变更 → 制令单
        "MOU_APPLY_M|MOU_ASSESS_M",              // 开模申请单 → 模具评估单
        "MOU_ACCEPT_M|MOU_APPLY_M",              // 模具承认单 → 开模申请单
    };

    /// <summary>返回 null 表示放行。</summary>
    public static async Task<ChainBlock?> CheckAsync(
        SqlConnection connection,
        SqlTransaction? transaction,
        string masterTable,
        IReadOnlyList<string> pkOrder,
        IReadOnlyList<string> keyValues,
        bool approve,
        CancellationToken token)
    {
        var edges = await ReadEdgesAsync(connection, transaction, masterTable, approve, token);
        if (edges.Count == 0)
            return null;

        // 每条边都要用"本单在边上那几列的值"去对面表匹配。按列名去重后一次读出本单行。
        var ownColumns = edges
            .SelectMany(edge => edge.Keys.Select(key => key.SelfColumn))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var ownValues = await ReadColumnsAsync(connection, transaction, masterTable, pkOrder, keyValues, ownColumns, token);
        if (ownValues is null)
            return null;   // 本单行读不到：交给上层既有的"记录不存在"闸去报

        foreach (var edge in edges)
        {
            if (!await OppositeTableHasConfirmTagAsync(connection, transaction, edge.OtherTable, token))
                continue;   // 对面没有批核状态位（明细表 / 纯主档）⇒ 这条链到此为止，不拦

            // 复合键必须**每一列都有值**才拿它定位：缺一列就说明这条边看不全，放行。
            var match = new List<(string Column, string Value)>();
            foreach (var key in edge.Keys)
            {
                var value = ownValues.GetValueOrDefault(key.SelfColumn);
                if (string.IsNullOrWhiteSpace(value))
                {
                    match.Clear();
                    break;
                }
                match.Add((key.OtherColumn, value));
            }
            if (match.Count == 0)
                continue;

            var hit = await FindOppositeRowAsync(connection, transaction, edge, match, approve, token);
            if (hit is null)
                continue;

            var description = $"{hit.ColumnName} = {hit.ColumnValue}";
            return approve
                ? new ChainBlock(UpstreamNotConfirmedCode,
                    $"上游单据 {edge.OtherTable} 尚未批核（{description}），本单没有来源，不能批核。")
                : new ChainBlock(DownstreamConfirmedCode,
                    $"下游单据 {edge.OtherTable} 已批核（{description}），请先解批下游单，再解批本单。");
        }
        return null;
    }

    /// <summary>一个复合键列对：本单那侧的列 ↔ 对面表的列。方向按 <c>approve</c> 决定。</summary>
    private sealed record EdgeKey(string SelfColumn, string OtherColumn);

    /// <summary>一条登记边：本单表、对面表，以及这条边的全部键列对。</summary>
    private sealed record Edge(string SelfTable, string OtherTable, IReadOnlyList<EdgeKey> Keys);

    /// <summary>命中的对面行：用于文案的列与值（属性名避开 <c>Value</c>，免得与 Nullable 的 Value 混淆）。</summary>
    private sealed record OppositeHit(string ColumnName, string ColumnValue);

    /// <summary>
    /// 读出与本单相关、且**已登记**的边。批核时找"本单是下游"的边（`FROM_TABLE = 本单主表`），
    /// 解批时找"本单是上游"的边（`TO_TABLE = 本单主表`）。
    /// 同一对表的多行（复合键的各列）在这里合并成一条边 —— 只用第一列匹配会退化成"按类型匹配"，
    /// 把同类型的其它单据也算进来（那是实打实的误拦）。
    /// </summary>
    private static async Task<List<Edge>> ReadEdgesAsync(
        SqlConnection connection, SqlTransaction? transaction, string masterTable, bool approve, CancellationToken token)
    {
        var filterColumn = approve ? "FROM_TABLE" : "TO_TABLE";
        await using var command = new SqlCommand($"""
            SELECT LTRIM(RTRIM(FROM_TABLE)), LTRIM(RTRIM(FROM_COLUMN)),
                   LTRIM(RTRIM(TO_TABLE)), LTRIM(RTRIM(TO_COLUMN)), ISNULL(KEY_ORDINAL, 0)
            FROM dbo.FIELD_RELATION
            WHERE RELATION_KIND = N'EFFECT'
              AND {filterColumn} = @MasterTable
              AND LTRIM(RTRIM(FROM_TABLE)) <> LTRIM(RTRIM(TO_TABLE))
            ORDER BY LTRIM(RTRIM(FROM_TABLE)), LTRIM(RTRIM(TO_TABLE)), ISNULL(KEY_ORDINAL, 0), RELATION_ID;
            """, connection, transaction);
        command.Parameters.Add("@MasterTable", SqlDbType.NVarChar, 100).Value = masterTable.Trim();

        var order = new List<string>();
        var keysByPair = new Dictionary<string, List<EdgeKey>>(StringComparer.OrdinalIgnoreCase);
        await using (var reader = await command.ExecuteReaderAsync(token))
        {
            while (await reader.ReadAsync(token))
            {
                var fromTable = reader.GetString(0);
                var fromColumn = reader.GetString(1);
                var toTable = reader.GetString(2);
                var toColumn = reader.GetString(3);
                // 只认显式登记的单据链（见 AllowedEdges）：其余边一律当作"看不全"，放行。
                if (!AllowedEdges.Contains($"{fromTable}|{toTable}"))
                    continue;
                var pair = $"{fromTable}|{toTable}";
                if (!keysByPair.TryGetValue(pair, out var keys))
                {
                    keys = new List<EdgeKey>();
                    keysByPair[pair] = keys;
                    order.Add(pair);
                }
                keys.Add(approve
                    ? new EdgeKey(fromColumn, toColumn)   // 本单 = 下游：本单列是 FROM_COLUMN
                    : new EdgeKey(toColumn, fromColumn));  // 本单 = 上游：本单列是 TO_COLUMN
            }
        }

        var edges = new List<Edge>();
        foreach (var pair in order)
        {
            var parts = pair.Split('|');
            edges.Add(approve
                ? new Edge(parts[0], parts[1], keysByPair[pair])   // 本单是下游
                : new Edge(parts[1], parts[0], keysByPair[pair])); // 本单是上游
        }
        return edges;
    }

    /// <summary>读本单行上指定的若干列值；行不存在返回 null。</summary>
    private static async Task<Dictionary<string, string?>?> ReadColumnsAsync(
        SqlConnection connection, SqlTransaction? transaction, string table,
        IReadOnlyList<string> pkOrder, IReadOnlyList<string> keyValues, IReadOnlyList<string> columns, CancellationToken token)
    {
        if (pkOrder.Count == 0 || keyValues.Count < pkOrder.Count)
            return null;
        var select = string.Join(",", columns.Select(c => $"[{c}]"));
        var where = WorkbenchSql.BuildKeyWhere(pkOrder, keyValues);
        await using var command = new SqlCommand($"SELECT {select} FROM dbo.[{table}] WHERE {where};", connection, transaction);
        WorkbenchSql.AddKeyParameters(command, pkOrder, keyValues);
        await using var reader = await command.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token))
            return null;
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < columns.Count; index++)
            values[columns[index]] = reader.IsDBNull(index) ? null : reader.GetValue(index).ToString()?.Trim();
        return values;
    }

    /// <summary>对面表是否有批核状态位。没有就不拦（它多半是明细表或纯主档，链到此为止）。</summary>
    private static async Task<bool> OppositeTableHasConfirmTagAsync(
        SqlConnection connection, SqlTransaction? transaction, string table, CancellationToken token)
    {
        await using var command = new SqlCommand("""
            SELECT CASE WHEN COL_LENGTH('dbo.' + @Table, 'CONFIRM_TAG') IS NULL THEN 0 ELSE 1 END;
            """, connection, transaction);
        command.Parameters.Add("@Table", SqlDbType.NVarChar, 100).Value = table.Trim();
        return Convert.ToInt32(await command.ExecuteScalarAsync(token) ?? 0, System.Globalization.CultureInfo.InvariantCulture) == 1;
    }

    /// <summary>
    /// 在对面表里找一条"违反口径"的行：批核时找**未批核**的上游行，解批时找**已批核**的下游行。
    /// 命中的列值用于文案；没命中返回 null。
    /// </summary>
    private static async Task<OppositeHit?> FindOppositeRowAsync(
        SqlConnection connection, SqlTransaction? transaction, Edge edge,
        IReadOnlyList<(string Column, string Value)> match, bool approve, CancellationToken token)
    {
        var confirmPredicate = approve ? "ISNULL(CONFIRM_TAG,0) = 0" : "ISNULL(CONFIRM_TAG,0) = 1";
        var predicates = string.Join(" AND ", match.Select((item, index) => $"[{item.Column}] = @v{index}"));
        var display = $"[{match[0].Column}]";
        await using var command = new SqlCommand($"""
            SELECT TOP 1 CONVERT(nvarchar(100), {display})
            FROM dbo.[{edge.OtherTable}]
            WHERE {predicates} AND {confirmPredicate};
            """, connection, transaction);
        for (var index = 0; index < match.Count; index++)
            command.Parameters.Add($"@v{index}", SqlDbType.NVarChar, 100).Value = match[index].Value;
        var result = await command.ExecuteScalarAsync(token);
        return result is null || result is DBNull
            ? null
            : new OppositeHit(match[0].Column, result.ToString()?.Trim() ?? string.Empty);
    }
}
