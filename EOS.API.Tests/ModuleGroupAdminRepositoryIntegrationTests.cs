using System.Data;
using System.Text.RegularExpressions;
using EOS.API.Data;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 模块分组配置面（2315）的写入路径真库验证：新增 / 修改 / 删除 / 排序，以及三条拒存规则
/// （表达式超出受控子集、目标模块不是统一工作台模块、模块不存在）。
///
/// **为什么非要有真库用例**：写入路径的校验在**已开事务**的连接上跑，而读路径在事务外跑——
/// 同一段 SQL 在两种上下文里命运不同（命令没带事务即 500，见 LESSONS L87）。这类缺陷
/// 编译、纯单测与静态门禁一个都拦不住。
///
/// 用例自建一个**临时工作台模块**（主表取 PRODUCT，它有登记的字段元数据）并把分组挂在它上面，
/// 不触碰任何真实模块的配置；Dispose 清理临时模块与它的分组。拿不到连接串时跳过。
/// </summary>
[Trait("Category", "Integration")]
public sealed class ModuleGroupAdminRepositoryIntegrationTests : IDisposable
{
    private static readonly Lazy<string?> ConnectionString = new(ResolveConnectionString);

    private readonly ModuleGroupAdminRepository _repository;
    private readonly int _moduleId;

    public ModuleGroupAdminRepositoryIntegrationTests()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:ErpDatabase"] = ConnectionString.Value,
            })
            .Build();
        var connections = new DbConnectionFactory(config);
        _repository = new ModuleGroupAdminRepository(connections, NullLogger<ModuleGroupAdminRepository>.Instance);
        _moduleId = 98_000_000 + Random.Shared.Next(0, 9_999_999);
    }

    [Fact]
    public async Task 新增分组经事务写入且读得回来()
    {
        if (ConnectionString.Value is null) return;
        await SeedModuleAsync();

        var created = await _repository.CreateAsync(_moduleId, " 供应商 ", " AREA ", "integration-test", CancellationToken.None);

        Assert.True(created.GroupId > 0);
        Assert.Equal("供应商", created.Description);
        Assert.Equal("AREA", created.Expression);

        var view = await _repository.GetAsync(_moduleId, CancellationToken.None);
        Assert.NotNull(view);
        Assert.Equal("WORKBENCH", view!.NodeKind);
        Assert.Equal("PRODUCT", view.MasterTable);
        var row = Assert.Single(view.Groups);
        Assert.Equal(created.GroupId, row.GroupId);
        Assert.True(row.Available);
        Assert.Null(row.Error);
    }

    [Fact]
    public async Task 修改与删除按分组编号生效()
    {
        if (ConnectionString.Value is null) return;
        await SeedModuleAsync();
        var created = await _repository.CreateAsync(_moduleId, "供应商", "AREA", "integration-test", CancellationToken.None);

        var updated = await _repository.UpdateAsync(_moduleId, created.GroupId, "厂商", "ADDING_QTY", "integration-test", CancellationToken.None);
        Assert.Equal("厂商", updated.Description);
        var view = await _repository.GetAsync(_moduleId, CancellationToken.None);
        Assert.Equal("ADDING_QTY", Assert.Single(view!.Groups).Expression);

        await _repository.DeleteAsync(_moduleId, created.GroupId, "integration-test", CancellationToken.None);
        var after = await _repository.GetAsync(_moduleId, CancellationToken.None);
        Assert.Empty(after!.Groups);
    }

    [Fact]
    public async Task 排序重写同模块内的顺序且边界移动是无操作()
    {
        if (ConnectionString.Value is null) return;
        await SeedModuleAsync();
        var first = await _repository.CreateAsync(_moduleId, "第一", "BIEMO_NO", "integration-test", CancellationToken.None);
        var second = await _repository.CreateAsync(_moduleId, "第二", "AREA", "integration-test", CancellationToken.None);

        // 上移到顶：末位那组变第一
        await _repository.MoveAsync(_moduleId, second.GroupId, "up", "integration-test", CancellationToken.None);
        var view = await _repository.GetAsync(_moduleId, CancellationToken.None);
        Assert.Equal([second.GroupId, first.GroupId], view!.Groups.Select(group => group.GroupId).ToArray());
        Assert.Equal([10, 20], view.Groups.Select(group => group.SortIdx).ToArray());

        // 已在顶部再上移：无操作，不报错
        await _repository.MoveAsync(_moduleId, second.GroupId, "up", "integration-test", CancellationToken.None);
        var again = await _repository.GetAsync(_moduleId, CancellationToken.None);
        Assert.Equal([second.GroupId, first.GroupId], again!.Groups.Select(group => group.GroupId).ToArray());
    }

    [Fact]
    public async Task 表达式超出受控子集时拒存并指名到列()
    {
        if (ConnectionString.Value is null) return;
        await SeedModuleAsync();

        var error = await Assert.ThrowsAsync<ArgumentException>(() =>
            _repository.CreateAsync(_moduleId, "非法", "NOT_A_FIELD", "integration-test", CancellationToken.None));
        Assert.Contains("NOT_A_FIELD", error.Message);

        var view = await _repository.GetAsync(_moduleId, CancellationToken.None);
        Assert.Empty(view!.Groups);
    }

    [Fact]
    public async Task 非工作台模块与不存在的模块都拒存()
    {
        if (ConnectionString.Value is null) return;

        // 2301 菜单管理是自定义承载页：没有分组消费方
        var error = await Assert.ThrowsAsync<ArgumentException>(() =>
            _repository.CreateAsync(2301, "x", "AREA", "integration-test", CancellationToken.None));
        Assert.Contains("统一工作台模块", error.Message);

        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            _repository.CreateAsync(999_999_999, "x", "AREA", "integration-test", CancellationToken.None));
    }

    [Fact]
    public async Task 路径里的模块号与分组归属不符时拒改()
    {
        if (ConnectionString.Value is null) return;
        await SeedModuleAsync();
        var created = await _repository.CreateAsync(_moduleId, "供应商", "AREA", "integration-test", CancellationToken.None);

        var error = await Assert.ThrowsAsync<ArgumentException>(() =>
            _repository.UpdateAsync(2301, created.GroupId, "改错地方", "AREA", "integration-test", CancellationToken.None));
        Assert.Contains("不属于模块", error.Message);

        var view = await _repository.GetAsync(_moduleId, CancellationToken.None);
        Assert.Equal("供应商", Assert.Single(view!.Groups).Description);
    }

    [Fact]
    public async Task 分组数量上界生效()
    {
        if (ConnectionString.Value is null) return;
        await SeedModuleAsync();
        for (var index = 0; index < ModuleGroupAdminRepository.MaxGroupsPerModule; index++)
        {
            await _repository.CreateAsync(_moduleId, $"组{index}", "BIEMO_NO", "integration-test", CancellationToken.None);
        }

        var error = await Assert.ThrowsAsync<ArgumentException>(() =>
            _repository.CreateAsync(_moduleId, "超出一组", "AREA", "integration-test", CancellationToken.None));
        Assert.Contains("上界", error.Message);
    }

    /// <summary>临时工作台模块：主表取 PRODUCT（有字段元数据），M_URL 留 /workbench。</summary>
    private async Task SeedModuleAsync()
    {
        await using var connection = new SqlConnection(ConnectionString.Value);
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            """
            INSERT INTO dbo.MODULES (M_IDX,M_DESC,M_URL,M_ROOT_IDX,SORT_IDX,M_TAG,MASTER_TABLE,LAST_UPDATE_BY)
            VALUES (@Id,N'分组集成测试-临时模块',N'/workbench',@Id,9999,1,N'PRODUCT',N'integration-test');
            """, connection);
        command.Parameters.Add("@Id", SqlDbType.Int).Value = _moduleId;
        await command.ExecuteNonQueryAsync();
    }

    public void Dispose()
    {
        if (ConnectionString.Value is null) return;
        try
        {
            using var connection = new SqlConnection(ConnectionString.Value);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                DELETE FROM dbo.MODULE_GROUPS WHERE M_IDX=@Id;
                DELETE FROM dbo.MODULES WHERE M_IDX=@Id;
                """;
            command.Parameters.Add("@Id", SqlDbType.Int).Value = _moduleId;
            command.ExecuteNonQuery();
        }
        catch
        {
            // 清理失败不影响测试结论；残留仅为开发库测试数据
        }
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
