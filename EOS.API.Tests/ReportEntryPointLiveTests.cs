using System.Text.Json;
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

    /// <summary>仓库根（以 EOS.slnx 为标记）；夹具在仓内相对路径下，不能用输出目录拼。</summary>
    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "EOS.slnx")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        return directory!.FullName;
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
    public void 已删除的历史报表模块号不得重现()
    {
        // 这些编号是报表承载页时代的产物（XX98 目录 + /reports 承载页 + 其下非 /reports 子节点）。
        // 它们一旦重现，意味着有人又按"一张报表一个模块"的老路子建模块——
        // 表现形式是一张报表悄悄长出一个模块节点，不会报错，只会让归属表再长出第二套锚点。
        var fixture = Path.Combine(RepoRoot(), "EOS.API.Tests", "Fixtures", "report-legacy-module-ids.json");
        Assert.True(File.Exists(fixture), $"缺少历史模块号清单夹具：{fixture}");
        using var document = JsonDocument.Parse(File.ReadAllText(fixture));
        var legacy = document.RootElement.GetProperty("moduleIds").EnumerateArray().Select(item => item.GetInt32()).ToArray();
        var kept = document.RootElement.GetProperty("keepKept").GetInt32();

        var alive = Collect("""
            SELECT CAST(M_IDX AS varchar(20)) FROM dbo.MODULES WITH (NOLOCK);
            """).Select(int.Parse).ToHashSet();

        var reappeared = legacy.Where(id => id != kept && alive.Contains(id)).OrderBy(id => id).ToArray();
        Assert.True(reappeared.Length == 0,
            $"已删除的历史报表模块号重现了：{string.Join(", ", reappeared)}");

        // 唯一保留项也必须还在（它是 /bom-expand 的唯一承载页）
        Assert.Contains(kept, alive);
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
