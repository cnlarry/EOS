using System.Security.Claims;
using EOS.API.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace EOS.API.Controllers;

/// <summary>
/// 只读助手试点接口：固定场景 + 服务端模块白名单 + 复用现有权限与字段过滤。
/// 当前支持采购单列表/详情与系统能力知识检索；不提供任意查询，也不向模型开放任何写能力。
/// </summary>
[ApiController, Authorize, Route("api/assistant")]
public sealed class AssistantController(
    DocumentWorkbenchRepository repository,
    LegacyRightsRepository rightsRepository) : ControllerBase
{
    /// <summary>
    /// 按关键字返回当前用户有权限查看的采购单主表记录，最多 10 条。
    /// keyword 仅用于记录行的服务端参数化检索，不参与模块/字段选择。
    /// </summary>
    [HttpGet("purchase-orders")]
    public async Task<IActionResult> PurchaseOrders(
        [FromQuery] string? keyword = null,
        [FromQuery] int limit = 5,
        CancellationToken token = default)
    {
        var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (userId is null)
        {
            return Unauthorized();
        }

        var definition = await AuthorizePurchaseOrderAsync(userId, token);
        if (definition is null)
        {
            return NotFound(new { code = "ASSISTANT_MODULE_NOT_FOUND", message = "未找到可用的采购单模块或当前用户无权访问" });
        }

        var pageSize = Math.Clamp(limit, 1, 10);
        var data = await repository.GetRowsAsync(
            definition,
            detail: false,
            keys: new Dictionary<string, string>(),
            page: 1,
            pageSize,
            token,
            query: null,
            keyword: string.IsNullOrWhiteSpace(keyword) ? null : keyword.Trim(),
            sortField: null,
            sortDirection: null);

        return Ok(new
        {
            moduleId = definition.ModuleId,
            moduleTitle = definition.Title,
            total = data.Total,
            rows = data.Rows,
        });
    }

    /// <summary>
    /// 采购单详情：按编号检索主表一行，并用主键取明细，返回给模型做单据解释。
    /// </summary>
    [HttpGet("purchase-orders/detail")]
    public async Task<IActionResult> PurchaseOrderDetail(
        [FromQuery] string keyword,
        CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(keyword))
        {
            return BadRequest(new { code = "INVALID_ARGUMENT", message = "keyword 不能为空" });
        }

        var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (userId is null)
        {
            return Unauthorized();
        }

        var definition = await AuthorizePurchaseOrderAsync(userId, token);
        if (definition is null)
        {
            return NotFound(new { code = "ASSISTANT_MODULE_NOT_FOUND", message = "未找到可用的采购单模块或当前用户无权访问" });
        }

        var masterData = await repository.GetRowsAsync(
            definition,
            detail: false,
            keys: new Dictionary<string, string>(),
            page: 1,
            pageSize: 1,
            token,
            query: null,
            keyword: keyword.Trim(),
            sortField: null,
            sortDirection: null);
        var master = masterData.Rows.FirstOrDefault();
        if (master is null)
        {
            return Ok(new { moduleTitle = definition.Title, total = masterData.Total, master = (object?)null, details = Array.Empty<object>() });
        }

        var keys = definition.MasterFields
            .Where(field => field.IsPrimaryKey && master.ContainsKey(field.Key))
            .ToDictionary(
                field => field.Key,
                field => master[field.Key]?.ToString() ?? string.Empty,
                StringComparer.OrdinalIgnoreCase);
        IReadOnlyList<Dictionary<string, object?>> details = [];
        if (keys.Count > 0 && definition.DetailTable is not null)
        {
            details = (await repository.GetRowsAsync(
                definition,
                detail: true,
                keys: keys,
                page: 1,
                pageSize: 50,
                token,
                query: null,
                keyword: null,
                sortField: null,
                sortDirection: null)).Rows;
        }

        return Ok(new
        {
            moduleTitle = definition.Title,
            total = masterData.Total,
            master,
            details,
        });
    }

    /// <summary>
    /// 系统能力知识检索：模块标题与字段元数据（字段含义），供模型回答"XX 字段/功能是什么意思"。
    /// </summary>
    [HttpGet("system-knowledge")]
    public async Task<IActionResult> SystemKnowledge(
        [FromQuery] string? query = null,
        [FromQuery] int limit = 8,
        CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return BadRequest(new { code = "INVALID_ARGUMENT", message = "query 不能为空" });
        }

        var result = await repository.SearchSystemKnowledgeAsync(query.Trim(), Math.Clamp(limit, 1, 20), token);
        return Ok(result);
    }

    private async Task<WorkbenchDefinition?> AuthorizePurchaseOrderAsync(string userId, CancellationToken token)
    {
        // 旧系统模块标题为"采购单"（PUR_PURCHASE_M），不是"采购订单"
        var moduleId = await repository.FindGenericModuleIdByTitleAsync("采购单", token);
        if (moduleId is null)
        {
            return null;
        }

        var rights = await rightsRepository.GetAsync(userId, moduleId.Value, token);
        if (!rights.CanBrowse)
        {
            return null;
        }

        return await repository.GetDefinitionAsync(
            moduleId.Value,
            userId,
            rights.CanViewCost,
            rights.CanViewSecrecy,
            rights.DeniedMasterFields,
            rights.DeniedDetailFields,
            token);
    }
}
