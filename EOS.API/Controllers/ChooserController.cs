using EOS.API.Data;
using EOS.API.Errors;
using EOS.API.Models;
using EOS.API.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace EOS.API.Controllers;

/// <summary>
/// 统一选择器（通用数据源查询）：sourceKey 由服务端注册表解析，权限按数据源定义收紧。
/// 前端只传 sourceKey + 白名单参数，不传表名/列名/SQL；动态查询全部参数化。
/// </summary>
[ApiController, Authorize, Route("api/v1/chooser")]
public sealed class ChooserController(
    ChooserRepository repository,
    ModuleRightsRepository rightsRepository,
    CurrentUserContext userContext) : ControllerBase
{
    [HttpPost("query")]
    public async Task<IActionResult> Query(UnifiedChooserQueryRequest request, CancellationToken token)
    {
        var sourceKey = request.SourceKey?.Trim();
        if (!ChooserRepository.IsRegistered(sourceKey))
            return BadRequest(ApiProblem.Create(StatusCodes.Status400BadRequest, "UNKNOWN_SOURCE", "未知的选择器数据源。"));
        if (ChooserRepository.PermissionModuleId(sourceKey) is { } moduleId)
        {
            var rights = await rightsRepository.GetAsync(userContext.UserId, moduleId, token);
            // 权限不足按 404 返回（与单据路径同一防探测口径）：403 会告诉调用方"该数据源存在、
            // 只是你没权限"，等于把数据源清单当探测面。前端只依赖"拿不到数据"这一事实。
            if (!rights.CanBrowse)
                return NotFound(ApiProblem.Create(StatusCodes.Status404NotFound, "SOURCE_NOT_FOUND", "选择器数据源不存在。"));
        }
        var result = await repository.QueryAsync(request, token);
        return result is null
            ? NotFound(ApiProblem.Create(StatusCodes.Status404NotFound, "SOURCE_EMPTY", "选择器数据源无返回。"))
            : Ok(result);
    }
}
