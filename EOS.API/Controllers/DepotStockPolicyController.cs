using EOS.API.Data;
using EOS.API.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace EOS.API.Controllers;

/// <summary>
/// 库存策略（管到多细：位置 / 存放 / 批次 / 容量 / 混品号 / 混批次 + 月结维度）的读写。
///
/// 权限门挂在 <c>110306 仓库资料</c> 上：策略决定全仓作业方式与数据语义，属高权限配置，
/// 不应与日常的库位维护同权限。读要求 CanBrowse、写要求 CanSetup。
/// </summary>
/// <remarks>
/// 保存与求值共用 <see cref="DepotStockPolicyService"/> 这一个出口：界面能看到的取值口径，
/// 与服务端计算用的口径必须来自同一处，否则会出现"配上了却不生效"。
/// 硬性组合规则与月结作用域违规在服务端拒存（fail-closed），不依赖界面拦截。
/// </remarks>
[ApiController, Authorize, Route("api/v1/admin/depot-stock-policy")]
public sealed class DepotStockPolicyController(
    DepotStockPolicyService service,
    ModuleRightsRepository rightsRepository,
    CurrentUserContext userContext) : ControllerBase
{
    private const int DepotAdminModuleId = 110306;

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken token)
    {
        if (!await CanBrowse(token)) return Forbid();

        var policies = await service.ListAsync(token);
        return Ok(policies.Select(ToDto).ToList());
    }

    /// <summary>保存一条策略行；<c>depotId</c> 传 <c>*</c> 即部署级默认行。</summary>
    [HttpPut("{depotId}")]
    public async Task<IActionResult> Save(
        string depotId,
        SaveDepotStockPolicyRequest request,
        CancellationToken token)
    {
        if (!await CanSetup(token)) return Forbid();

        var candidate = new DepotStockPolicy(
            string.IsNullOrWhiteSpace(depotId) ? DepotStockPolicyService.DeploymentScope : depotId.Trim(),
            request.LocationMode,
            (request.StorageMode ?? string.Empty).Trim().ToUpperInvariant(),
            request.BatchMode,
            request.CapacityMode,
            request.MixProduct,
            request.MixBatch,
            request.MonthCloseByBatch,
            request.MonthCloseByLocation);

        var result = await service.SaveAsync(candidate, userContext.EmployeeName, token);

        var payload = new DepotStockPolicySaveResultDto(result.Saved, result.Errors, result.Warnings);
        return result.Saved ? Ok(payload) : BadRequest(payload);
    }

    private async Task<bool> CanBrowse(CancellationToken token) =>
        (await rightsRepository.GetAsync(userContext.UserId, DepotAdminModuleId, token)).CanBrowse;

    private async Task<bool> CanSetup(CancellationToken token) =>
        (await rightsRepository.GetAsync(userContext.UserId, DepotAdminModuleId, token)).CanSetup;

    private static DepotStockPolicyDto ToDto(DepotStockPolicy policy) => new(
        policy.DepotId,
        policy.LocationMode,
        policy.StorageMode,
        policy.BatchMode,
        policy.CapacityMode,
        policy.MixProduct,
        policy.MixBatch,
        policy.MonthCloseByBatch,
        policy.MonthCloseByLocation);
}

public sealed record DepotStockPolicyDto(
    string DepotId,
    int LocationMode,
    string StorageMode,
    int BatchMode,
    int CapacityMode,
    bool MixProduct,
    bool MixBatch,
    bool MonthCloseByBatch,
    bool MonthCloseByLocation);

public sealed record SaveDepotStockPolicyRequest(
    int LocationMode,
    string? StorageMode,
    int BatchMode,
    int CapacityMode,
    bool MixProduct,
    bool MixBatch,
    bool MonthCloseByBatch,
    bool MonthCloseByLocation);

/// <summary>保存结果：<c>Saved=false</c> 时 <c>Errors</c> 说明被拒原因；<c>Warnings</c> 为软性提示（可保存）。</summary>
public sealed record DepotStockPolicySaveResultDto(
    bool Saved,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> Warnings);
