using System.Collections.Concurrent;
using System.Data;
using Microsoft.Data.SqlClient;

namespace EOS.API.Data;

/// <summary>源表选择器跨表 JOIN 目录（来自 TABLES.QUERY_RELATION 受控解析，ADR-008 P3）。</summary>
public sealed record ChooserSourceJoins(
    string SourceTable,
    IReadOnlySet<string> Aliases,
    IReadOnlyList<VirtualJoin> Joins,
    string? Error);

/// <summary>
/// 选择器跨表 JOIN 目录（ADR-008 P3）：以 TABLES.QUERY_RELATION 为权威来源，经
/// <see cref="VirtualExpressionParser.TryParseRelation"/> 严格受控解析（仅 LEFT JOIN + 表.列=表.列 /
/// 常量条件），解析失败的源表整体 fail-closed（选择器不跨表）。解析结果进程内缓存，
/// QUERY_RELATION 变更需重启 API 生效（与 Definition 快照语义一致）。
/// </summary>
public static class ChooserJoinCatalog
{
    private static readonly ConcurrentDictionary<string, Lazy<Task<ChooserSourceJoins>>> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static async Task<ChooserSourceJoins> GetAsync(
        SqlConnection connection,
        string sourceTable,
        CancellationToken token)
    {
        var lazy = Cache.GetOrAdd(sourceTable, _ => new Lazy<Task<ChooserSourceJoins>>(
            () => LoadAsync(connection, sourceTable, token)));
        return await lazy.Value.ConfigureAwait(false);
    }

    private static async Task<ChooserSourceJoins> LoadAsync(
        SqlConnection connection,
        string sourceTable,
        CancellationToken token)
    {
        const string sql = """
            SELECT LTRIM(RTRIM(ISNULL(QUERY_RELATION,'')))
            FROM dbo.TABLES WITH (NOLOCK)
            WHERE LTRIM(RTRIM(T_ID))=@Table;
            """;
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@Table", SqlDbType.NVarChar, 100).Value = sourceTable;
        var relation = await command.ExecuteScalarAsync(token) as string;
        if (string.IsNullOrWhiteSpace(relation))
        {
            return new ChooserSourceJoins(sourceTable,
                new HashSet<string>(StringComparer.OrdinalIgnoreCase) { sourceTable },
                [], null);
        }
        if (!VirtualExpressionParser.TryParseRelation(relation, sourceTable, out var joins, out var error))
        {
            return new ChooserSourceJoins(sourceTable,
                new HashSet<string>(StringComparer.OrdinalIgnoreCase) { sourceTable },
                [], error);
        }
        var aliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { sourceTable };
        foreach (var join in joins)
        {
            aliases.Add(join.Alias);
        }
        return new ChooserSourceJoins(sourceTable, aliases, joins, null);
    }

    /// <summary>
    /// 由编译结果引用的跨表别名重建受控 JOIN 段（依赖闭包：仅选取解析依赖链上的 JOIN，
    /// 避免整条 QUERY_RELATION 造成行放大）。引用别名无法覆盖时返回 null（fail-closed）。
    /// </summary>
    public static string? BuildJoinClause(ChooserSourceJoins catalog, IReadOnlyCollection<string> referencedAliases)
    {
        if (referencedAliases.Count == 0)
        {
            return null;
        }
        // 反向依赖：被引用别名 + 其 ON 条件引用的上游表（到不动点），只选依赖链上的 JOIN
        var needed = new HashSet<string>(referencedAliases, StringComparer.OrdinalIgnoreCase);
        var pending = true;
        while (pending)
        {
            pending = false;
            foreach (var join in catalog.Joins)
            {
                if (!needed.Contains(join.Alias))
                {
                    continue;
                }
                foreach (var condition in join.Conditions)
                {
                    pending |= needed.Add(condition.LeftTable);
                    pending |= needed.Add(condition.RightTable);
                }
                foreach (var constant in join.Constants)
                {
                    pending |= needed.Add(constant.Table);
                }
            }
        }
        var selected = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { catalog.SourceTable };
        var chosen = new List<VirtualJoin>();
        var remaining = new List<VirtualJoin>(catalog.Joins.Where(join => needed.Contains(join.Alias)));
        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var join in remaining.ToList())
            {
                // JOIN 自身 ON 条件必然引用其别名：候选集 = 已选表 ∪ 本 JOIN 别名
                var candidates = new HashSet<string>(selected, StringComparer.OrdinalIgnoreCase) { join.Alias };
                var conditionsResolvable = join.Conditions.All(c =>
                    candidates.Contains(c.LeftTable) && candidates.Contains(c.RightTable));
                var constantsResolvable = join.Constants.All(c => candidates.Contains(c.Table));
                if (!conditionsResolvable || !constantsResolvable)
                {
                    continue;
                }
                selected.Add(join.Alias);
                chosen.Add(join);
                remaining.Remove(join);
                changed = true;
            }
        }
        if (!referencedAliases.All(alias => selected.Contains(alias)))
        {
            return null;
        }
        if (chosen.Count == 0)
        {
            return null;
        }
        var parts = new List<string>();
        foreach (var join in chosen)
        {
            var conditions = join.Conditions
                .Select(c => $"[{c.LeftTable}].[{c.LeftColumn}]=[{c.RightTable}].[{c.RightColumn}]")
                .Concat(join.Constants
                    .Select(c => c.IsString
                        ? $"[{c.Table}].[{c.Column}]=N'{c.Literal.Replace("'", "''")}'"
                        : $"[{c.Table}].[{c.Column}]={c.Literal}"));
            parts.Add($"LEFT JOIN dbo.[{join.Table}] [{join.Alias}] WITH (NOLOCK) ON {string.Join(" AND ", conditions)}");
        }
        return string.Join(" ", parts);
    }
}
