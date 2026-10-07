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
        // ADR-024 收尾（2026-10-08）：这两张系统清单报表原先挂在没有主表的空壳模块名下、
        // 长期 404（归属模块 230901/230902 无主表、报表也未登记汇总源）。
        // 数据源就是 MODULES / TABLES 表本身，按汇总报表登记后真正可达。
        "SYS_Modules_List", "SYS_Talbles_List",
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
    public void Registry_EveryAggregateColumnDeclaresFieldPrivilege()
    {
        foreach (var reportId in ExpectedReportIds)
        {
            var aggregate = ReportAggregateRegistry.Find(reportId)!;
            foreach (var column in aggregate.Columns)
            {
                // 聚合列没有物理表可供反查 FIELDS，成本位/保密位只能在注册表里逐列声明；
                // 留空等于"漏标"，运行期按 fail-closed 丢列（静默少列），故在构建期就用用例钉死。
                Assert.True(column.IsCost is not null,
                    $"{reportId} 列 {column.Key} 未声明 IsCost（漏标不得等于公开）");
                Assert.True(column.IsSecrecy is not null,
                    $"{reportId} 列 {column.Key} 未声明 IsSecrecy（漏标不得等于公开）");
            }
        }
    }

    [Fact]
    public void FilterAggregateColumns_AppliesSameFieldPrivilegesAsMasterTableBranch()
    {
        var denied = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "PRO_NO" };
        var declared = new List<EOS.API.Models.ReportColumn>
        {
            new("QTY", "数量", "float", null, IsCost: false, IsSecrecy: false),
            new("PRICE", "单价", "float", null, IsCost: true, IsSecrecy: false),
            new("ID_CARD", "身份证", "nvarchar", null, IsCost: false, IsSecrecy: true),
            new("PRO_NO", "料号", "nvarchar", null, IsCost: false, IsSecrecy: false),
        };

        // 无成本权、无保密权：成本列与保密列被过滤；拒绝名单命中的列也被过滤
        var limited = ReportRepository.FilterAggregateColumns(declared, canViewCost: false, canViewSecrecy: false, denied);
        Assert.Equal(["QTY"], limited.Select(item => item.Key));

        // 有成本权、无保密权：成本列放行，保密列仍被过滤
        var costOnly = ReportRepository.FilterAggregateColumns(declared, canViewCost: true, canViewSecrecy: false, denied);
        Assert.Equal(["QTY", "PRICE"], costOnly.Select(item => item.Key));

        // 两者都有、拒绝名单为空：全列放行
        var full = ReportRepository.FilterAggregateColumns(declared, canViewCost: true, canViewSecrecy: true,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        Assert.Equal(declared.Count, full.Count);
    }

    [Fact]
    public void FilterAggregateColumns_TreatsUndeclaredPrivilegeAsDenied()
    {
        var declared = new List<EOS.API.Models.ReportColumn>
        {
            // 未声明任何权限位（既有写法或漏标）：必须被丢弃，不得默认放行
            new("PRICE_Q", "期初单价", "float"),
            new("PRICE_J", "收入单价", "float", null, IsCost: true),
            new("DEPT_NAME", "部门", "nvarchar", null, IsCost: false),
            new("QTY", "数量", "float", null, IsCost: false, IsSecrecy: false),
        };

        var result = ReportRepository.FilterAggregateColumns(declared, canViewCost: true, canViewSecrecy: true,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase));

        Assert.Equal(["QTY"], result.Select(item => item.Key));
    }

    [Fact]
    public void FilterAggregateColumns_UsesDenyKeyWhenDeclared()
    {
        var denied = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "PRICE" };
        var declared = new List<EOS.API.Models.ReportColumn>
        {
            // 派生列可以声明它对应的物理字段名作为拒绝名单匹配键（此列键名与 F_ID 不同）
            new("PRICE_Q", "期初单价", "float", null, IsCost: true, IsSecrecy: false, DenyKey: "PRICE"),
        };

        Assert.Empty(ReportRepository.FilterAggregateColumns(declared, canViewCost: true, canViewSecrecy: true, denied));
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
