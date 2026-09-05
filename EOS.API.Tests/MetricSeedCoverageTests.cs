using EOS.API.Features.Assistant.Metrics;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// Coverage classification of the REPORT_METRIC seed definitions (migration 027)
/// against the controlled metric SQL subset. Every seed must parse and validate;
/// each is classified into one of the four known definition forms so unsupported
/// shapes would surface here instead of at runtime.
/// </summary>
public sealed class MetricSeedCoverageTests
{
    public enum SeedForm
    {
        SimpleAggregate,
        AggregateArithmetic,
        AggregateDivisionNullIf,
        DistinctCount,
    }

    private sealed record Seed(
        string MetricId, string Definition, string SourceTable,
        string DimensionKeys, SeedForm Form, string[] SourceColumns,
        string? MasterTable = null, string[]? MasterColumns = null);

    private static readonly Seed[] Seeds =
    [
        new("sales_amount", "SUM(AMOUNT_TAX)", "COP_ORDER_D", "ORDER_DATE,CLIENT_ID,PRO_NO",
            SeedForm.SimpleAggregate, ["ORDER_NO", "ORDER_TYPE", "SERIAL_NO", "AMOUNT_TAX", "AMOUNT", "QTY", "REBATE", "PRO_NO"],
            "COP_ORDER_M", ["ORDER_NO", "ORDER_TYPE", "ORDER_DATE", "CLIENT_ID", "CONFIRM_TAG"]),
        new("sales_qty", "SUM(QTY)", "COP_ORDER_D", "ORDER_DATE,CLIENT_ID,PRO_NO",
            SeedForm.SimpleAggregate, ["ORDER_NO", "ORDER_TYPE", "SERIAL_NO", "AMOUNT_TAX", "AMOUNT", "QTY", "REBATE", "PRO_NO"],
            "COP_ORDER_M", ["ORDER_NO", "ORDER_TYPE", "ORDER_DATE", "CLIENT_ID", "CONFIRM_TAG"]),
        new("sales_discount", "SUM(REBATE)", "COP_ORDER_D", "ORDER_DATE,CLIENT_ID,PRO_NO",
            SeedForm.SimpleAggregate, ["ORDER_NO", "ORDER_TYPE", "SERIAL_NO", "AMOUNT_TAX", "AMOUNT", "QTY", "REBATE", "PRO_NO"],
            "COP_ORDER_M", ["ORDER_NO", "ORDER_TYPE", "ORDER_DATE", "CLIENT_ID", "CONFIRM_TAG"]),
        new("sales_amount_ex", "SUM(AMOUNT)", "COP_ORDER_D", "ORDER_DATE,CLIENT_ID,PRO_NO",
            SeedForm.SimpleAggregate, ["ORDER_NO", "ORDER_TYPE", "SERIAL_NO", "AMOUNT_TAX", "AMOUNT", "QTY", "REBATE", "PRO_NO"],
            "COP_ORDER_M", ["ORDER_NO", "ORDER_TYPE", "ORDER_DATE", "CLIENT_ID", "CONFIRM_TAG"]),
        new("sales_gross_margin", "SUM(AMOUNT_TAX - COST_AMOUNT)", "COP_ORDER_D", "ORDER_DATE,CLIENT_ID",
            SeedForm.AggregateArithmetic, ["ORDER_NO", "ORDER_TYPE", "SERIAL_NO", "AMOUNT_TAX", "AMOUNT", "QTY", "REBATE", "PRO_NO", "COST_AMOUNT"],
            "COP_ORDER_M", ["ORDER_NO", "ORDER_TYPE", "ORDER_DATE", "CLIENT_ID", "CONFIRM_TAG"]),
        new("order_count", "COUNT(DISTINCT ORDER_NO)", "COP_ORDER_D", "ORDER_DATE,CLIENT_ID",
            SeedForm.DistinctCount, ["ORDER_NO", "ORDER_TYPE", "SERIAL_NO", "AMOUNT_TAX", "AMOUNT", "QTY", "REBATE", "PRO_NO"],
            "COP_ORDER_M", ["ORDER_NO", "ORDER_TYPE", "ORDER_DATE", "CLIENT_ID", "CONFIRM_TAG"]),
        new("purchase_amount", "SUM(AMOUNT_TAX)", "PUR_PURCHASE_D", "PURCHASE_DATE,SUPPLIER_ID,PRO_NO",
            SeedForm.SimpleAggregate, ["PURCHASE_NO", "PURCHASE_TYPE", "SERIAL_NO", "AMOUNT_TAX", "AMOUNT", "QTY", "PRO_NO"],
            "PUR_PURCHASE_M", ["PURCHASE_NO", "PURCHASE_TYPE", "PURCHASE_DATE", "SUPPLIER_ID"]),
        new("purchase_qty", "SUM(QTY)", "PUR_PURCHASE_D", "PURCHASE_DATE,SUPPLIER_ID,PRO_NO",
            SeedForm.SimpleAggregate, ["PURCHASE_NO", "PURCHASE_TYPE", "SERIAL_NO", "AMOUNT_TAX", "AMOUNT", "QTY", "PRO_NO"],
            "PUR_PURCHASE_M", ["PURCHASE_NO", "PURCHASE_TYPE", "PURCHASE_DATE", "SUPPLIER_ID"]),
        new("purchase_order_count", "COUNT(DISTINCT PURCHASE_NO)", "PUR_PURCHASE_D", "PURCHASE_DATE,SUPPLIER_ID",
            SeedForm.DistinctCount, ["PURCHASE_NO", "PURCHASE_TYPE", "SERIAL_NO", "AMOUNT_TAX", "AMOUNT", "QTY", "PRO_NO"],
            "PUR_PURCHASE_M", ["PURCHASE_NO", "PURCHASE_TYPE", "PURCHASE_DATE", "SUPPLIER_ID"]),
        new("receive_qty", "SUM(RECEIVE_QTY)", "PUR_RECEIVE_D", "RECEIVE_DATE,SUPPLIER_ID,PRO_NO",
            SeedForm.SimpleAggregate, ["RECEIVE_NO", "RECEIVE_TYPE", "SERIAL_NO", "RECEIVE_QTY", "QTY", "PRO_NO"],
            "PUR_RECEIVE_M", ["RECEIVE_NO", "RECEIVE_TYPE", "RECEIVE_DATE", "SUPPLIER_ID"]),
        new("inventory_qty", "SUM(QTY)", "INV_PRO_DEPOT", "DEPOT_ID,PRO_NO",
            SeedForm.SimpleAggregate, ["DEPOT_ID", "PRO_NO", "QTY", "IN_QTY"]),
        new("inventory_turnover", "SUM(QTY) / NULLIF(SUM(IN_QTY),0)", "INV_PRO_DEPOT", "DEPOT_ID,PRO_NO",
            SeedForm.AggregateDivisionNullIf, ["DEPOT_ID", "PRO_NO", "QTY", "IN_QTY"]),
        new("produce_qty", "SUM(QTY)", "MOC_PRODUCE_D", "PRODUCE_DATE,PRO_NO",
            SeedForm.SimpleAggregate, ["PRODUCE_NO", "SERIAL_NO", "QTY", "FINISHED_QTY", "PRO_NO"],
            "MOC_PRODUCE_M", ["PRODUCE_NO", "PRODUCE_DATE"]),
        new("produce_finished_qty", "SUM(FINISHED_QTY)", "MOC_PRODUCE_D", "PRODUCE_DATE,PRO_NO",
            SeedForm.SimpleAggregate, ["PRODUCE_NO", "SERIAL_NO", "QTY", "FINISHED_QTY", "PRO_NO"],
            "MOC_PRODUCE_M", ["PRODUCE_NO", "PRODUCE_DATE"]),
        new("account_receivable", "SUM(AMOUNT_TAX)", "COP_ACCOUNT_D", "ACCOUNT_DATE,CLIENT_ID",
            SeedForm.SimpleAggregate, ["ACCOUNT_NO", "SERIAL_NO", "AMOUNT_TAX", "AMOUNT"],
            "COP_ACCOUNT_M", ["ACCOUNT_NO", "ACCOUNT_DATE", "CLIENT_ID"]),
        new("account_payable", "SUM(AMOUNT_TAX)", "PUR_DUE_D", "DUE_DATE,SUPPLIER_ID",
            SeedForm.SimpleAggregate, ["DUE_NO", "SERIAL_NO", "AMOUNT_TAX", "AMOUNT"],
            "PUR_DUE_M", ["DUE_NO", "DUE_DATE", "SUPPLIER_ID"]),
        new("receipt_amount", "SUM(AMOUNT)", "COP_RECEIPT_D", "RECEIPT_DATE,CLIENT_ID",
            SeedForm.SimpleAggregate, ["RECEIPT_NO", "SERIAL_NO", "AMOUNT", "AMOUNT_TAX"],
            "COP_RECEIPT_M", ["RECEIPT_NO", "RECEIPT_DATE", "CLIENT_ID"]),
        new("payment_amount", "SUM(AMOUNT)", "PUR_PAY_D", "PAY_DATE,SUPPLIER_ID",
            SeedForm.SimpleAggregate, ["PAY_NO", "SERIAL_NO", "AMOUNT", "AMOUNT_TAX"],
            "PUR_PAY_M", ["PAY_NO", "PAY_DATE", "SUPPLIER_ID"]),
        new("employee_count", "COUNT(DISTINCT EMP_ID)", "HR_EMPLOYEE", "DEPT_ID",
            SeedForm.DistinctCount, ["EMP_ID", "DEPT_ID"]),
    ];

    private sealed class SeedProbe : IMetricSchemaProbe
    {
        public Task<bool> TableExistsAsync(string table, CancellationToken token) =>
            Task.FromResult(Seeds.Any(s => s.SourceTable.Equals(table, StringComparison.OrdinalIgnoreCase)
                || s.MasterTable?.Equals(table, StringComparison.OrdinalIgnoreCase) == true));

        public Task<IReadOnlySet<string>> GetColumnsAsync(string table, CancellationToken token)
        {
            var columns = Seeds
                .Where(s => s.SourceTable.Equals(table, StringComparison.OrdinalIgnoreCase))
                .SelectMany(s => s.SourceColumns)
                .Concat(Seeds
                    .Where(s => s.MasterTable?.Equals(table, StringComparison.OrdinalIgnoreCase) == true)
                    .SelectMany(s => s.MasterColumns ?? []))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            return Task.FromResult<IReadOnlySet<string>>(columns);
        }
    }

    [Fact]
    public void All_Seed_Definitions_Parse_Into_Controlled_Ast()
    {
        var failures = Seeds
            .Select(seed => (seed.MetricId, Result: MetricExpressionParser.Parse(seed.Definition)))
            .Where(pair => !pair.Result.Ok)
            .ToList();
        Assert.True(failures.Count == 0,
            "解析失败的种子：" + string.Join("; ", failures.Select(f => $"{f.MetricId}: {f.Result.Error}")));
    }

    [Fact]
    public async Task All_Seed_Definitions_Validate_Within_Whitelist()
    {
        var validator = new MetricDefinitionValidator(new SeedProbe());
        var failures = new List<string>();
        foreach (var seed in Seeds)
        {
            var parse = MetricExpressionParser.Parse(seed.Definition);
            var input = new MetricValidationInput(
                parse.Expression!, seed.SourceTable,
                seed.SourceColumns.ToHashSet(StringComparer.OrdinalIgnoreCase),
                seed.DimensionKeys, RowFilterJson: null,
                MasterTable: seed.MasterTable,
                MasterColumns: seed.MasterColumns?.ToHashSet(StringComparer.OrdinalIgnoreCase));
            var result = await validator.ValidateAsync(input, CancellationToken.None);
            if (!result.Ok)
            {
                failures.Add($"{seed.MetricId}: {result.Error}");
            }
        }
        Assert.True(failures.Count == 0, "校验失败的种子：" + string.Join("; ", failures));
    }

    [Fact]
    public void Seed_Forms_Cover_Exactly_The_Four_Known_Shapes()
    {
        foreach (var seed in Seeds)
        {
            var expression = MetricExpressionParser.Parse(seed.Definition).Expression!;
            var actual = Classify(expression);
            Assert.True(actual == seed.Form,
                $"{seed.MetricId} 分类不符：期望 {seed.Form}，实际 {actual}");
        }
        // 形态分布与迁移 027 的种子一致（14 简单聚合 / 1 聚合内算术 / 1 聚合间除法 / 3 去重计数）。
        Assert.Equal(14, Seeds.Count(s => s.Form == SeedForm.SimpleAggregate));
        Assert.Equal(1, Seeds.Count(s => s.Form == SeedForm.AggregateArithmetic));
        Assert.Equal(1, Seeds.Count(s => s.Form == SeedForm.AggregateDivisionNullIf));
        Assert.Equal(3, Seeds.Count(s => s.Form == SeedForm.DistinctCount));
    }

    private static SeedForm Classify(MetricExpression expression) => expression switch
    {
        MetricAggregate { Distinct: true, Function: "COUNT" } => SeedForm.DistinctCount,
        MetricAggregate aggregate => ContainsBinary(aggregate.Argument)
            ? SeedForm.AggregateArithmetic
            : SeedForm.SimpleAggregate,
        _ => SeedForm.AggregateDivisionNullIf,
    };

    private static bool ContainsBinary(MetricExpression expression) => expression switch
    {
        MetricBinary => true,
        MetricAggregate aggregate => ContainsBinary(aggregate.Argument),
        MetricNullIf nullIf => ContainsBinary(nullIf.Left) || ContainsBinary(nullIf.Right),
        _ => false,
    };
}
