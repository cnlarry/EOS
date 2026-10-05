using EOS.API.Data;
using EOS.API.Errors;
using EOS.API.Models;
using EOS.API.Security;
using Microsoft.AspNetCore.Mvc;

namespace EOS.API.Controllers;

public sealed record ParseExpressionRequest(string Kind, string? Expression);

[ApiController]
[Route("api/v1/admin")]
public sealed class FieldAdminController(
    FieldAdminRepository repository,
    ModuleRightsRepository rightsRepository,
    CurrentUserContext userContext,
    RestrictedExpressionService expressionService) : ControllerBase
{
    private const int AdminModuleId = 2302;

    [HttpGet("tables")]
    public async Task<IActionResult> Tables([FromQuery] string? kind = null, CancellationToken token = default)
    {
        if (!await CanBrowse(token)) return Forbid();
        return Ok(await repository.GetTablesAsync(kind, token));
    }

    [HttpGet("tables/{table}")]
    public async Task<IActionResult> Table(string table, CancellationToken token)
    {
        if (!await CanBrowse(token)) return Forbid();
        var detail = await repository.GetTableAsync(table, token);
        return detail is null ? NotFound() : Ok(detail);
    }

    [HttpPost("tables")]
    public async Task<IActionResult> CreateTable(CreateFieldAdminTableRequest request, CancellationToken token)
    {
        if (!await CanSetup(token)) return Forbid();
        await repository.CreateTableAsync(request, userContext.EmployeeName, token);
        return NoContent();
    }

    [HttpPut("tables/{table}")]
    public async Task<IActionResult> UpdateTable(string table, UpdateFieldAdminTableRequest request, CancellationToken token)
    {
        if (!await CanSetup(token)) return Forbid();
        await repository.UpdateTableAsync(table, request.Table, request.Original, userContext.EmployeeName, token);
        return NoContent();
    }

    [HttpDelete("tables/{table}")]
    public async Task<IActionResult> DeleteTable(string table, CancellationToken token)
    {
        if (!await CanSetup(token)) return Forbid();
        await repository.DeleteTableAsync(table, userContext.EmployeeName, token);
        return NoContent();
    }

    [HttpGet("lookups/modules")]
    public async Task<IActionResult> LookupModules(CancellationToken token)
    {
        if (!await CanBrowse(token)) return Forbid();
        return Ok(await repository.GetModulesAsync(token));
    }

    /// <summary>未登记进 TABLES 的物理表/视图候选（新增数据表元数据按选取方式录入）。</summary>
    [HttpGet("lookups/physical-tables")]
    public async Task<IActionResult> LookupPhysicalTables(CancellationToken token)
    {
        if (!await CanBrowse(token)) return Forbid();
        return Ok(await repository.GetPhysicalObjectsAsync(token));
    }

    /// <summary>
    /// 从物理表/视图登记表元数据并自动生成字段元数据（描述取表/列说明，类型按物理列匹配）。
    /// 只登记已存在的物理对象，不创建物理表。
    /// </summary>
    [HttpPost("tables/from-physical")]
    public async Task<IActionResult> RegisterPhysicalTable(RegisterPhysicalTableRequest request, CancellationToken token)
    {
        if (!await CanSetup(token)) return Forbid();
        return Ok(await repository.RegisterPhysicalTableAsync(request, userContext.EmployeeName, token));
    }

    [HttpGet("tables/{table}/fields")]
    public async Task<IActionResult> Fields(
        string table,
        [FromQuery] string? keyword = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] bool excludeSystem = false,
        CancellationToken token = default)
    {
        if (!await CanBrowse(token)) return Forbid();
        return Ok(await repository.GetFieldsAsync(table, keyword, page, pageSize, token, excludeSystem));
    }

    [HttpGet("tables/{table}/fields/unmanaged")]
    public async Task<IActionResult> UnmanagedFields(string table, CancellationToken token)
    {
        if (!await CanBrowse(token)) return Forbid();
        return Ok(await repository.GetUnmanagedFieldsAsync(table, token));
    }

    [HttpPost("tables/{table}/fields/batch")]
    public async Task<IActionResult> CreateUnmanagedFields(string table, CreateUnmanagedFieldsRequest request, CancellationToken token)
    {
        if (!await CanSetup(token)) return Forbid();
        if (!string.Equals(request.TableId, table, StringComparison.OrdinalIgnoreCase))
            return BadRequest(ApiProblem.Create(StatusCodes.Status400BadRequest, "TABLE_MISMATCH", "路径表名与请求体表名不一致。"));
        return Ok(await repository.CreateUnmanagedFieldsAsync(request, userContext.EmployeeName, token));
    }

    /// <summary>幽灵字段清单（元数据存在、物理列已不存在；虚拟字段不属幽灵字段，不在清单内）。</summary>
    [HttpGet("tables/{table}/fields/ghosts")]
    public async Task<IActionResult> GhostFields(string table, CancellationToken token)
    {
        if (!await CanBrowse(token)) return Forbid();
        return Ok(await repository.GetGhostFieldsAsync(table, token));
    }

    /// <summary>清理幽灵字段（删除 FIELDS 元数据并清理其历史列配置；逐条按当前库状态复核）。</summary>
    [HttpPost("tables/{table}/fields/ghosts/cleanup")]
    public async Task<IActionResult> CleanupGhostFields(string table, CleanupGhostFieldsRequest request, CancellationToken token)
    {
        if (!await CanSetup(token)) return Forbid();
        if (!string.Equals(request.TableId, table, StringComparison.OrdinalIgnoreCase))
            return BadRequest(ApiProblem.Create(StatusCodes.Status400BadRequest, "TABLE_MISMATCH", "路径表名与请求体表名不一致。"));
        return Ok(await repository.CleanupGhostFieldsAsync(request, userContext.EmployeeName, token));
    }

    [HttpGet("fields/{table}/{field}")]
    public async Task<IActionResult> Field(string table, string field, CancellationToken token)
    {
        if (!await CanBrowse(token)) return Forbid();
        var metadata = await repository.GetMetadataAsync(table, field, token);
        return metadata is null ? NotFound() : Ok(metadata);
    }

    /// <summary>表列（物理列 + 来源表内受控虚拟列，含类型与 isVirtual；字段设置数据来源/回填构建器下拉用，权限门 CanBrowse 2302）。</summary>
    [HttpGet("tables/{table}/columns")]
    public async Task<IActionResult> TableColumns(string table, CancellationToken token)
    {
        if (!await CanBrowse(token)) return Forbid();
        return Ok(await repository.GetTableColumnsAsync(table, token));
    }

    /// <summary>字段变更历史（AUDIT_EVENT/FIELD_CHANGE，只读；权限门 CanBrowse 2302）。</summary>
    [HttpGet("fields/{table}/{field}/history")]
    public async Task<IActionResult> FieldHistory(string table, string field, CancellationToken token)
    {
        if (!await CanBrowse(token)) return Forbid();
        return Ok(await repository.GetFieldHistoryAsync(table, field.Trim(), token));
    }

    [HttpPost("fields")]
    public async Task<IActionResult> Create(CreateFieldAdminRequest request, CancellationToken token)
    {
        if (!await CanSetup(token)) return Forbid();
        await repository.CreateAsync(request, userContext.EmployeeName, token);
        return NoContent();
    }

    [HttpPut("fields/{table}/{field}")]
    public async Task<IActionResult> Update(string table, string field, UpdateFieldAdminRequest request, CancellationToken token)
    {
        if (!await CanSetup(token)) return Forbid();
        await repository.UpdateAsync(table, field, request.Field, request.Original, userContext.EmployeeName, token);
        return NoContent();
    }

    /// <summary>受控表达式结构回读：字段设置构建器初始化（与校验同源解析器；不触库、不执行表达式）。</summary>
    [HttpPost("fields/expressions/parse")]
    public async Task<IActionResult> ParseExpression(ParseExpressionRequest request, CancellationToken token)
    {
        if (!await CanBrowse(token)) return Forbid();
        if (!TryParseKind(request.Kind, out var kind))
            return BadRequest(ApiProblem.Create(StatusCodes.Status400BadRequest, "INVALID_EXPRESSION_KIND", "kind 仅支持 virtual_exp / convert_function。"));
        return Ok(RestrictedExpressionService.ParseStructure(kind, request.Expression));
    }

    /// <summary>受控表达式注册表（白名单版本 + 转换函数名）：构建器下拉的唯一来源。</summary>
    [HttpGet("fields/expressions/registry")]
    public async Task<IActionResult> ExpressionRegistry(CancellationToken token)
    {
        if (!await CanBrowse(token)) return Forbid();
        return Ok(await expressionService.GetRegistryAsync(token));
    }

    /// <summary>表关联白名单（TABLES.QUERY_RELATION）：虚拟表达式构建器的跨表引用候选（只读元数据）。</summary>
    [HttpGet("tables/{table}/relations")]
    public async Task<IActionResult> TableRelations(string table, CancellationToken token)
    {
        if (!await CanBrowse(token)) return Forbid();
        return Ok(await repository.GetTableRelationsAsync(table, token));
    }

    /// <summary>对全部已发布表达式按当前白名单版本重校验（P3：版本升级后标记需复核项）。</summary>
    [HttpPost("fields/expressions/rescan")]
    public async Task<IActionResult> RescanExpressions(CancellationToken token)
    {
        if (!await CanSetup(token)) return Forbid();
        return Ok(await expressionService.RescanAsync(token));
    }

    /// <summary>表达式审计总览（2302）：白名单版本 + 分类/可见性计数 + 重校验残留清单。</summary>
    [HttpGet("fields/expressions/overview")]
    public async Task<IActionResult> ExpressionOverview(CancellationToken token)
    {
        if (!await CanBrowse(token)) return Forbid();
        return Ok(await expressionService.OverviewAsync(token));
    }

    private static bool TryParseKind(string kind, out RestrictedExpressionKind parsed)
    {
        parsed = kind.Trim().ToLowerInvariant() switch
        {
            "virtual_exp" => RestrictedExpressionKind.VirtualExp,
            "convert_function" => RestrictedExpressionKind.ConvertFunction,
            _ => (RestrictedExpressionKind)(-1),
        };
        return parsed != (RestrictedExpressionKind)(-1);
    }

    [HttpDelete("fields/{table}/{field}")]
    public async Task<IActionResult> Delete(string table, string field, CancellationToken token)
    {
        if (!await CanSetup(token)) return Forbid();
        await repository.DeleteAsync(table, field, userContext.EmployeeName, token);
        return NoContent();
    }

    private async Task<bool> CanBrowse(CancellationToken token) =>
        (await rightsRepository.GetAsync(userContext.UserId, AdminModuleId, token)).CanBrowse;

    private async Task<bool> CanSetup(CancellationToken token) =>
        (await rightsRepository.GetAsync(userContext.UserId, AdminModuleId, token)).CanSetup;
}
