using EOS.API.Data;
using Xunit;

namespace EOS.API.Tests;

public sealed class ChooserFilterCompilerTests
{
    private static readonly IReadOnlySet<string> Joins = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "CLIENT",
        "PRODUCT",
    };

    [Fact]
    public void Compile_SimpleEq_ProducesParameterizedPredicate()
    {
        var filter = new ChooserFilterStruct("AND",
        [
            new ChooserFilterItem("CLIENT_ID", "EQ", "C001"),
        ]);
        var result = ChooserFilterCompiler.Compile(filter, "ORDER_M", Joins, null);

        Assert.NotNull(result);
        Assert.Equal("([ORDER_M].[CLIENT_ID] = @cf0)", result!.Predicate);
        Assert.Single(result.Parameters);
        Assert.Equal("C001", result.Parameters[0].RawValue);
        Assert.Empty(result.Joins);
    }

    [Fact]
    public void Compile_MasterTemplate_BindsRuntimeValue()
    {
        var filter = new ChooserFilterStruct("AND",
        [
            new ChooserFilterItem("CLIENT_ID", "EQ", "{m.CLIENT_ID}"),
        ]);
        var result = ChooserFilterCompiler.Compile(filter, "ORDER_M", Joins, null);

        Assert.NotNull(result);
        var bound = ChooserFilterCompiler.BindRuntimeValue(
            result!.Parameters[0],
            new Dictionary<string, string> { ["CLIENT_ID"] = "C100" },
            null,
            1209);
        Assert.Equal("C100", bound);
    }

    [Fact]
    public void Compile_ModuleTemplate_BindsModuleId()
    {
        var filter = new ChooserFilterStruct("AND",
        [
            new ChooserFilterItem("B_M_IDX", "EQ", "{module}"),
        ]);
        var result = ChooserFilterCompiler.Compile(filter, "BILLKIND", Joins, null);

        Assert.NotNull(result);
        var bound = ChooserFilterCompiler.BindRuntimeValue(result!.Parameters[0], null, null, 1401);
        Assert.Equal("1401", bound);
    }

    [Fact]
    public void Compile_CrossTableField_EmitsJoinAndQualifiedColumn()
    {
        var filter = new ChooserFilterStruct("AND",
        [
            new ChooserFilterItem("CLIENT.BUSINESS_TAG", "EQ", "0"),
        ]);
        var result = ChooserFilterCompiler.Compile(filter, "ORDER_M", Joins, null);

        Assert.NotNull(result);
        Assert.Equal("([CLIENT].[BUSINESS_TAG] = @cf0)", result!.Predicate);
        Assert.Contains("CLIENT", result.Joins);
        Assert.Contains(("CLIENT", "BUSINESS_TAG"), result.ForeignColumns);
    }

    [Fact]
    public void Compile_IsNullZeroMacro_EmitsIsNullPredicate()
    {
        var filter = new ChooserFilterStruct("AND",
        [
            new ChooserFilterItem("CONFIRM_TAG", "ISNULL_ZERO"),
        ]);
        var result = ChooserFilterCompiler.Compile(filter, "ORDER_M", Joins, null);

        Assert.NotNull(result);
        Assert.Equal("(ISNULL([ORDER_M].[CONFIRM_TAG],0) = 0)", result!.Predicate);
        Assert.Empty(result.Parameters);
    }

    [Fact]
    public void Compile_NullSafeZero_WrapsColumn()
    {
        var filter = new ChooserFilterStruct("AND",
        [
            new ChooserFilterItem("STATE", "LT", "4", "ZERO"),
        ]);
        var result = ChooserFilterCompiler.Compile(filter, "HR_EMPLOYEE", Joins, null);

        Assert.NotNull(result);
        Assert.Equal("(ISNULL([HR_EMPLOYEE].[STATE],0) < @cf0)", result!.Predicate);
    }

    [Fact]
    public void Compile_DaysFromToday_EmitsDatediff()
    {
        var filter = new ChooserFilterStruct("AND",
        [
            new ChooserFilterItem("SHIPMENT_DATE", "DAYS_FROM_TODAY", "0"),
        ]);
        var result = ChooserFilterCompiler.Compile(filter, "SHIPMENT_M", Joins, null);

        Assert.NotNull(result);
        Assert.Equal("(DATEDIFF(day,[SHIPMENT_M].[SHIPMENT_DATE],GETDATE()) = @cf0)", result!.Predicate);
    }

    [Fact]
    public void Compile_InvalidOperator_ReturnsNull()
    {
        var filter = new ChooserFilterStruct("AND",
        [
            new ChooserFilterItem("CLIENT_ID", "SIDEWAYS", "x"),
        ]);
        Assert.Null(ChooserFilterCompiler.Compile(filter, "ORDER_M", Joins, null));
    }

    [Fact]
    public void Compile_UnknownCrossTable_ReturnsNull()
    {
        var filter = new ChooserFilterStruct("AND",
        [
            new ChooserFilterItem("EVIL_TABLE.COL", "EQ", "1"),
        ]);
        Assert.Null(ChooserFilterCompiler.Compile(filter, "ORDER_M", Joins, null));
    }

    [Fact]
    public void Compile_OrLogic_JoinsWithOr()
    {
        var filter = new ChooserFilterStruct("OR",
        [
            new ChooserFilterItem("A", "EQ", "1"),
            new ChooserFilterItem("B", "EQ", "2"),
        ]);
        var result = ChooserFilterCompiler.Compile(filter, "T", null, null);
        Assert.NotNull(result);
        Assert.Equal("([T].[A] = @cf0) OR ([T].[B] = @cf1)", result!.Predicate);
    }

    [Fact]
    public void Compile_NestedGroup_AndNegate()
    {
        var filter = new ChooserFilterStruct("AND",
        [
            new ChooserFilterItem(Negate: true, Group: new ChooserFilterStruct("OR",
            [
                new ChooserFilterItem("A", "EQ", "1"),
                new ChooserFilterItem("B", "EQ", "2"),
            ])),
        ]);
        var result = ChooserFilterCompiler.Compile(filter, "T", null, null);
        Assert.NotNull(result);
        Assert.Equal("(NOT (([T].[A] = @cf0) OR ([T].[B] = @cf1)))", result!.Predicate);
    }

    [Fact]
    public void Compile_ArithmeticExpression_EmitsParenthesizedArith()
    {
        var filter = new ChooserFilterStruct("AND",
        [
            new ChooserFilterItem(
                Operator: "GT",
                Left: new ChooserFilterExpression("arith", Op: "-",
                    Left: new ChooserFilterExpression("column", Column: "QTY"),
                    Right: new ChooserFilterExpression("column", Column: "FINISHED_QTY")),
                Right: new ChooserFilterExpression("literal", Value: "0")),
        ]);
        var result = ChooserFilterCompiler.Compile(filter, "ORDER_D", null, null);
        Assert.NotNull(result);
        Assert.Equal("(([ORDER_D].[QTY] - [ORDER_D].[FINISHED_QTY]) > @cf0)", result!.Predicate);
    }

    [Fact]
    public void Compile_IsNullExpression()
    {
        var filter = new ChooserFilterStruct("AND",
        [
            new ChooserFilterItem(
                Operator: "GT",
                Left: new ChooserFilterExpression("isnull",
                    Left: new ChooserFilterExpression("arith", Op: "-",
                        Left: new ChooserFilterExpression("column", Column: "QTY"),
                        Right: new ChooserFilterExpression("column", Column: "RETURN_QTY")),
                    Value: "0"),
                Right: new ChooserFilterExpression("literal", Value: "0")),
        ]);
        var result = ChooserFilterCompiler.Compile(filter, "ORDER_D", null, null);
        Assert.NotNull(result);
        Assert.Equal("(ISNULL(([ORDER_D].[QTY] - [ORDER_D].[RETURN_QTY]),0) > @cf0)", result!.Predicate);
    }

    [Fact]
    public void Compile_NotInSubquery()
    {
        var filter = new ChooserFilterStruct("AND",
        [
            new ChooserFilterItem(
                Operator: "NOT_IN",
                Left: new ChooserFilterExpression("column", Table: "PRODUCT", Column: "PRO_NO"),
                Subquery: new ChooserFilterSubquery(Table: "BOM_STRU_M", Column: "PRO_NO")),
        ]);
        var result = ChooserFilterCompiler.Compile(filter, "ORDER_D", new HashSet<string> { "PRODUCT" }, null);
        Assert.NotNull(result);
        Assert.Equal("([PRODUCT].[PRO_NO] NOT IN (SELECT [PRO_NO] FROM dbo.[BOM_STRU_M] [BOM_STRU_M]))", result!.Predicate);
        Assert.Contains("PRODUCT", result.Joins);
    }

    [Fact]
    public void Compile_InFunction_EmitsParameterizedFunctionCall()
    {
        var filter = new ChooserFilterStruct("AND",
        [
            new ChooserFilterItem(
                Operator: "IN_FUNCTION",
                Left: new ChooserFilterExpression("column", Table: "UNIT", Column: "UNIT_ID"),
                Subquery: new ChooserFilterSubquery(
                    Function: "f_get_pro_units",
                    Column: "UNIT_ID",
                    Args: ["{d.PRO_NO}"])),
        ]);
        var result = ChooserFilterCompiler.Compile(filter, "ORDER_D", new HashSet<string> { "UNIT" }, null);
        Assert.NotNull(result);
        Assert.Equal("([UNIT].[UNIT_ID] IN (SELECT [UNIT_ID] FROM dbo.[f_get_pro_units](@cf0)))", result!.Predicate);
        Assert.Equal("{d.PRO_NO}", result.Parameters[0].RawValue);
    }

    [Fact]
    public void Compile_Exists_WithWhere()
    {
        var filter = new ChooserFilterStruct("AND",
        [
            new ChooserFilterItem(
                Operator: "EXISTS",
                Subquery: new ChooserFilterSubquery(
                    Table: "HR_APPLY_D",
                    Filter:
                    [
                        new ChooserFilterItem("HR_APPLY_D.APPLY_NO", "EQ", "{m.APPLY_NO}"),
                    ])),
        ]);
        var result = ChooserFilterCompiler.Compile(filter, "HR_APPLY_M", null, null);
        Assert.NotNull(result);
        Assert.Equal("(EXISTS (SELECT 1 FROM dbo.[HR_APPLY_D] [HR_APPLY_D] WHERE ([HR_APPLY_D].[APPLY_NO] = @cf0)))", result!.Predicate);
    }

    [Fact]
    public void Compile_IsNullOperator()
    {
        var filter = new ChooserFilterStruct("AND",
        [
            new ChooserFilterItem(
                Operator: "IS_NULL",
                Left: new ChooserFilterExpression("column", Column: "DIMISSION_DATE")),
        ]);
        var result = ChooserFilterCompiler.Compile(filter, "HR_EMPLOYEE", null, null);
        Assert.NotNull(result);
        Assert.Equal("([HR_EMPLOYEE].[DIMISSION_DATE] IS NULL)", result!.Predicate);
    }
}
