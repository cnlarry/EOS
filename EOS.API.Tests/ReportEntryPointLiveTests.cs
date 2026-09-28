using Microsoft.Data.SqlClient;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 报表的**入口形态**不变量：菜单树里不得再有可见的报表承载页节点。
///
/// 报表的发现走报表中心目录 / 深链 / 情境入口（表单工具条），菜单树只保留一个报表入口。
/// 承载页节点如果在菜单里可见，用户点进去会落在一个"报表已按业务模块归位、这里什么都没有"的空页上——
/// 它不会报错，所以只能靠断言把这条形态钉住。报表中心的导航项是前端固定项，不落在 MODULES 里。
/// </summary>
[Trait("Category", "live-database")]
[Collection("live-database")]
public sealed class ReportEntryPointLiveTests
{
    private static string RequireConnection()
    {
        var value = Environment.GetEnvironmentVariable("MSSQL_ERP_CONN");
        Assert.False(string.IsNullOrWhiteSpace(value),
            "真库集成测试需要 MSSQL_ERP_CONN；未配置即失败（无连接跳过≠已验证）。");
        return value!;
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
    public void 菜单树里不得有可见的报表承载页节点()
    {
        var nodes = Collect("""
            SELECT CONCAT(CAST(M_IDX AS varchar(20)), '|', LTRIM(RTRIM(ISNULL(M_DESC, ''))))
            FROM dbo.MODULES WITH (NOLOCK)
            WHERE M_URL = '/reports' AND ISNULL(M_TAG, 0) = 1
            ORDER BY M_IDX;
            """);

        Assert.True(nodes.Length == 0,
            $"菜单里仍有可见的报表承载页节点，点进去是空页（报表已按业务模块归位）：{string.Join(", ", nodes)}");
    }

    [Fact]
    public void 隐藏的承载页里不得还挂着报表或条件()
    {
        // 反过来说：隐藏只解决"看得见"，没解决"还指着它"。
        // 若有报表的主表还挂在这些节点上，隐藏菜单只是把问题挪到看不见的地方。
        var offenders = Collect("""
            SELECT TOP 10 CONCAT(CAST(m.M_IDX AS varchar(20)), '|', LTRIM(RTRIM(ISNULL(m.M_DESC, ''))),
                                  '|报表=', CAST((SELECT COUNT(*) FROM dbo.REPORT r WITH (NOLOCK) WHERE r.M_IDX = m.M_IDX) AS varchar(10)),
                                  '|条件=', CAST((SELECT COUNT(*) FROM dbo.SYSQR_DEFAULT d WITH (NOLOCK) WHERE d.M_IDX = m.M_IDX) AS varchar(10)))
            FROM dbo.MODULES m WITH (NOLOCK)
            WHERE m.M_URL = '/reports'
              AND (EXISTS (SELECT 1 FROM dbo.REPORT r WITH (NOLOCK) WHERE r.M_IDX = m.M_IDX)
                OR EXISTS (SELECT 1 FROM dbo.SYSQR_DEFAULT d WITH (NOLOCK) WHERE d.M_IDX = m.M_IDX))
            ORDER BY m.M_IDX;
            """);

        Assert.True(offenders.Length == 0,
            $"承载页里仍挂着报表或条件（隐藏菜单不解决问题，应归位到业务模块）：{string.Join(", ", offenders)}");
    }
}
