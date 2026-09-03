using EOS.API.Data;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// ChooserJoinCatalog.BuildJoinClause 闭包单测。
/// 回归背景：原实现要求 ON 条件两侧都已在选中集合内，而 JOIN 自身 ON 必然引用
/// 其别名（base.col=alias.col），导致任何跨表 JOIN 永远构建失败、运行期跨表过滤全量 fail-closed。
/// </summary>
public sealed class ChooserJoinCatalogTests
{
    private static VirtualJoinCondition C(string lt, string lc, string rt, string rc) => new(lt, lc, rt, rc);

    private static ChooserSourceJoins Catalog(params VirtualJoin[] joins) => new(
        "COP_SEND_D",
        new HashSet<string>(joins.Select(join => join.Alias).Append("COP_SEND_D"), StringComparer.OrdinalIgnoreCase),
        joins,
        null);

    [Fact]
    public void BuildJoinClause_SelfReferentialOn_ReturnsJoin()
    {
        var catalog = Catalog(new VirtualJoin(
            "COP_SEND_M", "COP_SEND_M",
            new[] { C("COP_SEND_D", "SEND_TYPE", "COP_SEND_M", "SEND_TYPE"), C("COP_SEND_D", "SEND_NO", "COP_SEND_M", "SEND_NO") },
            []));
        var clause = ChooserJoinCatalog.BuildJoinClause(
            catalog, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "COP_SEND_M" });
        Assert.NotNull(clause);
        Assert.Contains("[COP_SEND_M]", clause);
        Assert.Contains("[COP_SEND_D].[SEND_TYPE]=[COP_SEND_M].[SEND_TYPE]", clause);
    }

    [Fact]
    public void BuildJoinClause_TransitiveChain_PullsDependenciesOnly()
    {
        var catalog = Catalog(
            new VirtualJoin("COP_SEND_M", "COP_SEND_M", new[] { C("COP_SEND_D", "SEND_NO", "COP_SEND_M", "SEND_NO") }, []),
            new VirtualJoin("CLIENT", "CLIENT", new[] { C("COP_SEND_M", "CLIENT_ID", "CLIENT", "CLIENT_ID") }, []),
            new VirtualJoin("PRODUCT", "PRODUCT", new[] { C("COP_SEND_D", "PRO_NO", "PRODUCT", "PRO_NO") }, []));
        // 仅引用 CLIENT：应连带拉取依赖 COP_SEND_M，不拉无关的 PRODUCT
        var clause = ChooserJoinCatalog.BuildJoinClause(
            catalog, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "CLIENT" });
        Assert.NotNull(clause);
        Assert.Contains("[CLIENT]", clause);
        Assert.Contains("[COP_SEND_M]", clause);
        Assert.DoesNotContain("[PRODUCT]", clause);
    }

    [Fact]
    public void BuildJoinClause_ConstantCondition_ReturnsJoin()
    {
        var catalog = Catalog(new VirtualJoin(
            "SYSDL", "SYSDL", [],
            new[] { new VirtualJoinConstant("SYSDL", "ACTIVE_TAG", "1", false) }));
        var clause = ChooserJoinCatalog.BuildJoinClause(
            catalog, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "SYSDL" });
        Assert.NotNull(clause);
        Assert.Contains("[SYSDL].[ACTIVE_TAG]=1", clause);
    }

    [Fact]
    public void BuildJoinClause_UnreachableAlias_ReturnsNull()
    {
        var catalog = Catalog(new VirtualJoin(
            "COP_SEND_M", "COP_SEND_M", new[] { C("COP_SEND_D", "SEND_NO", "COP_SEND_M", "SEND_NO") }, []));
        var clause = ChooserJoinCatalog.BuildJoinClause(
            catalog, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "NOT_IN_CHAIN" });
        Assert.Null(clause);
    }

    [Fact]
    public void BuildJoinClause_EmptyReferenced_ReturnsNull()
    {
        var catalog = Catalog();
        Assert.Null(ChooserJoinCatalog.BuildJoinClause(catalog, new HashSet<string>(StringComparer.OrdinalIgnoreCase)));
    }
}
