using System.Data;
using EOS.API.Data;
using Microsoft.Data.SqlClient;

namespace EOS.API.Features.BusinessFlow;

public sealed record BusinessFlowDomain(
    int RootIdx,
    string Name,
    int ModuleCount,
    int InternalEdgeCount,
    int OutEdgeCount,
    int InEdgeCount,
    // 登记关系边里「下游模块在本域」的条数（方向约定见 BusinessFlowModuleEdge 的注释）。
    int RelationEdgeCount);

/// <summary>
/// 域间连线。<see cref="EdgeCount"/> 是字段引用边数（既有口径），
/// <see cref="RelationEdgeCount"/> 是已登记单据关系边数——两者出自不同数据源、互不重叠，故分开计数。
/// </summary>
public sealed record BusinessFlowDomainLink(int FromRootIdx, int ToRootIdx, int EdgeCount, int RelationEdgeCount);

/// <summary>
/// 图上的一个节点：**一个模块**（一份可点进去的单据或业务页），不是一张表。
///
/// <c>ModuleId</c> / <c>RouteUrl</c> 是"点进去办业务"的入口：路由由服务端按与菜单同一套白名单
/// （<see cref="ModuleRouteValidator"/>）解析好下发，前端直接跳转、不自己拼 URL；
/// 能不能进由统一工作台自己的权限门把关。
/// </summary>
public sealed record BusinessFlowModuleNode(
    int ModuleId,
    string ModuleName,
    string MasterTable,
    bool InDomain,
    int Degree,
    int SelfReferenceCount,
    string? RouteUrl);

/// <summary>
/// 图上的一条边，方向统一为「<c>FromModule</c> 上游（被引用）→ <c>ToModule</c> 下游（引用方）」。
///
/// 两类事实合成一条边，避免同一对模块画出两条重叠的线：
/// <list type="bullet">
/// <item><c>Fields</c>/<c>FieldCount</c>：来自 FIELD_DATASOURCE 的字段取值来源（"这个字段的值取自那个模块"）。</item>
/// <item><c>Registered</c>/<c>RelationName</c>/<c>KeyPairs</c>：来自 FIELD_RELATION 的已登记单据关系
/// （"本单用哪些键列定位到目标单"），是真正意义上的单据上下游。</item>
/// </list>
/// 表级证据不丢：<c>TablePairs</c> 给出这条模块关系具体落在哪几对表上。
///
/// <c>DownstreamInDomain</c> / <c>UpstreamInDomain</c> 标出这条边的哪一端落在当前域里。
/// 返回的边集包含「任意一端在本域」的全部边，前端默认只画 <c>DownstreamInDomain</c> 的那些
/// （即"本域引用了谁"），打开上游视角后再把 <c>UpstreamInDomain</c> 的也画出来
/// （即"本域被谁引用"）——后者只能由服务端返回，前端无法从下游视图里推出来。
/// </summary>
public sealed record BusinessFlowModuleEdge(
    int FromModuleId,
    int ToModuleId,
    string Kind,
    bool Registered,
    string? RelationName,
    int FieldCount,
    IReadOnlyList<string> Fields,
    IReadOnlyList<string> KeyPairs,
    IReadOnlyList<string> TablePairs,
    bool DownstreamInDomain,
    bool UpstreamInDomain);

public sealed record BusinessFlowDomainGraph(
    int RootIdx,
    string Name,
    IReadOnlyList<BusinessFlowModuleNode> Nodes,
    IReadOnlyList<BusinessFlowModuleEdge> Edges);

/// <summary>
/// 两个业务域之间的一条模块关系（第三级「模块关系」视图）：上游模块 → 下游模块，
/// 外加这条线为什么存在——字段取值来源明细、表级对账，以及它是否同时是已登记单据关系。
/// 两个来源合成一条记录，与域内图同一口径（同一对模块只出现一次）。
/// </summary>
public sealed record BusinessFlowLinkEdge(
    int FromModuleId,
    int ToModuleId,
    string FromModuleName,
    string ToModuleName,
    bool Registered,
    string? RelationName,
    int FieldCount,
    IReadOnlyList<string> Fields,
    IReadOnlyList<string> KeyPairs,
    IReadOnlyList<string> TablePairs);

/// <summary>
/// 两个业务域之间的全部模块关系：总览图上那条带数字的连线点进去看到的就是它。
///
/// 逐条关系的判据与总览给该连线计数时用的完全一致（上游模块在本域、下游模块在对端域），
/// 但**条数不等于线上两个数字之和**：同一对模块可能既靠字段取值来源、又是已登记单据关系，
/// 而这里按"一对模块一条关系"合并（与域内图同一口径），故行数是两类事实的**并集**。
/// 线上写 "6+3" 而这里 8 条，说明其中 1 条被两类事实同时命中——
/// 那个重合数由服务端如实给出（<c>Registered</c> 且 <c>Fields</c> 非空的条数），界面必须说出口。
/// </summary>
public sealed record BusinessFlowLinkGraph(
    int FromRootIdx,
    string FromName,
    int ToRootIdx,
    string ToName,
    IReadOnlyList<BusinessFlowLinkEdge> Edges);

/// <summary>业务域总览：全部业务域与域间连线。</summary>
public sealed record BusinessFlowOverview(
    IReadOnlyList<BusinessFlowDomain> Domains,
    IReadOnlyList<BusinessFlowDomainLink> Links);

/// <summary>
/// 业务流程图数据源：把两张表承载的**表间关系事实**按「模块」聚合、再按业务域分组后交给前端作图。
///
/// 为什么节点是模块而不是表：这一页要回答的是"单据怎么往下走、模块与模块之间怎么衔接"，
/// 表是实现细节。表级事实不丢——每条模块边都带 <c>TablePairs</c>，细节面板与第三级列表都给出。
///
/// 两个数据源说明的是两件事，但**在同一对模块上会重合**：
/// <list type="bullet">
/// <item>FIELD_DATASOURCE（字段数据来源）：量大、含大量"选基础资料"，语义是"字段取值来自谁"。</item>
/// <item>FIELD_RELATION（已登记单据关系，RELATION_KIND=EFFECT）：量小、集中在业务域，
/// 语义是"单据之间用键列关联到谁"，是真正意义上的单据上下游。</item>
/// </list>
/// 合并意味着"两类事实各自的条数之和"是**边数**、不是**并集大小**。
///
/// 业务域 = 模块的根模块（MODULES.M_ROOT_IDX）。表归到哪个模块由
/// 「有主表则取主表模块、否则取明细表模块」决定；两张表归到同一个模块（同一份单据的主表与明细表）
/// 时不画边——那是单据内部结构，不是模块之间的关系。**没有任何模块承载的表不参与作图**：
/// 本页的轴是模块，不属于任何模块的表没有可归属的节点。
///
/// 边的分类不依赖人工维护清单：两端同域即域内流程，跨域即跨域引用。
/// 既有元数据里没有「主档 / 单据」标记，任何按表名或引用频次的自动判据都会误判，
/// 故只采用这条完全由既有元数据推导、无需维护的判据。
/// </summary>
public sealed class BusinessFlowRepository(DbConnectionFactory connections, ILogger<BusinessFlowRepository> logger)
{
    /// <summary>
    /// 公共 CTE：表 → 承载它的模块，以及模块 → 根模块（业务域）。
    /// 同一张表被多个模块引用时取编号最小的那个，保证结果稳定可复现。
    /// </summary>
    private const string TableModuleCte = """
        WITH MasterModule AS (
            SELECT m.MASTER_TABLE AS T_ID, MIN(m.M_IDX) AS M_IDX
            FROM dbo.MODULES m WITH (NOLOCK)
            WHERE m.MASTER_TABLE IS NOT NULL AND m.MASTER_TABLE <> N''
            GROUP BY m.MASTER_TABLE
        )
        , DetailModule AS (
            SELECT m.DETAIL_TABLE AS T_ID, MIN(m.M_IDX) AS M_IDX
            FROM dbo.MODULES m WITH (NOLOCK)
            WHERE m.DETAIL_TABLE IS NOT NULL AND m.DETAIL_TABLE <> N''
            GROUP BY m.DETAIL_TABLE
        )
        , TableModule AS (
            SELECT t.T_ID, ISNULL(mm.M_IDX, dm.M_IDX) AS M_IDX
            FROM dbo.TABLES t WITH (NOLOCK)
            LEFT JOIN MasterModule mm ON mm.T_ID = t.T_ID
            LEFT JOIN DetailModule dm ON dm.T_ID = t.T_ID
        )
        , ModuleDomain AS (
            SELECT m.M_IDX, ISNULL(m.M_ROOT_IDX, 0) AS ROOT_IDX
            FROM dbo.MODULES m WITH (NOLOCK)
        )
        """;

    // 字段取值来源：表级事实先按"一对表一条"，再归到模块对。
    private const string FieldEdgeCte = """
        , FieldTableEdge AS (
            SELECT DISTINCT d.SOURCE_T_ID AS UP_T, d.T_ID AS DOWN_T
            FROM dbo.FIELD_DATASOURCE d WITH (NOLOCK)
        )
        , FieldModuleEdge AS (
            SELECT DISTINCT um.M_IDX AS UP_M, dm.M_IDX AS DOWN_M
            FROM FieldTableEdge e
            JOIN TableModule um ON um.T_ID = e.UP_T
            JOIN TableModule dm ON dm.T_ID = e.DOWN_T
            WHERE um.M_IDX IS NOT NULL AND dm.M_IDX IS NOT NULL AND um.M_IDX <> dm.M_IDX
        )
        """;

    // 已登记单据关系（FIELD_RELATION，RELATION_KIND=EFFECT）。
    //
    // 方向必须**反转**才能与 FIELD_DATASOURCE 同向。表里存的是「本单 → 定位目标表」：
    // FROM_TABLE 是执行动作的本单（SOURCE_SCOPE=MASTER/DETAIL/TABLE 那一侧），TO_TABLE 是它要
    // 定位/回写的目标单据——MODULE_BUSINESS_ACTION_OP 的保存 lint 按 toTable == 动作的 TARGET_TABLE 校验
    // （原另有 scripts/verify-field-relation-edges.ps1 做同口径全量核验，该脚本已退役，见 scripts/README.md）。
    // 而本图统一按「上游（被引用）→ 下游（引用方）」画，
    // 于是把 TO_TABLE 当上游、FROM_TABLE 当下游：目标单是既有单据，本单由它派生。
    //
    // 反转的依据是两个独立数据源互证：这些登记边与 FIELD_DATASOURCE 推出的方向大量一致、
    // 无一冲突；若按正向作图，两者会在同一对模块上给出相反的箭头。
    private const string RelationEdgeCte = """
        , RelationTableEdge AS (
            SELECT DISTINCT r.TO_TABLE AS UP_T, r.FROM_TABLE AS DOWN_T
            FROM dbo.FIELD_RELATION r WITH (NOLOCK)
            WHERE r.RELATION_KIND = N'EFFECT' AND r.TO_TABLE <> r.FROM_TABLE
        )
        , RelationModuleEdge AS (
            SELECT DISTINCT um.M_IDX AS UP_M, dm.M_IDX AS DOWN_M
            FROM RelationTableEdge e
            JOIN TableModule um ON um.T_ID = e.UP_T
            JOIN TableModule dm ON dm.T_ID = e.DOWN_T
            WHERE um.M_IDX IS NOT NULL AND dm.M_IDX IS NOT NULL AND um.M_IDX <> dm.M_IDX
        )
        """;

    public async Task<BusinessFlowOverview> GetOverviewAsync(CancellationToken token)
    {
        // CTE 只对紧随其后的那一条语句有效，而这里要一次拿五个结果集，
        // 所以每条语句各自带上自己的 CTE——共用一份的话，从第二个结果集起就会报"对象名无效"。
        const string moduleCounts =
            """
            SELECT ISNULL(m.M_ROOT_IDX, 0), ISNULL(r.M_DESC, N'未归属'), COUNT(*)
            FROM dbo.MODULES m WITH (NOLOCK)
            LEFT JOIN dbo.MODULES r WITH (NOLOCK) ON r.M_IDX = m.M_ROOT_IDX
            WHERE m.MASTER_TABLE IS NOT NULL AND m.MASTER_TABLE <> N''
            GROUP BY ISNULL(m.M_ROOT_IDX, 0), r.M_DESC
            ORDER BY ISNULL(m.M_ROOT_IDX, 0);
            """;

        const string sql =
            moduleCounts
            + TableModuleCte + FieldEdgeCte + """
            SELECT umd.ROOT_IDX, dmd.ROOT_IDX, COUNT(*)
            FROM FieldModuleEdge e
            JOIN ModuleDomain umd ON umd.M_IDX = e.UP_M
            JOIN ModuleDomain dmd ON dmd.M_IDX = e.DOWN_M
            WHERE umd.ROOT_IDX <> dmd.ROOT_IDX
            GROUP BY umd.ROOT_IDX, dmd.ROOT_IDX
            ORDER BY umd.ROOT_IDX, dmd.ROOT_IDX;
            """
            + TableModuleCte + FieldEdgeCte + """
            SELECT umd.ROOT_IDX, COUNT(*)
            FROM FieldModuleEdge e
            JOIN ModuleDomain umd ON umd.M_IDX = e.UP_M
            JOIN ModuleDomain dmd ON dmd.M_IDX = e.DOWN_M
            WHERE umd.ROOT_IDX = dmd.ROOT_IDX
            GROUP BY umd.ROOT_IDX;
            """
            + TableModuleCte + RelationEdgeCte + """
            SELECT umd.ROOT_IDX, dmd.ROOT_IDX, COUNT(*)
            FROM RelationModuleEdge e
            JOIN ModuleDomain umd ON umd.M_IDX = e.UP_M
            JOIN ModuleDomain dmd ON dmd.M_IDX = e.DOWN_M
            WHERE umd.ROOT_IDX <> dmd.ROOT_IDX
            GROUP BY umd.ROOT_IDX, dmd.ROOT_IDX
            ORDER BY umd.ROOT_IDX, dmd.ROOT_IDX;
            """
            + TableModuleCte + RelationEdgeCte + """
            SELECT dmd.ROOT_IDX, COUNT(*)
            FROM RelationModuleEdge e
            JOIN ModuleDomain dmd ON dmd.M_IDX = e.DOWN_M
            GROUP BY dmd.ROOT_IDX;
            """;

        var names = new Dictionary<int, string>();
        var moduleCountByRoot = new Dictionary<int, int>();
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
                moduleCountByRoot[rootIdx] = reader.GetInt32(2);
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

        // 域间边可能指向没有登记任何模块的域，把这类域补进清单，避免前端拿到悬空链接。
        foreach (var rootIdx in links.Keys.SelectMany(key => new[] { key.Up, key.Down }))
        {
            names.TryAdd(rootIdx, $"模块 {rootIdx}");
            moduleCountByRoot.TryAdd(rootIdx, 0);
        }

        var domainLinks = links
            .OrderBy(pair => pair.Key.Up)
            .ThenBy(pair => pair.Key.Down)
            .Select(pair => new BusinessFlowDomainLink(pair.Key.Up, pair.Key.Down, pair.Value.Edges, pair.Value.Relations))
            .ToList();

        var domains = names
            .OrderByDescending(pair => moduleCountByRoot.GetValueOrDefault(pair.Key))
            .ThenBy(pair => pair.Key)
            .Select(pair => new BusinessFlowDomain(
                pair.Key,
                pair.Value,
                moduleCountByRoot.GetValueOrDefault(pair.Key),
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
    /// <see cref="BusinessFlowModuleEdge.DownstreamInDomain"/> / <see cref="BusinessFlowModuleEdge.UpstreamInDomain"/>
    /// 标出落在域内的是哪一端：
    /// <list type="bullet">
    /// <item>默认（下游视角）前端只画 <c>DownstreamInDomain</c> 的边——即"本域引用了谁"，同一条关系只在下游域出现一次。</item>
    /// <item>打开上游视角后，<c>UpstreamInDomain</c> 的边也画出来——即"本域被谁引用"，含被域外单据引用
    /// （如销售订单被生产计划引用）。这类边在下游域里看不到，只能由服务端给出。</item>
    /// </list>
    /// 域外模块会作为另一端一起带进图里，并用 InDomain=false 标出。
    ///
    /// 域名、模块清单、字段引用明细、登记关系明细与模块候选**一次往返**取回。
    /// </summary>
    public async Task<BusinessFlowDomainGraph?> GetDomainGraphAsync(int rootIdx, CancellationToken token)
    {
        const string sql =
            """
            SELECT r.M_DESC FROM dbo.MODULES r WITH (NOLOCK) WHERE r.M_IDX = @RootIdx;
            """
            + """
            SELECT m.M_IDX, ISNULL(m.M_DESC, N''), ISNULL(m.M_URL, N''), ISNULL(m.M_ROOT_IDX, 0), ISNULL(m.MASTER_TABLE, N'')
            FROM dbo.MODULES m WITH (NOLOCK);
            """
            + TableModuleCte + """
            SELECT e.SOURCE_T_ID, e.T_ID, e.F_ID, um.M_IDX, dm.M_IDX
            FROM dbo.FIELD_DATASOURCE e WITH (NOLOCK)
            JOIN TableModule um ON um.T_ID = e.SOURCE_T_ID
            JOIN TableModule dm ON dm.T_ID = e.T_ID
            JOIN dbo.MODULES md WITH (NOLOCK) ON md.M_IDX = dm.M_IDX
            WHERE um.M_IDX <> dm.M_IDX AND ISNULL(md.M_ROOT_IDX, 0) = @RootIdx
            UNION
            SELECT e.SOURCE_T_ID, e.T_ID, e.F_ID, um.M_IDX, dm.M_IDX
            FROM dbo.FIELD_DATASOURCE e WITH (NOLOCK)
            JOIN TableModule um ON um.T_ID = e.SOURCE_T_ID
            JOIN TableModule dm ON dm.T_ID = e.T_ID
            JOIN dbo.MODULES mu WITH (NOLOCK) ON mu.M_IDX = um.M_IDX
            WHERE um.M_IDX <> dm.M_IDX AND ISNULL(mu.M_ROOT_IDX, 0) = @RootIdx
            ORDER BY 2, 1, 3;
            """
            + TableModuleCte + """
            SELECT r.FROM_TABLE, r.TO_TABLE, r.FROM_COLUMN, r.TO_COLUMN, ISNULL(r.RELATION_NAME, N''),
                   um.M_IDX, dm.M_IDX
            FROM dbo.FIELD_RELATION r WITH (NOLOCK)
            JOIN TableModule um ON um.T_ID = r.TO_TABLE
            JOIN TableModule dm ON dm.T_ID = r.FROM_TABLE
            JOIN dbo.MODULES md WITH (NOLOCK) ON md.M_IDX = dm.M_IDX
            WHERE r.RELATION_KIND = N'EFFECT'
              AND r.TO_TABLE <> r.FROM_TABLE
              AND um.M_IDX <> dm.M_IDX
              AND ISNULL(md.M_ROOT_IDX, 0) = @RootIdx
            UNION
            SELECT r.FROM_TABLE, r.TO_TABLE, r.FROM_COLUMN, r.TO_COLUMN, ISNULL(r.RELATION_NAME, N''),
                   um.M_IDX, dm.M_IDX
            FROM dbo.FIELD_RELATION r WITH (NOLOCK)
            JOIN TableModule um ON um.T_ID = r.TO_TABLE
            JOIN TableModule dm ON dm.T_ID = r.FROM_TABLE
            JOIN dbo.MODULES mu WITH (NOLOCK) ON mu.M_IDX = um.M_IDX
            WHERE r.RELATION_KIND = N'EFFECT'
              AND r.TO_TABLE <> r.FROM_TABLE
              AND um.M_IDX <> dm.M_IDX
              AND ISNULL(mu.M_ROOT_IDX, 0) = @RootIdx
            ORDER BY 1, 2, 3;
            """
            + TableModuleCte + """
            SELECT tm.M_IDX, COUNT(*)
            FROM dbo.FIELD_DATASOURCE d WITH (NOLOCK)
            JOIN TableModule tm ON tm.T_ID = d.T_ID
            JOIN ModuleDomain md ON md.M_IDX = tm.M_IDX
            WHERE d.SOURCE_T_ID = d.T_ID AND md.ROOT_IDX = @RootIdx
            GROUP BY tm.M_IDX;
            """;

        string? domainName = null;
        var modules = new Dictionary<int, ModuleInfo>();
        var moduleEdges = new Dictionary<(int Up, int Down), EdgeFact>();
        var domainSelfReferences = new Dictionary<int, int>();

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
                {
                    var moduleId = reader.GetInt32(0);
                    modules[moduleId] = new ModuleInfo(
                        moduleId,
                        reader.GetString(1).Trim(),
                        reader.GetString(2).Trim(),
                        reader.GetInt32(3),
                        reader.GetString(4).Trim());
                }
            }

            if (await reader.NextResultAsync(token))
            {
                while (await reader.ReadAsync(token))
                {
                    var upTable = reader.GetString(0).Trim();
                    var downTable = reader.GetString(1).Trim();
                    var fact = GetFact(moduleEdges, reader.GetInt32(3), reader.GetInt32(4));
                    fact.AddField($"{downTable}.{reader.GetString(2).Trim()}");
                    fact.AddTablePair(upTable, downTable);
                }
            }

            if (await reader.NextResultAsync(token))
            {
                while (await reader.ReadAsync(token))
                {
                    // 表里存的是「本单 → 定位目标」，本图统一按「上游 → 下游」画，故这里把 TO 当上游。
                    var fact = GetFact(moduleEdges, reader.GetInt32(5), reader.GetInt32(6));
                    fact.AddKeyPair(reader.GetString(2).Trim(), reader.GetString(3).Trim());
                    if (!reader.IsDBNull(4)) fact.AddName(reader.GetString(4).Trim());
                    fact.AddTablePair(reader.GetString(0).Trim(), reader.GetString(1).Trim());
                }
            }

            if (await reader.NextResultAsync(token))
            {
                while (await reader.ReadAsync(token))
                    domainSelfReferences[reader.GetInt32(0)] = reader.GetInt32(1);
            }
        }

        // 查不到模块：请求了一个不存在的域，交由上层 404。
        if (domainName is null) return null;

        var edges = new List<BusinessFlowModuleEdge>();
        var degrees = new Dictionary<int, int>();

        var pairs = moduleEdges.Keys.ToList();
        pairs.Sort((left, right) => left.Up != right.Up ? left.Up.CompareTo(right.Up) : left.Down.CompareTo(right.Down));

        foreach (var (up, down) in pairs)
        {
            var upstreamInDomain = modules.TryGetValue(up, out var upInfo) && upInfo.RootIdx == rootIdx;
            var downstreamInDomain = modules.TryGetValue(down, out var downInfo) && downInfo.RootIdx == rootIdx;
            // 只收「任意一端在本域」的边；两端都在域外的与这个域无关，不进图。
            if (!upstreamInDomain && !downstreamInDomain) continue;

            var fact = moduleEdges[(up, down)];
            // internal 必须是两端都在域内。只按"上游在域内"判会在上游视角下把跨域边误标成域内边。
            var kind = upstreamInDomain && downstreamInDomain ? "internal" : "external";
            edges.Add(new BusinessFlowModuleEdge(
                up,
                down,
                kind,
                fact.HasRelation,
                fact.RelationName,
                fact.Fields.Count,
                fact.Fields,
                fact.Keys,
                fact.TablePairs,
                downstreamInDomain,
                upstreamInDomain));

            degrees[up] = degrees.GetValueOrDefault(up) + 1;
            degrees[down] = degrees.GetValueOrDefault(down) + 1;
        }

        var nodes = edges
            .SelectMany(edge => new[] { edge.FromModuleId, edge.ToModuleId })
            .Distinct()
            .Select(moduleId =>
            {
                modules.TryGetValue(moduleId, out var info);
                var inDomain = info is not null && info.RootIdx == rootIdx;
                return new BusinessFlowModuleNode(
                    moduleId,
                    info?.Name ?? $"模块 {moduleId}",
                    info?.MasterTable ?? string.Empty,
                    inDomain,
                    degrees.GetValueOrDefault(moduleId),
                    domainSelfReferences.GetValueOrDefault(moduleId),
                    ResolveRoute(info));
            })
            .OrderBy(node => node.ModuleId)
            .ToList();

        logger.LogDebug(
            "业务流程图域图 rootIdx={RootIdx} nodes={NodeCount} edges={EdgeCount}",
            rootIdx,
            nodes.Count,
            edges.Count);

        return new BusinessFlowDomainGraph(rootIdx, domainName, nodes, edges);
    }

    /// <summary>
    /// 取两个业务域之间的全部模块关系（第三级视图）。每条关系的过滤判据与总览给
    /// 「这两个域之间的连线」计数时用的完全一致——上游模块属于 <paramref name="fromRootIdx"/>、
    /// 下游模块属于 <paramref name="toRootIdx"/>，登记关系同样先把 TO_TABLE 当上游再过滤。
    /// 但返回的是**合并后**的关系：同一对模块在两类事实都成立时只出一条（<c>Registered</c> 标出），
    /// 故条数是并集、可能小于线上两个数字之和（见 <see cref="BusinessFlowLinkGraph"/>）。
    /// 域编号解析不出模块名时返回 null，交由上层 404。
    /// </summary>
    public async Task<BusinessFlowLinkGraph?> GetDomainLinkGraphAsync(int fromRootIdx, int toRootIdx, CancellationToken token)
    {
        const string sql =
            """
            SELECT r.M_DESC FROM dbo.MODULES r WITH (NOLOCK) WHERE r.M_IDX = @FromRootIdx;
            """
            + """
            SELECT r.M_DESC FROM dbo.MODULES r WITH (NOLOCK) WHERE r.M_IDX = @ToRootIdx;
            """
            + """
            SELECT m.M_IDX, ISNULL(m.M_DESC, N'')
            FROM dbo.MODULES m WITH (NOLOCK);
            """
            + TableModuleCte + """
            SELECT e.SOURCE_T_ID, e.T_ID, e.F_ID, um.M_IDX, dm.M_IDX
            FROM dbo.FIELD_DATASOURCE e WITH (NOLOCK)
            JOIN TableModule um ON um.T_ID = e.SOURCE_T_ID
            JOIN TableModule dm ON dm.T_ID = e.T_ID
            JOIN dbo.MODULES md WITH (NOLOCK) ON md.M_IDX = dm.M_IDX
            WHERE um.M_IDX <> dm.M_IDX
              AND ISNULL(md.M_ROOT_IDX, 0) = @ToRootIdx
              AND EXISTS (SELECT 1 FROM dbo.MODULES mu WITH (NOLOCK)
                          WHERE mu.M_IDX = um.M_IDX AND ISNULL(mu.M_ROOT_IDX, 0) = @FromRootIdx)
            ORDER BY e.T_ID, e.SOURCE_T_ID, e.F_ID
            OPTION (RECOMPILE);
            """
            + TableModuleCte + """
            SELECT r.FROM_TABLE, r.TO_TABLE, r.FROM_COLUMN, r.TO_COLUMN, ISNULL(r.RELATION_NAME, N''),
                   um.M_IDX, dm.M_IDX
            FROM dbo.FIELD_RELATION r WITH (NOLOCK)
            JOIN TableModule um ON um.T_ID = r.TO_TABLE
            JOIN TableModule dm ON dm.T_ID = r.FROM_TABLE
            JOIN dbo.MODULES md WITH (NOLOCK) ON md.M_IDX = dm.M_IDX
            WHERE r.RELATION_KIND = N'EFFECT'
              AND r.TO_TABLE <> r.FROM_TABLE
              AND um.M_IDX <> dm.M_IDX
              AND ISNULL(md.M_ROOT_IDX, 0) = @ToRootIdx
              AND EXISTS (SELECT 1 FROM dbo.MODULES mu WITH (NOLOCK)
                          WHERE mu.M_IDX = um.M_IDX AND ISNULL(mu.M_ROOT_IDX, 0) = @FromRootIdx)
            ORDER BY r.FROM_TABLE, r.TO_TABLE, r.FROM_COLUMN
            OPTION (RECOMPILE);
            """;

        string? fromName = null;
        string? toName = null;
        var moduleNames = new Dictionary<int, string>();
        var moduleEdges = new Dictionary<(int Up, int Down), EdgeFact>();

        await using (var connection = connections.Create())
        {
            await using var command = new SqlCommand(sql, connection);
            command.Parameters.Add("@FromRootIdx", SqlDbType.Int).Value = fromRootIdx;
            command.Parameters.Add("@ToRootIdx", SqlDbType.Int).Value = toRootIdx;
            await connection.OpenAsync(token);
            await using var reader = await command.ExecuteReaderAsync(token);

            if (await reader.ReadAsync(token) && !reader.IsDBNull(0))
                fromName = reader.GetString(0).Trim();

            if (await reader.NextResultAsync(token) && await reader.ReadAsync(token) && !reader.IsDBNull(0))
                toName = reader.GetString(0).Trim();

            if (await reader.NextResultAsync(token))
            {
                while (await reader.ReadAsync(token))
                    moduleNames[reader.GetInt32(0)] = reader.GetString(1).Trim();
            }

            if (await reader.NextResultAsync(token))
            {
                while (await reader.ReadAsync(token))
                {
                    var upTable = reader.GetString(0).Trim();
                    var downTable = reader.GetString(1).Trim();
                    var fact = GetFact(moduleEdges, reader.GetInt32(3), reader.GetInt32(4));
                    fact.AddField($"{downTable}.{reader.GetString(2).Trim()}");
                    fact.AddTablePair(upTable, downTable);
                }
            }

            if (await reader.NextResultAsync(token))
            {
                while (await reader.ReadAsync(token))
                {
                    var fact = GetFact(moduleEdges, reader.GetInt32(5), reader.GetInt32(6));
                    fact.AddKeyPair(reader.GetString(2).Trim(), reader.GetString(3).Trim());
                    if (!reader.IsDBNull(4)) fact.AddName(reader.GetString(4).Trim());
                    fact.AddTablePair(reader.GetString(0).Trim(), reader.GetString(1).Trim());
                }
            }
        }

        // 任一端的域解析不出模块：请求了不存在的域，交由上层 404。
        if (fromName is null || toName is null) return null;

        var pairs = moduleEdges.Keys.ToList();
        pairs.Sort((left, right) => left.Up != right.Up ? left.Up.CompareTo(right.Up) : left.Down.CompareTo(right.Down));

        var edges = new List<BusinessFlowLinkEdge>(pairs.Count);
        foreach (var (up, down) in pairs)
        {
            var fact = moduleEdges[(up, down)];
            edges.Add(new BusinessFlowLinkEdge(
                up,
                down,
                moduleNames.GetValueOrDefault(up, $"模块 {up}"),
                moduleNames.GetValueOrDefault(down, $"模块 {down}"),
                fact.HasRelation,
                fact.RelationName,
                fact.Fields.Count,
                fact.Fields,
                fact.Keys,
                fact.TablePairs));
        }

        logger.LogDebug(
            "业务流程图域间明细从={FromRootIdx} 到={ToRootIdx} edges={EdgeCount}",
            fromRootIdx,
            toRootIdx,
            edges.Count);

        return new BusinessFlowLinkGraph(fromRootIdx, fromName, toRootIdx, toName, edges);
    }

    private static EdgeFact GetFact(Dictionary<(int Up, int Down), EdgeFact> edges, int up, int down)
    {
        if (!edges.TryGetValue((up, down), out var fact))
        {
            fact = new EdgeFact();
            edges[(up, down)] = fact;
        }
        return fact;
    }

    /// <summary>
    /// 模块入口：按与菜单同一套白名单解析路由，解析成兜底页的不算入口，否则点了只会落进 /fallback；
    /// 解析不出就不给入口——能不能进由统一工作台自己的权限门把关。
    /// </summary>
    private static string? ResolveRoute(ModuleInfo? info)
    {
        if (info is null || string.IsNullOrWhiteSpace(info.Url)) return null;
        var route = ModuleRouteValidator.Resolve(info.Url, info.ModuleId);
        return route.StartsWith("/fallback/", StringComparison.OrdinalIgnoreCase) ? null : route;
    }

    /// <summary>
    /// 一对模块之间的两类事实：字段取值来源（含具体字段名）与已登记单据关系（含键列对与语义名），
    /// 外加这两类事实各自落到哪几对表上。同一对模块可能命中多个边组，故都按出现顺序去重累积。
    /// </summary>
    private sealed class EdgeFact
    {
        private readonly List<string> names = [];

        public List<string> Fields { get; } = [];

        public List<string> Keys { get; } = [];

        public List<string> TablePairs { get; } = [];

        public string? RelationName => names.Count == 0 ? null : string.Join(" / ", names);

        public bool HasRelation { get; private set; }

        public void AddField(string field)
        {
            if (!Fields.Contains(field)) Fields.Add(field);
        }

        public void AddTablePair(string upTable, string downTable)
        {
            var pair = $"{upTable} → {downTable}";
            if (!TablePairs.Contains(pair)) TablePairs.Add(pair);
        }

        public void AddKeyPair(string fromColumn, string toColumn)
        {
            HasRelation = true;
            var pair = $"{fromColumn} → {toColumn}";
            if (!Keys.Contains(pair)) Keys.Add(pair);
        }

        public void AddName(string name)
        {
            if (name.Length > 0 && !names.Contains(name)) names.Add(name);
        }
    }

    /// <summary>模块的基本信息：显示名、菜单路由、所属根模块，以及它承载的主表。</summary>
    private sealed record ModuleInfo(int ModuleId, string Name, string Url, int RootIdx, string MasterTable);
}
