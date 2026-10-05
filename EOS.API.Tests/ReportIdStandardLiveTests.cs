using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 报表编号规范（`^[A-Za-z0-9_-]{3,40}$`）是**全库数据**的约束，不只是新增时的校验：
/// 编号是报表的身份，7 张表按它引用，一个不合规编号就是一处必须转义/可能被截断的引用点。
///
/// <para>
/// 校验器只管住"以后新增的"，存量靠一次性清理；把两者钉在一起，才能保证
/// "新增的不会被写坏 + 存量的不会偷偷回来"。
/// </para>
/// </summary>
[Trait("Category", "Integration")]
[Collection("live-database")]
public sealed class ReportIdStandardLiveTests
{
    /// <summary>与 `ReportAdminValidator.ReportIdPattern` 同一条规范（有意重复一次：测试不该只为复述实现而存在）。</summary>
    private static readonly Regex Pattern = new("^[A-Za-z0-9_-]{3,40}$", RegexOptions.Compiled);

    private static string RequireConnection()
    {
        var value = Environment.GetEnvironmentVariable("MSSQL_ERP_CONN");
        Assert.False(string.IsNullOrWhiteSpace(value),
            "真库集成测试需要 MSSQL_ERP_CONN；未配置即失败（无连接跳过≠已验证）。");
        return value!;
    }

    private static List<string> Collect(string sql)
    {
        using var connection = new SqlConnection(RequireConnection());
        connection.Open();
        using var command = new SqlCommand(sql, connection);
        using var reader = command.ExecuteReader();
        var rows = new List<string>();
        while (reader.Read()) rows.Add(reader.GetString(0));
        return rows;
    }

    [Fact]
    public void 全部报表编号都符合规范()
    {
        var ids = Collect("SELECT LTRIM(RTRIM(REPORT_ID)) FROM dbo.REPORT WITH (NOLOCK) ORDER BY REPORT_ID;");
        Assert.NotEmpty(ids);
        var offenders = ids.Where(id => !Pattern.IsMatch(id)).ToArray();
        Assert.True(offenders.Length == 0,
            $"存在不合规的报表编号（应为 3-40 位字母/数字/下划线/连字符）：{string.Join(", ", offenders)}");
    }

    [Fact]
    public void 报表编号全库唯一()
    {
        var duplicates = Collect("""
            SELECT TOP 10 CONCAT(LTRIM(RTRIM(REPORT_ID)), '×', CAST(COUNT(*) AS varchar(6)))
            FROM dbo.REPORT WITH (NOLOCK) GROUP BY LTRIM(RTRIM(REPORT_ID)) HAVING COUNT(*) > 1;
            """);
        Assert.True(duplicates.Count == 0, $"报表编号重复：{string.Join(", ", duplicates)}");
    }

    /// <summary>
    /// 存量孤儿（报表已不存在、引用行还在）的登记数：`REPORT_SORT` 119 / `SYSQR` 70，其余表为 0。
    ///
    /// <para>
    /// 这些是历史数据（旧报表删过但没级联），与本次编号规范化无关；本轮只修"删除路径本身不再制造孤儿"，
    /// 不做历史清库——清库会顺带删掉一些报表的排序方案与打印偏好，属另一件事，需要单独评估。
    /// 这条断言因此做成**棘轮**：数量不得增加（新增即说明有路径又在漏级联）。
    /// </para>
    /// </summary>
    private static readonly Dictionary<string, int> KnownOrphanCounts = new(StringComparer.OrdinalIgnoreCase)
    {
        ["REPORT_SORT"] = 119,
        ["SYSQR"] = 70,
        ["SYSDD_REPORT"] = 0,
        ["REPORT_INBOX"] = 0,
        ["REPORT_SUBSCRIPTION"] = 0,
    };

    [Fact]
    public void 引用报表编号的表里不得新增不存在的编号()
    {
        // 改名/删除必须级联；漏一张表就是一条永远取不到的孤儿行（不报错，只是静默不生效）。
        foreach (var (table, known) in KnownOrphanCounts)
        {
            var orphans = Collect($"""
                SELECT TOP 10 LTRIM(RTRIM(x.REPORT_ID))
                FROM dbo.{table} x WITH (NOLOCK)
                WHERE NOT EXISTS (SELECT 1 FROM dbo.REPORT r WITH (NOLOCK)
                                  WHERE LTRIM(RTRIM(r.REPORT_ID)) = LTRIM(RTRIM(x.REPORT_ID)));
                """);
            var count = Collect($"""
                SELECT CAST(COUNT(*) AS varchar(10))
                FROM dbo.{table} x WITH (NOLOCK)
                WHERE NOT EXISTS (SELECT 1 FROM dbo.REPORT r WITH (NOLOCK)
                                  WHERE LTRIM(RTRIM(r.REPORT_ID)) = LTRIM(RTRIM(x.REPORT_ID)));
                """);
            var actual = count.Count == 0 ? 0 : int.Parse(count[0]);
            Assert.True(actual <= known,
                $"{table} 的孤儿行从登记值 {known} 增加到 {actual}（说明某条删除/改名路径没级联）：{string.Join(", ", orphans)}");
        }
    }
}
