using EOS.API.Data;
using EOS.API.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace EOS.API.Controllers;

/// <summary>
/// 库存策略（管到多细：位置 / 存放 / 批次 / 容量 / 混品号 / 混批次 + 月结维度）的读写。
///
/// 权限门挂在 <c>110310 库存策略</c> 模块上：策略决定全仓作业方式与数据语义，属高权限配置，
/// 不应与日常的库位维护（110309）或仓库资料（110306）同权限。读要求 CanBrowse、写要求 CanSetup。
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
    private const int StockPolicyModuleId = 110310;

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken token)
    {
        if (!await CanBrowse(token)) return Forbid();

        var policies = await service.ListAsync(token);
        return Ok(policies.Select(ToDto).ToList());
    }

    /// <summary>
    /// 档位目录（界面渲染用）：每个维度的候选值与"本版是否实现"。未实现的档位界面灰显，
    /// 与服务端拒存规则同源（<see cref="DepotStockPolicyService.Tiers"/>）。
    /// </summary>
    [HttpGet("tiers")]
    public async Task<IActionResult> Tiers(CancellationToken token)
    {
        if (!await CanBrowse(token)) return Forbid();

        return Ok(DepotStockPolicyService.Tiers);
    }

    /// <summary>仓库清单（新增策略行时选库别用；已配策略的过滤由界面按策略行清单做）。</summary>
    [HttpGet("depots")]
    public async Task<IActionResult> Depots(CancellationToken token)
    {
        if (!await CanBrowse(token)) return Forbid();

        return Ok(await service.ListDepotsAsync(token));
    }

    /// <summary>某库别下可用的归位目标库位（启用中，不含哨兵行）。</summary>
    [HttpGet("{depotId}/locations")]
    public async Task<IActionResult> Locations(string depotId, CancellationToken token)
    {
        if (!await CanBrowse(token)) return Forbid();

        return Ok(await service.ListLocationsAsync(depotId, token));
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
            request.MonthCloseByLocation,
            request.MonthCloseScopeHalfStock);

        var result = await service.SaveAsync(
            candidate, userContext.EmployeeName, request.ConfirmDowngrade, request.RelocateTo, token);

        var payload = new DepotStockPolicySaveResultDto(result.Saved, result.Errors, result.Warnings, result.RequiresConfirmation);
        // 需要二次确认时同样返回 400，但响应里 requiresConfirmation=true 是机器可判的信号：
        // 调用方据此弹确认，确认后原样重发并带上 confirmDowngrade=true。
        return result.Saved ? Ok(payload) : BadRequest(payload);
    }

    /// <summary>删除一条库别策略行；部署级默认行不允许删除。</summary>
    [HttpDelete("{depotId}")]
    public async Task<IActionResult> Delete(string depotId, CancellationToken token)
    {
        if (!await CanSetup(token)) return Forbid();

        var result = await service.DeleteAsync(depotId, userContext.EmployeeName, token);
        return result.Deleted
            ? Ok(new { saved = true, message = result.Message })
            : result.Errors.Any(error => error.Contains("无需删除"))
                ? NotFound(new { saved = false, message = result.Errors.First() })
                : BadRequest(new { saved = false, message = string.Join('；', result.Errors) });
    }

    /// <summary>
    /// 独立归位（不改策略配置）：把该库别记在『未指定位置』上的存量改记到目标库位。
    /// confirm 非真时只预览（校验 + 数出几组，一行不写）；confirm 为真时执行。
    /// </summary>
    [HttpPost("{depotId}/relocate")]
    public async Task<IActionResult> Relocate(
        string depotId,
        RelocateSentinelRequest request,
        CancellationToken token)
    {
        if (!await CanSetup(token)) return Forbid();

        if (request is null || request.Confirm)
        {
            var result = await service.RelocateStandaloneAsync(
                depotId, request?.RelocateTo ?? string.Empty, userContext.EmployeeName, token);
            return result.Relocated
                ? Ok(new { saved = true, message = result.Message })
                : BadRequest(new { saved = false, message = result.Errors.Count > 0
                    ? string.Join('；', result.Errors) : result.Message });
        }

        var preview = await service.PreviewRelocateAsync(
            depotId, request.RelocateTo ?? string.Empty, token);
        return preview.Errors.Count > 0
            ? BadRequest(new { saved = false, message = string.Join('；', preview.Errors) })
            : Ok(new
            {
                saved = false,
                requiresConfirmation = true,
                pendingGroups = preview.PendingGroups,
                message = preview.Message,
            });
    }

    private async Task<bool> CanBrowse(CancellationToken token) =>
        (await rightsRepository.GetAsync(userContext.UserId, StockPolicyModuleId, token)).CanBrowse;

    private async Task<bool> CanSetup(CancellationToken token) =>
        (await rightsRepository.GetAsync(userContext.UserId, StockPolicyModuleId, token)).CanSetup;

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
    bool MonthCloseByLocation,
    /// <summary>月结范围是否含半成品（按制程）账。本版只允许 false——见
    /// <see cref="DepotStockPolicyService.ValidateMonthCloseScope"/> 的理由（快照侧未落地）。</summary>
    bool MonthCloseScopeHalfStock = false,
    /// <summary>档位下调（位置 / 批次档位降低）的二次确认标志；不确认则拒存且不写入任何改动。</summary>
    bool ConfirmDowngrade = false,
    /// <summary>位置档位**升档**时的归位目标库位：给了就把「未指定位置」的存量改记到该库位。
    /// 不给且确有存量时会要求明确表态（见 <see cref="DepotStockPolicyService.SaveAsync"/> 的说明）。</summary>
    string? RelocateTo = null);

/// <summary>保存结果：<c>Saved=false</c> 时 <c>Errors</c> 说明被拒原因；<c>Warnings</c> 为软性提示（可保存）。
/// <c>RequiresConfirmation=true</c> 表示这是一次尚未确认的破坏性档位下调。</summary>
public sealed record DepotStockPolicySaveResultDto(
    bool Saved,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> Warnings,
    bool RequiresConfirmation);

/// <summary>独立归位请求：目标库位 + 是否确认执行（非真即预览）。</summary>
public sealed record RelocateSentinelRequest(
    string? RelocateTo,
    bool Confirm = false);
