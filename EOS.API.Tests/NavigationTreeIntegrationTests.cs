using System.Security.Claims;
using System.Text.Json;
using EOS.API.Controllers;
using EOS.API.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 菜单导航与可访问模块集合的一致性（真实 EOS.ERP 开发库）。
/// 菜单搜索按模块编号找的就是导航树上的节点，所以这条底线是「有权限的搜得到、没权限的与隐藏的搜不到」：
/// - 带页面的可访问模块必须在树上（元数据断链——M_P_IDX 指向已删除的父模块、祖先链不完整、
///   顶层节点自己就是页面——会让模块从菜单里消失且按编号搜不到，编译与前端单测都照不出来）；
/// - 导航里不得出现用户无权访问、或被系统隐藏（M_TAG=0）的模块编号。
/// 连接串来自 env MSSQL_ERP_CONN；拿不到时跳过。
/// </summary>
[Trait("Category", "Integration")]
public sealed class NavigationTreeIntegrationTests
{
    private static readonly Lazy<string?> ConnectionString = new(() => Environment.GetEnvironmentVariable("MSSQL_ERP_CONN"));
    private const string UserId = "admin";

    [Fact]
    public async Task 带页面的可访问模块都出现在导航树且导航不越权()
    {
        if (ConnectionString.Value is null) return;
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:ErpDatabase"] = ConnectionString.Value,
            })
            .Build();
        var repository = new NavigationRepository(new DbConnectionFactory(configuration), NullLogger<NavigationRepository>.Instance);
        var modules = await repository.GetForUserAsync(UserId, CancellationToken.None);
        Assert.NotEmpty(modules);

        var controller = new ApplicationController(repository, configuration)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                    [
                        new Claim(ClaimTypes.NameIdentifier, UserId),
                        new Claim(ClaimTypes.Name, UserId),
                    ], "test")),
                },
            },
        };
        // 取一次导航树上的模块编号集合。末尾那条"隐藏模块不得泄漏"要反复取，故包成局部函数。
        async Task<HashSet<int>> NavigationIdsAsync()
        {
            var bootstrap = Assert.IsType<OkObjectResult>(await controller.Bootstrap(CancellationToken.None));
            using var payload = JsonDocument.Parse(
                JsonSerializer.Serialize(bootstrap.Value, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            var collected = new HashSet<int>();
            CollectModuleIds(payload.RootElement.GetProperty("navigation"), collected);
            return collected;
        }

        var inNavigation = await NavigationIdsAsync();

        var accessible = modules.Where(module => module.Enabled).ToDictionary(module => module.Id);
        var unreachable = accessible.Values
            .Where(module => !string.IsNullOrWhiteSpace(module.SourceUrl) && !inNavigation.Contains(module.Id))
            .Select(module => $"{module.Id}（{module.Label}）")
            .ToList();
        Assert.True(unreachable.Count == 0, $"这些模块带页面却不在导航树里（菜单按编号搜不到）：{string.Join('、', unreachable)}");

        var unauthorized = inNavigation.Where(id => !accessible.ContainsKey(id)).ToList();
        Assert.True(unauthorized.Count == 0, $"导航里出现用户无权访问的模块编号：{string.Join('、', unauthorized)}");

        // 隐藏模块（M_TAG=0）不得出现在导航里。**库内当前一个隐藏模块都没有**（退役清理之后 M_TAG
        // 全为 1），这条防线因此无样本可验——但不能靠"没样本就跳过"让它悄悄消失，改为自造样本并正反
        // 对照：先证明样本确实会被导航收录（否则"它不出现"是恒真的假断言），再把它的 M_TAG 置 0。
        // 样本是自造的键（模块行 + 权限行）；仓储自己开连接、读不到未提交的事务，故只能先落库再删。
        const int sampleId = 999901;
        try
        {
            await SeedNavigableSampleAsync(ConnectionString.Value, sampleId, hidden: false);
            Assert.Contains(sampleId, await NavigationIdsAsync());

            await SetSampleHiddenAsync(ConnectionString.Value, sampleId);
            Assert.DoesNotContain(sampleId, await NavigationIdsAsync());
        }
        finally
        {
            await RemoveNavigableSampleAsync(ConnectionString.Value, sampleId);
        }
    }

    /// <summary>
    /// 造一个"本来会被导航收录"的样本模块：描述非空、挂在顶层，并带一行 admin 的模块权限。
    /// 导航只收录有权限行的模块——缺了权限行，样本永远不会出现，"隐藏后不出现"就成了恒真的假断言。
    /// </summary>
    private static async Task SeedNavigableSampleAsync(string connectionString, int moduleId, bool hidden)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand("""
            DELETE FROM dbo.SYSDD WHERE M_IDX = @id;
            DELETE FROM dbo.MODULES WHERE M_IDX = @id;
            INSERT INTO dbo.MODULES (M_IDX, M_DESC, M_URL, M_TAG, M_P_IDX, SORT_IDX)
            VALUES (@id, N'ZZNAV 导航用例样本', N'/zz-nav-test', @tag, 0, 9999);
            INSERT INTO dbo.SYSDD (USER_ID, M_IDX, EXEC_TAG) VALUES (@user, @id, N'Z');
            """, connection);
        command.Parameters.Add("@id", System.Data.SqlDbType.Int).Value = moduleId;
        command.Parameters.Add("@tag", System.Data.SqlDbType.Bit).Value = !hidden;
        command.Parameters.Add("@user", System.Data.SqlDbType.NChar, 20).Value = UserId;
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>把样本模块置为系统隐藏（M_TAG=0）。</summary>
    private static async Task SetSampleHiddenAsync(string connectionString, int moduleId)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand("UPDATE dbo.MODULES SET M_TAG = 0 WHERE M_IDX = @id;", connection);
        command.Parameters.Add("@id", System.Data.SqlDbType.Int).Value = moduleId;
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>删掉样本模块与它的权限行（用例自造的键，无论成败都要清干净）。</summary>
    private static async Task RemoveNavigableSampleAsync(string connectionString, int moduleId)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand("""
            DELETE FROM dbo.SYSDD WHERE M_IDX = @id;
            DELETE FROM dbo.MODULES WHERE M_IDX = @id;
            """, connection);
        command.Parameters.Add("@id", System.Data.SqlDbType.Int).Value = moduleId;
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>收集导航树上所有带模块编号的节点：叶子都带，「分组自带页面」的分组节点也带。</summary>
    private static void CollectModuleIds(JsonElement nodes, HashSet<int> target)
    {
        foreach (var node in nodes.EnumerateArray())
        {
            if (node.TryGetProperty("moduleId", out var moduleId) && moduleId.ValueKind == JsonValueKind.Number)
                target.Add(moduleId.GetInt32());
            if (node.TryGetProperty("children", out var children) && children.ValueKind == JsonValueKind.Array)
                CollectModuleIds(children, target);
        }
    }
}
