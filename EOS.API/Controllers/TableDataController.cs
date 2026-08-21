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
            WHERE EXISTS (SELECT 1 FROM sys.tables st JOIN sys.schemas ss ON st.schema_id=ss.schema_id
                          WHERE ss.name=N'dbo' AND st.name=t.T_ID)
              AND EXISTS (SELECT 1 FROM sys.indexes si
                          JOIN sys.tables st2 ON si.object_id=st2.object_id
                          JOIN sys.schemas ss2 ON st2.schema_id=ss2.schema_id
                          WHERE ss2.name=N'dbo' AND st2.name=t.T_ID AND si.is_primary_key=1)
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
            SELECT c.name AS COLUMN_NAME
            FROM sys.indexes i
            JOIN sys.index_columns ic ON i.object_id = ic.object_id AND i.index_id = ic.index_id
            JOIN sys.columns c ON ic.object_id = c.object_id AND ic.column_id = c.column_id
            JOIN sys.tables t ON i.object_id = t.object_id
            JOIN sys.schemas s ON t.schema_id = s.schema_id
            WHERE s.name = N'dbo' AND t.name = @Table AND i.is_primary_key = 1
            ORDER BY ic.key_ordinal;
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
