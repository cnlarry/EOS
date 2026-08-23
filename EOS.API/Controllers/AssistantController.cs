using System.Security.Claims;
using EOS.API.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace EOS.API.Controllers;

/// <summary>
/// 只读助手试点接口：固定场景 + 服务端模块白名单 + 复用现有权限与字段过滤。
/// 当前支持采购单列表/详情与系统能力知识检索；不提供任意查询，也不向模型开放任何写能力。
/// </summary>
[ApiController, Authorize, Route("api/v1/assistant")]
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
        [FromQuery] string? dateFrom = null,
        [FromQuery] string? dateTo = null,
        [FromQuery] string? status = null,
        [FromQuery] string? amountMin = null,
        [FromQuery] string? amountMax = null,
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
        AssistantFilterResult filterResult;
        try
        {
            filterResult = AssistantQueryBuilder.Build(definition, dateFrom, dateTo, status, amountMin, amountMax);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { code = "INVALID_ARGUMENT", message = ex.Message });
        }

        var data = await repository.GetRowsAsync(
            definition,
            detail: false,
            keys: new Dictionary<string, string>(),
            page: 1,
            pageSize,
            token,
            query: filterResult.Query,
            keyword: string.IsNullOrWhiteSpace(keyword) ? null : keyword.Trim(),
            sortField: null,
            sortDirection: null);

        return Ok(new
        {
            moduleId = definition.ModuleId,
            moduleTitle = definition.Title,
            total = data.Total,
            rows = data.Rows,
            labels = BuildLabels(definition.MasterFields),
            filtersNotApplied = filterResult.NotApplied,
        });
    }

    /// <summary>
    /// 库存盘点单列表：按关键字/日期/状态返回当前用户有权限查看的盘点单主表记录，最多 10 条。
    /// 与采购单列表同一套白名单 + 参数化检索机制，验证只读助手可复用到第二个业务域。
    /// </summary>
    [HttpGet("inventory-counts")]
    public async Task<IActionResult> InventoryCounts(
        [FromQuery] string? keyword = null,
        [FromQuery] string? dateFrom = null,
        [FromQuery] string? dateTo = null,
        [FromQuery] string? status = null,
        [FromQuery] string? amountMin = null,
        [FromQuery] string? amountMax = null,
        [FromQuery] int limit = 5,
        CancellationToken token = default)
    {
        var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (userId is null)
        {
            return Unauthorized();
        }

        var definition = await AuthorizeModuleByTitleAsync(userId, "库存盘点单", token);
        if (definition is null)
        {
            return NotFound(new { code = "ASSISTANT_MODULE_NOT_FOUND", message = "未找到可用的库存盘点单模块或当前用户无权访问" });
        }

        var pageSize = Math.Clamp(limit, 1, 10);
        AssistantFilterResult filterResult;
        try
        {
            filterResult = AssistantQueryBuilder.Build(definition, dateFrom, dateTo, status, amountMin, amountMax);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { code = "INVALID_ARGUMENT", message = ex.Message });
        }

        var data = await repository.GetRowsAsync(
            definition,
            detail: false,
            keys: new Dictionary<string, string>(),
            page: 1,
            pageSize,
            token,
            query: filterResult.Query,
            keyword: string.IsNullOrWhiteSpace(keyword) ? null : keyword.Trim(),
            sortField: null,
            sortDirection: null);

        return Ok(new
        {
            moduleId = definition.ModuleId,
            moduleTitle = definition.Title,
            total = data.Total,
            rows = data.Rows,
            labels = BuildLabels(definition.MasterFields),
            filtersNotApplied = filterResult.NotApplied,
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
            labels = BuildLabels(definition.MasterFields),
            detailLabels = definition.DetailFields.Count > 0
                ? BuildLabels(definition.DetailFields)
                : new Dictionary<string, string>(),
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

    /// <summary>
    /// 系统模块清单（总数 + 前 limit 个标题），用于回答"系统中有多少个/有哪些模块"。
    /// 仅返回安全元数据（模块 ID 与标题），不返回业务数据或高危表达式。
    /// </summary>
    [HttpGet("modules")]
    public async Task<IActionResult> Modules([FromQuery] int limit = 20, CancellationToken token = default)
    {
        var result = await repository.ListModulesAsync(Math.Clamp(limit, 1, 50), token);
        return Ok(result);
    }

    /// <summary>字段标签映射（列名 → 业务标题），供客户端/Agent 以可读文本呈现数据。</summary>
    private static Dictionary<string, string> BuildLabels(IReadOnlyList<WorkbenchField> fields)
        => fields.ToDictionary(field => field.Key, field => field.Label, StringComparer.OrdinalIgnoreCase);

    private async Task<WorkbenchDefinition?> AuthorizePurchaseOrderAsync(string userId, CancellationToken token)
        => await AuthorizeModuleByTitleAsync(userId, "采购单", token);

    private async Task<WorkbenchDefinition?> AuthorizeModuleByTitleAsync(string userId, string moduleTitle, CancellationToken token)
    {
        var moduleId = await repository.FindGenericModuleIdByTitleAsync(moduleTitle, token);
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
            rights.ExecuteTag,
            rights.CanViewCost,
            rights.CanViewSecrecy,
            rights.DeniedMasterFields,
            rights.DeniedDetailFields,
            token);
    }
}
