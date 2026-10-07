using System.Data;
using Microsoft.Data.SqlClient;

namespace EOS.API.Data.Workbench;

/// <summary>
/// 单据上下游的状态守卫：**批核前查上游、解批前查下游**。
///
/// **依据只有一处**：`FIELD_RELATION` 里 `RELATION_KIND = 'EFFECT'` 的登记边 —— 它由业务动作的定位键
/// 派生，并有 `scripts/verify-field-relation-edges.ps1` 全量核验。表里每行是一个**键列对**：
/// `FROM_TABLE.FROM_COLUMN`（下游）↔ `TO_TABLE.TO_COLUMN`（上游），`KEY_ORDINAL` 是复合键位序；
/// 同一个 `RELATION_NAME` 下的若干行合起来才是一条边。方向以"**被引用方是上游**"为准
/// （`BusinessFlowRepository` 里已用两个独立数据源互证过这个方向）。
///
/// 口径（2026-10-07 用户拍板，见 docs/guide/48-生命周期与审批流.md）：
/// <list type="bullet">
/// <item>**批核**：上游未批核时本单没有来源，批核要被拦住（B 未批核则批核 C 应被拒）。</item>
/// <item>**解批**：下游已批核时不许解批（引用链逐级解开：A→B→C，解批 A 要求 B 未批核）。</item>
/// <item>**查不到就放行**（宁可漏拦、不可误拦）：对面表没有 `CONFIRM_TAG` 列、键列读不到值、
/// 边落在明细/中间表上 —— 一律视为"这条链看不全"，不拦。</item>
/// </list>
///
/// 解批**不删除任何下游单据**；这里只拒绝"下游还站着"的情形。
/// </summary>
internal static class DocumentChainGuard
{
    internal const string DownstreamConfirmedCode = "DOWNSTREAM_CONFIRMED";
    internal const string UpstreamNotConfirmedCode = "UPSTREAM_NOT_CONFIRMED";

    internal sealed record ChainBlock(string Code, string Message);

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

        // 每次检查都需要"本单在边上的那几列的值"：批核时它们是本单的 FROM_COLUMN（下游列），
        // 解批时是本单的 TO_COLUMN（上游列）。按列名去重后一次读出。
        var ownColumns = edges
            .Select(edge => new { edge.SelfColumn, edge.SelfTable })
            .Where(item => string.Equals(item.SelfTable, masterTable, StringComparison.OrdinalIgnoreCase))
            .Select(item => item.SelfColumn)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (ownColumns.Count == 0)
            return null;

        var ownValues = await ReadColumnsAsync(connection, transaction, masterTable, pkOrder, keyValues, ownColumns, token);
        if (ownValues is null)
            return null;   // 本单行读不到：交给上层既有前置闸去报"记录不存在"

        foreach (var edge in edges)
        {
            var selfValue = ownValues.GetValueOrDefault(edge.SelfColumn);
            if (string.IsNullOrWhiteSpace(selfValue))
                continue;   // 键列没值 ⇒ 这条边看不全，放行

            if (!await OppositeTableHasConfirmTagAsync(connection, transaction, edge.OtherTable, token))
                continue;   // 对面不是"有批核状态的单据"（明细/中间表/主档）⇒ 不拦

            var hit = await FindOppositeRowAsync(connection, transaction, edge, selfValue, approve, token);
            if (hit is null)
                continue;

            var otherValue = hit;
            return approve
                ? new ChainBlock(UpstreamNotConfirmedCode,
                    $"上游单据 {edge.OtherTable} 尚未批核（{edge.OtherColumn} = {otherValue}），本单没有来源，不能批核。")
                : new ChainBlock(DownstreamConfirmedCode,
                    $"下游单据 {edge.OtherTable} 已批核（{edge.OtherColumn} = {otherValue}），请先解批下游单，再解批本单。");
        }
        return null;
    }

    /// <summary>一条边：本单那一侧的列、对面表与对面列。一个 <c>RELATION_NAME</c> 下的多行会合并成一条。</summary>
    private sealed record Edge(
        string SelfTable, string SelfColumn, string OtherTable, string OtherColumn, string MatchValue);

    /// <summary>
    /// 读出与本单相关的登记边。批核时找"本单是下游"的边（`FROM_TABLE = 本单主表`），
    /// 解批时找"本单是上游"的边（`TO_TABLE = 本单主表`）。
    /// 同一条边有多个键列时，按"第一个键列"匹配即可——多键列是**定位用**的复合键，
    /// 单列匹配会放宽，但方向对、且只用于"是否存在已批核/未批核的对面单据"这一个判断。
    /// </summary>
    private static async Task<List<Edge>> ReadEdgesAsync(
        SqlConnection connection, SqlTransaction? transaction, string masterTable, bool approve, CancellationToken token)
    {
        var column = approve ? "FROM_TABLE" : "TO_TABLE";
        await using var command = new SqlCommand($"""
            SELECT LTRIM(RTRIM(FROM_TABLE)), LTRIM(RTRIM(FROM_COLUMN)),
                   LTRIM(RTRIM(TO_TABLE)), LTRIM(RTRIM(TO_COLUMN)), ISNULL(KEY_ORDINAL, 0)
            FROM dbo.FIELD_RELATION
            WHERE RELATION_KIND = N'EFFECT'
              AND {column} = @MasterTable
              AND LTRIM(RTRIM(FROM_TABLE)) <> LTRIM(RTRIM(TO_TABLE))
            ORDER BY LTRIM(RTRIM(FROM_TABLE)), LTRIM(RTRIM(TO_TABLE)), ISNULL(KEY_ORDINAL, 0);
            """, connection, transaction);
        command.Parameters.Add("@MasterTable", SqlDbType.NVarChar, 100).Value = masterTable.Trim();

        var edges = new List<Edge>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            var fromTable = reader.GetString(0);
            var fromColumn = reader.GetString(1);
            var toTable = reader.GetString(2);
            var toColumn = reader.GetString(3);
            var edge = approve
                ? new Edge(fromTable, fromColumn, toTable, toColumn, fromColumn)
                : new Edge(toTable, toColumn, fromTable, fromColumn, toColumn);
            // 同一条边只会取到第一个键列（ORDER BY KEY_ORDINAL），其余键列是同一条边的其他列。
            if (seen.Add($"{edge.SelfTable}|{edge.SelfColumn}|{edge.OtherTable}|{edge.OtherColumn}"))
                edges.Add(edge);
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

    /// <summary>对面表是否有批核状态位。没有就不拦（它多半是明细表或主档，链到此为止）。</summary>
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
    /// 返回被命中的列值（用于文案），没命中返回 null。
    /// </summary>
    private static async Task<string?> FindOppositeRowAsync(
        SqlConnection connection, SqlTransaction? transaction, Edge edge, string selfValue, bool approve, CancellationToken token)
    {
        var confirmPredicate = approve ? "ISNULL(CONFIRM_TAG,0) = 0" : "ISNULL(CONFIRM_TAG,0) = 1";
        await using var command = new SqlCommand($"""
            SELECT TOP 1 CONVERT(nvarchar(100), [{edge.OtherColumn}])
            FROM dbo.[{edge.OtherTable}]
            WHERE [{edge.OtherColumn}] = @Value AND {confirmPredicate};
            """, connection, transaction);
        command.Parameters.Add("@Value", SqlDbType.NVarChar, 100).Value = selfValue.Trim();
        var result = await command.ExecuteScalarAsync(token);
        return result is null || result is DBNull ? null : result.ToString()?.Trim();
    }
}
