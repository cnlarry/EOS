using System.Data;
using System.Text.RegularExpressions;
using EOS.API.Models;
using Microsoft.Data.SqlClient;

namespace EOS.API.Data;

/// <summary>
/// 通用查询中心（旧 Comm/SearchCenter.aspx 的受控等价）：
/// 覆盖 2501–2508 查询中心模块——按 MODULES.SEARCH_1/SEARCH_2 提供可搜索模块/表，
/// 字段 + 值（或关键字）受控查询，结果元数据网格。
/// 动态标识符全部来自服务端元数据（FIELDS + INFORMATION_SCHEMA），值参数化。
/// </summary>
public sealed class SearchCenterRepository(DbConnectionFactory connections, ILogger<SearchCenterRepository> logger)
{
    private static readonly Regex Identifier = new("^[A-Za-z_][A-Za-z0-9_]{0,127}$", RegexOptions.Compiled);

    public async Task<IReadOnlyList<SearchableModule>> GetModulesAsync(CancellationToken token)
    {
        await using var connection=connections.Create();
        await connection.OpenAsync(token);
        const string sql="""
            SELECT M_IDX,LTRIM(RTRIM(M_DESC)),LTRIM(RTRIM(MASTER_TABLE)),LTRIM(RTRIM(ISNULL(DETAIL_TABLE,''))),
                   COALESCE(SEARCH_1,0),COALESCE(SEARCH_2,0)
            FROM dbo.MODULES WITH (NOLOCK)
            WHERE COALESCE(SEARCH_1,0)=1 OR COALESCE(SEARCH_2,0)=1
            ORDER BY M_IDX;
            """;
        await using var command=new SqlCommand(sql,connection);
        await using var reader=await command.ExecuteReaderAsync(token);
        var result=new List<SearchableModule>();
        while(await reader.ReadAsync(token))
            result.Add(new SearchableModule(reader.GetInt32(0),reader.GetString(1),reader.GetString(2),
                reader.GetString(3).Length>0?reader.GetString(3):null,
                reader.GetInt32(4)==1,reader.GetInt32(5)==1));
        return result;
    }

    public async Task<SearchDefinition?> GetDefinitionAsync(
        int moduleId,
        string table,
        bool canViewCost,
        bool canViewSecrecy,
        IReadOnlySet<string> deniedFields,
        CancellationToken token)
    {
        if(!Identifier.IsMatch(table))return null;
        await using var connection=connections.Create();
        await connection.OpenAsync(token);
        const string moduleSql="""
            SELECT LTRIM(RTRIM(M_DESC)),LTRIM(RTRIM(MASTER_TABLE)),LTRIM(RTRIM(ISNULL(DETAIL_TABLE,''))),
                   COALESCE(SEARCH_1,0),COALESCE(SEARCH_2,0)
            FROM dbo.MODULES WITH (NOLOCK) WHERE M_IDX=@ModuleId;
            """;
        await using var moduleCommand=new SqlCommand(moduleSql,connection);
        moduleCommand.Parameters.Add("@ModuleId",SqlDbType.Int).Value=moduleId;
        await using var reader=await moduleCommand.ExecuteReaderAsync(token);
        if(!await reader.ReadAsync(token))return null;
        var title=reader.GetString(0);
        var master=reader.GetString(1);
        var detail=reader.GetString(2);
        var search1=reader.GetInt32(3)==1;
        var search2=reader.GetInt32(4)==1;
        await reader.DisposeAsync();
        var isMaster=table.Equals(master,StringComparison.OrdinalIgnoreCase);
        var isDetail=detail.Length>0&&table.Equals(detail,StringComparison.OrdinalIgnoreCase);
        if(!isMaster&&!isDetail)return null;
        if(isMaster&&!search1)return null;
        if(isDetail&&!search2)return null;
        var fields=await ReadFieldsAsync(connection,table,canViewCost,canViewSecrecy,deniedFields,token);
        var pkOrder=await GetPrimaryKeyColumnsAsync(connection,table,token);
        return new SearchDefinition(moduleId,title,table,fields,fields,pkOrder);
    }

    public async Task<SearchQueryResult> QueryAsync(
        SearchDefinition definition,
        SearchQueryRequest request,
        int page,
        int pageSize,
        CancellationToken token)
    {
        page=Math.Max(1,page);
        pageSize=Math.Clamp(pageSize,10,200);
        await using var connection=connections.Create();
        await connection.OpenAsync(token);
        var predicates=new List<string>();
        var command=new SqlCommand();
        command.Connection=connection;
        if(!string.IsNullOrWhiteSpace(request.Field)&&!string.IsNullOrWhiteSpace(request.Value))
        {
            var field=definition.Fields.FirstOrDefault(item=>item.Key.Equals(request.Field,StringComparison.OrdinalIgnoreCase));
            if(field is not null)
            {
                var parameter=$"@sc{command.Parameters.Count}";
                predicates.Add($"[{field.Key}] LIKE {parameter}");
                command.Parameters.AddWithValue(parameter,$"%{request.Value.Trim()}%");
            }
        }
        if(!string.IsNullOrWhiteSpace(request.Keyword))
        {
            var textFields=definition.Fields.Where(field=>
                field.DataType.Contains("char",StringComparison.OrdinalIgnoreCase)
                ||field.DataType.Contains("text",StringComparison.OrdinalIgnoreCase)).Take(5).ToList();
            var parts=new List<string>();
            foreach(var field in textFields)
            {
                var parameter=$"@sc{command.Parameters.Count}";
                parts.Add($"[{field.Key}] LIKE {parameter}");
                command.Parameters.AddWithValue(parameter,$"%{request.Keyword.Trim()}%");
            }
            if(parts.Count>0)predicates.Add("("+string.Join(" OR ",parts)+")");
        }
        var where=predicates.Count>0?" WHERE "+string.Join(" AND ",predicates):"";
        var selected=definition.Columns.Take(40).Select(column=>column.Key).ToList();
        if(selected.Count==0)return new([],0,page,pageSize);
        var order=string.Join(',',definition.PkOrder.Select(pk=>$"[{pk}]"));
        command.CommandText=$"""
            SELECT COUNT_BIG(1) FROM dbo.[{definition.Table}] WITH (NOLOCK){where};
            SELECT {string.Join(',',selected.Select(column=>$"[{column}]"))} FROM dbo.[{definition.Table}] WITH (NOLOCK){where}
            ORDER BY {order} OFFSET @Offset ROWS FETCH NEXT @PageSize ROWS ONLY;
            """;
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
        logger.LogDebug("通用查询 module={ModuleId} table={Table} total={Total}",definition.ModuleId,definition.Table,total);
        return new SearchQueryResult(rows,total,page,pageSize);
    }

    private static async Task<IReadOnlyList<SearchField>> ReadFieldsAsync(
        SqlConnection connection,
        string table,
        bool canViewCost,
        bool canViewSecrecy,
        IReadOnlySet<string> deniedFields,
        CancellationToken token)
    {
        const string sql="""
            SELECT LTRIM(RTRIM(f.F_ID)),COALESCE(NULLIF(LTRIM(RTRIM(f.F_DESC)),''),LTRIM(RTRIM(f.F_ID))),
                   COALESCE(NULLIF(LTRIM(RTRIM(f.F_TYPE)),''),'nvarchar'),COALESCE(f.IS_COST,0),COALESCE(f.IS_SECRECY,0),
                   COALESCE(f.IS_VISIBLE,1),LTRIM(RTRIM(ISNULL(f.DISPLAY_FORMAT,'')))
            FROM dbo.FIELDS f WITH (NOLOCK)
            WHERE f.T_ID=@Table AND COALESCE(f.IS_VIRTUAL,0)=0
              AND EXISTS (SELECT 1 FROM sys.columns c JOIN sys.objects o ON c.object_id=o.object_id AND o.type IN ('U','V') JOIN sys.schemas s ON o.schema_id=s.schema_id WHERE s.name=N'dbo' AND o.name=@Table AND c.name=f.F_ID)
            ORDER BY COALESCE(f.VERIFY_INDEX,999),f.F_ID;
            """;
        await using var command=new SqlCommand(sql,connection);
        command.Parameters.Add("@Table",SqlDbType.NVarChar,100).Value=table;
        await using var reader=await command.ExecuteReaderAsync(token);
        var result=new List<SearchField>();
        while(await reader.ReadAsync(token))
        {
            var key=reader.GetString(0);
            if(deniedFields.Contains(key))continue;
            if(Convert.ToBoolean(reader.GetValue(3))&&!canViewCost)continue;
            if(Convert.ToBoolean(reader.GetValue(4))&&!canViewSecrecy)continue;
            var displayFormat=reader.GetString(6);
            result.Add(new SearchField(key,reader.GetString(1),reader.GetString(2),
                string.IsNullOrWhiteSpace(displayFormat)?null:displayFormat.Trim()));
        }
        return result;
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
}
