using EOS.API.Data;
using EOS.API.Errors;
using EOS.API.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace EOS.API.Controllers;

/// <summary>
/// 工作助手**管理侧**端点（菜单组 31 / 模块 3101，见 ADR-030）。
///
/// <para>
/// 与 <see cref="AssistantController"/>（个人侧）的分工：那一侧所有数据按 <c>USER_ID</c> 强制隔离，
/// 这一侧面向管理员、能看全系统会话，因此**单独一套端点与仓储**——把跨用户能力塞进个人侧会
/// 破坏它"越权一律看不到"的契约。
/// </para>
///
/// <para>
/// **只看元数据，不看正文**（ADR-030 §2）：这里没有"取某会话的消息"这种端点。要看正文是另一个
/// 需要独立权限位与审计留痕的决定。
/// </para>
///
/// <para>
/// 权限门：读 = <c>CanBrowse(3101)</c>，归档/删除 = <c>CanEdit(3101)</c>。
/// </para>
/// </summary>
[ApiController, Authorize, Route("api/v1/admin/assistant")]
public sealed class AssistantAdminController(
    IAssistantAdminRepository repository,
    CurrentUserContext userContext,
    ModuleRightsRepository rightsRepository,
    IEnumerable<Features.Assistant.Tools.IAssistantTool> assistantTools) : ControllerBase
{
    /// <summary>
    /// 能力面的边界。写在服务端而不是让前端硬编码：这些是"能力面上根本表达不出来"的东西，
    /// 摊在总览页上是为了让"助手不能做什么"与"能做什么"同样可见。
    /// </summary>
    private static readonly object[] MechanismBoundaries =
    [
        new
        {
            title = "权限授予类配置不可代劳",
            detail = "分权与授权（用户、用户组、按钮权限）在能力面上不存在：动作枚举里没有对应成员，"
                + "写入口也不在可触达的仓储方法集合里（结构断言见 NoPrivilegeConfigCallTests）。",
        },
        new
        {
            title = "批核族不可代理",
            detail = "批核 / 解批 / 结案 / 取消结案不在可代理动作里：助手只准备「操作请求卡」，"
                + "真正执行由界面直接调既有端点。",
        },
        new
        {
            title = "会话正文不开放给管理面",
            detail = "菜单组 31 的会话管理只给元数据，不提供对话正文（ADR-030 §2）；"
                + "真要开放，应当是「独立权限位 ＋ 审计留痕」的另一个决定。",
        },
        new
        {
            title = "密钥不入库",
            detail = "模型密钥只以环境变量名入库，密钥本身不落库、不下发前端、不进诊断包（ADR-030 §3）。",
        },
    ];

    /// <summary>归档 / 取消归档。<c>Archived</c> 缺省视为 <c>true</c>——空 body 不该把会话"取消归档"。</summary>
    public sealed record ArchiveSessionRequest(bool? Archived);

    /// <summary>
    /// 跨用户分页列会话（元数据）。<paramref name="state"/> 三态、<paramref name="owner"/> 按归属用户筛、
    /// <paramref name="sortBy"/><paramref name="sortDir"/> 排序。
    ///
    /// <para>
    /// **排序必须由服务端做**：列表是服务端分页的，在前端排只会排到当前这一页（看着"排了"其实是错的）。
    /// </para>
    /// </summary>
    [HttpGet("sessions")]
    public async Task<IActionResult> ListSessions(
        [FromQuery] int offset = 0,
        [FromQuery] int limit = 50,
        [FromQuery] string? state = null,
        [FromQuery] string? keyword = null,
        [FromQuery] string? owner = null,
        [FromQuery] string? sortBy = null,
        [FromQuery] string? sortDir = null,
        CancellationToken token = default)
    {
        if (!await CanBrowse(token)) return Forbid();
        var (items, total) = await repository.ListSessionsAsync(
            offset, limit, ParseState(state), keyword, owner,
            ParseSort(sortBy), string.Equals(sortDir, "asc", StringComparison.OrdinalIgnoreCase), token);
        return Ok(new { items, total });
    }

    /// <summary>出现过的归属用户（管理页"按用户筛选"的下拉）。</summary>
    [HttpGet("sessions/owners")]
    public async Task<IActionResult> ListOwners(CancellationToken token)
    {
        if (!await CanBrowse(token)) return Forbid();
        return Ok(await repository.ListOwnersAsync(token));
    }

    [HttpPut("sessions/{sessionId:long}/archive")]
    public async Task<IActionResult> ArchiveSession(long sessionId, [FromBody] ArchiveSessionRequest? request, CancellationToken token)
    {
        if (!await CanEdit(token)) return Forbid();
        var updated = await repository.ArchiveSessionAsync(sessionId, request?.Archived ?? true, token);
        return updated > 0 ? NoContent() : NotFound();
    }

    /// <summary>
    /// 永久删除会话（连消息，不可恢复）。**只对已归档会话生效**（与个人侧同一口径）：
    /// 仓储 SQL 里带 <c>ARCHIVED_AT IS NOT NULL</c>，所以"还在在列的会话"删不掉。
    /// </summary>
    [HttpDelete("sessions/{sessionId:long}")]
    public async Task<IActionResult> DeleteSession(long sessionId, CancellationToken token)
    {
        if (!await CanEdit(token)) return Forbid();
        var deleted = await repository.DeleteSessionAsync(sessionId, token);
        if (deleted > 0) return NoContent();
        // 没删掉有两种可能，这里分不出来（受影响行数为 0），所以如实把两种都告诉调用方
        return Conflict(ApiProblem.Create(
            StatusCodes.Status409Conflict, "NOT_DELETABLE",
            "会话不存在，或它还在「在列」——只有已归档的会话可以永久删除。"));
    }

    private async Task<bool> CanBrowse(CancellationToken token) =>
        (await rightsRepository.GetAsync(
            userContext.UserId, PermissionModules.AssistantAdmin.SessionAdmin, token)).CanBrowse;

    private async Task<bool> CanEdit(CancellationToken token) =>
        (await rightsRepository.GetAsync(
            userContext.UserId, PermissionModules.AssistantAdmin.SessionAdmin, token)).CanEdit;

    /// <summary>
    /// 机制与工具总览（只读）：当前挂着的工具、可选动作清单与能力面边界。权限门 3104 的 CanBrowse。
    ///
    /// <para>
    /// 数据全部**现算**（工具来自 DI 注册表、动作来自静态目录），不落库也不缓存：
    /// 这份清单的意义就是"代码里现在到底是什么"，缓存反而会让它说谎。
    /// </para>
    /// </summary>
    [HttpGet("mechanism")]
    public async Task<IActionResult> GetMechanism(CancellationToken token)
    {
        if (!await CanBrowseMechanism(token)) return Forbid();
        return Ok(new
        {
            tools = assistantTools
                .OrderBy(item => item.Name, StringComparer.Ordinal)
                .Select(item => new
                {
                    name = item.Name,
                    risk = item.Risk.ToString(),
                    description = item.Description,
                    parametersJson = item.ParametersJson,
                }),
            actions = Features.Assistant.Actions.AssistantActionRegistry.All,
            boundaries = MechanismBoundaries,
        });
    }

    private async Task<bool> CanBrowseMechanism(CancellationToken token) =>
        (await rightsRepository.GetAsync(
            userContext.UserId, PermissionModules.AssistantAdmin.Mechanism, token)).CanBrowse;

    /// <summary>未知取值一律按"在列"处理——列表查不出东西比抛 400 更难排查（与个人侧同口径）。</summary>
    internal static AssistantSessionListState ParseState(string? state) => state?.Trim().ToLowerInvariant() switch
    {
        "archived" or "only" or "onlyarchived" => AssistantSessionListState.Archived,
        "all" or "true" => AssistantSessionListState.All,
        _ => AssistantSessionListState.Active,
    };

    /// <summary>
    /// 排序列的解析：只认白名单里的名字，未知一律回落"最近活跃"。
    /// 传出去的是**枚举**，列名由仓储自己映射——调用方无法把列名拼进 SQL。
    /// </summary>
    internal static AssistantSessionSort ParseSort(string? sortBy) => sortBy?.Trim().ToLowerInvariant() switch
    {
        "title" => AssistantSessionSort.Title,
        "created" or "createdat" => AssistantSessionSort.Created,
        "messages" or "messagecount" => AssistantSessionSort.Messages,
        _ => AssistantSessionSort.LastActive,
    };
}
