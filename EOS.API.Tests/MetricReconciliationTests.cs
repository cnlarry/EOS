using EOS.API.Data;
using EOS.API.Features.Assistant.Metrics;
using EOS.API.Models;
using EOS.API.Security;
using EOS.API.Telemetry;
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

    private static WorkbenchDefinition Definition() => new(
        1405, "客户订单", "COP_ORDER_M", "COP_ORDER_D",
        MasterFields:
        [
            new WorkbenchField("ORDER_NO", "订单号", "nvarchar", 100, null, true),
            new WorkbenchField("ORDER_TYPE", "单别", "nvarchar", 100, null, true),
            new WorkbenchField("CONFIRM_TAG", "批核", "bit", 100, null, false),
            new WorkbenchField("ORDER_DATE", "订单日期", "datetime", 100, null, false),
            new WorkbenchField("CLIENT_ID", "客户", "nvarchar", 100, null, false),
        ],
        DetailFields:
        [
            new WorkbenchField("ORDER_NO", "订单号", "nvarchar", 100, null, true),
            new WorkbenchField("ORDER_TYPE", "单别", "nvarchar", 100, null, true),
            new WorkbenchField("SERIAL_NO", "项次", "int", 100, null, true),
            new WorkbenchField("AMOUNT_TAX", "含税金额", "decimal", 100, null, false),
            new WorkbenchField("QTY", "数量", "decimal", 100, null, false),
        ],
        DefaultSort: null, HasAdd: true, HasEdit: true, DetailNoSave: false,
        MasterPkOrder: ["ORDER_TYPE", "ORDER_NO"], DetailNoFields: string.Empty,
        HasWorkflow: true, UserId: "reconciliation", ExecTag: "A");

    private static async Task<(MetricPlan Plan, MetricDefinitionRow Metric)> BuildPlanAsync(
        string connectionString, IReadOnlyList<MetricDimensionFilter> dimensions)
    {
        var repository = new MetricRepository(ConnectionFactory(connectionString));
        var probe = new SysMetricSchemaProbe(ConnectionFactory(connectionString));
        var validator = new MetricDefinitionValidator(probe);
        var metric = await repository.GetAsync(MetricId, CancellationToken.None)
            ?? throw new InvalidOperationException("口径 sales_amount 不存在。");
        if (!string.Equals(metric.ConfirmStatus, "CONFIRMED", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("口径 sales_amount 未确认，对账前提不成立。");
        }

        var parse = MetricExpressionParser.Parse(metric.Definition);
        Assert.True(parse.Ok, parse.Error);
        var definition = Definition();
        var validation = await validator.ValidateAsync(new MetricValidationInput(
            parse.Expression!, metric.SourceTable,
            definition.DetailFields.Select(f => f.Key).ToHashSet(StringComparer.OrdinalIgnoreCase),
            metric.DimensionKeys, metric.RowFilter,
            definition.MasterTable,
            definition.MasterFields.Select(f => f.Key).ToHashSet(StringComparer.OrdinalIgnoreCase)),
            CancellationToken.None);
        Assert.True(validation.Ok, validation.Error);

        // 权限范围最小化（EXEC_TAG=A、无 DATA_FILTER）：对账对比的是口径本身，
        // 范围注入一致性由单元测试与既有 scope 过滤器回归保障。
        var permission = new ModulePermission(MinimalRights());
        var scopeFilter = new WorkbenchScopeFilter(new ApiMetrics());
        Assert.True(scopeFilter.TryBuildRecordScopePredicate(definition, permission.Rights.DataFilter,
            out var scopePredicate, out var scopeValues));

        var joinColumns = definition.MasterPkOrder;
        var plan = MetricPlanCompiler.Compile(parse.Expression!, metric.SourceTable,
            validation.RowFilter, dimensions, definition.MasterTable, joinColumns,
            scopePredicate, scopeValues);
        return (plan, metric);
    }

    private static ModuleRights MinimalRights() => new(
        CanBrowse: true, CanViewCost: false, CanViewSecrecy: false, CanSetup: false,
        DeniedMasterFields: new HashSet<string>(), DeniedDetailFields: new HashSet<string>(),
        CanAddNew: false, CanEdit: false, CanDelete: false, CanApprove: false, CanDeapprove: false,
        CanEndCase: false, CanUnEndCase: false, CanFileView: false, CanFileUpda: false,
        CanFileEdit: false, CanFileDele: false,
        DenyNewMasterFields: new HashSet<string>(), DenyNewDetailFields: new HashSet<string>(),
        DenyModiMasterFields: new HashSet<string>(), DenyModiDetailFields: new HashSet<string>(),
        DataFilter: string.Empty, ExecuteTag: "A");

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
}
