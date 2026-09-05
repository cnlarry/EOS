using EOS.API.Data;
using EOS.API.Features.Assistant.Metrics;
using Microsoft.Extensions.Configuration;
using Microsoft.Data.SqlClient;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// Reconciliation of the compiled controlled plan for the business-confirmed
/// sales_amount metric (SUM(AMOUNT_TAX) over approved orders only) against a
/// hand-written reference query. Scenario dimensions are sampled from live data
/// so the check stays valid as data evolves. Requires a real database connection.
/// </summary>
public sealed class MetricReconciliationTests
{
    private const string ConnectionStringEnvironmentVariable = "EOS_ERP_TEST_CONNECTION";
    private const string MetricId = "sales_amount";

    private static string? TestConnection() =>
        Environment.GetEnvironmentVariable(ConnectionStringEnvironmentVariable);

    private static string RequireConnection()
    {
        var connectionString = TestConnection()
            ?? throw new InvalidOperationException(
                "对账测试需要 EOS_ERP_TEST_CONNECTION；未配置即失败（无连接跳过≠已验证）。");
        using var connection = new SqlConnection(connectionString);
        connection.Open();
        using var command = new SqlCommand(
            "SELECT 1 WHERE COL_LENGTH(N'dbo.REPORT_METRIC', N'CONFIRM_STATUS') IS NOT NULL;",
            connection);
        if (command.ExecuteScalar() is null)
        {
            throw new InvalidOperationException(
                "dbo.REPORT_METRIC.CONFIRM_STATUS 不存在（迁移 046 未执行）；未就绪即失败。");
        }
        return connectionString;
    }

    private static async Task<(MetricPlan Plan, MetricDefinitionRow Metric)> BuildPlanAsync(
        string connectionString, IReadOnlyList<MetricDimensionFilter> dimensions, string metricId = MetricId)
    {
        var factory = ConnectionFactory(connectionString);
        var repository = new MetricRepository(factory);
        var probe = new SysMetricSchemaProbe(factory);
        var validator = new MetricDefinitionValidator(probe);
        var metric = await repository.GetAsync(metricId, CancellationToken.None)
            ?? throw new InvalidOperationException($"口径 {metricId} 不存在。");
        if (!string.Equals(metric.ConfirmStatus, "CONFIRMED", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"口径 {metricId} 未确认，对账前提不成立。");
        }

        var parse = MetricExpressionParser.Parse(metric.Definition);
        Assert.True(parse.Ok, parse.Error);

        // 对账为开发者视角全列可见；EXEC_TAG=A、无 DATA_FILTER、无模块 FILTER——
        // 对比的是口径计算本身，权限注入一致性由 ResolveMetricTool 单测覆盖。
        var sourceColumns = await probe.GetColumnsAsync(metric.SourceTable, CancellationToken.None);
        string? masterTable = null;
        IReadOnlySet<string>? masterColumns = null;
        var joinColumns = new List<string>();
        if (!string.IsNullOrWhiteSpace(metric.RowFilter))
        {
            using var filterDoc = System.Text.Json.JsonDocument.Parse(metric.RowFilter);
            if (filterDoc.RootElement.TryGetProperty("table", out var filterTable)
                && filterTable.GetString() is { Length: > 0 } tableName
                && !tableName.Equals(metric.SourceTable, StringComparison.OrdinalIgnoreCase))
            {
                masterTable = tableName;
                masterColumns = await probe.GetColumnsAsync(tableName, CancellationToken.None);
            }
            if (filterDoc.RootElement.TryGetProperty("on", out var onArray)
                && onArray.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                joinColumns.AddRange(onArray.EnumerateArray()
                    .Select(e => e.GetString() ?? string.Empty)
                    .Where(name => name.Length > 0));
            }
        }

        var validation = await validator.ValidateAsync(new MetricValidationInput(
            parse.Expression!, metric.SourceTable, sourceColumns, metric.DimensionKeys,
            metric.RowFilter, masterTable, masterColumns), CancellationToken.None);
        Assert.True(validation.Ok, validation.Error);

        var plan = MetricPlanCompiler.Compile(parse.Expression!, metric.SourceTable,
            validation.RowFilter, dimensions, masterTable, joinColumns, null, null);
        return (plan, metric);
    }

    /// <summary>人工口径 A 参考查询：明细含税金额合计，仅统计已批核主单。</summary>
    private static string ReferenceSql(string? clientEquals, string? dateRangePredicate)
    {
        var conditions = new List<string> { "m.[CONFIRM_TAG] = 1" };
        if (clientEquals is not null)
        {
            conditions.Add($"m.[CLIENT_ID] = '{clientEquals.Replace("'", "''")}'");
        }
        if (dateRangePredicate is not null)
        {
            conditions.Add(dateRangePredicate);
        }
        return "SELECT SUM(d.[AMOUNT_TAX]) FROM dbo.[COP_ORDER_D] d" +
               " JOIN dbo.[COP_ORDER_M] m ON m.[ORDER_TYPE]=d.[ORDER_TYPE] AND m.[ORDER_NO]=d.[ORDER_NO]" +
               " WHERE " + string.Join(" AND ", conditions) + ";";
    }

    private static DbConnectionFactory ConnectionFactory(string connectionString) =>
        new(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:ErpDatabase"] = connectionString,
            })
            .Build());

    private static async Task<decimal?> ExecuteScalarAsync(string connectionString, string sql)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        var result = await command.ExecuteScalarAsync();
        return result is null or DBNull ? null : Convert.ToDecimal(result);
    }

    private static void AssertEqual(decimal? expected, decimal? actual)
    {
        if (expected is null || actual is null)
        {
            Assert.Equal(expected, actual);
            return;
        }
        Assert.True(Math.Abs(expected.Value - actual.Value) < 0.0001m,
            $"对账不一致：参考值 {expected}，计划值 {actual}");
    }

    [Fact]
    public async Task Plan_Matches_Reference_For_Total_Sales_Amount()
    {
        var connectionString = RequireConnection();
        var (plan, _) = await BuildPlanAsync(connectionString, []);
        var actual = await new MetricExecutor(ConnectionFactory(connectionString)).ExecuteAsync(plan, CancellationToken.None);
        var expected = await ExecuteScalarAsync(connectionString, ReferenceSql(null, null));
        AssertEqual(expected, actual);
    }

    [Fact]
    public async Task Plan_Matches_Reference_For_Sampled_Client()
    {
        var connectionString = RequireConnection();
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            "SELECT TOP 1 m.[CLIENT_ID] FROM dbo.[COP_ORDER_M] m WITH (NOLOCK)" +
            " WHERE m.[CLIENT_ID] IS NOT NULL AND m.[CONFIRM_TAG] = 1 ORDER BY m.[CLIENT_ID];", connection);
        var client = await command.ExecuteScalarAsync() as string
            ?? throw new InvalidOperationException("库内没有已批核订单样本，无法抽样对账。");

        var (plan, _) = await BuildPlanAsync(connectionString,
        [
            new MetricDimensionFilter("CLIENT_ID", MetricDimensionTarget.Master,
                MetricDimensionOperator.Equals, [client]),
        ]);
        var actual = await new MetricExecutor(ConnectionFactory(connectionString)).ExecuteAsync(plan, CancellationToken.None);
        var expected = await ExecuteScalarAsync(connectionString, ReferenceSql(client, null));
        AssertEqual(expected, actual);
    }

    [Fact]
    public async Task Plan_Matches_Reference_For_Sampled_Date_Range()
    {
        var connectionString = RequireConnection();
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            "SELECT CONVERT(NCHAR(10), MIN(m.[ORDER_DATE]), 120), CONVERT(NCHAR(10), MAX(m.[ORDER_DATE]), 120)" +
            " FROM dbo.[COP_ORDER_M] m WITH (NOLOCK) WHERE m.[CONFIRM_TAG] = 1;", connection);
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync() || reader.IsDBNull(0) || reader.IsDBNull(1))
        {
            throw new InvalidOperationException("库内没有已批核订单日期样本，无法抽样对账。");
        }
        var from = reader.GetString(0);
        var to = reader.GetString(1);

        var (plan, _) = await BuildPlanAsync(connectionString,
        [
            new MetricDimensionFilter("ORDER_DATE", MetricDimensionTarget.Master,
                MetricDimensionOperator.Range, [from, to]),
        ]);
        var actual = await new MetricExecutor(ConnectionFactory(connectionString)).ExecuteAsync(plan, CancellationToken.None);
        var expected = await ExecuteScalarAsync(connectionString,
            ReferenceSql(null, $"m.[ORDER_DATE] >= '{from}' AND m.[ORDER_DATE] <= '{to}'"));
        AssertEqual(expected, actual);
    }
    private const string ApprovedJoin =
        " JOIN dbo.[COP_ORDER_M] m ON m.[ORDER_TYPE]=d.[ORDER_TYPE] AND m.[ORDER_NO]=d.[ORDER_NO] WHERE m.[CONFIRM_TAG] = 1";

    public static TheoryData<string, string> ConfirmedMetricReferences() => new()
    {
        { "sales_amount_ex", "SELECT SUM(d.[AMOUNT]) FROM dbo.[COP_ORDER_D] d" + ApprovedJoin },
        { "sales_qty", "SELECT SUM(d.[QTY]) FROM dbo.[COP_ORDER_D] d" + ApprovedJoin },
        { "sales_discount", "SELECT SUM(d.[REBATE]) FROM dbo.[COP_ORDER_D] d" + ApprovedJoin },
        { "order_count", "SELECT COUNT(DISTINCT d.[ORDER_NO]) FROM dbo.[COP_ORDER_D] d" + ApprovedJoin },
        { "account_receivable",
            "SELECT SUM(d.[AMOUNT_TAX]) FROM dbo.[COP_ACCOUNT_D] d" +
            " JOIN dbo.[COP_ACCOUNT_M] m ON m.[ACCOUNT_TYPE]=d.[ACCOUNT_TYPE] AND m.[ACCOUNT_NO]=d.[ACCOUNT_NO]" +
            " WHERE m.[CONFIRM_TAG] = 1" },
        { "payment_amount",
            "SELECT SUM(d.[AMOUNT]) FROM dbo.[PUR_PAY_D] d" +
            " JOIN dbo.[PUR_PAY_M] m ON m.[PAY_TYPE]=d.[PAY_TYPE] AND m.[PAY_NO]=d.[PAY_NO]" +
            " WHERE m.[CONFIRM_TAG] = 1" },
        { "purchase_amount",
            "SELECT SUM(d.[AMOUNT_TAX]) FROM dbo.[PUR_PURCHASE_D] d" +
            " JOIN dbo.[PUR_PURCHASE_M] m ON m.[PURCHASE_TYPE]=d.[PURCHASE_TYPE] AND m.[PURCHASE_NO]=d.[PURCHASE_NO]" +
            " WHERE m.[CONFIRM_TAG] = 1" },
        { "employee_count", "SELECT COUNT(DISTINCT [EMP_ID]) FROM dbo.[HR_EMPLOYEE]" },
        { "inventory_qty", "SELECT SUM([QTY]) FROM dbo.[INV_PRO_DEPOT]" },
    };

    [Theory]
    [MemberData(nameof(ConfirmedMetricReferences))]
    public async Task Plan_Matches_Reference_For_Confirmed_Metrics(string metricId, string referenceSql)
    {
        var connectionString = RequireConnection();
        var (plan, _) = await BuildPlanAsync(connectionString, [], metricId);
        var actual = await new MetricExecutor(ConnectionFactory(connectionString)).ExecuteAsync(plan, CancellationToken.None);
        var expected = await ExecuteScalarAsync(connectionString, referenceSql + ";");
        AssertEqual(expected, actual);
    }

    [Fact]
    public async Task Ghost_Column_Metrics_Are_Deterministically_Refused_On_Production_Data()
    {
        // inventory_turnover 引用的 IN_QTY 列在 INV_PRO_DEPOT 上不存在（sys.columns 核实），
        // validator 必须在生产数据上确定性拒绝，绝不静默降级执行。
        var connectionString = RequireConnection();
        var repository = new MetricRepository(ConnectionFactory(connectionString));
        var probe = new SysMetricSchemaProbe(ConnectionFactory(connectionString));
        var validator = new MetricDefinitionValidator(probe);
        var metric = await repository.GetAsync("inventory_turnover", CancellationToken.None)
            ?? throw new InvalidOperationException("口径 inventory_turnover 不存在。");
        var parse = MetricExpressionParser.Parse(metric.Definition);
        Assert.True(parse.Ok, parse.Error);
        var sourceColumns = await probe.GetColumnsAsync(metric.SourceTable, CancellationToken.None);
        var validation = await validator.ValidateAsync(new MetricValidationInput(
            parse.Expression!, metric.SourceTable, sourceColumns, metric.DimensionKeys,
            metric.RowFilter, null, null), CancellationToken.None);
        Assert.False(validation.Ok);
        Assert.Contains("不存在", validation.Error);
    }
}
