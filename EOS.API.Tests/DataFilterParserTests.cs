using EOS.API.Data;
using Xunit;

namespace EOS.API.Tests;

public class DataFilterParserTests
{
    private static IReadOnlySet<string> Fields(params string[] values) =>
        values.ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static bool Try(string? filter, string masterTable, IReadOnlySet<string> allowed,
        out string predicate, out IReadOnlyList<object> parameters) =>
        DataFilterParser.TryParse(filter, masterTable, allowed, out predicate, out parameters);

    [Fact]
    public void SimpleEquality_CompilesToParameterizedPredicate()
    {
        Assert.True(Try("CLIENT_ID='C001'", "PRODUCT_EDITION", Fields("CLIENT_ID"), out var predicate, out var parameters));
        Assert.Equal("[CLIENT_ID] = @df0", predicate);
        Assert.Equal(["C001"], parameters);
    }

    [Fact]
    public void MasterTablePrefix_IsAccepted()
    {
        Assert.True(Try("PRODUCT_EDITION.PRO_NO='X'", "PRODUCT_EDITION", Fields("PRO_NO"), out var predicate, out _));
        Assert.Equal("[PRO_NO] = @df0", predicate);
    }

    [Fact]
    public void LegacyBraceFieldSyntax_IsAccepted()
    {
        // 旧系统 FILTER 使用 {表.列} 花括号语法（如 18069801 HR_EMPLOYEE）。
        Assert.True(Try(
            "{HR_EMPLOYEE.STATE}<4 and {HR_EMPLOYEE.IF_SHOW}=1",
            "HR_EMPLOYEE", Fields("STATE", "IF_SHOW"),
            out var predicate, out var parameters));
        Assert.Equal("[STATE] < @df0 AND [IF_SHOW] = @df1", predicate);
        Assert.Equal(2, parameters.Count);
    }

    [Fact]
    public void LegacyBraceAndBooleanLiteral_IsAccepted()
    {
        // 存量 MODULES.FILTER 真实写法：{表.列} 花括号 + 裸字段花括号 + TRUE 布尔字面量。
        // 前端构建器（简单子集）无法表达，但服务端权威解析器必须兼容旧系统。
        Assert.True(Try(
            "{HR_EMPLOYEE.STATE}<4 AND {IF_SHOW}=TRUE",
            "HR_EMPLOYEE", Fields("STATE", "IF_SHOW"),
            out var predicate, out var parameters));
        Assert.Equal("[STATE] < @df0 AND [IF_SHOW] = @df1", predicate);
        Assert.Equal(["4", true], parameters);
    }

    [Fact]
    public void ParenthesizedArithmeticComparison_IsAccepted()
    {
        // 1310 报表过滤：({MOC_PRODUCE_M.FINISHED_QTY}-{...FITOUT_QTY}-{...SCRAP_QTY})>0
        Assert.True(Try(
            "({MOC_PRODUCE_M.FINISHED_QTY}-{MOC_PRODUCE_M.FINISHED_FITOUT_QTY}-{MOC_PRODUCE_M.SCRAP_QTY})>0",
            "MOC_PRODUCE_M", Fields("FINISHED_QTY", "FINISHED_FITOUT_QTY", "SCRAP_QTY"),
            out var predicate, out var parameters));
        Assert.Equal("([FINISHED_QTY]-[FINISHED_FITOUT_QTY]-[SCRAP_QTY]) > @df0", predicate);
        Assert.Equal(1, parameters.Count);
    }

    [Fact]
    public void ForeignTablePrefix_IsRejected()
    {
        Assert.False(Try("CLIENT.SALES_ID='YW2-08'", "PRODUCT_EDITION", Fields("CLIENT_ID"), out _, out _));
    }

    [Fact]
    public void NotInSubquery_SimpleTable_CompilesToWhitelistedSubquery()
    {
        Assert.True(Try("PRO_NO NOT IN (SELECT PRO_NO FROM BOM_COST_M)", "PRODUCT", Fields("PRO_NO"),
            out var predicate, out _));
        Assert.Equal("[PRO_NO] NOT IN (SELECT [PRO_NO] FROM dbo.[BOM_COST_M])", predicate);
    }

    [Fact]
    public void InSubquery_DistinctAndMultipleFilters_AreSupported()
    {
        Assert.True(Try("M_IDX IN (SELECT DISTINCT M_IDX FROM MODULES) AND A='1'", "T", Fields("M_IDX", "A"),
            out var predicate, out _));
        Assert.Equal("[M_IDX] IN (SELECT DISTINCT [M_IDX] FROM dbo.[MODULES]) AND [A] = @df0", predicate);
    }

    [Fact]
    public void InSubquery_QualifiedSelectColumn_IsAccepted()
    {
        Assert.True(Try("EMP_ID NOT IN (SELECT HR_EMPLOYEE_CARD.EMP_ID FROM HR_EMPLOYEE_CARD)", "HR_EMPLOYEE", Fields("EMP_ID"),
            out var predicate, out _));
        Assert.Equal("[EMP_ID] NOT IN (SELECT [EMP_ID] FROM dbo.[HR_EMPLOYEE_CARD])", predicate);
    }

    [Fact]
    public void InSubquery_NonWhitelistedTable_IsRejected()
    {
        Assert.False(Try("PRO_NO NOT IN (SELECT PRO_NO FROM HACK_TABLE)", "PRODUCT", Fields("PRO_NO"), out _, out _));
    }

    [Fact]
    public void InSubquery_NonWhitelistedColumn_IsRejected()
    {
        Assert.False(Try("PRO_NO NOT IN (SELECT SECRET_COL FROM BOM_COST_M)", "PRODUCT", Fields("PRO_NO"), out _, out _));
    }

    [Fact]
    public void InSubquery_WithWhere_IsRejectedForNow()
    {
        Assert.True(Try("EMP_ID NOT IN (SELECT EMP_ID FROM HR_APPLY_D WHERE APPLY_TYPE='A' AND APPLY_NO='B')", "HR_EMPLOYEE",
            Fields("EMP_ID"), out var predicate, out var parameters));
        Assert.Equal("[EMP_ID] NOT IN (SELECT [EMP_ID] FROM dbo.[HR_APPLY_D] WHERE [APPLY_TYPE] = @df0 AND [APPLY_NO] = @df1)", predicate);
        Assert.Equal(["A", "B"], parameters);
    }

    [Fact]
    public void InSubquery_TableFunction_IsSupportedWithParameter()
    {
        Assert.True(Try("UNIT_ID IN (SELECT UNIT_ID FROM dbo.f_get_pro_units('P001'))", "COP_ORDER_D",
            Fields("UNIT_ID"), out var predicate, out var parameters));
        Assert.Equal("[UNIT_ID] IN (SELECT [UNIT_ID] FROM dbo.[f_get_pro_units](@df0))", predicate);
        Assert.Equal(["P001"], parameters);
    }

    [Fact]
    public void InSubquery_MultiTableFrom_IsRejected()
    {
        // 多表现已支持（阶段 4）；保留此用例验证「无关联条件的多表」仍拒绝
        Assert.False(Try("EMP_ID NOT IN (SELECT EMP_ID FROM HR_BASEPAY_M m, HR_BASEPAY_D d WHERE m.COUNT_MONTH='202608')", "HR_EMPLOYEE",
            Fields("EMP_ID"), out _, out _));
    }

    [Fact]
    public void InSubquery_UnknownFunction_IsRejected()
    {
        Assert.False(Try("X IN (SELECT X FROM dbo.hack('y'))", "T",
            Fields("X"), out _, out _));
    }

    [Fact]
    public void InSubquery_NotEqualWhereCondition_IsSupported()
    {
        Assert.True(Try("PRO_NO NOT IN (SELECT PRO_NO FROM MOU_ASSESS_M WHERE MOU_ASSESS_M.ASSESS_NO<>'A1')", "PRODUCT",
            Fields("PRO_NO"), out var predicate, out var parameters));
        Assert.Equal("[PRO_NO] NOT IN (SELECT [PRO_NO] FROM dbo.[MOU_ASSESS_M] WHERE [ASSESS_NO] <> @df0)", predicate);
        Assert.Equal(["A1"], parameters);
    }

    [Fact]
    public void InSubquery_FullChooserFilter_Compiles()
    {
        Assert.True(Try(
            "PRODUCT.BUSINESS_TAG=0 AND PRODUCT.CONFIRM_TAG = 1 AND PRODUCT.CLIENT_ID='TTA' AND PRODUCT.PRO_NO NOT IN (SELECT PRO_NO FROM MOU_ASSESS_M WHERE MOU_ASSESS_M.ASSESS_NO<>'ZZZ')",
            "PRODUCT", Fields("BUSINESS_TAG", "CONFIRM_TAG", "CLIENT_ID", "PRO_NO"),
            out var predicate, out _));
        Assert.Contains("NOT IN (SELECT [PRO_NO] FROM dbo.[MOU_ASSESS_M] WHERE [ASSESS_NO] <> @df", predicate);
    }

    [Fact]
    public void AndOrWithParentheses_IsSupported()
    {
        Assert.True(Try("(A='1') AND (B='2' OR C='3')", "T", Fields("A", "B", "C"), out var predicate, out var parameters));
        Assert.Equal("([A] = @df0) AND ([B] = @df1 OR [C] = @df2)", predicate);
        Assert.Equal(["1", "2", "3"], parameters);
    }

    [Fact]
    public void UnknownField_IsRejected()
    {
        Assert.False(Try("SECRET='1'", "T", Fields("A"), out _, out _));
    }

    [Fact]
    public void UnsupportedOperator_IsRejected()
    {
        Assert.False(Try("A LIKE 'x%'", "T", Fields("A"), out _, out _));
        Assert.False(Try("A BETWEEN 1 AND 2", "T", Fields("A"), out _, out _));
    }

    [Theory]
    [InlineData("T.PRO_TYPE=1", "[PRO_TYPE] = @df0")]
    [InlineData("T.APPLY_TYPE='QG'", "[APPLY_TYPE] = @df0")]
    [InlineData("T.QTY>0.1", "[QTY] > @df0")]
    [InlineData("T.STATE<4", "[STATE] < @df0")]
    [InlineData("T.MOU_SORT=2", "[MOU_SORT] = @df0")]
    [InlineData("FINISHED_TAG=0 AND QTY>10", "[FINISHED_TAG] = @df0 AND [QTY] > @df1")]
    public void ModuleFilter_数值与比较运算符(string filter, string expectedPredicate)
    {
        Assert.True(Try(filter, "T", Fields("PRO_TYPE", "APPLY_TYPE", "QTY", "STATE", "MOU_SORT", "FINISHED_TAG", "RECEIVE_QTY"), out var predicate, out var parameters));
        Assert.Equal(expectedPredicate, predicate);
        Assert.All(parameters, parameter => Assert.True(parameter is decimal or string));
    }

    [Fact]
    public void ModuleFilter_同表列算术_被编译为参数化谓词()
    {
        Assert.True(Try("SUM_AMOUNT-RECEIVE_AMOUNT>0", "COP_ACCOUNT_M", Fields("SUM_AMOUNT", "RECEIVE_AMOUNT"), out var predicate, out var parameters));
        Assert.Equal("[SUM_AMOUNT]-[RECEIVE_AMOUNT] > @df0", predicate);
        Assert.Equal([0m], parameters);
    }

    [Fact]
    public void ModuleFilter_直接数值比较_参数化为字符串避免char转换()
    {
        Assert.True(Try("PRO_TYPE=1", "PRODUCT", Fields("PRO_TYPE"), out var predicate, out var parameters));
        Assert.Equal("[PRO_TYPE] = @df0", predicate);
        Assert.Equal(["1"], parameters);
    }

    [Fact]
    public void ModuleFilter_带主表前缀的列算术_被接受()
    {
        Assert.True(Try("PUR_PURCHASE_D.QTY-PUR_PURCHASE_D.RECEIVE_QTY>0", "PUR_PURCHASE_D", Fields("QTY", "RECEIVE_QTY"), out var predicate, out _));
        Assert.Equal("[QTY]-[RECEIVE_QTY] > @df0", predicate);
    }

    [Fact]
    public void ModuleFilter_GetDate_被求值为日期参数()
    {
        Assert.True(Try("PLAN_DELIVERY_DATE<=getdate()", "PUR_PURCHASE_D", Fields("PLAN_DELIVERY_DATE"), out var predicate, out var parameters));
        Assert.Equal("[PLAN_DELIVERY_DATE] <= @df0", predicate);
        var value = Assert.IsType<DateTime>(parameters[0]);
        Assert.True((DateTime.Now - value).Duration() < TimeSpan.FromMinutes(1));
    }

    [Fact]
    public void ModuleFilter_ConvertGetDate_求值为年月字符串()
    {
        Assert.True(Try("SEND_DATE<convert(varchar(7),getdate(),120)", "COP_SEND_D", Fields("SEND_DATE"), out var predicate, out var parameters));
        Assert.Equal("[SEND_DATE] < @df0", predicate);
        var value = Assert.IsType<string>(parameters[0]);
        Assert.Matches(@"^\d{4}-\d{2}$", value);
    }

    [Fact]
    public void ModuleFilter_日期边界表达式_求值为上月26号字符串()
    {
        Assert.True(Try("SEND_DATE<convert(varchar(7),dateadd(month,-1,getdate()),120)+'-26'",
            "COP_SEND_D", Fields("SEND_DATE"), out var predicate, out var parameters));
        Assert.Equal("[SEND_DATE] < @df0", predicate);
        var value = Assert.IsType<string>(parameters[0]);
        Assert.Matches(@"^\d{4}-\d{2}-26$", value);
    }

    [Fact]
    public void ModuleFilter_真实组合_今日需到料()
    {
        Assert.True(Try(
            "PUR_PURCHASE_D.finished_tag=0 and PUR_PURCHASE_D.PLAN_DELIVERY_DATE<=getdate() and PUR_PURCHASE_D.QTY-PUR_PURCHASE_D.RECEIVE_QTY>0",
            "PUR_PURCHASE_D", Fields("FINISHED_TAG", "PLAN_DELIVERY_DATE", "QTY", "RECEIVE_QTY"), out var predicate, out var parameters));
        Assert.Equal("[finished_tag] = @df0 AND [PLAN_DELIVERY_DATE] <= @df1 AND [QTY]-[RECEIVE_QTY] > @df2", predicate);
        Assert.Equal(3, parameters.Count);
        Assert.Equal("0", parameters[0]); // 直接列比较数值字面量 → 字符串参数（避免 char 列隐式转 numeric）
        Assert.IsType<DateTime>(parameters[1]);
        Assert.Equal(0m, parameters[2]); // 列间算术右值保持 decimal
    }

    [Theory]
    [InlineData("PRO_NO LIKE '%X%'")]
    public void ModuleFilter_不支持表达式被拒绝(string filter)
    {
        Assert.False(Try(filter, "COP_ACCOUNT_M", Fields("SUM_AMOUNT", "RECEIVE_AMOUNT", "QTY", "SEND_DATE", "PRO_NO"), out _, out _));
    }

    [Theory]
    [InlineData("COP_SEND_M.SEND_DATE<getdate()")]
    [InlineData("PLAN_QTY>FINISHED_PLAN_QTY")]
    [InlineData("SEND_DATE<fancy(getdate())")]
    [InlineData("QTY-RECEIVE_QTY")]
    public void ModuleFilter_跨表与未知函数被拒绝(string filter)
    {
        Assert.False(Try(filter, "COP_SEND_D", Fields("SEND_DATE", "PLAN_QTY", "FINISHED_PLAN_QTY", "QTY", "RECEIVE_QTY"), out _, out _));
    }

    [Fact]
    public void ApostropheInLiteral_IsUnescaped()
    {
        Assert.True(Try("NAME='O''Brien'", "T", Fields("NAME"), out _, out var parameters));
        Assert.Equal(["O'Brien"], parameters);
    }

    [Fact]
    public void BlankFilter_IsNotAParsedScope()
    {
        Assert.False(Try("   ", "T", Fields("A"), out _, out _));
        Assert.False(Try(null, "T", Fields("A"), out _, out _));
    }

    [Fact]
    public void IsNull_CompilesToParameterizedPredicate()
    {
        Assert.True(Try("ISNULL(CLIENT.BUSINESS_TAG,0)=0", "CLIENT", Fields("BUSINESS_TAG", "CONFIRM_TAG"), out var predicate, out var parameters));
        Assert.Equal("ISNULL([BUSINESS_TAG],@df0) = @df1", predicate);
        Assert.Equal(["0", "0"], parameters);
    }

    [Fact]
    public void IsNull_And_Combo_ChooserFilter()
    {
        Assert.True(Try("ISNULL(CLIENT.BUSINESS_TAG,0)=0 AND ISNULL(CLIENT.CONFIRM_TAG,0)=1", "CLIENT",
            Fields("BUSINESS_TAG", "CONFIRM_TAG"), out var predicate, out var parameters));
        Assert.Equal("ISNULL([BUSINESS_TAG],@df0) = @df1 AND ISNULL([CONFIRM_TAG],@df2) = @df3", predicate);
        Assert.Equal(4, parameters.Count);
    }

    [Fact]
    public void IsNull_UnknownField_Or_NonConstant_Rejected()
    {
        Assert.False(Try("ISNULL(SECRET,0)=0", "CLIENT", Fields("BUSINESS_TAG"), out _, out _));
        Assert.False(Try("ISNULL(BUSINESS_TAG,X)=0", "CLIENT", Fields("BUSINESS_TAG"), out _, out _));
    }

    [Fact]
    public void ForeignJoinTable_CompilesWithJoins()
    {
        var foreign = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["PRODUCT"] = "PRO_NO" };
        Assert.True(DataFilterParser.TryParseWithJoins(
            "CLIENT_PRICE_D.CURR_ID='RMB' AND PRODUCT.BUSINESS_TAG=0 AND PRODUCT.CONFIRM_TAG=1",
            "CLIENT_PRICE_D", Fields("CURR_ID", "PRO_NO", "CLIENT_ID"), foreign, null,
            out var predicate, out var parameters, out var joins, out var foreignColumns));
        Assert.Contains("PRODUCT", joins);
        Assert.Contains(("PRODUCT", "BUSINESS_TAG"), foreignColumns);
        Assert.Contains(("PRODUCT", "CONFIRM_TAG"), foreignColumns);
        Assert.Contains("[PRODUCT].[BUSINESS_TAG]", predicate);
        Assert.Equal(3, parameters.Count);
    }

    [Fact]
    public void ForeignJoinTable_NotWhitelisted_Rejected()
    {
        var foreign = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["PRODUCT"] = "PRO_NO" };
        Assert.False(DataFilterParser.TryParseWithJoins(
            "SECRET.X=1 AND PRODUCT.BUSINESS_TAG=0", "T", Fields("A"), foreign, null,
            out _, out _, out _, out _));
        Assert.False(DataFilterParser.TryParseWithJoins(
            "OTHER.BUSINESS_TAG=0", "T", Fields("A"), foreign, null,
            out _, out _, out _, out _));
    }

    [Fact]
    public void InSubquery_WithJoinsAndMasterTemplate_Compiles()
    {
        var foreign = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["PRODUCT"] = "PRO_NO" };
        Assert.True(DataFilterParser.TryParseWithJoins(
            "PRODUCT.BUSINESS_TAG=0 AND PRODUCT.CONFIRM_TAG=1 AND PRODUCT.CLIENT_ID='TTA' AND PRODUCT.PRO_NO NOT IN (SELECT PRO_NO FROM MOU_ASSESS_M WHERE MOU_ASSESS_M.ASSESS_NO<>'ZZZ')",
            "PRODUCT", Fields("BUSINESS_TAG", "CONFIRM_TAG", "CLIENT_ID", "PRO_NO"), foreign, null,
            out var predicate, out var parameters, out var joins, out _));
        Assert.Contains("NOT IN (SELECT [PRO_NO] FROM dbo.[MOU_ASSESS_M] WHERE [ASSESS_NO] <> @df", predicate);
    }

    [Fact]
    public void NumericColumnTypes_BindNumericLiterals()
    {
        var types = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["BUSINESS_TAG"] = "bit", ["CONFIRM_TAG"] = "bit", ["CLIENT_ID"] = "nchar",
        };
        Assert.True(DataFilterParser.TryParseWithJoins(
            "PRODUCT.BUSINESS_TAG=0 AND PRODUCT.CONFIRM_TAG=1 AND PRODUCT.CLIENT_ID='TTA'",
            "PRODUCT", Fields("BUSINESS_TAG", "CONFIRM_TAG", "CLIENT_ID"), null, types,
            out var predicate, out var parameters, out _, out _));
        Assert.Equal([0m, 1m, "TTA"], parameters);
    }

    [Fact]
    public void InSubquery_MultiTableFrom_CompilesWithJoin()
    {
        Assert.True(Try(
            "EMP_ID NOT IN (SELECT EMP_ID FROM HR_BASEPAY_M m,HR_BASEPAY_D d WHERE m.BASEPAY_TYPE=d.BASEPAY_TYPE AND m.BASEPAY_NO=d.BASEPAY_NO AND m.COUNT_MONTH='202608')",
            "HR_EMPLOYEE", Fields("EMP_ID"), out var predicate, out var parameters));
        Assert.Equal(
            "[EMP_ID] NOT IN (SELECT [d].[EMP_ID] FROM dbo.[HR_BASEPAY_M] AS [m] JOIN dbo.[HR_BASEPAY_D] AS [d] ON [m].[BASEPAY_TYPE]=[d].[BASEPAY_TYPE] AND [m].[BASEPAY_NO]=[d].[BASEPAY_NO] WHERE [m].[COUNT_MONTH] = @df0)",
            predicate);
        Assert.Equal(["202608"], parameters);
    }

    [Fact]
    public void InSubquery_MultiTablePrefixedSelect_Compiles()
    {
        Assert.True(Try(
            "EMP_ID NOT IN (SELECT d.EMP_ID FROM HR_PLAN_M m,HR_PLAN_D d WHERE m.PLAN_TYPE=d.PLAN_TYPE AND m.PLAN_NO=d.PLAN_NO AND m.COUNT_MONTH='202608')",
            "HR_EMPLOYEE", Fields("EMP_ID"), out var predicate, out _));
        Assert.Contains("SELECT [d].[EMP_ID] FROM dbo.[HR_PLAN_M] AS [m] JOIN dbo.[HR_PLAN_D] AS [d]", predicate);
    }

    [Fact]
    public void InSubquery_MultiTableWithoutJoinCondition_IsRejected()
    {
        Assert.False(Try(
            "EMP_ID NOT IN (SELECT EMP_ID FROM HR_WAGE_M m,HR_WAGE_D d WHERE m.COUNT_MONTH='202608')",
            "HR_EMPLOYEE", Fields("EMP_ID"), out _, out _));
    }

    [Fact]
    public void InSubquery_ConcatCastExpression_Compiles()
    {
        Assert.True(Try(
            "SHIPMENT_NO+CAST(SERIAL_NO AS CHAR(6)) NOT IN (SELECT SHIPMENT_NO+CAST(SHIPMENT_SERIAL_NO AS CHAR(6)) FROM SFC_PLAN_M,SFC_PLAN_D WHERE SFC_PLAN_M.PLAN_TYPE=SFC_PLAN_D.PLAN_TYPE AND SFC_PLAN_M.PLAN_NO=SFC_PLAN_D.PLAN_NO AND SFC_PLAN_D.PLAN_NO<>'m.PLAN_NO' AND SFC_PLAN_M.SORT_IDX='{m.SORT_IDX}')",
            "COP_SHIPMENT_D", Fields("SHIPMENT_NO", "SERIAL_NO"), out var predicate, out var parameters));
        Assert.Contains("NOT IN (SELECT [SFC_PLAN_D].[SHIPMENT_NO]+CAST([SFC_PLAN_D].[SHIPMENT_SERIAL_NO] AS CHAR(6)) FROM dbo.[SFC_PLAN_M] AS [SFC_PLAN_M] JOIN dbo.[SFC_PLAN_D] AS [SFC_PLAN_D] ON", predicate);
        Assert.Contains("[SFC_PLAN_D].[PLAN_NO] <> @df0", predicate);
        Assert.Contains("[SFC_PLAN_M].[SORT_IDX] = @df1", predicate);
        Assert.Equal(2, parameters.Count);
    }
}
