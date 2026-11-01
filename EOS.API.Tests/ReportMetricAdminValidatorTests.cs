using EOS.API.Data;
using EOS.API.Data.Reports;
using EOS.API.Features.Assistant.Metrics;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 口径维护的校验（ADR-046 `FIX_AT=metric`）：界面更严的那一半在服务端。
/// 表达式必须过受控解析，来源表与维度必须是物理列，行过滤的表归属与关联列沿用
/// `MetricDefinitionValidator`（与 resolve_metric 同一实现，不另立标准）。
/// </summary>
public sealed class ReportMetricAdminValidatorTests
{
    private sealed class FakeProbe : IMetricSchemaProbe
    {
        public Task<bool> TableExistsAsync(string table, CancellationToken token) =>
            Task.FromResult(table is "COP_ORDER_D" or "COP_ORDER_M");

        public Task<IReadOnlySet<string>> GetColumnsAsync(string table, CancellationToken token)
        {
            IReadOnlySet<string> columns = table switch
            {
                "COP_ORDER_D" => new HashSet<string>(
                    ["ORDER_NO", "ORDER_TYPE", "SERIAL_NO", "AMOUNT_TAX", "QTY", "PRO_NO"], StringComparer.OrdinalIgnoreCase),
                "COP_ORDER_M" => new HashSet<string>(
                    ["ORDER_NO", "ORDER_TYPE", "APPROVED", "ORDER_DATE", "CLIENT_ID"], StringComparer.OrdinalIgnoreCase),
                _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            };
            return Task.FromResult(columns);
        }
    }

    private static ReportMetricAdminDraft Draft(
        string metricId = "smoke_amount",
        string definition = "SUM(AMOUNT_TAX)",
        string sourceTable = "COP_ORDER_D",
        string? dimensions = "ORDER_DATE,CLIENT_ID",
        string? rowFilter = """{"table":"COP_ORDER_M","on":["ORDER_TYPE","ORDER_NO"],"conditions":[{"column":"APPROVED","op":"=","value":true}]}""",
        string status = "CANDIDATE") =>
        new(metricId, "冒烟口径", definition, sourceTable, dimensions, "销售", rowFilter, null, status);

    private static ReportMetricAdminValidator Validator() => new(new FakeProbe());

    [Fact]
    public async Task Accepts_Valid_Definition()
    {
        await Validator().ValidateAsync(Draft(), CancellationToken.None);
    }

    [Theory]
    [InlineData("Sales Amount")]
    [InlineData("ab")]
    [InlineData("1abc")]
    [InlineData("sales-amount")]
    public async Task Rejects_Invalid_Metric_Id(string metricsId)
    {
        var error = await Assert.ThrowsAsync<ArgumentException>(() =>
            Validator().ValidateAsync(Draft(metricId: metricsId), CancellationToken.None));
        Assert.Contains("口径标识", error.Message);
    }

    [Fact]
    public async Task Rejects_Unparsable_Definition()
    {
        var error = await Assert.ThrowsAsync<ArgumentException>(() =>
            Validator().ValidateAsync(Draft(definition: "SUM("), CancellationToken.None));
        Assert.Contains("无法按受控语法解析", error.Message);
    }

    [Fact]
    public async Task Rejects_Unknown_Source_Table()
    {
        var error = await Assert.ThrowsAsync<ArgumentException>(() =>
            Validator().ValidateAsync(Draft(sourceTable: "NO_SUCH_TABLE"), CancellationToken.None));
        Assert.Contains("来源表不存在", error.Message);
    }

    [Fact]
    public async Task Rejects_Dimension_Not_On_Source_Or_Filter_Table()
    {
        var error = await Assert.ThrowsAsync<ArgumentException>(() =>
            Validator().ValidateAsync(Draft(dimensions: "ORDER_DATE,REGION"), CancellationToken.None));
        Assert.Contains("维度列", error.Message);
    }

    [Fact]
    public async Task Rejects_Unknown_Confirm_Status()
    {
        var error = await Assert.ThrowsAsync<ArgumentException>(() =>
            Validator().ValidateAsync(Draft(status: "DONE"), CancellationToken.None));
        Assert.Contains("确认状态", error.Message);
    }

    [Fact]
    public async Task Rejects_Row_Filter_Table_That_Does_Not_Exist()
    {
        var error = await Assert.ThrowsAsync<ArgumentException>(() => Validator().ValidateAsync(
            Draft(rowFilter: """{"table":"NO_SUCH_TABLE","on":["ORDER_TYPE"],"conditions":[{"column":"APPROVED","op":"=","value":true}]}"""),
            CancellationToken.None));
        Assert.Contains("行过滤表不存在", error.Message);
    }

    [Fact]
    public async Task Rejects_Row_Filter_Column_Not_In_Table()
    {
        var error = await Assert.ThrowsAsync<ArgumentException>(() => Validator().ValidateAsync(
            Draft(rowFilter: """{"table":"COP_ORDER_M","on":["ORDER_TYPE","ORDER_NO"],"conditions":[{"column":"NOPE","op":"=","value":true}]}"""),
            CancellationToken.None));
        Assert.Contains("行过滤列", error.Message);
    }
}
