using System.Security.Claims;
using EOS.API.Data;
using EOS.API.Models;
using EOS.API.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace EOS.API.Controllers;

/// <summary>
/// 报表的**身份优先**入口：`/api/v1/report/{reportId}/...`。
/// </summary>
/// <remarks>
/// <para>
/// 为什么要有这条路：报表原先只能"按模块打开"（`/api/v1/reports/{moduleId}/...`），报表身份是
/// 页面内状态——谁在什么条件下打开了哪张报表，URL 里看不出来，也就没法深链、没法分享、没法收藏
/// 到具体一张。本控制器把报表编号提升为 URL 身份：拿到编号先解析出**归属模块**，再按那个模块
/// 判权限并取数。
/// </para>
/// <para>
/// 归属模块一律由服务端解析（`REPORT.M_IDX`），**不接受调用方指定**：否则任何人都能把一个报表
/// 编号挂到别的模块号上去、借用那个模块的权限打开它。解析失败 → 404；解析成功但当前用户对
/// 该模块没有浏览权 → 403（两者语义不同，前者是"没有这张报表"，后者是"有但不给你"）。
/// </para>
/// <para>
/// 取数、定义、条件选项、导出、PDF 这五件事与老的模块路径**是同一套编排**，只是模块号来源不同。
/// 所以这里解析出模块号之后**转发给既有动作**，而不是把编排抄第二遍——抄一遍就有两处会各自漂移
/// （历史上已经有先例：条件选项与导出两处把报表编号丢掉，用的其实是默认报表的定义与权限）。
/// </para>
/// </remarks>
[ApiController]
[Authorize]
[Route("api/v1/report")]
public sealed class ReportResourceController(
    ReportRepository repository,
    PrintSettingsRepository printSettingsRepository,
    IPermissionService permissions,
    IServiceProvider provider) : ControllerBase
{
    /// <summary>报表身份 + 同模块可见的其他报表（查看器据此渲染标题与切换清单）。</summary>
    [HttpGet("{reportId}")]
    public async Task<IActionResult> Identity(string reportId, CancellationToken token)
    {
        var gate = await AuthorizeAsync(reportId, token);
        if (gate.Result is not null) return gate.Result;
        var identity = gate.Identity!;
        return Ok(new ReportIdentity(identity.ModuleId, identity.ReportId, identity.ReportName, identity.ModuleName)
        {
            // 兄弟清单只列**当前用户可见**的那些：目录里看不到的报表，不该从深链的标题栏漏出来。
            Siblings = await SiblingsAsync(identity.ModuleId, gate.UserId!, token),
        });
    }

    /// <summary>按归属模块列报表（工具条「报表」动作、以及老模块地址跳转后的落点）。</summary>
    [HttpGet]
    public async Task<IActionResult> ByModule([FromQuery] int moduleId, CancellationToken token)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId is null) return Unauthorized();
        var rights = (await permissions.GetAsync(userId, moduleId, token)).Rights;
        if (!rights.CanBrowse) return Forbid();
        var reports = await SiblingsAsync(moduleId, userId, token);
        return Ok(new ReportModuleReports(moduleId, await ModuleNameAsync(moduleId, token), reports));
    }

    [HttpGet("{reportId}/definition")]
    public async Task<IActionResult> Definition(string reportId, CancellationToken token)
        => await ForwardAsync(reportId, (legacy, moduleId) => legacy.Definition(moduleId, reportId, token), token);

    [HttpPost("{reportId}/query")]
    public async Task<IActionResult> Query(string reportId,
        [FromQuery] int page, [FromQuery] int pageSize,
        [FromBody] ReportQueryRequest request, CancellationToken token)
        => await ForwardAsync(reportId, (legacy, moduleId) => legacy.Query(moduleId, page, pageSize, reportId, request, token), token);

    [HttpGet("{reportId}/condition-options/{serialNo:int}")]
    public async Task<IActionResult> ConditionOptions(string reportId, int serialNo, CancellationToken token)
        => await ForwardAsync(reportId, (legacy, moduleId) => legacy.ConditionOptions(moduleId, serialNo, reportId, token), token);

    [HttpPost("{reportId}/export")]
    public async Task<IActionResult> Export(string reportId,
        [FromBody] ReportQueryRequest request, CancellationToken token)
        => await ForwardAsync(reportId, (legacy, moduleId) => legacy.Export(moduleId, reportId, request, token), token);

    [HttpPost("{reportId}/pdf")]
    public async Task<IActionResult> Pdf(string reportId,
        [FromBody] ReportPdfRequest request, CancellationToken token)
        => await ForwardAsync(reportId,
            // 路径上的编号就是这张报表的身份，覆盖请求体里的同名字段：否则 URL 与请求体各说一套，
            // 打印出来的可能是另一张（或默认那张）。
            (legacy, moduleId) => legacy.Pdf(moduleId, request with { ReportId = reportId }, token),
            token);

    /// <summary>解析身份 + 判权限。404 = 没有这张报表；403 = 有但不给。</summary>
    private async Task<(int ModuleId, string UserId, IActionResult? Result, ReportIdentity? Identity)> AuthorizeAsync(
        string reportId, CancellationToken token)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId is null) return (0, string.Empty, Unauthorized(), null);
        var identity = await repository.FindIdentityAsync(reportId, token);
        if (identity is null) return (0, userId, NotFound(), null);
        var rights = (await permissions.GetAsync(userId, identity.ModuleId, token)).Rights;
        if (!rights.CanBrowse) return (identity.ModuleId, userId, Forbid(), identity);
        return (identity.ModuleId, userId, null, identity);
    }

    private async Task<IActionResult> ForwardAsync(string reportId,
        Func<ReportController, int, Task<IActionResult>> invoke, CancellationToken token)
    {
        var gate = await AuthorizeAsync(reportId, token);
        if (gate.Result is not null) return gate.Result;
        return await invoke(Legacy(), gate.ModuleId);
    }

    /// <summary>
    /// 既有模块路径的那个控制器实例：把编排复用过来，而不是抄一遍。
    /// `ControllerContext` 必须一并带上——动作里的 <c>User</c>、<c>Forbid()</c>、<c>File()</c>
    /// 都挂在它上面，不带就等于换了个人、丢了响应通道。
    /// </summary>
    private ReportController Legacy()
    {
        var controller = (ReportController)ActivatorUtilities.CreateInstance(provider, typeof(ReportController));
        controller.ControllerContext = ControllerContext;
        return controller;
    }

    /// <summary>归属模块下当前用户可见的报表清单（含默认报表优先序）。</summary>
    private async Task<IReadOnlyList<ReportSibling>> SiblingsAsync(int moduleId, string userId, CancellationToken token)
    {
        var settings = await printSettingsRepository.GetAsync(moduleId, userId, token);
        return settings.Reports
            .Select(item => new ReportSibling(item.ReportId, item.ReportName, item.IsDefault))
            .ToList();
    }

    private async Task<string> ModuleNameAsync(int moduleId, CancellationToken token)
        => await repository.FindModuleNameAsync(moduleId, token) ?? string.Empty;
}
