using System.Text.RegularExpressions;
using EOS.API.Data;
using EOS.API.Models;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 选择器的 FROM 组装：**过滤条件的跨表 JOIN** 与**虚拟列解析的 JOIN** 会拼进同一段 FROM，
/// 两路各拼一次同名别名时 SQL Server 直接报「在 FROM 子句中多次指定了相关名称」。
/// 这是执行期错误——只看拼出来的字符串是看不出来的，所以本用例把两段拼好后**真的执行一次**。
///
/// 真实触发例（模块 1405 明细字段 PRO_NO 的来源表 CLIENT_PRICE_D）：
/// 过滤条件引用 <c>PRODUCT.BUSINESS_TAG</c> / <c>PRODUCT.CONFIRM_TAG</c> ⇒ 过滤段拼进 PRODUCT；
/// 回填映射要 <c>PRODUCT.PRO_NAME</c> / <c>PRO_SPEC</c> 等虚拟列 ⇒ 虚拟列解析再拼一次 PRODUCT。
/// 修法：<c>BuildJoinClause</c> 回传本段实际出现的别名，虚拟列解析把重复的别名让出去
/// （别名两侧同源同一张 QUERY_RELATION，让出的那一段必然已存在）。
/// </summary>
[Trait("Category", "Integration")]
public sealed class ChooserJoinCompositionLiveTests
{
    private const string SourceTable = "CLIENT_PRICE_D";

    /// <summary>过滤条件引用的跨表别名（取自 COP_ORDER_D.PRO_NO 该来源的 FILTER_STRUCT）。</summary>
    private static readonly string[] FilterJoinAliases = ["CLIENT_PRICE_M", "PRODUCT"];

    /// <summary>回填映射里**不是源表物理列**的列：正是虚拟列解析要补的那些。</summary>
    private static readonly string[] VirtualColumns = ["PRO_NAME", "PRO_SPEC", "COLOR_NAME", "STUFF_NAME"];

    private static readonly Lazy<string?> ConnectionString = new(ResolveConnectionString);

    [Fact]
    public async Task ChooserFrom_WithFilterAndVirtualJoins_NoDuplicateAlias()
    {
        if (ConnectionString.Value is null)
        {
            return;
        }
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:ErpDatabase"] = ConnectionString.Value,
            })
            .Build();
        await using var connection = new DbConnectionFactory(config).Create();
        await connection.OpenAsync();

        var catalog = await ChooserJoinCatalog.GetAsync(connection, SourceTable, CancellationToken.None);
        Assert.Null(catalog.Error);
        var filterJoins = ChooserJoinCatalog.BuildJoinClause(catalog, FilterJoinAliases, out var joinedAliases);
        Assert.NotNull(filterJoins);

        var virtualFields = await ReadVirtualFieldsAsync(connection);
        Assert.Equal(VirtualColumns.Length, virtualFields.Count);

        var resolver = new VirtualColumnResolver(connection);
        var withoutExclusion = await resolver.ResolveAsync(SourceTable, virtualFields, CancellationToken.None);
        var withExclusion = await resolver.ResolveAsync(
            SourceTable, virtualFields, CancellationToken.None, alreadyJoined: joinedAliases);

        // 只看 JOIN 段本身：别处（ON 条件）引用 [PRODUCT] 是正常且必需的
        Assert.Contains("LEFT JOIN dbo.[PRODUCT]", withoutExclusion.JoinFragment);
        Assert.DoesNotContain("LEFT JOIN dbo.[PRODUCT]", withExclusion.JoinFragment);
        // 让出去的别名仍被 SELECT 片段引用——它由过滤段提供，不是把字段丢了
        Assert.Contains("[PRODUCT].[PRO_NAME]", string.Join(",", withExclusion.SelectFragments));
        // 其余虚拟列（不在过滤段里的）照旧由这里补上
        Assert.Contains("LEFT JOIN dbo.[STUFF]", withExclusion.JoinFragment);

        // 重复别名只在执行期炸：两版各执行一次，坏的那版必须真报错、修好的那版必须跑通
        await AssertComposedFromFailsAsync(connection, filterJoins + withoutExclusion.JoinFragment);
        await AssertComposedFromRunsAsync(connection, filterJoins + withExclusion.JoinFragment);
    }

    private static async Task<IReadOnlyList<WorkbenchField>> ReadVirtualFieldsAsync(SqlConnection connection)
    {
        const string sql = """
            SELECT LTRIM(RTRIM(F_ID)), LTRIM(RTRIM(ISNULL(F_DESC, F_ID))), LTRIM(RTRIM(ISNULL(F_TYPE, 'nvarchar'))),
                   LTRIM(RTRIM(ISNULL(VIRTUAL_EXP, '')))
              FROM dbo.FIELDS
             WHERE RTRIM(T_ID) = @Table AND ISNULL(IS_VIRTUAL, 0) = 1
               AND LTRIM(RTRIM(F_ID)) IN (SELECT value FROM STRING_SPLIT(@Columns, ','))
             ORDER BY F_ID;
            """;
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@Table", SourceTable);
        command.Parameters.AddWithValue("@Columns", string.Join(',', VirtualColumns));
        var fields = new List<WorkbenchField>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            fields.Add(new WorkbenchField(
                reader.GetString(0), reader.GetString(1), reader.GetString(2), 100, null, false,
                IsVirtual: true, VirtualExpression: reader.GetString(3)));
        }
        return fields;
    }

    private static Task AssertComposedFromFailsAsync(SqlConnection connection, string joinFragment)
        => Assert.ThrowsAsync<SqlException>(() => ExecuteProbeAsync(connection, joinFragment));

    private static async Task AssertComposedFromRunsAsync(SqlConnection connection, string joinFragment)
    {
        var rows = await ExecuteProbeAsync(connection, joinFragment);
        Assert.True(rows >= 0);
    }

    private static async Task<int> ExecuteProbeAsync(SqlConnection connection, string joinFragment)
    {
        var sql = $"SELECT COUNT_BIG(1) FROM dbo.[{SourceTable}] WITH (NOLOCK){joinFragment};";
        await using var command = new SqlCommand(sql, connection);
        return Convert.ToInt32(await command.ExecuteScalarAsync() ?? 0);
    }

    private static string? ResolveConnectionString()
    {
        var env = Environment.GetEnvironmentVariable("MSSQL_ERP_CONN");
        if (!string.IsNullOrWhiteSpace(env))
        {
            return env;
        }

        try
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "config.toml");
            var text = File.ReadAllText(path);
            var match = Regex.Match(text, "\\[mcp_servers\\.mssql\\.env\\][\\s\\S]*?MSSQL_ERP_CONN\\s*=\\s*\"([^\"]+)\"");
            return match.Success && match.Groups[1].Value.Contains("Database=EOS.ERP")
                ? match.Groups[1].Value
                : null;
        }
        catch
        {
            return null;
        }
    }
}
