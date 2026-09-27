using EOS.API.Data;
using Xunit;

namespace EOS.API.Tests;

public class VirtualExpressionParserTests
{
    // ---- VIRTUAL_EXP 表达式：仅接受「表.列」单跨表取值 ----

    [Theory]
    [InlineData("PRODUCT.PRO_NAME", "PRODUCT", "PRO_NAME")]
    [InlineData("SYSDN.EMP_NAME", "SYSDN", "EMP_NAME")]
    [InlineData("PRODUCT_J.ELEMENT_PRO_NO", "PRODUCT_J", "ELEMENT_PRO_NO")]
    [InlineData("COLOR_M.COLOR_NAME", "COLOR_M", "COLOR_NAME")]
    public void SimpleCrossTableReference_IsAccepted(string expression, string expectedTable, string expectedColumn)
    {
        Assert.True(VirtualExpressionParser.TryParseExpression(expression, out var table, out var column));
        Assert.Equal(expectedTable, table);
        Assert.Equal(expectedColumn, column);
    }

    [Theory]
    [InlineData("case when PRODUCT_J.ELEMENT_PRO_NO=BOM_STRU_D.ELEMENT_PRO_NO then '' else '不同' end")]
    [InlineData("DATEDIFF(MM,IN_DATE,GETDATE())")]
    [InlineData("(PRODUCT_M.P_WIDTH+PRODUCT_M.P_LENGTH-BOM_STRU_M.P_WIDTH_OLD-BOM_STRU_M.P_LENGTH_OLD)")]
    [InlineData("'RMB'")]
    [InlineData("1")]
    [InlineData("100")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("A.B.C")]
    [InlineData("1.A")]
    [InlineData("A.1")]
    [InlineData("A..B")]
    [InlineData("A.")]
    [InlineData(".A")]
    [InlineData("A.B = C.D")]
    [InlineData("A.B; DROP TABLE FIELDS")]
    [InlineData("A.B--comment")]
    [InlineData("A.\nB")]
    [InlineData("ISNULL(CLIENT.BUSINESS_TAG,0)=0")]
    [InlineData("SELECT 1")]
    [InlineData("dbo.PRODUCT.PRO_NAME")]
    public void NonSimpleReference_IsRejected(string expression)
    {
        Assert.False(VirtualExpressionParser.TryParseExpression(expression, out _, out _));
    }

    [Fact]
    public void NullExpression_IsRejected()
    {
        Assert.False(VirtualExpressionParser.TryParseExpression(null, out _, out _));
    }

    // ---- QUERY_RELATION：严格语法 ----

    [Fact]
    public void ClientRelation_ParsesAllSixJoins()
    {
        const string relation = """
            CLIENT WITH (NOLOCK)
            LEFT JOIN CLIENT_LINKMAN WITH (NOLOCK) ON CLIENT.CLIENT_ID=CLIENT_LINKMAN.CLIENT_ID
            LEFT JOIN SYSDN WITH (NOLOCK) ON CLIENT.SALES_ID=SYSDN.EMP_ID
            LEFT JOIN TAX WITH (NOLOCK) ON CLIENT.TAX_ID=TAX.TAX_ID
            LEFT JOIN CURR WITH (NOLOCK) ON CLIENT.CURR_ID=CURR.CURR_ID
            LEFT JOIN BANK WITH (NOLOCK) ON CLIENT.BANK_ID=BANK.BANK_ID
            LEFT JOIN REPORT_HEADER WITH (NOLOCK) ON CLIENT.HEADER_ID=REPORT_HEADER.HEADER_ID
            """;

        Assert.True(VirtualExpressionParser.TryParseRelation(relation, "CLIENT", out var joins, out var error), error);
        Assert.Equal(6, joins.Count);
        Assert.Equal(["CLIENT_LINKMAN", "SYSDN", "TAX", "CURR", "BANK", "REPORT_HEADER"], joins.Select(join => join.Alias));
        var sysdn = joins.Single(join => join.Alias.Equals("SYSDN", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("SYSDN", sysdn.Table);
        var condition = Assert.Single(sysdn.Conditions);
        Assert.Equal(("CLIENT", "SALES_ID"), (condition.LeftTable, condition.LeftColumn));
        Assert.Equal(("SYSDN", "EMP_ID"), (condition.RightTable, condition.RightColumn));
    }

    [Fact]
    public void AliasJoinWithHint_Parses()
    {
        const string relation =
            "BOM_STRU_D WITH (NOLOCK) " +
            "LEFT JOIN PRODUCT PRODUCT_J WITH (NOLOCK) ON BOM_STRU_D.PRO_NO=PRODUCT_J.PRO_NO";
        Assert.True(VirtualExpressionParser.TryParseRelation(relation, "BOM_STRU_D", out var joins, out var error), error);
        var join = Assert.Single(joins);
        Assert.Equal("PRODUCT", join.Table);
        Assert.Equal("PRODUCT_J", join.Alias);
    }

    [Fact]
    public void NoSpaceHintBeforeOn_IsNormalized()
    {
        const string relation =
            "PUR_CALLBACK_D WITH(NOLOCK) " +
            "LEFT JOIN PUR_CALLBACK_M WITH(NOLOCK) ON PUR_CALLBACK_M.CALLBACK_TYPE=PUR_CALLBACK_D.CALLBACK_TYPE AND PUR_CALLBACK_M.CALLBACK_NO=PUR_CALLBACK_D.CALLBACK_NO " +
            "LEFT JOIN SUPPLIER WITH(NOLOCK)ON PUR_CALLBACK_M.SUPPLIER_ID=SUPPLIER.SUPPLIER_ID";
        Assert.True(VirtualExpressionParser.TryParseRelation(relation, "PUR_CALLBACK_D", out var joins, out var error), error);
        var join = Assert.Single(joins, item => item.Table.Equals("SUPPLIER", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("SUPPLIER", join.Table);
        Assert.Equal("SUPPLIER", join.Alias);
    }

    [Fact]
    public void TransitiveDependencyCondition_Parses()
    {
        const string relation =
            "CUS_MATERIN_D WITH(NOLOCK) " +
            "LEFT JOIN CUS_MANUAL_MAT WITH(NOLOCK) ON CUS_MATERIN_D.MANUAL_NO=CUS_MANUAL_MAT.MANUAL_NO AND CUS_MATERIN_D.MAT_SERIAL_NO=CUS_MANUAL_MAT.SERIAL_NO " +
            "LEFT JOIN CUS_PRODUCT WITH(NOLOCK) ON CUS_MANUAL_MAT.PRO_ID=CUS_PRODUCT.PRO_ID";
        Assert.True(VirtualExpressionParser.TryParseRelation(relation, "CUS_MATERIN_D", out var joins, out var error), error);
        Assert.Equal(2, joins.Count);
        Assert.Equal(["CUS_MANUAL_MAT", "CUS_PRODUCT"], joins.Select(join => join.Alias));
        Assert.Equal(2, joins[0].Conditions.Count);
    }

    [Fact]
    public void RelationWithNoJoins_IsAccepted()
    {
        Assert.True(VirtualExpressionParser.TryParseRelation("BANK WITH (NOLOCK)", "BANK", out var joins, out var error), error);
        Assert.Empty(joins);
    }

    [Theory]
    [InlineData("HR_WAGE WITH (NOLOCK) LEFT JOIN FIELDS WITH (NOLOCK) ON HR_WAGE.WAGE_FIELD=FIELDS.F_ID AND FIELDS.T_ID='HR_WAGE_D'", "HR_WAGE")]
    [InlineData("HRM_WAGE WITH (NOLOCK) LEFT JOIN FIELDS WITH (NOLOCK) ON HRM_WAGE.WAGE_FIELD=FIELDS.F_ID AND FIELDS.T_ID='HRM_WAGE_D'", "HRM_WAGE")]
    public void QualifiedLiteralConditionInRelation_IsAccepted(string relation, string baseTable)
    {
        Assert.True(VirtualExpressionParser.TryParseRelation(relation, baseTable, out var joins, out var error), error);
        var fieldsJoin = Assert.Single(joins.Where(join => join.Table.Equals("FIELDS", StringComparison.OrdinalIgnoreCase)));
        Assert.Single(fieldsJoin.Constants);
    }

    [Theory]
    [InlineData("HR_WAGE WITH (NOLOCK) LEFT JOIN FIELDS WITH (NOLOCK) ON HR_WAGE.WAGE_FIELD=FIELDS.F_ID AND T_ID='HR_WAGE'")]
    [InlineData("HR_WAGE WITH (NOLOCK) LEFT JOIN FIELDS WITH (NOLOCK) ON HR_WAGE.WAGE_FIELD=FIELDS.F_ID AND FIELDS.T_ID=1+1")]
    public void UnqualifiedOrComplexLiteralCondition_IsRejected(string relation)
    {
        Assert.False(VirtualExpressionParser.TryParseRelation(relation, "HR_WAGE", out _, out _));
    }

    [Theory]
    [InlineData("OTHER WITH (NOLOCK) LEFT JOIN CLIENT WITH (NOLOCK) ON OTHER.CLIENT_ID=CLIENT.CLIENT_ID")]
    [InlineData("T WITH (NOLOCK) LEFT JOIN A WITH (NOLOCK) ON T.X=A.Y LEFT JOIN B WITH (NOLOCK) ON A.X=B.Y LEFT JOIN A WITH (NOLOCK) ON B.X=A.Y")]
    [InlineData("T WITH (NOLOCK) LEFT JOIN A WITH (NOLOCK) ON UNKNOWN.X=A.Y")]
    [InlineData("T WITH (NOLOCK) LEFT JOIN A WITH (NOLOCK) ON T.X=ISNULL(A.Y,'')")]
    [InlineData("T WITH (NOLOCK) LEFT JOIN A WITH (NOLOCK) ON T.X=A.Y; DROP TABLE FIELDS")]
    [InlineData("T WITH (NOLOCK) LEFT JOIN A WITH (NOLOCK) ON T.X=A.Y UNION SELECT 1")]
    [InlineData("T WITH (NOLOCK) LEFT JOIN A WITH (NOLOCK) ON T.X=A.Y AND T.X IN (SELECT 1)")]
    [InlineData("T WITH (NOLOCK) LEFT JOIN A WITH (NOLOCK) ON T.X=A.Y OR T.X=B.Y")]
    public void MalformedRelation_IsRejected(string relation)
    {
        Assert.False(VirtualExpressionParser.TryParseRelation(relation, "T", out _, out _));
    }

    [Fact]
    public void NullOrEmptyRelation_IsRejected()
    {
        Assert.False(VirtualExpressionParser.TryParseRelation(null, "T", out _, out _));
        Assert.False(VirtualExpressionParser.TryParseRelation("", "T", out _, out _));
    }

    private static VirtualJoin Join(string table, string alias) => new(table, alias, [], []);

    [Fact]
    public void ResolveJoin_AliasWinsOverTableName()
    {
        // 同一物理表两段：一段无别名、一段带别名 ⇒ 引用名优先按别名命中，不吃歧义
        VirtualJoin[] joins = [Join("PRODUCT", "PRODUCT"), Join("PRODUCT", "PRODUCT_J")];
        Assert.True(VirtualExpressionParser.TryResolveJoin(joins, "PRODUCT_J", out var aliased));
        Assert.Equal("PRODUCT_J", aliased.Alias);
        Assert.True(VirtualExpressionParser.TryResolveJoin(joins, "product", out var plain));
        Assert.Equal("PRODUCT", plain.Alias);
    }

    [Fact]
    public void ResolveJoin_PhysicalTableNameResolvesWhenUnique()
    {
        // 该表在关系里只出现一次且带别名 ⇒ 写物理表名也能确定关联段（与校验同一口径）
        VirtualJoin[] joins = [Join("PRODUCT", "PRODUCT_J"), Join("COLOR", "COLOR_M")];
        Assert.True(VirtualExpressionParser.TryResolveJoin(joins, "PRODUCT", out var join));
        Assert.Equal("PRODUCT_J", join.Alias);
        Assert.Equal("PRODUCT", join.Table);
    }

    [Fact]
    public void ResolveJoin_AmbiguousTableName_IsRejected()
    {
        // 同表两段且都带别名 ⇒ 写物理表名无法确定指向哪一段，一律判不可用（fail-closed）
        VirtualJoin[] joins = [Join("PRODUCT", "PRODUCT_J"), Join("PRODUCT", "PRODUCT_M")];
        Assert.False(VirtualExpressionParser.TryResolveJoin(joins, "PRODUCT", out _));
    }

    [Theory]
    [InlineData("OTHER")]
    [InlineData("")]
    public void ResolveJoin_UnknownReference_IsRejected(string reference)
    {
        VirtualJoin[] joins = [Join("PRODUCT", "PRODUCT_J")];
        Assert.False(VirtualExpressionParser.TryResolveJoin(joins, reference, out _));
    }
}
