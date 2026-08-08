using System.Data;
using System.Text.RegularExpressions;
using EOS.API.Data;
using EOS.API.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;

namespace EOS.API.Controllers;

[ApiController]
[Authorize]
[Route("api/table-data")]
public sealed class TableDataController(
    DbConnectionFactory connections,
    LegacyRightsRepository rightsRepository,
    CurrentUserContext userContext) : ControllerBase
{
    private static readonly Regex Identifier = new("^[A-Za-z_][A-Za-z0-9_]{0,127}$", RegexOptions.Compiled);
    private static readonly int[] AdminModules = [2310, 2312];
    private const int FieldAuditModuleId = 2303;

    /// <summary>
    /// 数据表数据维护（2310/2312 受控只读版）：TABLES 白名单中有主键的表，
    /// 提供分页查看（运维用途）；写操作经各模块统一表单/领域服务，不在此开放。
    /// </summary>
    [HttpGet("tables")]
    public async Task<IActionResult> Tables(CancellationToken token)
    {
        if(!await CanBrowseAsync(token))return Forbid();
        await using var connection=connections.Create();
        await connection.OpenAsync(token);
        const string sql="""
            SELECT LTRIM(RTRIM(t.T_ID)),COALESCE(NULLIF(LTRIM(RTRIM(t.T_DESC)),''),LTRIM(RTRIM(t.T_ID)))
            FROM dbo.TABLES t WITH (NOLOCK)
            WHERE EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.TABLES it WHERE it.TABLE_SCHEMA='dbo' AND it.TABLE_NAME=t.T_ID)
              AND EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS tc
                          INNER JOIN INFORMATION_SCHEMA.KEY_COLUMN_USAGE ku ON ku.CONSTRAINT_NAME=tc.CONSTRAINT_NAME
                          WHERE tc.CONSTRAINT_TYPE='PRIMARY KEY' AND tc.TABLE_SCHEMA='dbo' AND tc.TABLE_NAME=t.T_ID)
            ORDER BY t.T_DESC,t.T_ID;
            """;
        await using var command=new SqlCommand(sql,connection);
        await using var reader=await command.ExecuteReaderAsync(token);
        var result=new List<object>();
        while(await reader.ReadAsync(token))
            result.Add(new{Table=reader.GetString(0),Desc=reader.GetString(1)});
        return Ok(result);
    }

    /// <summary>
    /// 字段元数据审计（2303 读写数据表信息受控只读版）：物理列与 FIELDS 元数据的差集。
    /// kind=unmanaged：物理表存在但 FIELDS 无元数据的列（旧页面"未受管理字段"）；
    /// kind=orphan：FIELDS 有元数据但物理表不存在的列（旧页面"未知的管理字段"）。
    /// 只读，不开放写；权限门：模块 2303 可浏览。
    /// </summary>
    [HttpGet("field-audit/{kind}")]
    public async Task<IActionResult> FieldAudit(string kind, CancellationToken token)
    {
        if(!await CanBrowseFieldAuditAsync(token)) return Forbid();
        var normalized=kind.Trim().ToLowerInvariant();
        if(normalized is not ("unmanaged" or "orphan")) return BadRequest(new{code="INVALID_AUDIT_KIND",message="kind 仅支持 unmanaged 或 orphan。"});
        await using var connection=connections.Create();
        await connection.OpenAsync(token);
        const int limit=2000;
        string sql;
        if(normalized=="unmanaged")
        {
            sql=$$"""
                SELECT c.TABLE_NAME AS T_ID,c.COLUMN_NAME AS F_ID,c.DATA_TYPE AS F_TYPE
                FROM INFORMATION_SCHEMA.COLUMNS c
                WHERE c.TABLE_SCHEMA='dbo'
                  AND NOT EXISTS (SELECT 1 FROM dbo.FIELDS f WHERE f.T_ID=c.TABLE_NAME AND f.F_ID=c.COLUMN_NAME)
                ORDER BY c.TABLE_NAME,c.ORDINAL_POSITION
                OFFSET 0 ROWS FETCH NEXT {{limit}} ROWS ONLY;
                SELECT COUNT_BIG(1) FROM INFORMATION_SCHEMA.COLUMNS c
                WHERE c.TABLE_SCHEMA='dbo'
                  AND NOT EXISTS (SELECT 1 FROM dbo.FIELDS f WHERE f.T_ID=c.TABLE_NAME AND f.F_ID=c.COLUMN_NAME);
                """;
        }
        else
        {
            sql=$$"""
                SELECT TOP ({{limit}}) LTRIM(RTRIM(f.T_ID)) AS T_ID,LTRIM(RTRIM(f.F_ID)) AS F_ID,
                       LTRIM(RTRIM(ISNULL(f.F_TYPE,''))) AS F_TYPE,LTRIM(RTRIM(ISNULL(f.F_DESC,''))) AS F_DESC,
                       CAST(ISNULL(f.IS_VIRTUAL,0) AS bit) AS IS_VIRTUAL
                FROM dbo.FIELDS f WITH (NOLOCK)
                WHERE NOT EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS c
                                  WHERE c.TABLE_SCHEMA='dbo' AND c.TABLE_NAME=f.T_ID AND c.COLUMN_NAME=f.F_ID)
                ORDER BY f.T_ID,f.F_ID;
                SELECT COUNT_BIG(1) FROM dbo.FIELDS f WITH (NOLOCK)
                WHERE NOT EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS c
                                  WHERE c.TABLE_SCHEMA='dbo' AND c.TABLE_NAME=f.T_ID AND c.COLUMN_NAME=f.F_ID);
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

    [HttpGet("{table}")]
    public async Task<IActionResult> Data(string table,[FromQuery]int page,[FromQuery]int pageSize,CancellationToken token)
    {
        if(!await CanBrowseAsync(token))return Forbid();
        if(!Identifier.IsMatch(table))return BadRequest(new{code="INVALID_TABLE",message="表名不合法。"});
        page=Math.Max(1,page);
        pageSize=Math.Clamp(pageSize,10,200);
        await using var connection=connections.Create();
        await connection.OpenAsync(token);
        if(!await IsWhitelistedAsync(connection,table,token))
            return BadRequest(new{code="TABLE_NOT_WHITELISTED",message="该表不在 TABLES 白名单中，无法查看。"});
        var pkOrder=await GetPrimaryKeyColumnsAsync(connection,table,token);
        if(pkOrder.Count==0)return BadRequest(new{code="NO_PRIMARY_KEY",message="该表没有主键，无法按行查看。"});
        var columns=await GetColumnNamesAsync(connection,table,token);
        if(columns.Count==0)return NotFound();
        var selected=columns.Take(30).Select(column=>$"[{column}]").ToList();
        var order=string.Join(',',pkOrder.Select(pk=>$"[{pk}]"));
        await using var command=new SqlCommand(
            $"SELECT COUNT_BIG(1) FROM dbo.[{table}] WITH (NOLOCK); SELECT {string.Join(',',selected)} FROM dbo.[{table}] WITH (NOLOCK) ORDER BY {order} OFFSET @Offset ROWS FETCH NEXT @PageSize ROWS ONLY;",connection);
        command.Parameters.Add("@Offset",SqlDbType.Int).Value=(page-1)*pageSize;
        command.Parameters.Add("@PageSize",SqlDbType.Int).Value=pageSize;
        await using var reader=await command.ExecuteReaderAsync(token);
        await reader.ReadAsync(token);
        var total=Convert.ToInt32(reader.GetInt64(0));
        await reader.NextResultAsync(token);
        var rows=new List<Dictionary<string,object?>>();
        while(await reader.ReadAsync(token))
        {
            var row=new Dictionary<string,object?>(StringComparer.OrdinalIgnoreCase);
            for(var i=0;i<reader.FieldCount;i++)row[reader.GetName(i)]=reader.IsDBNull(i)?null:reader.GetValue(i);
            rows.Add(row);
        }
        return Ok(new{Table=table,PrimaryKeys=pkOrder,Columns=columns.Take(30).ToList(),Rows=rows,Total=total,Page=page,PageSize=pageSize});
    }

    private async Task<bool> CanBrowseAsync(CancellationToken token)
    {
        foreach(var moduleId in AdminModules)
            if((await rightsRepository.GetAsync(userContext.UserId,moduleId,token)).CanBrowse)
                return true;
        return false;
    }

    private async Task<bool> CanBrowseFieldAuditAsync(CancellationToken token) =>
        (await rightsRepository.GetAsync(userContext.UserId,FieldAuditModuleId,token)).CanBrowse;

    private static async Task<bool> IsWhitelistedAsync(SqlConnection connection,string table,CancellationToken token)
    {
        const string sql="SELECT TOP 1 1 FROM dbo.TABLES WITH (NOLOCK) WHERE LTRIM(RTRIM(T_ID))=@Table;";
        await using var command=new SqlCommand(sql,connection);
        command.Parameters.Add("@Table",SqlDbType.NVarChar,100).Value=table;
        return await command.ExecuteScalarAsync(token) is not null;
    }

    private static async Task<IReadOnlyList<string>> GetPrimaryKeyColumnsAsync(SqlConnection connection,string table,CancellationToken token)
    {
        const string sql="""
            SELECT ku.COLUMN_NAME FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS tc
            INNER JOIN INFORMATION_SCHEMA.KEY_COLUMN_USAGE ku
              ON ku.CONSTRAINT_NAME=tc.CONSTRAINT_NAME AND ku.CONSTRAINT_SCHEMA=tc.CONSTRAINT_SCHEMA
            WHERE tc.CONSTRAINT_TYPE='PRIMARY KEY' AND tc.TABLE_SCHEMA='dbo' AND tc.TABLE_NAME=@Table
            ORDER BY ku.ORDINAL_POSITION;
            """;
        await using var command=new SqlCommand(sql,connection);
        command.Parameters.Add("@Table",SqlDbType.NVarChar,100).Value=table;
        await using var reader=await command.ExecuteReaderAsync(token);
        var result=new List<string>();
        while(await reader.ReadAsync(token))result.Add(reader.GetString(0));
        return result;
    }

    private static async Task<IReadOnlyList<string>> GetColumnNamesAsync(SqlConnection connection,string table,CancellationToken token)
    {
        const string sql="""
            SELECT COLUMN_NAME FROM INFORMATION_SCHEMA.COLUMNS
            WHERE TABLE_SCHEMA='dbo' AND TABLE_NAME=@Table
              AND DATA_TYPE NOT IN ('binary','varbinary','image','rowversion','timestamp')
            ORDER BY ORDINAL_POSITION;
            """;
        await using var command=new SqlCommand(sql,connection);
        command.Parameters.Add("@Table",SqlDbType.NVarChar,100).Value=table;
        await using var reader=await command.ExecuteReaderAsync(token);
        var result=new List<string>();
        while(await reader.ReadAsync(token))result.Add(reader.GetString(0));
        return result;
    }
}
