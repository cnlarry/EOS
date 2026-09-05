using EOS.API.Features.Assistant.Metrics;
using Xunit;

namespace EOS.API.Tests;

public sealed class MetricDefinitionValidatorTests
{
    private static readonly IReadOnlySet<string> AllColumns =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    private static MetricValidationInput Input(
        string expression, string sourceTable = "COP_ORDER_D",
        string? dims = null, string? rowFilter = null,
        string? masterTable = null, IReadOnlySet<string>? masterColumns = null,
        IReadOnlySet<string>? allowedColumns = null) => new(
        MetricExpressionParser.Parse(expression).Expression!,
        sourceTable,
        allowedColumns ?? AllColumns,
        dims,
        rowFilter,
        masterTable,
        masterColumns);

    private sealed class FakeProbe(IReadOnlyDictionary<string, IReadOnlySet<string>> tables) : IMetricSchemaProbe
    {
        public Task<bool> TableExistsAsync(string table, CancellationToken token) =>
            Task.FromResult(tables.ContainsKey(table));

        public Task<IReadOnlySet<string>> GetColumnsAsync(string table, CancellationToken token) =>
            Task.FromResult(tables.TryGetValue(table, out var columns)
                ? columns
                : (IReadOnlySet<string>)new HashSet<string>(StringComparer.OrdinalIgnoreCase));
    }

    private static IMetricSchemaProbe Probe() => new FakeProbe(new Dictionary<string, IReadOnlySet<string>>(
        StringComparer.OrdinalIgnoreCase)
    {
        ["COP_ORDER_D"] = Columns("ORDER_NO", "ORDER_TYPE", "SERIAL_NO", "AMOUNT_TAX", "AMOUNT", "QTY", "REBATE", "PRO_NO"),
        ["COP_ORDER_M"] = Columns("ORDER_NO", "ORDER_TYPE", "CONFIRM_TAG", "ORDER_DATE", "CLIENT_ID", "OWNER"),
        ["INV_PRO_DEPOT"] = Columns("DEPOT_ID", "PRO_NO", "QTY", "IN_QTY", "OUT_QTY", "AMOUNT"),
    });

    private static IReadOnlySet<string> Columns(params string[] names) =>
        new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);

    [Fact]
    public async Task Validate_Accepts_Seed_Forms()
    {
        var validator = new MetricDefinitionValidator(Probe());
        var result = await validator.ValidateAsync(
            Input("SUM(AMOUNT_TAX)", allowedColumns: Columns("AMOUNT_TAX")), CancellationToken.None);
        Assert.True(result.Ok, result.Error);
    }

    [Fact]
    public async Task Validate_Rejects_Column_Missing_Physically()
    {
        var validator = new MetricDefinitionValidator(Probe());
        var result = await validator.ValidateAsync(
            Input("SUM(NOT_A_COLUMN)", allowedColumns: Columns("AMOUNT_TAX", "NOT_A_COLUMN")), CancellationToken.None);
        Assert.False(result.Ok);
        Assert.Contains("不存在", result.Error);
    }

    [Fact]
    public async Task Validate_Rejects_Column_Outside_Permission_Whitelist()
    {
        var validator = new MetricDefinitionValidator(Probe());
        var result = await validator.ValidateAsync(
            Input("SUM(AMOUNT_TAX)", allowedColumns: Columns("QTY")), CancellationToken.None);
        Assert.False(result.Ok);
        Assert.Contains("不可见", result.Error);
    }

    [Fact]
    public async Task Validate_Requires_Aggregate()
    {
        var validator = new MetricDefinitionValidator(Probe());
        var result = await validator.ValidateAsync(
            Input("AMOUNT_TAX + 1", allowedColumns: Columns("AMOUNT_TAX")), CancellationToken.None);
        Assert.False(result.Ok);
        Assert.Contains("聚合", result.Error);
    }

    [Fact]
    public async Task Validate_Rejects_Missing_Source_Table()
    {
        var validator = new MetricDefinitionValidator(Probe());
        var result = await validator.ValidateAsync(
            Input("SUM(QTY)", sourceTable: "GHOST_TABLE", allowedColumns: Columns("QTY")), CancellationToken.None);
        Assert.False(result.Ok);
        Assert.Contains("不存在", result.Error);
    }

    [Fact]
    public async Task Validate_Dimension_May_Live_On_Master()
    {
        var validator = new MetricDefinitionValidator(Probe());
        var result = await validator.ValidateAsync(
            Input("SUM(AMOUNT_TAX)", dims: "ORDER_DATE,CLIENT_ID,PRO_NO",
                masterTable: "COP_ORDER_M", masterColumns: Columns("ORDER_DATE", "CLIENT_ID", "CONFIRM_TAG"),
                allowedColumns: Columns("AMOUNT_TAX")), CancellationToken.None);
        Assert.True(result.Ok, result.Error);
    }

    [Fact]
    public async Task Validate_Dimension_Unknown_Is_Rejected()
    {
        var validator = new MetricDefinitionValidator(Probe());
        var result = await validator.ValidateAsync(
            Input("SUM(AMOUNT_TAX)", dims: "REGION",
                masterTable: "COP_ORDER_M", masterColumns: Columns("ORDER_DATE", "CLIENT_ID"),
                allowedColumns: Columns("AMOUNT_TAX")), CancellationToken.None);
        Assert.False(result.Ok);
        Assert.Contains("维度", result.Error);
    }

    [Fact]
    public async Task Validate_Accepts_Wellformed_Row_Filter_On_Master()
    {
        var validator = new MetricDefinitionValidator(Probe());
        var result = await validator.ValidateAsync(
            Input("SUM(AMOUNT_TAX)", rowFilter:
                """{"table":"COP_ORDER_M","on":["ORDER_TYPE","ORDER_NO"],"conditions":[{"column":"CONFIRM_TAG","op":"=","value":true}]}""",
                masterTable: "COP_ORDER_M",
                masterColumns: Columns("ORDER_DATE", "CLIENT_ID", "CONFIRM_TAG", "ORDER_NO", "ORDER_TYPE"),
                allowedColumns: Columns("AMOUNT_TAX")), CancellationToken.None);
        Assert.True(result.Ok, result.Error);
        Assert.NotNull(result.RowFilter);
        Assert.Equal("COP_ORDER_M", result.RowFilter.Table);
        Assert.Equal(2, result.RowFilter.JoinColumns.Count);
        Assert.Single(result.RowFilter.Conditions);
        Assert.True((bool)result.RowFilter.Conditions[0].Value);
    }

    [Theory]
    [InlineData("""{"table":"COP_ORDER_M","on":["ORDER_NO"],"conditions":[{"column":"CONFIRM_TAG","op":"LIKE","value":1}]}""", "运算符")]
    [InlineData("""{"table":"COP_ORDER_M","on":["ORDER_NO"],"conditions":[{"column":"GHOST","op":"=","value":1}]}""", "不存在")]
    [InlineData("""{"table":"COP_ORDER_M","on":["GHOST"],"conditions":[{"column":"CONFIRM_TAG","op":"=","value":1}]}""", "关联列")]
    [InlineData("""{"table":"COP_ORDER_M","on":["ORDER_NO"],"conditions":[{"column":"CONFIRM_TAG","op":"=","value":{"a":1}}]}""", "标量")]
    [InlineData("""{"table":"COP_OTHER","on":["ORDER_NO"],"conditions":[{"column":"X","op":"=","value":1}]}""", "既不是")]
    [InlineData("""{"table":"COP_ORDER_M","conditions":[{"column":"CONFIRM_TAG","op":"=","value":1}]}""", "关联列")]
    [InlineData("""{"table":"COP_ORDER_M","on":["ORDER_NO"],"conditions":[]}""", "缺少条件")]
    [InlineData("""{"unknown":1}""", "条件")]
    public async Task Validate_Rejects_Malformed_Row_Filters(string rowFilter, string expectedFragment)
    {
        var validator = new MetricDefinitionValidator(Probe());
        var result = await validator.ValidateAsync(
            Input("SUM(AMOUNT_TAX)", rowFilter: rowFilter,
                masterTable: "COP_ORDER_M",
                masterColumns: Columns("ORDER_DATE", "CLIENT_ID", "CONFIRM_TAG", "ORDER_NO", "ORDER_TYPE"),
                allowedColumns: Columns("AMOUNT_TAX")), CancellationToken.None);
        Assert.False(result.Ok);
        Assert.Contains(expectedFragment, result.Error);
    }

    [Fact]
    public async Task Validate_Row_Filter_On_Source_Table_Is_Inline()
    {
        var validator = new MetricDefinitionValidator(Probe());
        var result = await validator.ValidateAsync(
            Input("SUM(AMOUNT_TAX)", rowFilter:
                """{"conditions":[{"column":"QTY","op":">","value":0}]}""",
                allowedColumns: Columns("AMOUNT_TAX", "QTY")), CancellationToken.None);
        Assert.True(result.Ok, result.Error);
        Assert.Equal("COP_ORDER_D", result.RowFilter.Table);
        Assert.Empty(result.RowFilter.JoinColumns);
    }

    [Theory]
    [InlineData("SUM(QTY) / NULLIF(SUM(IN_QTY), 0)", true)]
    [InlineData("SUM(QTY) / NULLIF(SUM(IN_QTY), 0) / NULLIF(SUM(OUT_QTY), 0)", true)]
    [InlineData("SUM(QTY) / 2", true)]
    [InlineData("SUM(QTY) / 2.5", true)]
    [InlineData("SUM(QTY) / SUM(IN_QTY)", false)]
    [InlineData("SUM(QTY) / 0", false)]
    [InlineData("AVG(AMOUNT / 1.5)", true)]
    [InlineData("SUM(QTY) / NULLIF(SUM(IN_QTY), 0) / SUM(OUT_QTY)", false)]
    public async Task Validate_Division_Denominator_Must_Be_Nullif_Or_Nonzero_Literal(
        string expression, bool shouldPass)
    {
        var validator = new MetricDefinitionValidator(Probe());
        var result = await validator.ValidateAsync(
            Input(expression, sourceTable: "INV_PRO_DEPOT",
                allowedColumns: Columns("QTY", "IN_QTY", "OUT_QTY", "AMOUNT")), CancellationToken.None);
        Assert.Equal(shouldPass, result.Ok);
        if (!shouldPass)
        {
            Assert.Contains("除", result.Error);
        }
    }
}
