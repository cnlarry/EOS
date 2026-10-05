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
        var ok = Assert.IsType<OkObjectResult>(await controller.Bootstrap(CancellationToken.None));
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(ok.Value, new JsonSerializerOptions(JsonSerializerDefaults.Web)));

        var inNavigation = new HashSet<int>();
        CollectModuleIds(document.RootElement.GetProperty("navigation"), inNavigation);

        var accessible = modules.Where(module => module.Enabled).ToDictionary(module => module.Id);
        var unreachable = accessible.Values
            .Where(module => !string.IsNullOrWhiteSpace(module.SourceUrl) && !inNavigation.Contains(module.Id))
            .Select(module => $"{module.Id}（{module.Label}）")
            .ToList();
        Assert.True(unreachable.Count == 0, $"这些模块带页面却不在导航树里（菜单按编号搜不到）：{string.Join('、', unreachable)}");

        var unauthorized = inNavigation.Where(id => !accessible.ContainsKey(id)).ToList();
        Assert.True(unauthorized.Count == 0, $"导航里出现用户无权访问的模块编号：{string.Join('、', unauthorized)}");

        var hidden = await HiddenModuleIdsAsync(ConnectionString.Value);
        Assert.NotEmpty(hidden);
        var leaked = hidden.Where(inNavigation.Contains).ToList();
        Assert.True(leaked.Count == 0, $"导航里出现被系统隐藏（M_TAG=0）的模块编号：{string.Join('、', leaked)}");
    }

    /// <summary>被系统隐藏的模块（M_TAG=0）：不在导航集合里，因而菜单搜索也搜不到。</summary>
    private static async Task<List<int>> HiddenModuleIdsAsync(string connectionString)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand("SELECT M_IDX FROM dbo.MODULES WHERE ISNULL(M_TAG, 1) = 0;", connection);
        var ids = new List<int>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) ids.Add(reader.GetInt32(0));
        return ids;
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
