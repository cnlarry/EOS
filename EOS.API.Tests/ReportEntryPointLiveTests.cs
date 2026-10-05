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
[Trait("Category", "Integration")]
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

    /// <summary>
    /// 已删除的历史报表模块号（181 个：23 个 XX98 目录 + 156 个 /reports 承载页 + 3 个子树内非 /reports 子节点，
    /// 已扣除保留项 129802）。清单直接写在用例里而不是落成夹具文件：夹具目录在本仓库不入库，
    /// 一条"不得重现"的断言依赖一个不入库的文件，换了机器就等于没断。
    /// 原始行留底在 logs/adr024-r8-modules-backup.csv。
    /// </summary>
    private static readonly int[] DeletedLegacyModuleIds =
    [
        1198, 1298, 1310, 1398, 1498, 1598, 1698, 1998, 2098, 2198, 2298, 2398,
        2498, 2698, 2798, 2898, 2998, 119801, 119802, 119803, 119804, 119805, 119806, 119807,
        119808, 119809, 119810, 119811, 129801, 129804, 129805, 129806, 129807, 129808, 129809, 129810,
        139801, 139802, 139803, 139804, 139805, 139806, 139807, 139808, 139809, 139810, 139811, 139901,
        149801, 149802, 149803, 149804, 149805, 149806, 149807, 149808, 149809, 149810, 149811, 149813,
        149814, 149815, 149816, 159801, 159802, 159803, 159804, 159805, 169801, 169802, 169803, 169804,
        169805, 169806, 169807, 169808, 170198, 170298, 180198, 180298, 180398, 180498, 180698, 199801,
        199802, 199803, 199804, 209801, 209802, 209803, 209804, 209805, 219801, 219802, 229801, 230904,
        230905, 230906, 239801, 239804, 239805, 239806, 249801, 249802, 249803, 249804, 269801, 269802,
        269803, 269804, 279801, 279802, 279803, 279804, 279805, 279806, 279807, 289801, 289802, 299801,
        299802, 299803, 299804, 299805, 299806, 299807, 299808, 299809, 329801, 17019801, 17019802, 17019803,
        17019804, 17019805, 17019806, 17029801, 17029802, 17029803, 17029804, 17029805, 18019801, 18019802, 18019803, 18019804,
        18019805, 18019806, 18019807, 18029801, 18029802, 18029803, 18029804, 18029805, 18029806, 18029807, 18029808, 18029809,
        18029810, 18029811, 18039801, 18039802, 18039803, 18039804, 18039805, 18039806, 18039807, 18039808, 18039809, 18039810,
        18039812, 18049801, 18049802, 18069801, 18069802, 18069803, 18069804, 18069805, 18069806, 18069807, 180398081, 180398091,
        180398101,
    ];

    [Fact]
    public void 已删除的历史报表模块号不得重现()
    {
        // 这些编号是报表承载页时代的产物。它们一旦重现，意味着有人又按"一张报表一个模块"的老路子建模块——
        // 表现形式是一张报表悄悄长出一个模块节点，不会报错，只会让归属表再长出第二套锚点。
        var alive = Collect("""
            SELECT CAST(M_IDX AS varchar(20)) FROM dbo.MODULES WITH (NOLOCK);
            """).Select(int.Parse).ToHashSet();

        var reappeared = DeletedLegacyModuleIds.Where(alive.Contains).OrderBy(id => id).ToArray();
        Assert.True(reappeared.Length == 0,
            $"已删除的历史报表模块号重现了：{string.Join(", ", reappeared)}");

        // 唯一保留项也必须还在（它是 /bom-expand 的唯一承载页，且菜单可见）
        Assert.Contains(129802, alive);
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
