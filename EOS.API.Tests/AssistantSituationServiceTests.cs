using EOS.API.Features.Assistant.Situation;
using EOS.API.Features.Assistant.Tools;
using EOS.API.Security;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 处境认知：身份段来自登录声明、待办/最近被拒按预算组装、权限清单不常驻。
/// 全部走事实读取器替身，不发 SQL。
/// </summary>
public sealed class AssistantSituationServiceTests
{
    private static AssistantSituationService CreateService(
        AssistantSituationDoubles.FakeFacts facts,
        Action<AssistantSituationBudgetOptions>? configure = null)
        => new(AssistantSituationDoubles.UserContext(), facts,
            AssistantSituationDoubles.Budget(configure), NullLogger<AssistantSituationService>.Instance);

    [Fact]
    public async Task 身份段来自登录声明并可补汇报关系与工作组()
    {
        var facts = new AssistantSituationDoubles.FakeFacts
        {
            Relationship = new SituationRelationship("李经理", "王总", ["采购", "审批组"]),
        };
        var service = CreateService(facts);

        var identity = await service.LoadIdentityAsync(CancellationToken.None);

        Assert.Equal("u1", identity.UserId);
        Assert.Equal("张三", identity.EmployeeName);
        Assert.Equal("E001", identity.EmployeeId);
        Assert.Equal("采购部", identity.DepartmentName);
        Assert.Equal("C1", identity.CompanyId);
        Assert.Equal("李经理", identity.DirectLeader);
        Assert.Equal("王总", identity.DepartmentLeader);
        Assert.Equal(["采购", "审批组"], identity.WorkGroups);
    }

    [Fact]
    public async Task 常驻处境段包含身份与最近被拒且声明不是授权依据()
    {
        var facts = new AssistantSituationDoubles.FakeFacts { Pending = new SituationPending(3, 1) };
        facts.Failures.Add(new SituationFailureFact(
            DateTime.UtcNow.AddHours(-2), "master-field-write", 1606,
            "字段 IN_SUM 由引擎维护，不允许人工修改。", "BUSINESS_VALIDATION_FAILED"));
        var service = CreateService(facts);

        var text = await service.BuildResidentTextAsync("u1", CancellationToken.None);

        Assert.Contains("张三", text);
        Assert.Contains("待我审批 3 条", text);
        Assert.Contains("IN_SUM", text);
        Assert.Contains("不得据此放宽任何权限判断", text);
        // 权限清单不常驻（避免成本、幻觉与"以模型已知为由削弱工具层"）
        Assert.Contains("list_my_capabilities", text);
    }

    [Fact]
    public async Task 待办与最近被拒都为空时不产生空的零摘要句()
    {
        var service = CreateService(new AssistantSituationDoubles.FakeFacts());

        var text = await service.BuildResidentTextAsync("u1", CancellationToken.None);

        Assert.DoesNotContain("待我审批 0 条", text);
        Assert.Contains("身份", text);
    }

    [Fact]
    public async Task 常驻段超预算时被硬截断()
    {
        var facts = new AssistantSituationDoubles.FakeFacts
        {
            Relationship = new SituationRelationship(new string('上', 200), new string('领', 200), []),
        };
        var service = CreateService(facts, options =>
        {
            options.ResidentTokenLimit = 120;
            options.IdentityTokenLimit = 100;
            options.PendingTokenLimit = 20;
        });

        var text = await service.BuildResidentTextAsync("u1", CancellationToken.None);

        Assert.True(text.Length <= 120, $"常驻段长度应被预算压住，实际 {text.Length}");
    }

    [Fact]
    public async Task 快照给出预算生效值供观测()
    {
        var facts = new AssistantSituationDoubles.FakeFacts();
        var service = CreateService(facts);
        var digest = new SituationDigest([], [], []);

        var snapshot = await service.BuildSnapshotAsync(
            "u1", SituationContext.Empty, digest, CancellationToken.None);

        Assert.Equal(300, snapshot.Budget.ResidentTokenLimit);
        Assert.True(snapshot.Budget.ResidentTokens > 0);
        Assert.Empty(snapshot.Digest.Items);
    }
}

/// <summary>
/// 能力发现工具：只说本人**有**的权（避免误承诺），且清单只作按需查询、不常驻。
/// </summary>
public sealed class ListMyCapabilitiesToolTests
{
    [Fact]
    public async Task 只列出本人有浏览权限的模块()
    {
        var gateway = new AssistantSituationDoubles.FakeGateway();
        gateway.Modules.Add(new EOS.API.Models.SystemKnowledgeModule(1606, "客户订单"));
        gateway.Modules.Add(new EOS.API.Models.SystemKnowledgeModule(2301, "系统设置"));
        var permissions = new AssistantSituationDoubles.FakePermissions();
        permissions.Browsable.Add(1606);
        var tool = new ListMyCapabilitiesTool(gateway, permissions);

        var result = await tool.ExecuteAsync("u1", default, CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Contains("客户订单", result.ContentForModel);
        Assert.DoesNotContain("系统设置", result.ContentForModel);
        Assert.Contains("浏览/新增/修改/删除", result.ContentForModel);
    }

    [Fact]
    public void 动作位清单按实际授权位生成()
    {
        var granted = ListMyCapabilitiesTool.DescribeActions(
            new ModulePermission(AssistantSituationDoubles.FakePermissions.RightsFor(true, canSetup: true)));
        Assert.Equal("浏览/新增/修改/删除/配置", granted);
        // 没有的权不出现：不说"批核"才叫"避免误承诺"
        Assert.DoesNotContain("批核", granted);

        var browseOnly = ListMyCapabilitiesTool.DescribeActions(
            new ModulePermission(new EOS.API.Models.ModuleRights(
                CanBrowse: true, CanViewCost: false, CanViewSecrecy: false, CanSetup: false,
                DeniedMasterFields: new HashSet<string>(), DeniedDetailFields: new HashSet<string>(),
                CanAddNew: false, CanEdit: false, CanDelete: false, CanApprove: false, CanDeapprove: false,
                CanEndCase: false, CanUnEndCase: false, CanFileView: false, CanFileUpda: false,
                CanFileEdit: false, CanFileDele: false,
                DenyNewMasterFields: new HashSet<string>(), DenyNewDetailFields: new HashSet<string>(),
                DenyModiMasterFields: new HashSet<string>(), DenyModiDetailFields: new HashSet<string>(),
                DataFilter: string.Empty, ExecuteTag: "A")));

        Assert.Equal("浏览", browseOnly);
    }
}
