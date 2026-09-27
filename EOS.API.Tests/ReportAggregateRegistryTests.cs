using EOS.API.Data;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 汇总报表受控数据源注册表的闭集校验：报表编号/参数/列/排序字段全部来自服务端常量，
/// 客户端只能给条件值。这里钉住"注册项本身合法"与"排序只能落在声明列上"两条边界。
/// </summary>
public sealed class ReportAggregateRegistryTests
{
    private static readonly string[] ExpectedReportIds =
    [
        "HR_Employee_1", "HR_Employee_3", "HR_Employee_4", "HR_Employee_5",
        "HR_Employee_6", "HR_Employee_7", "HR_Diary_1",
        "INV_Pro_Depot_1", "INV_Pro_Depot_1_H", "INV_Pro_Depot_1_sum",
        "INV_Batch_Expiry_1",
    ];

    [Fact]
    public void Registry_CoversPortedReports_AndLookupIsCaseInsensitive()
    {
        Assert.Equal(ExpectedReportIds.OrderBy(item => item), ReportAggregateRegistry.RegisteredReportIds.OrderBy(item => item));
        foreach (var reportId in ExpectedReportIds)
        {
            var aggregate = ReportAggregateRegistry.Find(reportId);
            Assert.NotNull(aggregate);
            Assert.Equal(reportId, aggregate!.ReportId);
            Assert.Same(aggregate, ReportAggregateRegistry.Find($"  {reportId.ToUpperInvariant()}  "));
        }
        Assert.Null(ReportAggregateRegistry.Find(null));
        Assert.Null(ReportAggregateRegistry.Find("  "));
        Assert.Null(ReportAggregateRegistry.Find("P_RPT_HR_EMPLOYEE_1"));
    }

    [Fact]
    public void Registry_EntriesAreSelfConsistent()
    {
        foreach (var reportId in ExpectedReportIds)
        {
            var aggregate = ReportAggregateRegistry.Find(reportId)!;
            Assert.False(string.IsNullOrWhiteSpace(aggregate.Sql));
            // 排序由查询层统一追加：SQL 本体不得自带收尾 ORDER BY、也不得以分号结束（否则拼出非法语句）；
            // WITHIN GROUP (ORDER BY …) 属合法用法，只禁"结尾处"的 ORDER BY
            Assert.DoesNotMatch(@"(?i)order\s+by\s+\[?[A-Za-z_][A-Za-z0-9_]*\]?\s*$", aggregate.Sql.Trim());
            Assert.DoesNotContain(";", aggregate.Sql);
            Assert.False(string.IsNullOrWhiteSpace(aggregate.OrderBy));
            Assert.NotEmpty(aggregate.Columns);
            // 列键唯一；列名与排序字段都是合法标识符（进 SQL 的都是声明值，不接受客户端拼接）
            Assert.Equal(aggregate.Columns.Count, aggregate.Columns.Select(item => item.Key).Distinct(StringComparer.OrdinalIgnoreCase).Count());
            foreach (var column in aggregate.Columns)
            {
                Assert.True(WorkbenchSql.Identifier.IsMatch(column.Key), $"{reportId} 列名非法：{column.Key}");
                Assert.False(string.IsNullOrWhiteSpace(column.Label));
            }
            foreach (var field in aggregate.OrderBy.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                Assert.Contains(aggregate.Columns, column => column.Key.Equals(field, StringComparison.OrdinalIgnoreCase));
            // 参数名唯一且都有取数来源（条件序号或常量）
            Assert.Equal(aggregate.Parameters.Count, aggregate.Parameters.Select(item => item.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count());
            foreach (var parameter in aggregate.Parameters)
            {
                Assert.True(WorkbenchSql.Identifier.IsMatch(parameter.Name), $"{reportId} 参数名非法：{parameter.Name}");
                // 取数来源三选一：条件序号（前端） / 注册表常量 / 系统参数键
                Assert.True(
                    parameter.Constant is not null || parameter.SerialNo >= 1 || parameter.SystemParameterKey is { Length: > 0 },
                    $"{reportId} 参数 {parameter.Name} 缺少取数来源");
                // 系统参数键必须是"归属模块|键"的形态（读取侧按它查 SYSSS）
                if (parameter.SystemParameterKey is { Length: > 0 } systemKey)
                    Assert.Matches(@"^\d{6}\|[A-Za-z_][A-Za-z0-9_]*$", systemKey);
                Assert.True(parameter.MaxLength > 0);
            }
            // SQL 引用的参数必须都在声明内（防止漏传导致运行时"未提供参数"）
            foreach (var match in System.Text.RegularExpressions.Regex.Matches(aggregate.Sql, @"@[A-Za-z_][A-Za-z0-9_]*").Cast<System.Text.RegularExpressions.Match>())
                Assert.Contains(aggregate.Parameters, parameter => $"@{parameter.Name}".Equals(match.Value, StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public void BuildAggregateSql_OnlyAcceptsDeclaredColumnsAsSortFields()
    {
        var aggregate = ReportAggregateRegistry.Find("HR_Employee_3")!;

        Assert.EndsWith("ORDER BY DEPT_ID, PROVINCE_ID;", ReportRepository.BuildAggregateSql(aggregate, []));
        // 声明列（含表限定写法）可用于排序
        Assert.EndsWith("ORDER BY [MAN_COUNT];", ReportRepository.BuildAggregateSql(aggregate, ["MAN_COUNT"]));
        Assert.EndsWith("ORDER BY [MAN_COUNT], [DEPT_ID];",
            ReportRepository.BuildAggregateSql(aggregate, ["HR_Employee_3.MAN_COUNT", "DEPT_ID"]));
        // 未声明列/注入尝试一律被丢弃并回落默认排序
        Assert.EndsWith("ORDER BY DEPT_ID, PROVINCE_ID;", ReportRepository.BuildAggregateSql(aggregate, ["1; DROP TABLE HR_EMPLOYEE"]));
        Assert.EndsWith("ORDER BY DEPT_ID, PROVINCE_ID;", ReportRepository.BuildAggregateSql(aggregate, ["[MAN_COUNT] DESC", "NOT_A_COLUMN"]));
        Assert.EndsWith("ORDER BY [MAN_COUNT];", ReportRepository.BuildAggregateSql(aggregate, ["MAN_COUNT", "MAN_COUNT"]));
    }

    [Fact]
    public void Definition_ExposesAggregateParametersAsClientMetadata()
    {
        var definition = new EOS.API.Models.ReportDefinition(
            18029811, "考勤分析表", string.Empty, null, [], [], [], [])
        {
            Aggregate = ReportAggregateRegistry.Find("HR_Diary_1"),
        };

        Assert.Equal("aggregate", definition.DataSource);
        Assert.Equal(["date1", "date2"], definition.Parameters.Select(item => item.Name));
        Assert.Equal([1, 1], definition.Parameters.Select(item => item.SerialNo));
        Assert.Equal([false, true], definition.Parameters.Select(item => item.IsTo));

        var tableDefinition = new EOS.API.Models.ReportDefinition(
            1405, "库存报表", "COP_ORDER_M", null, [], [], [], []);
        Assert.Equal("table", tableDefinition.DataSource);
        Assert.Empty(tableDefinition.Parameters);
    }
}
