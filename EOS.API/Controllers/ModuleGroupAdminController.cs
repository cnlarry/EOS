using EOS.API.Data;
using EOS.API.Data.ModuleGroups;
using EOS.API.Errors;
using EOS.API.Models;
using EOS.API.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace EOS.API.Controllers;

/// <summary>
/// 模块分组（2315「模块分组」，定制承载页 `/admin/module-groups`）：为**其它**模块维护列表分组
/// （分组名称 + 分组表达式 + 顺序）。
///
/// 权限门挂在 2315 上：读要求 CanBrowse，写要求 CanSetup；2315 的授权行按 2301 模块管理镜像
/// （同一批配置维护者）。目标模块的形态与表达式由 <see cref="ModuleGroupAdminRepository"/> 把关——
/// 只有统一工作台模块才有分组消费方，表达式必须来自它主表的物理字段。
///
/// **保存即生效**：分组表达式不进定义快照（组装工作台定义时实时读取），故这个配置面不涉及发布。
/// </summary>
[ApiController, Authorize, Route("api/v1/admin/module-groups")]
public sealed class ModuleGroupAdminController(
    ModuleGroupAdminRepository repository,
    ModuleRightsRepository rightsRepository,
    CurrentUserContext userContext,
    WorkbenchAuditWriter auditWriter) : ControllerBase
{
    private const int ModuleGroupsModuleId = 2315;

    [HttpGet("{moduleId:int}")]
    public async Task<IActionResult> Groups(int moduleId, CancellationToken token)
    {
        if (!await CanBrowse(token)) return Forbid();
        var view = await repository.GetAsync(moduleId, token);
        return view is null
            ? NotFound(ApiProblem.Create(StatusCodes.Status404NotFound, ApiErrorCodes.NotFound, $"模块 {moduleId} 不存在。"))
            : Ok(view);
    }

    /// <summary>该模块可参与分组表达式的字段（与写入校验同一份白名单，供界面提示可引用的列）。</summary>
    [HttpGet("{moduleId:int}/fields")]
    public async Task<IActionResult> Fields(int moduleId, CancellationToken token)
    {
        if (!await CanBrowse(token)) return Forbid();
        return Ok(new { fields = await repository.ReadAllowedFieldsAsync(moduleId, token) });
    }

    [HttpPost("{moduleId:int}")]
    public async Task<IActionResult> Create(int moduleId, ModuleGroupSaveRequest? request, CancellationToken token)
    {
        if (!await CanSetup(token)) return Forbid();
        var row = await repository.CreateAsync(
            moduleId, request?.Description, request?.Expression, userContext.EmployeeName, token);
        await AuditAsync(moduleId, row.GroupId, "MODULE_GROUP_CREATE", $"新增分组「{row.Description}」：{row.Expression}", token);
        return Ok(row);
    }

    [HttpPut("{moduleId:int}/{groupId:int}")]
    public async Task<IActionResult> Update(int moduleId, int groupId, ModuleGroupSaveRequest? request, CancellationToken token)
    {
        if (!await CanSetup(token)) return Forbid();
        var row = await repository.UpdateAsync(
            moduleId, groupId, request?.Description, request?.Expression, userContext.EmployeeName, token);
        await AuditAsync(moduleId, groupId, "MODULE_GROUP_UPDATE", $"修改分组「{row.Description}」：{row.Expression}", token);
        return Ok(row);
    }

    [HttpDelete("{moduleId:int}/{groupId:int}")]
    public async Task<IActionResult> Delete(int moduleId, int groupId, CancellationToken token)
    {
        if (!await CanSetup(token)) return Forbid();
        await repository.DeleteAsync(moduleId, groupId, userContext.EmployeeName, token);
        await AuditAsync(moduleId, groupId, "MODULE_GROUP_DELETE", "删除分组", token);
        return NoContent();
    }

    /// <summary>同一模块内的排序（top / up / down / bottom）；边界移动是无操作。</summary>
    [HttpPost("{moduleId:int}/{groupId:int}/move")]
    public async Task<IActionResult> Move(int moduleId, int groupId, ModuleGroupMoveRequest? request, CancellationToken token)
    {
        if (!await CanSetup(token)) return Forbid();
        var action = (request?.Action ?? string.Empty).Trim().ToLowerInvariant();
        await repository.MoveAsync(moduleId, groupId, action, userContext.EmployeeName, token);
        await AuditAsync(moduleId, groupId, "MODULE_GROUP_MOVE", $"分组排序：{action}", token);
        return NoContent();
    }

    /// <summary>
    /// 配置写留痕：模块号写**目标模块**（"谁的分组被改了"），资源键带上分组编号。
    /// 审计身份是真人（<c>userContext.EmployeeName</c>），与助手侧的代理身份口径一致。
    ///
    /// 动作名带 `MODULE_` 前缀：`GROUP_SAVE` / `GROUP_DELETE` 已被 2306 用户权限设定的**用户组**
    /// 增删占用，按 `ACTION` 检索审计时两者会混在一起（只能靠 `RESOURCE_TYPE` 再分辨）。
    /// </summary>
    private Task AuditAsync(int moduleId, int groupId, string action, string summary, CancellationToken token) =>
        auditWriter.WriteBestEffortAsync(
            moduleId, $"module-group:{groupId}", action, summary,
            userContext.EmployeeName, "MODULE_GROUP", result: 1, null, token);

    private async Task<bool> CanBrowse(CancellationToken token) =>
        (await rightsRepository.GetAsync(userContext.UserId, ModuleGroupsModuleId, token)).CanBrowse;

    private async Task<bool> CanSetup(CancellationToken token) =>
        (await rightsRepository.GetAsync(userContext.UserId, ModuleGroupsModuleId, token)).CanSetup;
}
