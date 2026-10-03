using EOS.API.Data;
using EOS.API.Features.BusinessFlow;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 业务流程图数据源集成测试：直连 EOS.ERP 验证「业务域总览」与「域内明细图」两段**多结果集** SQL 能真的跑通。
///
/// 为什么必须真库跑：这两段各含五个结果集，而 **SQL Server 的 CTE 只对紧随其后的那一条语句有效**——
/// 多个结果集共用一份 CTE 时只有第一个能执行，第二个起报「对象名无效」。这类缺陷编译、静态门禁、
/// 依赖 SQL 文本的单测都发现不了（它们不解析 T-SQL 作用域），只有把语句真的交给库才暴露。
///
/// 连接串取自 env MSSQL_ERP_CONN，拿不到时跳过。只读查询，不产生测试数据。
/// </summary>
[Trait("Category", "Integration")]
public sealed class BusinessFlowRepositoryLiveTests
{
    private static readonly Lazy<string?> ConnectionString = new(() => Environment.GetEnvironmentVariable("MSSQL_ERP_CONN"));

    private readonly BusinessFlowRepository _repository;

    public BusinessFlowRepositoryLiveTests()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:ErpDatabase"] = ConnectionString.Value,
            })
            .Build();
        _repository = new BusinessFlowRepository(new DbConnectionFactory(config), NullLogger<BusinessFlowRepository>.Instance);
    }

    /// <summary>
    /// 总览要同时拿到五个结果集（域清单 / 域间引用 / 域内流程数 / 域间登记关系 / 各域登记关系数），
    /// 缺一个都会让图表残缺——尤其是后两个：少了它们，域间连线就只剩"选基础资料"那一类。
    ///
    /// 未归属域（编号 0）必须在场：表不属于任何模块时 <c>MIN(M_ROOT_IDX)</c> 是 NULL，
    /// 未归一成 0 会让读取器直接抛 <c>SqlNullValueException</c>。
    /// </summary>
    [Fact]
    public async Task GetOverviewAsync_五个结果集都能取到()
    {
        if (ConnectionString.Value is null) return;

        var overview = await _repository.GetOverviewAsync(CancellationToken.None);

        Assert.NotEmpty(overview.Domains);
        Assert.True(overview.Domains.Count > 10, $"业务域数量异常偏少：{overview.Domains.Count}");
        Assert.Contains(overview.Domains, domain => domain.RootIdx == 0 && domain.Name == "未归属");
        Assert.Contains(overview.Domains, domain => domain.TableCount > 0 && domain.InternalEdgeCount > 0);
        Assert.NotEmpty(overview.Links);
        Assert.All(overview.Domains, domain => Assert.True(domain.TableCount >= 0));
        // 两个数据源的计数各自独立统计，不能被其中一个覆盖掉（这正是分成两列的原因）
        Assert.Contains(overview.Domains, domain => domain.RelationEdgeCount > 0);
        Assert.Contains(overview.Links, link => link.RelationEdgeCount > 0);
        Assert.Contains(overview.Links, link => link.EdgeCount > 0);
    }

    /// <summary>
    /// 域内明细图：挑一个确定有引用关系的域，五个结果集都要能取到
    /// （域名 / 域内表清单 / 引用明细 / 登记关系明细 / 表名与模块候选）。
    /// </summary>
    [Fact]
    public async Task GetDomainGraphAsync_域内明细图可构建()
    {
        if (ConnectionString.Value is null) return;

        var overview = await _repository.GetOverviewAsync(CancellationToken.None);
        var domain = overview.Domains
            .Where(item => item.TableCount > 0 && item.InternalEdgeCount > 0)
            .OrderByDescending(item => item.InternalEdgeCount)
            .First();

        var graph = await _repository.GetDomainGraphAsync(domain.RootIdx, CancellationToken.None);

        Assert.NotNull(graph);
        Assert.Equal(domain.RootIdx, graph!.RootIdx);
        Assert.False(string.IsNullOrWhiteSpace(graph.Name));
        Assert.NotEmpty(graph.Nodes);
        Assert.NotEmpty(graph.Edges);
        // 入口：至少有一部分节点能点进统一工作台——路由由服务端按菜单同一套白名单解析，解析不出兜底页。
        var entryNodes = graph.Nodes.Where(node => node.RouteUrl is not null && node.ModuleId is not null).ToList();
        Assert.NotEmpty(entryNodes);
        Assert.All(entryNodes, node => Assert.DoesNotContain("/fallback/", node.RouteUrl!, StringComparison.OrdinalIgnoreCase));
        // 两端都在域内的才是域内流程；有一端在域外的按跨域引用画
        Assert.Contains(graph.Edges, edge => edge.Kind == "internal");
        // 边集收「任意一端在本域」，且每条边都要标出端点在不在域内——前端靠它区分下游视角与上游视角
        Assert.All(graph.Edges, edge => Assert.True(edge.DownstreamInDomain || edge.UpstreamInDomain));
    }

    /// <summary>
    /// 上游视角的数据只能由服务端给出：这类边（本域的表被域外引用）落在别的域的下游视图里，
    /// 前端无法从默认视图翻转出来。若它们全都取不到，"上游视角"就是个没有效果的开关。
    /// </summary>
    [Fact]
    public async Task GetDomainGraphAsync_能取到被域外引用的边()
    {
        if (ConnectionString.Value is null) return;

        var overview = await _repository.GetOverviewAsync(CancellationToken.None);
        var candidates = overview.Domains
            .Where(item => item.RelationEdgeCount > 0)
            .OrderByDescending(item => item.RelationEdgeCount)
            .Take(5)
            .ToList();
        Assert.NotEmpty(candidates);

        var foundUpstreamOnly = false;
        foreach (var domain in candidates)
        {
            var graph = await _repository.GetDomainGraphAsync(domain.RootIdx, CancellationToken.None);
            Assert.NotNull(graph);
            foundUpstreamOnly |= graph!.Edges.Any(edge => edge.UpstreamInDomain && !edge.DownstreamInDomain);
        }

        Assert.True(foundUpstreamOnly, "登记关系最多的几个域里都没有「本域被域外引用」的边——上游视角会是个空开关。");
    }

    /// <summary>
    /// 登记关系边要带出"靠哪些键列关联"：这是它相对字段引用边多出来的信息，
    /// 缺了它就只剩"有这条线"而说不出为什么。
    /// </summary>
    [Fact]
    public async Task GetDomainGraphAsync_登记关系边带键列()
    {
        if (ConnectionString.Value is null) return;

        var overview = await _repository.GetOverviewAsync(CancellationToken.None);
        var domain = overview.Domains
            .Where(item => item.RelationEdgeCount > 0)
            .OrderByDescending(item => item.RelationEdgeCount)
            .First();

        var graph = await _repository.GetDomainGraphAsync(domain.RootIdx, CancellationToken.None);
        var registered = graph!.Edges.Where(edge => edge.Registered).ToList();

        Assert.NotEmpty(registered);
        Assert.All(registered, edge => Assert.NotEmpty(edge.KeyPairs));
        // 同一对表只出一条边：既是字段引用又是登记关系时，两种信息落在同一条边上而不是画两条重叠的线
        Assert.All(registered, edge => Assert.True(edge.DownstreamInDomain || edge.UpstreamInDomain));
        Assert.Equal(
            registered.Count,
            registered.Select(edge => (edge.FromTable, edge.ToTable)).Distinct().Count());
    }

    /// <summary>未归属域（编号 0）没有对应的 MODULES 行，名称必须由服务端兜底给出，不能落到 404。</summary>
    [Fact]
    public async Task GetDomainGraphAsync_未归属域可打开()
    {
        if (ConnectionString.Value is null) return;

        var graph = await _repository.GetDomainGraphAsync(0, CancellationToken.None);

        Assert.NotNull(graph);
        Assert.Equal("未归属", graph!.Name);
    }

    /// <summary>查不到模块编号的域要说"不存在"，交给控制器 404，而不是返回一个空图。</summary>
    [Fact]
    public async Task GetDomainGraphAsync_不存在的域返回null()
    {
        if (ConnectionString.Value is null) return;

        Assert.Null(await _repository.GetDomainGraphAsync(999_999, CancellationToken.None));
    }
}
