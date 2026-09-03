using EOS.API.Data;
using EOS.API.Errors;
using EOS.API.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;

namespace EOS.API.Controllers;

[ApiController]
[Authorize]
[Route("api/v1/table-data")]
public sealed class TableDataController(
    DbConnectionFactory connections,
    LegacyRightsRepository rightsRepository,
    CurrentUserContext userContext) : ControllerBase
{
    private const int FieldAuditModuleId = 2303;

    /// <summary>
    /// 字段元数据审计（2303 读写数据表信息受控只读版）：物理列与 FIELDS 元数据的差集。
    /// kind=unmanaged：物理表存在但 FIELDS 无元数据的列（旧页面"未受管理字段"）；
    /// kind=orphan：FIELDS 有元数据但物理表不存在的列（旧页面"未知的管理字段"）。
    /// 只读，不开放写；权限门：模块 2303 可浏览。
    /// 说明：原「数据表数据维护」（2310/2312 受控只读版 tables/data 端点）已随
    /// 模块下线移除（EOS-23，2026-08-28 用户拍板）——该页是全系统唯一绕过
    /// 成本/保密/禁止字段过滤与数据范围的原始数据窗口；表结构巡检由 2302/2303
    /// 承担，业务数据查看走各模块工作台，清库属 DBA 操作。
    /// </summary>
    [HttpGet("field-audit/{kind}")]
    public async Task<IActionResult> FieldAudit(string kind, CancellationToken token)
    {
        if(!await CanBrowseFieldAuditAsync(token)) return Forbid();
        var normalized=kind.Trim().ToLowerInvariant();
        if(normalized is not ("unmanaged" or "orphan")) return BadRequest(ApiProblem.Create(StatusCodes.Status400BadRequest,"INVALID_AUDIT_KIND","kind 仅支持 unmanaged 或 orphan。"));
        await using var connection=connections.Create();
        await connection.OpenAsync(token);
        const int limit=2000;
        string sql;
        if(normalized=="unmanaged")
        {
            sql=$$"""
                SELECT o.name AS T_ID, c.name AS F_ID, TYPE_NAME(c.user_type_id) AS F_TYPE
                FROM sys.columns c
                JOIN sys.objects o ON c.object_id=o.object_id AND o.type IN ('U','V')
                JOIN sys.schemas s ON o.schema_id=s.schema_id
                WHERE s.name=N'dbo'
                  AND NOT EXISTS (SELECT 1 FROM dbo.FIELDS f WHERE f.T_ID=o.name AND f.F_ID=c.name)
                ORDER BY o.name,c.column_id
                OFFSET 0 ROWS FETCH NEXT {{limit}} ROWS ONLY;
                SELECT COUNT_BIG(1)
                FROM sys.columns c
                JOIN sys.objects o ON c.object_id=o.object_id AND o.type IN ('U','V')
                JOIN sys.schemas s ON o.schema_id=s.schema_id
                WHERE s.name=N'dbo'
                  AND NOT EXISTS (SELECT 1 FROM dbo.FIELDS f WHERE f.T_ID=o.name AND f.F_ID=c.name);
                """;
        }
        else
        {
            sql=$$"""
                SELECT TOP ({{limit}}) LTRIM(RTRIM(f.T_ID)) AS T_ID,LTRIM(RTRIM(f.F_ID)) AS F_ID,
                       LTRIM(RTRIM(ISNULL(f.F_TYPE,''))) AS F_TYPE,LTRIM(RTRIM(ISNULL(f.F_DESC,''))) AS F_DESC,
                       CAST(ISNULL(f.IS_VIRTUAL,0) AS bit) AS IS_VIRTUAL
                FROM dbo.FIELDS f WITH (NOLOCK)
                WHERE NOT EXISTS (SELECT 1 FROM sys.columns c
                                  JOIN sys.objects o ON c.object_id=o.object_id AND o.type IN ('U','V')
                                  JOIN sys.schemas s ON o.schema_id=s.schema_id
                                  WHERE s.name=N'dbo' AND o.name=f.T_ID AND c.name=f.F_ID)
                ORDER BY f.T_ID,f.F_ID;
                SELECT COUNT_BIG(1) FROM dbo.FIELDS f WITH (NOLOCK)
                WHERE NOT EXISTS (SELECT 1 FROM sys.columns c
                                  JOIN sys.objects o ON c.object_id=o.object_id AND o.type IN ('U','V')
                                  JOIN sys.schemas s ON o.schema_id=s.schema_id
                                  WHERE s.name=N'dbo' AND o.name=f.T_ID AND c.name=f.F_ID);
                """;
        }
        await using var command=new SqlCommand(sql,connection);
        await using var reader=await command.ExecuteReaderAsync(token);
        var rows=new List<Dictionary<string,object?>>();
        while(await reader.ReadAsync(token))
        {
            var row=new Dictionary<string,object?>(StringComparer.OrdinalIgnoreCase);
            for(var i=0;i<reader.FieldCount;i++) row[reader.GetName(i)]=reader.IsDBNull(i)?null:reader.GetValue(i);
            rows.Add(row);
        }
        await reader.NextResultAsync(token);
        await reader.ReadAsync(token);
        var total=Convert.ToInt64(reader.GetInt64(0));
        return Ok(new{Kind=normalized,Rows=rows,Total=total,Limited=total>limit});
    }

    private async Task<bool> CanBrowseFieldAuditAsync(CancellationToken token) =>
        (await rightsRepository.GetAsync(userContext.UserId,FieldAuditModuleId,token)).CanBrowse;
}
