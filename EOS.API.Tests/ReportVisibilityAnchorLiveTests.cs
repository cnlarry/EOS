using Microsoft.Data.SqlClient;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 报表两条"锚点"的数据不变量：报表级例外行与筛选条件都必须挂在**报表归属模块**上。
///
/// 为什么必须用真库钉住：锚点错位的失效方式全是**静默放宽**——例外行挂着却永远命中不了，
/// 于是 `PREVIEW_TAG=0` 的排除不生效、`DATA_FILTER` 不生效，报表反而被放出来、明细行反而更多。
/// 接口不报错、单测不红、读代码也看不出（键是拼出来的），只有在库里对一遍才能发现。
///
/// 只读断言，不写任何行。
/// </summary>
[Trait("Category", "live-database")]
[Collection("live-database")]
public sealed class ReportVisibilityAnchorLiveTests
{
    private static string RequireConnection()
    {
        var value = Environment.GetEnvironmentVariable("MSSQL_ERP_CONN");
        Assert.False(string.IsNullOrWhiteSpace(value),
            "真库集成测试需要 MSSQL_ERP_CONN；未配置即失败（无连接跳过≠已验证）。");
        return value!;
    }

    private static int Count(string sql)
    {
        using var connection = new SqlConnection(RequireConnection());
        connection.Open();
        using var command = new SqlCommand(sql, connection);
        return Convert.ToInt32(command.ExecuteScalar());
    }

    private static string[] Collect(string sql)
    {
        using var connection = new SqlConnection(RequireConnection());
        connection.Open();
        using var command = new SqlCommand(sql, connection);
        using var reader = command.ExecuteReader();
        var rows = new List<string>();
        while (reader.Read()) rows.Add(reader.GetString(0).Trim());
        return [.. rows];
    }

    [Fact]
    public void 报表例外行的锚点必须等于该报表的归属模块()
    {
        var offenders = Collect("""
            SELECT TOP 20 CONCAT(LTRIM(RTRIM(d.USER_ID)), '|', CAST(d.M_IDX AS varchar(20)), '|',
                                 LTRIM(RTRIM(d.REPORT_ID)), '|归属=', CAST(r.M_IDX AS varchar(20)))
            FROM dbo.SYSDD_REPORT d WITH (NOLOCK)
            INNER JOIN dbo.REPORT r WITH (NOLOCK) ON LTRIM(RTRIM(r.REPORT_ID)) = LTRIM(RTRIM(d.REPORT_ID))
            WHERE d.M_IDX <> r.M_IDX
            ORDER BY d.USER_ID, d.REPORT_ID;
            """);

        Assert.True(offenders.Length == 0,
            $"例外行锚点不等于报表归属模块，这些行在运行时永远命中不了（等于悄悄放宽）：{string.Join(", ", offenders)}");
    }

    [Fact]
    public void 报表例外行不得锚在承载页上()
    {
        var offenders = Collect("""
            SELECT TOP 20 CONCAT(LTRIM(RTRIM(d.USER_ID)), '|', CAST(d.M_IDX AS varchar(20)), '|', LTRIM(RTRIM(d.REPORT_ID)))
            FROM dbo.SYSDD_REPORT d WITH (NOLOCK)
            INNER JOIN dbo.MODULES m WITH (NOLOCK) ON m.M_IDX = d.M_IDX
            WHERE m.M_URL = '/reports'
            ORDER BY d.USER_ID, d.REPORT_ID;
            """);

        Assert.True(offenders.Length == 0,
            $"仍有例外行锚在承载页（报表查询）模块上，承载页退场后这些行会变成悬空：{string.Join(", ", offenders)}");
    }

    [Fact]
    public void 例外行锚点归位是有内容的_不得因为表空而空过()
    {
        // 表为空时上面两条断言都会通过，那不是"验证过了"，而是"没东西可验"。
        // 这条把底数钉住：例外行存在，且全部锚在归属模块上。
        var total = Count("SELECT COUNT(*) FROM dbo.SYSDD_REPORT WITH (NOLOCK);");
        Assert.True(total > 0, "SYSDD_REPORT 为空：锚点断言在空表上会恒真，无法证明归位正确。");
    }

    /// <summary>
    /// 存量悬空条件行（与本次归属改造无关的历史数据，格式 `模块号|行号`）。
    /// 登记而不是清零：清零会让"不得新增悬空"这条断言变成一次性检查。
    /// </summary>
    private static readonly Dictionary<string, string[]> KnownOrphans = new(StringComparer.OrdinalIgnoreCase)
    {
        ["SYSQR_DEFAULT"] = ["99000001|1"],
        ["SYSQR_USER"] = [],
    };

    [Fact]
    public void 承载模块上不得再留筛选条件行()
    {
        // 条件按模块读：行留在哪个模块上，就只在那个模块生效。
        // 承载页列与承载页行都已退役，"留在承载页"已无从表达；这里钉一条更耐久的等价不变量——
        // 条件行的模块号必须指向存在的模块。悬空条件与"留在承载页"后果一样：
        // 取数范围变大（或变小）而无人察觉。
        foreach (var (table, label) in new[] { ("SYSQR_DEFAULT", "条件行"), ("SYSQR_USER", "用户填值") })
        {
            var offenders = Collect($"""
                SELECT TOP 10 CONCAT(CAST(x.M_IDX AS varchar(20)), '|', CAST(x.SERIAL_NO AS varchar(10)))
                FROM dbo.{table} x WITH (NOLOCK)
                WHERE NOT EXISTS (SELECT 1 FROM dbo.MODULES m WITH (NOLOCK) WHERE m.M_IDX = x.M_IDX)
                ORDER BY x.M_IDX, x.SERIAL_NO;
                """);
            // 棘轮：存量悬空已登记（历史数据，与本次归属改造无关），只断言"没有新增"。
            // 顺手清掉它们会让这条断言从此失去判别力——清完就再也不会失败，等于没有断言。
            var known = KnownOrphans.GetValueOrDefault(table, []);
            var added = offenders.Where(item => !known.Contains(item)).ToArray();
            Assert.True(added.Length == 0,
                $"{label}新增了指向不存在模块的悬空行：{string.Join(", ", added)}");
        }
    }

    [Fact]
    public void 例外行不得对不上任何报表()
    {
        var offenders = Collect("""
            SELECT TOP 10 CONCAT(LTRIM(RTRIM(d.USER_ID)), '|', CAST(d.M_IDX AS varchar(20)), '|', LTRIM(RTRIM(d.REPORT_ID)))
            FROM dbo.SYSDD_REPORT d WITH (NOLOCK)
            WHERE NOT EXISTS (SELECT 1 FROM dbo.REPORT r WITH (NOLOCK)
                              WHERE LTRIM(RTRIM(r.REPORT_ID)) = LTRIM(RTRIM(d.REPORT_ID)))
            ORDER BY d.USER_ID, d.REPORT_ID;
            """);

        Assert.True(offenders.Length == 0,
            $"存在报表号对不上任何 REPORT 行的例外行，归位会静默漏掉它们：{string.Join(", ", offenders)}");
    }
}
