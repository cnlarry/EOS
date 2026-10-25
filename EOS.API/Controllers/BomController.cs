using System.Security.Claims;
using System.Text.RegularExpressions;
using EOS.API.Data;
using EOS.API.Data.Bom;
using EOS.API.Errors;
using EOS.API.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace EOS.API.Controllers;

/// <summary>
/// 产品 BOM 的结构查询（模块 1204）：多层展开、毛需求、反查用途。
///
/// 这三个出口是 1204 的<b>视图</b>，不是独立模块（ADR-039 D10）——
/// 所以权限门只取 1204 的浏览权，不另立模块号。
///
/// "找不到"与"查到了但是空"是两件事，这里分开表达：
/// 料号不存在、或该料号没有该版次的 BOM 都返回 404 并给出原因；有 BOM 但明细为空才返回空数组。
/// 把两者混成一个空数组，用户会以为"BOM 是空的"，而这通常是版次选错或数据问题。
/// </summary>
[ApiController]
[Authorize]
[Route("api/v1/bom")]
public sealed class BomController(
    BomRepository repository,
    IPermissionService permissions,
    WorkbenchAuditWriter auditWriter,
    CurrentUserContext userContext) : ControllerBase
{
    private static readonly Regex EditionPattern = new(@"^\d{2}$", RegexOptions.Compiled);

    private string UserId =>
        User.FindFirstValue(ClaimTypes.NameIdentifier) ?? throw new UnauthorizedAccessException();

    /// <summary>多层展开（结构树）。每行带"这一层用了哪一版、是不是回退来的"。</summary>
    [HttpGet("expand")]
    public async Task<IActionResult> Expand(
        [FromQuery] string? proNo,
        [FromQuery] string? edition,
        [FromQuery] int maxDepth = BomRepository.DefaultTreeDepth,
        CancellationToken token = default)
    {
        await permissions.RequireAsync(UserId, ModuleIds.BomStructure, PermissionAction.Browse, token);

        var (no, error) = ValidateProNo(proNo);
        if (error is not null) return error;
        var editionError = ValidateEdition(edition);
        if (editionError is not null) return editionError;

        var resolved = await ResolveRootAsync(no, edition, token);
        if (resolved.Result is not null) return resolved.Result;

        var depth = BomRepository.ClampDepth(maxDepth);
        var rows = await repository.ExpandAsync(no, resolved.Edition!, depth, token);
        // 根的品名单独补：它不在任何明细行里，树却要把根画成与子件同一形状
        var proName = await repository.ProductNameAsync(no, token);
        // 最新版次：用户可能在看过期版而不自知（列表默认取最大版次，深链可以直接给 01）
        var latestEdition = await repository.ResolveEditionAsync(no, null, token) ?? resolved.Edition!;
        // 版次列表：树页顶部的切换控件用它（同一产品就地切，换产品仍回列表）
        var editions = await repository.EditionsAsync(no, token);
        return Ok(new BomExpandResponse(no, proName, resolved.Edition!, latestEdition, editions, depth, rows));
    }

    /// <summary>毛需求（叶子按料号合并，损耗沿路径累乘后乘以制令数量）。</summary>
    [HttpGet("gross-requirement")]
    public async Task<IActionResult> GrossRequirement(
        [FromQuery] string? proNo,
        [FromQuery] string? edition,
        [FromQuery] decimal qty = 1m,
        [FromQuery] int maxDepth = BomRepository.DefaultTreeDepth,
        CancellationToken token = default)
    {
        await permissions.RequireAsync(UserId, ModuleIds.BomStructure, PermissionAction.Browse, token);

        var (no, error) = ValidateProNo(proNo);
        if (error is not null) return error;
        var editionError = ValidateEdition(edition);
        if (editionError is not null) return editionError;
        if (qty <= 0)
            return BadRequest(new { code = "INVALID_QTY", message = "制令数量必须大于 0。" });

        var resolved = await ResolveRootAsync(no, edition, token);
        if (resolved.Result is not null) return resolved.Result;

        var depth = BomRepository.ClampDepth(maxDepth);
        var rows = await repository.GrossRequirementAsync(no, resolved.Edition!, depth, token);
        return Ok(new BomGrossResponse(no, resolved.Edition!, qty, depth,
            rows.Select(row => row with
            {
                QtyPerUnitWithLoss = row.QtyPerUnitWithLoss * qty,
                LostQtyPerUnit = row.LostQtyPerUnit * qty,
            }).ToList()));
    }

    /// <summary>反查用途：给定料号，向上找用它的父件。</summary>
    [HttpGet("where-used")]
    public async Task<IActionResult> WhereUsed(
        [FromQuery] string? proNo,
        [FromQuery] string? edition,
        [FromQuery] int maxDepth = BomRepository.DefaultWhereUsedDepth,
        CancellationToken token = default)
    {
        await permissions.RequireAsync(UserId, ModuleIds.BomStructure, PermissionAction.Browse, token);

        var (no, error) = ValidateProNo(proNo);
        if (error is not null) return error;
        var editionError = ValidateEdition(edition);
        if (editionError is not null) return editionError;

        // 反查的"版次"是父件的选版口径：不给时各父件取自身最大版次，
        // 故传空串让 EditionPick 里的"同名版次优先"恒不命中，落到"取最大版次"分支。
        var rootEdition = string.IsNullOrWhiteSpace(edition) ? string.Empty : edition.Trim();

        if (!await repository.ProductExistsAsync(no, token))
            return NotFound(new { code = "PRODUCT_NOT_FOUND", message = $"料号 {no} 不存在。" });

        var depth = BomRepository.ClampDepth(maxDepth);
        var rows = await repository.WhereUsedAsync(no, rootEdition, depth, token);
        return Ok(new BomWhereUsedResponse(no, depth, rows));
    }

    /// <summary>
    /// 新建版本：把当前版次的整棵结构复制到下一个版次。返回实际生成的版次。
    ///
    /// 这是 1204 定制页的**标配能力**，不是可配置按钮——判定就是"对这个模块有没有新增权"，
    /// 与"保存"同级。把它做成 `MANUAL` 动作会凭空多出两处管理负担（配按钮行 + 单独授权 + 发布），
    /// 而它的授权语义并不需要按钮级粒度。
    ///
    /// 版次由服务端算：客户端只提交"复制哪一版"，不提交"复制成哪一版"——版次是主键。
    /// </summary>
    [HttpPost("new-version")]
    public async Task<IActionResult> NewVersion(
        [FromBody] BomNewVersionRequest? request, CancellationToken token = default)
    {
        await permissions.RequireAsync(UserId, ModuleIds.BomStructure, PermissionAction.AddNew, token);

        var (no, error) = ValidateProNo(request?.ProNo);
        if (error is not null) return error;
        var edition = (request?.Edition ?? string.Empty).Trim();
        if (!EditionPattern.IsMatch(edition))
            return BadRequest(new { code = "INVALID_EDITION", message = "版次必须是两位数字（如 01）。" });
        if (!await repository.ProductExistsAsync(no, token))
            return NotFound(new { code = "PRODUCT_NOT_FOUND", message = $"料号 {no} 不存在。" });

        var next = await repository.CreateNewVersionAsync(no, edition, userContext.EmployeeName, token);

        await auditWriter.WriteBestEffortAsync(
            ModuleIds.BomStructure, "bom", "BOM_NEW_VERSION",
            $"料号 {no} 由版次 {edition} 新建版本 {next}",
            userContext.EmployeeName, "BOM_STRU_M", result: 1, fieldChanges: null, token);

        return Ok(new { proNo = no, edition = next });
    }

    /// <summary>
    /// 版本对比：按子件对齐两个版次，标出新增 / 删除 / 改动 / 相同。
    ///
    /// 比的是原始列而不是折算后的单件用量——用户改的就是 `ELEMENT_QTY` / `BASE_QTY` /
    /// `LOST_RATE` / `REMARK`，拿折算值比会把"改了底数"显示成"改了用量"。
    /// </summary>
    [HttpGet("compare")]
    public async Task<IActionResult> Compare(
        string proNo, string editionA, string editionB, CancellationToken token = default)
    {
        await permissions.RequireAsync(UserId, ModuleIds.BomStructure, PermissionAction.Browse, token);

        var (no, error) = ValidateProNo(proNo);
        if (error is not null) return error;
        if (string.IsNullOrWhiteSpace(editionA) || string.IsNullOrWhiteSpace(editionB))
            return BadRequest(new { code = "EDITION_REQUIRED", message = "对比需要指定两个版次。" });
        var errorA = ValidateEdition(editionA);
        if (errorA is not null) return errorA;
        var errorB = ValidateEdition(editionB);
        if (errorB is not null) return errorB;
        if (string.Equals(editionA.Trim(), editionB.Trim(), StringComparison.Ordinal))
            return BadRequest(new { code = "SAME_EDITION", message = "两个版次相同，没有可对比的差异。" });
        if (!await repository.ProductExistsAsync(no, token))
            return NotFound(new { code = "PRODUCT_NOT_FOUND", message = $"料号 {no} 不存在。" });

        var result = await repository.CompareAsync(no, editionA.Trim(), editionB.Trim(), token);
        if (result.Rows.Count == 0)
            return NotFound(new { code = "BOM_NOT_FOUND", message = $"料号 {no} 在这两个版次下都没有明细。" });
        return Ok(result);
    }

    /// <summary>
    /// 取试算表：把外部算好的明细行导入当前版次（`replace` 为真时整体替换）。
    ///
    /// 走 `Edit` 权限而不是 `AddNew`：这是改一张已有 BOM 的明细，不是新建记录。
    /// 项次由服务端接续分配——外部给号会与下游"单号 + 项次"的引用撞车。
    /// </summary>
    [HttpPost("import-lines")]
    public async Task<IActionResult> ImportLines(
        [FromBody] BomImportLinesRequest? request, CancellationToken token = default)
    {
        await permissions.RequireAsync(UserId, ModuleIds.BomStructure, PermissionAction.Edit, token);

        var (no, error) = ValidateProNo(request?.ProNo);
        if (error is not null) return error;
        var editionError = ValidateEdition(request?.Edition);
        if (editionError is not null) return editionError;
        if (string.IsNullOrWhiteSpace(request?.Edition))
            return BadRequest(new { code = "EDITION_REQUIRED", message = "导入需要指定版次。" });

        var rows = request!.Rows ?? [];
        if (rows.Count == 0)
            return BadRequest(new { code = "IMPORT_NO_ROWS", message = "没有可导入的数据行。" });
        for (var index = 0; index < rows.Count; index++)
        {
            if (string.IsNullOrWhiteSpace(rows[index].ElementProNo))
                return BadRequest(new { code = "IMPORT_BLANK_ELEMENT", message = $"第 {index + 1} 行缺少子件料号。" });
        }

        var lines = rows
            .Select(row => new BomImportLine(
                row.ElementProNo.Trim(),
                string.IsNullOrWhiteSpace(row.ElementEdition) ? null : row.ElementEdition.Trim(),
                row.Qty, row.BaseQty == 0 ? 1m : row.BaseQty, row.LostRate,
                string.IsNullOrWhiteSpace(row.Remark) ? null : row.Remark.Trim()))
            .ToList();

        int imported;
        try
        {
            imported = await repository.ImportLinesAsync(
                no, request.Edition.Trim(), request.Replace, lines, userContext.EmployeeName, token);
        }
        catch (InvalidOperationException ex)
        {
            // 业务拒绝（版本不存在、版次不是两位数字等）走统一错误契约；
            // 此前这里返回匿名对象 { code, message }，与 problem+json 不是同一个形状。
            return BadRequest(ApiProblem.Create(StatusCodes.Status400BadRequest, "IMPORT_REJECTED", ex.Message));
        }

        await auditWriter.WriteBestEffortAsync(
            ModuleIds.BomStructure, "bom", "BOM_IMPORT_LINES",
            $"料号 {no} 版次 {request.Edition.Trim()} 导入明细 {imported} 行"
                + (request.Replace ? "（整体替换）" : "（追加）"),
            userContext.EmployeeName, "BOM_STRU_D",
            result: (byte)Math.Min(imported, byte.MaxValue), fieldChanges: null, token);

        return Ok(new { imported, replaced = request.Replace, proNo = no, edition = request.Edition.Trim() });
    }

    /// <summary>解析根节点：先判料号存在，再判该料号有没有 BOM（含调用方指定的版次）。</summary>
    private async Task<(IActionResult? Result, string? Edition)> ResolveRootAsync(
        string proNo, string? edition, CancellationToken token)
    {
        if (!await repository.ProductExistsAsync(proNo, token))
        {
            return (NotFound(new { code = "PRODUCT_NOT_FOUND", message = $"料号 {proNo} 不存在。" }), null);
        }
        var resolved = await repository.ResolveEditionAsync(proNo, edition, token);
        if (resolved is null)
        {
            var reason = string.IsNullOrWhiteSpace(edition)
                ? $"料号 {proNo} 没有维护 BOM。"
                : $"料号 {proNo} 没有版次 {edition.Trim()} 的 BOM。";
            return (NotFound(new { code = "BOM_NOT_FOUND", message = reason }), null);
        }
        return (null, resolved);
    }

    private static (string ProNo, IActionResult? Error) ValidateProNo(string? proNo)
    {
        if (string.IsNullOrWhiteSpace(proNo))
            return (string.Empty, new BadRequestObjectResult(new { code = "PRO_NO_REQUIRED", message = "必须指定料号。" }));
        var trimmed = proNo.Trim();
        if (trimmed.Length > 30)
            return (string.Empty, new BadRequestObjectResult(new { code = "PRO_NO_TOO_LONG", message = "料号长度不能超过 30。" }));
        return (trimmed, null);
    }

    /// <summary>版次格式：两位数字（与 FIELDS.REGEX 同口径）。空表示"取最大版次"，返回 null。</summary>
    private static IActionResult? ValidateEdition(string? edition)
    {
        if (string.IsNullOrWhiteSpace(edition)) return null;
        return EditionPattern.IsMatch(edition.Trim())
            ? null
            : new BadRequestObjectResult(new { code = "INVALID_EDITION", message = "版次必须是两位数字（如 01）。" });
    }
}

public sealed record BomExpandResponse(
    string ProNo, string ProName, string Edition, string LatestEdition,
    IReadOnlyList<string> Editions, int MaxDepth, IReadOnlyList<BomTreeRow> Rows);

public sealed record BomGrossResponse(
    string ProNo, string Edition, decimal Qty, int MaxDepth, IReadOnlyList<BomGrossRow> Rows);

public sealed record BomWhereUsedResponse(
    string ProNo, int MaxDepth, IReadOnlyList<BomWhereUsedRow> Rows);

/// <summary>新建版本的请求：只指定"复制哪一版"，新版次由服务端算。</summary>
public sealed record BomNewVersionRequest(string ProNo, string Edition);

/// <summary>
/// 导入明细的一行。项次不在请求里——它由服务端接续分配。
/// <c>ElementEdition</c> 为子件自己的 BOM 版次：半成品必填、材料留空，判据在仓储层。
/// </summary>
public sealed record BomImportLineRequest(
    string ElementProNo, string? ElementEdition,
    decimal Qty, decimal BaseQty, decimal LostRate, string? Remark);

/// <summary>取试算表：<c>Replace</c> 为真时先清空该版次明细再插入。</summary>
public sealed record BomImportLinesRequest(
    string ProNo, string Edition, bool Replace, IReadOnlyList<BomImportLineRequest>? Rows);
