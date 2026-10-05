using EOS.API.Data;
using EOS.API.Models;
using EOS.API.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 系统参数读侧语义的真库校验：
/// ① 分组顺序来自 `GROUP_SEQ`（页签顺序是数据，不是键名字母序）；
/// ② `IsReferenced` 的判据（已发布配置引用 / 代码直读）——设置页靠它把"暂时没有读取方"的参数
/// 标出来，标错会误导操作员（把活开关当成没用的项，或反过来）。
/// </summary>
[Trait("Category", "Integration")]
[Collection("live-database")]
public sealed class SystemParameterServiceLiveTests
{
    private static readonly string? ConnectionString =
        Environment.GetEnvironmentVariable("MSSQL_ERP_CONN");

    private static SystemParameterService CreateService(string connectionString)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:ErpDatabase"] = connectionString,
            })
            .Build();
        var connections = new DbConnectionFactory(configuration);
        var auditWriter = new WorkbenchAuditWriter(
            connections,
            new HttpContextAccessor(),
            new WorkbenchDefinitionProvider(connections, NullLogger<WorkbenchDefinitionProvider>.Instance),
            Options.Create(new AuditSettings()));
        return new SystemParameterService(connections, auditWriter);
    }

    [Fact]
    public async Task 分组顺序按_GROUP_SEQ_呈现()
    {
        if (string.IsNullOrWhiteSpace(ConnectionString)) return;
        var service = CreateService(ConnectionString);

        var list = await service.ListAsync(ModuleIds.SystemSettings, CancellationToken.None);
        // 顺序 = 数据里的 GROUP_SEQ（往来与账期 → MRP 与可用量 → 料件与版次 …），不是分组键字母序
        Assert.Equal(
            new[] { "PARTNER", "MRP", "PRODUCT" },
            list.Groups.Take(3).Select(group => group.GroupCode).ToArray());
        // "其它"在 REMARK 退役后已下线（空分组不进页面）
        Assert.DoesNotContain(list.Groups, group => group.GroupCode == "MISC");
    }

    [Fact]
    public async Task 引用标记与判据一致()
    {
        if (string.IsNullOrWhiteSpace(ConnectionString)) return;
        var service = CreateService(ConnectionString);
        var token = CancellationToken.None;

        var system = await service.ListAsync(ModuleIds.SystemSettings, token);
        var byKey = system.Groups.SelectMany(group => group.Parameters)
            .ToDictionary(parameter => parameter.Key, StringComparer.OrdinalIgnoreCase);

        // 代码直读（MrpPlanAllocHandler / InventoryMoveHandler）：登记在 CodeReferencedKeys
        Assert.True(byKey["PRO_MRP"].IsReferenced);
        // 配置引用（校验规则 switch 门控、cop-send-check 的 gateFlag）
        Assert.True(byKey["FITOUT_TAG"].IsReferenced);
        Assert.True(byKey["SEND_TAG"].IsReferenced);
        // 当前既无配置引用也无代码直读：页面须标"当前无引用方"
        Assert.False(byKey["MRP_DAYS"].IsReferenced);
        Assert.False(byKey["SUPPLIER_DAYS"].IsReferenced);

        // 同一键在不同归属下结论不同：考勤日历只被 180213 的读取路径使用
        var attendance = await service.ListAsync(ModuleIds.HrSetup, token);
        var monthly = await service.ListAsync(ModuleIds.HrmSetup, token);
        static bool Flag(SystemParameterList list, string key) => list.Groups
            .SelectMany(group => group.Parameters)
            .Single(parameter => string.Equals(parameter.Key, key, StringComparison.OrdinalIgnoreCase))
            .IsReferenced;
        Assert.True(Flag(attendance, "SAT_REST_DAY"));
        Assert.False(Flag(monthly, "SAT_REST_DAY"));
    }
}
