using System.Security.Claims;
using System.Text.Json;
using EOS.API.Controllers;
using EOS.API.Data;
using EOS.API.Data.Reports;
using EOS.API.Features.Assistant.Metrics;
using EOS.API.Models;
using EOS.API.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 2201 报表设置的能力位与写门（离线：不连库，权限用桩）。
///
/// <para>
/// 两件事分别钉住：① `GET /report-admin/capabilities` 如实下发 `canSetup`，界面据此禁用写入口；
/// ② 能力位只是体验层——**每个写端点在只有浏览权时必须自己拒绝**，不依赖前端是否禁用了按钮。
/// </para>
/// </summary>
public class ReportAdminCapabilitiesTests
{
    private static readonly ModulePermission BrowseOnly = Permission(browse: true, setup: false);
    private static readonly ModulePermission Full = Permission(browse: true, setup: true);
    private static readonly ModulePermission None = Permission(browse: false, setup: false);

    private static ModulePermission Permission(bool browse, bool setup) => new(new ModuleRights(
        CanBrowse: browse,
        CanViewCost: false,
        CanViewSecrecy: false,
        CanSetup: setup,
        DeniedMasterFields: new HashSet<string>(),
        DeniedDetailFields: new HashSet<string>(),
        CanAddNew: false,
        CanEdit: false,
        CanDelete: false,
        CanApprove: false,
        CanDeapprove: false,
        CanEndCase: false,
        CanUnEndCase: false,
        CanFileView: false,
        CanFileUpda: false,
        CanFileEdit: false,
        CanFileDele: false,
        DenyNewMasterFields: new HashSet<string>(),
        DenyNewDetailFields: new HashSet<string>(),
        DenyModiMasterFields: new HashSet<string>(),
        DenyModiDetailFields: new HashSet<string>(),
        DataFilter: string.Empty,
        ExecuteTag: string.Empty));

    /// <summary>按模块号返回权限的桩；未登记的模块号直接报错（避免静默放过一个没考虑的模块号）。</summary>
    private sealed class StubPermissionService(params (int ModuleId, ModulePermission Permission)[] grants)
        : IPermissionService
    {
        public Task<ModulePermission> GetAsync(string userId, int moduleId, CancellationToken cancellationToken)
        {
            foreach (var (id, permission) in grants)
            {
                if (id == moduleId) return Task.FromResult(permission);
            }
            throw new InvalidOperationException($"桩未登记模块 {moduleId} 的权限。");
        }

        public Task<ModulePermission> RequireAsync(
            string userId, int moduleId, PermissionAction action, CancellationToken cancellationToken) =>
            throw new NotSupportedException("能力位与写门走 GetAsync；RequireAsync 不在本控制器的通道上。");

        public Task<ReportRights> GetReportAsync(
            string userId, int moduleId, string reportId, CancellationToken cancellationToken) =>
            throw new NotSupportedException("报表设置维护不消费报表权。");
    }

    private static ReportAdminController Controller(IPermissionService permissions, string? userId = "u1")
        => new(
            repository: null!,
            reportFormats: null!,
            permissions: permissions,
            metrics: null!,
            metricValidator: null!) // 这些端点不走仓储；本组用例只碰权限门
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                        userId is null ? [] : [new Claim(ClaimTypes.NameIdentifier, userId)], "test")),
                },
            },
        };

    private static async Task<JsonElement> PayloadAsync(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        using var document = JsonDocument.Parse(
            JsonSerializer.Serialize(ok.Value, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        return document.RootElement.Clone();
    }

    [Fact]
    public async Task Capabilities_Reports_CanSetup_True_For_Setup_Holder()
    {
        var controller = Controller(new StubPermissionService(
            (PermissionModules.ReportAdmin, Full), (PermissionModules.MetricCatalog, Full)));

        var payload = await PayloadAsync(await controller.Capabilities(CancellationToken.None));

        Assert.True(payload.GetProperty("canBrowse").GetBoolean());
        Assert.True(payload.GetProperty("canSetup").GetBoolean());
    }

    [Fact]
    public async Task Capabilities_Reports_CanSetup_False_For_BrowseOnly()
    {
        var controller = Controller(new StubPermissionService(
            (PermissionModules.ReportAdmin, BrowseOnly), (PermissionModules.MetricCatalog, Full)));

        var payload = await PayloadAsync(await controller.Capabilities(CancellationToken.None));

        Assert.True(payload.GetProperty("canBrowse").GetBoolean());
        Assert.False(payload.GetProperty("canSetup").GetBoolean());
    }

    [Fact]
    public async Task Capabilities_Forbids_When_Module_Not_Browsable()
    {
        var controller = Controller(new StubPermissionService(
            (PermissionModules.ReportAdmin, None), (PermissionModules.MetricCatalog, Full)));

        Assert.IsType<ForbidResult>(await controller.Capabilities(CancellationToken.None));
    }

    [Fact]
    public async Task Capabilities_Forbids_Without_Identity()
    {
        var controller = Controller(
            new StubPermissionService((PermissionModules.ReportAdmin, Full)), userId: null);

        Assert.IsType<ForbidResult>(await controller.Capabilities(CancellationToken.None));
    }

    /// <summary>
    /// 能力位不是权限依据：浏览权账号直接打写端点仍必须被拒。
    /// 参数故意给无效值——权限门在参数校验之前，被拒就证明"压根没走到校验"。
    /// </summary>
    [Fact]
    public async Task Write_Endpoints_Reject_BrowseOnly_Regardless_Of_Capabilities()
    {
        var controller = Controller(new StubPermissionService(
            (PermissionModules.ReportAdmin, BrowseOnly), (PermissionModules.MetricCatalog, BrowseOnly)));

        Assert.IsType<ForbidResult>(await controller.CreateReport(null!, CancellationToken.None));
        Assert.IsType<ForbidResult>(await controller.UpdateReport("RPT_X", null!, CancellationToken.None));
        Assert.IsType<ForbidResult>(await controller.DeleteReport("RPT_X", CancellationToken.None));
        Assert.IsType<ForbidResult>(await controller.CreateSort("RPT_X", null!, CancellationToken.None));
        Assert.IsType<ForbidResult>(await controller.UpdateSort("RPT_X", 1, null!, CancellationToken.None));
        Assert.IsType<ForbidResult>(await controller.DeleteSort("RPT_X", 1, CancellationToken.None));
        // 指标口径走独立模块 2317：同样只有浏览权 → 拒
        Assert.IsType<ForbidResult>(await controller.CreateMetric(null!, CancellationToken.None));
    }
}
