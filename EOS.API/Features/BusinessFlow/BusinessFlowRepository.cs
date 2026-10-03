using System.Data;
using EOS.API.Data;
using Microsoft.Data.SqlClient;

namespace EOS.API.Features.BusinessFlow;

public sealed record BusinessFlowDomain(
    int RootIdx,
    string Name,
    int TableCount,
    int InternalEdgeCount,
    int OutEdgeCount,
    int InEdgeCount,
    // 登记关系边里「引用方在本域」的条数（方向约定见 BusinessFlowEdge 的注释）。
    int RelationEdgeCount);

/// <summary>
/// 域间连线。<see cref="EdgeCount"/> 是字段引用边数（既有口径），
/// <see cref="RelationEdgeCount"/> 是已登记单据关系边数——两者出自不同数据源、互不重叠，故分开计数。
/// </summary>
public sealed record BusinessFlowDomainLink(int FromRootIdx, int ToRootIdx, int EdgeCount, int RelationEdgeCount);

public sealed record BusinessFlowOverview(
    IReadOnlyList<BusinessFlowDomain> Domains,
    IReadOnlyList<BusinessFlowDomainLink> Links);

/// <summary>
/// 域内图上的一个节点（一张表）。
///
/// <c>ModuleId</c> / <c>RouteUrl</c> 是"点进去办业务"的入口：指向该表对应、且**路由真的能打开**的
/// 那个模块。路由由服务端按与菜单同一套白名单（<see cref="ModuleRouteValidator"/>）解析好下发，
/// 前端直接跳转、不自己拼 URL；能不能进由统一工作台自己的权限门把关。
/// </summary>
public sealed record BusinessFlowNode(
    string TableId,
    string TableName,
    string ModuleName,
    bool InDomain,
    int Degree,
    int SelfReferenceCount,
    int? ModuleId,
    string? RouteUrl);

/// <summary>
/// 域内图上的一条边，方向统一为「<c>FromTable</c> 上游（被引用）→ <c>ToTable</c> 下游（引用方）」。
///
/// 两类事实合成一条边，避免同一对表画出两条重叠的线：
/// <list type="bullet">
/// <item><c>Fields</c>/<c>FieldCount</c>：来自 FIELD_DATASOURCE 的字段取值来源（"这个字段的值取自那张表"）。</item>
/// <item><c>Registered</c>/<c>RelationName</c>/<c>KeyPairs</c>：来自 FIELD_RELATION 的已登记单据关系
/// （"本单用哪些键列定位到目标单"）。它由登记工具从业务动作的定位键派生，有
/// <c>scripts/verify-field-relation-edges.ps1</c> 全量核验。</item>
/// </list>
///
/// <c>DownstreamInDomain</c> / <c>UpstreamInDomain</c> 标出这条边的哪一端落在当前域里。
/// 返回的边集包含「任意一端在本域」的全部边，前端默认只画 <c>DownstreamInDomain</c> 的那些
/// （即"本域的字段/动作引用了谁"），打开上游视角后再把 <c>UpstreamInDomain</c> 的也画出来
/// （即"本域的表被谁引用"）——后者只能由服务端返回，前端无法从下游视图里推出来。
/// </summary>
public sealed record BusinessFlowEdge(
    string FromTable,
    string ToTable,
    string Kind,
    bool Registered,
    string? RelationName,
    int FieldCount,
    IReadOnlyList<string> Fields,
    IReadOnlyList<string> KeyPairs,
    bool DownstreamInDomain,
    bool UpstreamInDomain);

public sealed record BusinessFlowDomainGraph(
    int RootIdx,
    string Name,
    IReadOnlyList<BusinessFlowNode> Nodes,
    IReadOnlyList<BusinessFlowEdge> Edges);

/// <summary>
/// 业务流程图数据源：把两张表承载的**表间关系事实**按业务域聚合后交给前端作图。
///
/// 两个数据源互补且不重叠——没有任何一对表同时出现在两个源里：
/// <list type="bullet">
/// <item>FIELD_DATASOURCE（字段数据来源）：量大、含大量"选基础资料"，语义是"字段取值来自谁"。</item>
/// <item>FIELD_RELATION（已登记单据关系，RELATION_KIND=EFFECT）：量小、集中在业务域，
/// 语义是"单据之间用键列关联到谁"，是真正意义上的单据上下游。</item>
/// </list>
///
/// 业务域 = 表的归属模块的根模块（MODULES.M_ROOT_IDX）；归属同时认主表与明细表
/// （单据的明细表要落到它所属单据的域里），无归属的表统一落在 <see cref="UnassignedRootIdx"/>。
///
/// 边的分类不依赖人工维护清单：两端同域即域内流程，跨域即跨域引用。
/// 既有元数据里没有「主档 / 单据」标记，任何按表名或引用频次的自动判据都会误判，
/// 故只采用这条完全由既有元数据推导、无需维护的判据。
/// </summary>
public sealed class BusinessFlowRepository(DbConnectionFactory connections, ILogger<BusinessFlowRepository> logger)
{
    /// <summary>未归属任何模块的表所在域的编号（真实模块号均为正数）。</summary>
    private const int UnassignedRootIdx = 0;

    private const string UnassignedName = "未归属";

    // 表 → 业务域。同一张表被多个模块引用时取编号最小的根模块，保证结果稳定可复现。
    private const string TableDomainCte = """
        WITH TableDomain AS (
            SELECT t.T_ID, MIN(m.M_ROOT_IDX) AS ROOT_IDX
            FROM dbo.TABLES t WITH (NOLOCK)
            LEFT JOIN dbo.MODULES m WITH (NOLOCK) ON m.MASTER_TABLE = t.T_ID OR m.DETAIL_TABLE = t.T_ID
            GROUP BY t.T_ID
        )
        """;

    // 去重后的「上游来源表 → 下游字段所属表」；同一对表被多个字段引用只算一条边。
    private const string EdgeCte = """
        , Edge AS (
            SELECT DISTINCT d.SOURCE_T_ID AS UP_TABLE, d.T_ID AS DOWN_TABLE
            FROM dbo.FIELD_DATASOURCE d WITH (NOLOCK)
        )
        , Resolved AS (
            SELECT e.UP_TABLE, e.DOWN_TABLE,
                   ISNULL(ud.ROOT_IDX, 0) AS UP_ROOT,
                   ISNULL(dd.ROOT_IDX, 0) AS DOWN_ROOT
            FROM Edge e
            LEFT JOIN TableDomain ud ON ud.T_ID = e.UP_TABLE
            LEFT JOIN TableDomain dd ON dd.T_ID = e.DOWN_TABLE
        )
        """;

    // 已登记单据关系（FIELD_RELATION，RELATION_KIND=EFFECT）。
    //
    // 方向必须**反转**才能与 FIELD_DATASOURCE 同向。表里存的是「本单 → 定位目标表」：
    // FROM_TABLE 是执行动作的本单（SOURCE_SCOPE=MASTER/DETAIL/TABLE 那一侧），TO_TABLE 是它要
    // 定位/回写的目标单据——MODULE_BUSINESS_ACTION_OP 的保存 lint 与 scripts/verify-field-relation-edges.ps1
    // 都按 toTable == 动作的 TARGET_TABLE 校验。而本图统一按「上游（被引用）→ 下游（引用方）」画，
    // 于是把 TO_TABLE 当上游、FROM_TABLE 当下游：目标单是既有单据，本单由它派生。
    //
    // 反转的依据是两个独立数据源互证：这些登记边与 FIELD_DATASOURCE 推出的方向大量一致、
    // 无一冲突；若按正向作图，两者会在同一对表上给出相反的箭头。
    private const string RelationEdgeCte = """
        , REdge AS (
            SELECT DISTINCT r.TO_TABLE AS UP_TABLE, r.FROM_TABLE AS DOWN_TABLE
            FROM dbo.FIELD_RELATION r WITH (NOLOCK)
            WHERE r.RELATION_KIND = N'EFFECT' AND r.TO_TABLE <> r.FROM_TABLE
        )
        """;

    private const string RelationResolvedCte = """
        , RResolved AS (
            SELECT ISNULL(ud.ROOT_IDX, 0) AS UP_ROOT,
                   ISNULL(dd.ROOT_IDX, 0) AS DOWN_ROOT
            FROM REdge re
            LEFT JOIN TableDomain ud ON ud.T_ID = re.UP_TABLE
            LEFT JOIN TableDomain dd ON dd.T_ID = re.DOWN_TABLE
        )
        """;

    public async Task<BusinessFlowOverview> GetOverviewAsync(CancellationToken token)
    {
        // CTE 只对紧随其后的那一条语句有效，而这里要一次拿五个结果集，
        // 所以每条语句各自带上自己的 CTE——共用一份的话，从第二个结果集起就会报"对象名无效"。
        const string sql =
            TableDomainCte + EdgeCte + """
            SELECT ISNULL(td.ROOT_IDX, 0), ISNULL(r.M_DESC, N'未归属'), COUNT(*)
            FROM TableDomain td
            LEFT JOIN dbo.MODULES r WITH (NOLOCK) ON r.M_IDX = td.ROOT_IDX
            GROUP BY ISNULL(td.ROOT_IDX, 0), r.M_DESC
            ORDER BY ISNULL(td.ROOT_IDX, 0);
            """
            + TableDomainCte + EdgeCte + """
            SELECT UP_ROOT, DOWN_ROOT, COUNT(*)
            FROM Resolved
            WHERE UP_ROOT <> DOWN_ROOT
            GROUP BY UP_ROOT, DOWN_ROOT
            ORDER BY UP_ROOT, DOWN_ROOT;
            """
            + TableDomainCte + EdgeCte + """
            SELECT UP_ROOT, COUNT(*)
            FROM Resolved
            WHERE UP_ROOT = DOWN_ROOT
            GROUP BY UP_ROOT;
            """
            + TableDomainCte + RelationEdgeCte + RelationResolvedCte + """
            SELECT UP_ROOT, DOWN_ROOT, COUNT(*)
            FROM RResolved
            WHERE UP_ROOT <> DOWN_ROOT
            GROUP BY UP_ROOT, DOWN_ROOT
            ORDER BY UP_ROOT, DOWN_ROOT;
            """
            + TableDomainCte + RelationEdgeCte + RelationResolvedCte + """
            SELECT DOWN_ROOT, COUNT(*)
            FROM RResolved
            GROUP BY DOWN_ROOT;
            """;

        var names = new Dictionary<int, string>();
        var tableCounts = new Dictionary<int, int>();
        var internalCounts = new Dictionary<int, int>();
        var outCounts = new Dictionary<int, int>();
        var inCounts = new Dictionary<int, int>();
        // 键是「上游域 → 下游域」，两类边的计数各自累加；同一对域可能只出现在其中一个数据源里。
        var links = new Dictionary<(int Up, int Down), (int Edges, int Relations)>();
        var relationCounts = new Dictionary<int, int>();

        await using (var connection = connections.Create())
        {
            await using var command = new SqlCommand(sql, connection);
            await connection.OpenAsync(token);
            await using var reader = await command.ExecuteReaderAsync(token);

            while (await reader.ReadAsync(token))
            {
                var rootIdx = reader.GetInt32(0);
                names[rootIdx] = reader.GetString(1).Trim();
                tableCounts[rootIdx] = reader.GetInt32(2);
            }

            if (await reader.NextResultAsync(token))
            {
                while (await reader.ReadAsync(token))
                {
                    var upRoot = reader.GetInt32(0);
                    var downRoot = reader.GetInt32(1);
                    var count = reader.GetInt32(2);
                    // 边的方向按业务流向记：上游来源域 → 下游引用域
                    Accumulate(links, upRoot, downRoot, count, 0);
                    outCounts[upRoot] = outCounts.GetValueOrDefault(upRoot) + count;
                    inCounts[downRoot] = inCounts.GetValueOrDefault(downRoot) + count;
                }
            }

            if (await reader.NextResultAsync(token))
            {
                while (await reader.ReadAsync(token))
                {
                    internalCounts[reader.GetInt32(0)] = reader.GetInt32(1);
                }
            }

            if (await reader.NextResultAsync(token))
            {
                while (await reader.ReadAsync(token))
                {
                    Accumulate(links, reader.GetInt32(0), reader.GetInt32(1), 0, reader.GetInt32(2));
                }
            }

            if (await reader.NextResultAsync(token))
            {
                while (await reader.ReadAsync(token))
                {
                    relationCounts[reader.GetInt32(0)] = reader.GetInt32(1);
                }
            }
        }

        // 域间边可能指向没有登记任何表的域（表不在 TABLES 里），把这类域补进清单，避免前端拿到悬空链接。
        foreach (var rootIdx in links.Keys.SelectMany(key => new[] { key.Up, key.Down }))
        {
            names.TryAdd(rootIdx, rootIdx == UnassignedRootIdx ? UnassignedName : $"模块 {rootIdx}");
            tableCounts.TryAdd(rootIdx, 0);
        }

        var domainLinks = links
            .OrderBy(pair => pair.Key.Up)
            .ThenBy(pair => pair.Key.Down)
            .Select(pair => new BusinessFlowDomainLink(pair.Key.Up, pair.Key.Down, pair.Value.Edges, pair.Value.Relations))
            .ToList();

        var domains = names
            .OrderByDescending(pair => tableCounts.GetValueOrDefault(pair.Key))
            .ThenBy(pair => pair.Key)
            .Select(pair => new BusinessFlowDomain(
                pair.Key,
                pair.Key == UnassignedRootIdx ? UnassignedName : pair.Value,
                tableCounts.GetValueOrDefault(pair.Key),
                internalCounts.GetValueOrDefault(pair.Key),
                outCounts.GetValueOrDefault(pair.Key),
                inCounts.GetValueOrDefault(pair.Key),
                relationCounts.GetValueOrDefault(pair.Key)))
            .ToList();

        logger.LogDebug(
            "业务流程图总览 domains={DomainCount} links={LinkCount} relationLinks={RelationLinkCount}",
            domains.Count,
            domainLinks.Count,
            domainLinks.Count(link => link.RelationEdgeCount > 0));

        return new BusinessFlowOverview(domains, domainLinks);
    }

    /// <summary>把一条域间连线的计数累加进字典（两个数据源各自累加，互不覆盖）。</summary>
    private static void Accumulate(
        Dictionary<(int Up, int Down), (int Edges, int Relations)> links,
        int upRoot,
        int downRoot,
        int edges,
        int relations)
    {
        var current = links.GetValueOrDefault((upRoot, downRoot));
        links[(upRoot, downRoot)] = (current.Edges + edges, current.Relations + relations);
    }

    /// <summary>
    /// 取某个业务域内的明细图。边集收「**任意一端在该域内**」的边，并用
    /// <see cref="BusinessFlowEdge.DownstreamInDomain"/> / <see cref="BusinessFlowEdge.UpstreamInDomain"/>
    /// 标出落在域内的是哪一端：
    /// <list type="bullet">
    /// <item>默认（下游视角）前端只画 <c>DownstreamInDomain</c> 的边——即"本域引用了谁"，同一条关系只在下游域出现一次。</item>
    /// <item>打开上游视角后，<c>UpstreamInDomain</c> 的边也画出来——即"本域被谁引用"，含被域外单据引用
    /// （如销售订单被生产计划引用）。这类边在下游域里看不到，只能由服务端给出。</item>
    /// </list>
    /// 域外表会作为另一端一起带进图里，并用 InDomain=false 标出。
    ///
    /// 域名、域内表清单、引用明细、登记关系明细、表名与模块候选**一次往返**取回。
    /// </summary>
    public async Task<BusinessFlowDomainGraph?> GetDomainGraphAsync(int rootIdx, CancellationToken token)
    {
        const string sql =
            """
            SELECT r.M_DESC FROM dbo.MODULES r WITH (NOLOCK) WHERE r.M_IDX = @RootIdx;
            """
            + TableDomainCte + """
            SELECT td.T_ID FROM TableDomain td WHERE ISNULL(td.ROOT_IDX, 0) = @RootIdx;
            """
            + TableDomainCte + """
            SELECT e.SOURCE_T_ID, e.T_ID, e.F_ID
            FROM dbo.FIELD_DATASOURCE e WITH (NOLOCK)
            WHERE e.T_ID IN (SELECT td.T_ID FROM TableDomain td WHERE ISNULL(td.ROOT_IDX, 0) = @RootIdx)
               OR e.SOURCE_T_ID IN (SELECT td.T_ID FROM TableDomain td WHERE ISNULL(td.ROOT_IDX, 0) = @RootIdx)
            ORDER BY e.T_ID, e.SOURCE_T_ID, e.F_ID;
            """
            + TableDomainCte + """
            SELECT r.FROM_TABLE, r.TO_TABLE, r.FROM_COLUMN, r.TO_COLUMN, ISNULL(r.RELATION_NAME, N'')
            FROM dbo.FIELD_RELATION r WITH (NOLOCK)
            WHERE r.RELATION_KIND = N'EFFECT'
              AND r.TO_TABLE <> r.FROM_TABLE
              AND (r.FROM_TABLE IN (SELECT td.T_ID FROM TableDomain td WHERE ISNULL(td.ROOT_IDX, 0) = @RootIdx)
                   OR r.TO_TABLE IN (SELECT td.T_ID FROM TableDomain td WHERE ISNULL(td.ROOT_IDX, 0) = @RootIdx))
            ORDER BY r.FROM_TABLE, r.TO_TABLE, r.KEY_ORDINAL, r.FROM_COLUMN;
            """
            + TableDomainCte + """
            SELECT t.T_ID, ISNULL(t.T_DESC, N''), ISNULL(td.ROOT_IDX, 0),
                   x.M_IDX, x.M_DESC, x.M_URL, x.RANK_ORDER
            FROM dbo.TABLES t WITH (NOLOCK)
            LEFT JOIN TableDomain td ON td.T_ID = t.T_ID
            OUTER APPLY (
                SELECT m.M_IDX, m.M_DESC, m.M_URL,
                       CASE WHEN m.MASTER_TABLE = t.T_ID THEN 0 ELSE 1 END AS RANK_ORDER
                FROM dbo.MODULES m WITH (NOLOCK)
                WHERE m.MASTER_TABLE = t.T_ID OR m.DETAIL_TABLE = t.T_ID
            ) x;
            """;

        string? domainName = null;
        var domainTableIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var edgeFields = new Dictionary<(string Up, string Down), List<string>>();
        var relations = new Dictionary<(string Up, string Down), RelationEntry>();
        var tableNames = new Dictionary<string, string>();
        // 一张表可能挂着多个模块：先全收下来，之后再挑一个"路由真能打开"的当入口
        var moduleCandidates = new Dictionary<string, List<ModuleCandidate>>();

        await using (var connection = connections.Create())
        {
            await using var command = new SqlCommand(sql, connection);
            command.Parameters.Add("@RootIdx", SqlDbType.Int).Value = rootIdx;
            await connection.OpenAsync(token);
            await using var reader = await command.ExecuteReaderAsync(token);

            if (await reader.ReadAsync(token) && !reader.IsDBNull(0))
                domainName = reader.GetString(0).Trim();

            if (await reader.NextResultAsync(token))
            {
                while (await reader.ReadAsync(token))
                    domainTableIds.Add(reader.GetString(0).Trim());
            }

            if (await reader.NextResultAsync(token))
            {
                while (await reader.ReadAsync(token))
                {
                    var up = reader.GetString(0).Trim();
                    var down = reader.GetString(1).Trim();
                    if (!edgeFields.TryGetValue((up, down), out var fields))
                    {
                        fields = [];
                        edgeFields[(up, down)] = fields;
                    }
                    fields.Add(reader.GetString(2).Trim());
                }
            }

            if (await reader.NextResultAsync(token))
            {
                while (await reader.ReadAsync(token))
                {
                    // 表里存的是「本单 → 定位目标」，本图统一按「上游 → 下游」画，故这里把 TO 当上游。
                    var up = reader.GetString(1).Trim();
                    var down = reader.GetString(0).Trim();
                    if (!relations.TryGetValue((up, down), out var entry))
                    {
                        entry = new RelationEntry();
                        relations[(up, down)] = entry;
                    }
                    entry.AddKeyPair(reader.GetString(2).Trim(), reader.GetString(3).Trim());
                    if (!reader.IsDBNull(4)) entry.AddName(reader.GetString(4).Trim());
                }
            }

            if (await reader.NextResultAsync(token))
            {
                while (await reader.ReadAsync(token))
                {
                    var tableId = reader.GetString(0).Trim();
                    tableNames[tableId] = reader.GetString(1).Trim();
                    if (!reader.IsDBNull(3))
                    {
                        if (!moduleCandidates.TryGetValue(tableId, out var candidates))
                        {
                            candidates = [];
                            moduleCandidates[tableId] = candidates;
                        }
                        candidates.Add(new ModuleCandidate(
                            reader.IsDBNull(6) ? 1 : reader.GetInt32(6),
                            reader.GetInt32(3),
                            reader.IsDBNull(4) ? string.Empty : reader.GetString(4).Trim(),
                            reader.IsDBNull(5) ? null : reader.GetString(5).Trim()));
                    }
                }
            }
        }

        // 无模块归属的表自成一个域，它的名字不来自 MODULES。
        if (rootIdx == UnassignedRootIdx) domainName = UnassignedName;

        // 既不是未归属域、也查不到模块：请求了一个不存在的域，交由上层 404。
        if (domainName is null) return null;

        var edges = new List<BusinessFlowEdge>();
        var degrees = new Dictionary<string, int>();
        var selfReferences = new Dictionary<string, int>();

        // 两类事实合成同一批边：同一对表若既被字段引用、又是已登记单据关系，只出一条边
        // （否则画布上会出现两条完全重叠的线），用 Registered 标出它同时是登记关系。
        var pairs = edgeFields.Keys.Union(relations.Keys).ToList();
        pairs.Sort((left, right) =>
        {
            var byUp = string.CompareOrdinal(left.Up, right.Up);
            return byUp != 0 ? byUp : string.CompareOrdinal(left.Down, right.Down);
        });

        foreach (var (up, down) in pairs)
        {
            var upstreamInDomain = domainTableIds.Contains(up);
            var downstreamInDomain = domainTableIds.Contains(down);
            // 只收「任意一端在本域」的边；两端都在域外的与这个域无关，不进图。
            if (!upstreamInDomain && !downstreamInDomain) continue;

            var fields = edgeFields.GetValueOrDefault((up, down)) ?? [];
            relations.TryGetValue((up, down), out var relation);

            if (up == down)
            {
                selfReferences[down] = selfReferences.GetValueOrDefault(down) + fields.Count;
                continue;
            }

            // internal 必须是两端都在域内。只按"上游在域内"判会在上游视角下把跨域边误标成域内边。
            var kind = upstreamInDomain && downstreamInDomain ? "internal" : "external";
            edges.Add(new BusinessFlowEdge(
                up,
                down,
                kind,
                relation is not null,
                relation?.Name,
                fields.Count,
                fields,
                relation?.Keys ?? [],
                downstreamInDomain,
                upstreamInDomain));

            degrees[up] = degrees.GetValueOrDefault(up) + 1;
            degrees[down] = degrees.GetValueOrDefault(down) + 1;
        }

        var nodes = edges
            .SelectMany(edge => new[] { edge.FromTable, edge.ToTable })
            .Distinct()
            .Select(tableId =>
            {
                var entry = ResolveEntry(moduleCandidates.GetValueOrDefault(tableId));
                return new BusinessFlowNode(
                    tableId,
                    tableNames.GetValueOrDefault(tableId, tableId),
                    entry.ModuleName ?? string.Empty,
                    domainTableIds.Contains(tableId),
                    degrees.GetValueOrDefault(tableId),
                    selfReferences.GetValueOrDefault(tableId),
                    entry.ModuleId,
                    entry.RouteUrl);
            })
            .OrderBy(node => node.TableId)
            .ToList();

        logger.LogDebug(
            "业务流程图域图 rootIdx={RootIdx} nodes={NodeCount} edges={EdgeCount}",
            rootIdx,
            nodes.Count,
            edges.Count);

        return new BusinessFlowDomainGraph(rootIdx, domainName, nodes, edges);
    }

    /// <summary>
    /// 一对表之间的登记关系：键列对（"本单列 → 目标列"，与 FIELD_RELATION 存储顺序一致）
    /// 与语义名。同一对表可能命中多个边组，故两者都按出现顺序去重累积。
    /// </summary>
    private sealed class RelationEntry
    {
        private readonly List<string> names = [];

        public List<string> Keys { get; } = [];

        public string? Name => names.Count == 0 ? null : string.Join(" / ", names);

        public void AddKeyPair(string fromColumn, string toColumn)
        {
            var pair = $"{fromColumn} → {toColumn}";
            if (!Keys.Contains(pair)) Keys.Add(pair);
        }

        public void AddName(string name)
        {
            if (name.Length > 0 && !names.Contains(name)) names.Add(name);
        }
    }

    /// <summary>候选模块：Rank 0 表示这张表是它的主表（比只当明细表更贴近"这个模块的主档"）。</summary>
    private sealed record ModuleCandidate(int Rank, int ModuleId, string ModuleName, string? Url);

    /// <summary>
    /// 挑一个"点进去真能打开"的模块当节点入口：先主表模块、再按编号。
    /// 路由经 <see cref="ModuleRouteValidator"/> 解析（与菜单同一套白名单），解析成兜底页的不算入口，
    /// 否则点了只会落进 /fallback；挑不到就返回空，节点上不给入口——能不能进由统一工作台自己的权限门把关。
    /// </summary>
    private static (int? ModuleId, string? RouteUrl, string? ModuleName) ResolveEntry(
        IReadOnlyList<ModuleCandidate>? candidates)
    {
        if (candidates is null || candidates.Count == 0) return (null, null, null);

        foreach (var candidate in candidates.OrderBy(item => item.Rank).ThenBy(item => item.ModuleId))
        {
            if (string.IsNullOrWhiteSpace(candidate.Url)) continue;
            var route = ModuleRouteValidator.Resolve(candidate.Url, candidate.ModuleId);
            if (route.StartsWith("/fallback/", StringComparison.OrdinalIgnoreCase)) continue;
            return (candidate.ModuleId, route, candidate.ModuleName);
        }
        return (null, null, null);
    }
}
