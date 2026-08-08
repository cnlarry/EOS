using System.Data;
using System.Text.RegularExpressions;
using EOS.API.Models;
using Microsoft.Data.SqlClient;

namespace EOS.API.Data;

/// <summary>
/// 通用单据打印数据（报表打印模板的第一步）：
/// 主表 + 明细 + 列定义（FIELDS 可见列，成本/保密/禁止过滤）+ 页头/页脚
/// （REPORT_HEADER / REPORT_TAIL，按模块默认报表或 DEFAULT）。
/// 全部标识符来自服务端元数据，键值参数化。
/// </summary>
public sealed class PrintService(DbConnectionFactory connections, ILogger<PrintService> logger)
{
    private static readonly Regex Identifier = new("^[A-Za-z_][A-Za-z0-9_]{0,127}$", RegexOptions.Compiled);

    public async Task<PrintData?> GetPrintDataAsync(
        int moduleId,
        IReadOnlyList<string> keyValues,
        bool canViewCost,
        bool canViewSecrecy,
        IReadOnlySet<string> deniedMasterFields,
        IReadOnlySet<string> deniedDetailFields,
        CancellationToken token)
    {
        await using var connection=connections.Create();
        await connection.OpenAsync(token);
        const string moduleSql="""
            SELECT LTRIM(RTRIM(M_DESC)),LTRIM(RTRIM(MASTER_TABLE)),LTRIM(RTRIM(ISNULL(DETAIL_TABLE,''))),
                   LTRIM(RTRIM(ISNULL(M_URL,'')))
            FROM dbo.MODULES WITH (NOLOCK) WHERE M_IDX=@ModuleId;
            """;
        await using var moduleCommand=new SqlCommand(moduleSql,connection);
        moduleCommand.Parameters.Add("@ModuleId",SqlDbType.Int).Value=moduleId;
        await using var reader=await moduleCommand.ExecuteReaderAsync(token);
        if(!await reader.ReadAsync(token))return null;
        var title=reader.GetString(0);
        var masterTable=reader.GetString(1);
        var detailTable=reader.GetString(2);
        var url=reader.GetString(3);
        await reader.DisposeAsync();
        if(!IsWorkbenchUrl(url)||!Identifier.IsMatch(masterTable))return null;

        var pkOrder=await GetPrimaryKeyColumnsAsync(connection,masterTable,token);
        if(pkOrder.Count==0||pkOrder.Count!=keyValues.Count)return null;
        var masterFields=await ReadFieldsAsync(connection,masterTable,canViewCost,canViewSecrecy,deniedMasterFields,token);
        var master=await ReadRowAsync(connection,masterTable,pkOrder,keyValues,masterFields.Select(field=>field.Key).ToList(),token);
        if(master is null)return null;

        IReadOnlyList<PrintField> detailFields=[];
        IReadOnlyList<IReadOnlyDictionary<string,object?>> details=[];
        if(detailTable.Length>0&&Identifier.IsMatch(detailTable))
        {
            detailFields=await ReadFieldsAsync(connection,detailTable,canViewCost,canViewSecrecy,deniedDetailFields,token);
            details=await ReadRowsAsync(connection,detailTable,pkOrder,keyValues,detailFields.Select(field=>field.Key).ToList(),token);
        }
        var (headerCompany,headerText,footerText)=await ReadHeaderFooterAsync(connection,moduleId,token);
        logger.LogDebug("打印数据 module={ModuleId} master={Master} details={DetailCount}",moduleId,masterTable,details.Count);
        return new PrintData(moduleId,title,headerCompany,headerText,footerText,masterFields,detailFields,master,details);
    }

    private static bool IsWorkbenchUrl(string url)
    {
        var value=url.Trim().Replace('\\','/').ToLowerInvariant();
        return value.Contains("view_frame")||value.Contains("/rpt/")||value.StartsWith("rpt/");
    }

    private static async Task<(string? Company,string? Header,string? Footer)> ReadHeaderFooterAsync(
        SqlConnection connection,int moduleId,CancellationToken token)
    {
        const string sql="""
            SELECT TOP 1 r.HEADER_ID,r.TAIL_ID
            FROM dbo.REPORT r WITH (NOLOCK)
            WHERE r.R_M_IDX=@ModuleId AND r.IS_DEFAULT=1
            ORDER BY r.REPORT_ID;
            """;
        await using var command=new SqlCommand(sql,connection);
        command.Parameters.Add("@ModuleId",SqlDbType.Int).Value=moduleId;
        await using var reader=await command.ExecuteReaderAsync(token);
        string? headerId=null;
        string? tailId=null;
        if(await reader.ReadAsync(token))
        {
            headerId=reader.IsDBNull(0)?"":reader.GetString(0).Trim();
            tailId=reader.IsDBNull(1)?"":reader.GetString(1).Trim();
        }
        await reader.DisposeAsync();
        if(string.IsNullOrEmpty(headerId))headerId="DEFAULT";
        const string headerSql="SELECT LTRIM(RTRIM(ISNULL(COMPANY_NAME,''))),LTRIM(RTRIM(ISNULL(HEADER_TEXT,''))) FROM dbo.REPORT_HEADER WITH (NOLOCK) WHERE HEADER_ID=@Id;";
        await using var headerCommand=new SqlCommand(headerSql,connection);
        headerCommand.Parameters.Add("@Id",SqlDbType.NVarChar,50).Value=headerId;
        await using var headerReader=await headerCommand.ExecuteReaderAsync(token);
        string? company=null;
        string? headerText=null;
        if(await headerReader.ReadAsync(token))
        {
            company=headerReader.GetString(0);
            headerText=headerReader.GetString(1);
        }
        await headerReader.DisposeAsync();
        string? footerText=null;
        if(!string.IsNullOrEmpty(tailId))
        {
            const string tailSql="SELECT LTRIM(RTRIM(ISNULL(TAIL_TEXT,''))) FROM dbo.REPORT_TAIL WITH (NOLOCK) WHERE TAIL_ID=@Id;";
            await using var tailCommand=new SqlCommand(tailSql,connection);
            tailCommand.Parameters.Add("@Id",SqlDbType.NVarChar,50).Value=tailId;
            footerText=await tailCommand.ExecuteScalarAsync(token) as string;
        }
        return (company,headerText,footerText);
    }

    private static async Task<IReadOnlyList<PrintField>> ReadFieldsAsync(
        SqlConnection connection,string table,bool canViewCost,bool canViewSecrecy,
        IReadOnlySet<string> deniedFields,CancellationToken token)
    {
        const string sql="""
            SELECT LTRIM(RTRIM(f.F_ID)),COALESCE(NULLIF(LTRIM(RTRIM(f.F_DESC)),''),LTRIM(RTRIM(f.F_ID))),
                   COALESCE(f.IS_COST,0),COALESCE(f.IS_SECRECY,0)
            FROM dbo.FIELDS f WITH (NOLOCK)
            WHERE f.T_ID=@Table AND COALESCE(f.IS_VISIBLE,1)=1 AND COALESCE(f.IS_VIRTUAL,0)=0
              AND EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS c WHERE c.TABLE_SCHEMA='dbo' AND c.TABLE_NAME=@Table AND c.COLUMN_NAME=f.F_ID)
            ORDER BY COALESCE(f.VERIFY_INDEX,999),f.F_ID;
            """;
        await using var command=new SqlCommand(sql,connection);
        command.Parameters.Add("@Table",SqlDbType.NVarChar,100).Value=table;
        await using var reader=await command.ExecuteReaderAsync(token);
        var result=new List<PrintField>();
        while(await reader.ReadAsync(token))
        {
            var key=reader.GetString(0);
            if(deniedFields.Contains(key))continue;
            if(Convert.ToBoolean(reader.GetValue(2))&&!canViewCost)continue;
            if(Convert.ToBoolean(reader.GetValue(3))&&!canViewSecrecy)continue;
            result.Add(new PrintField(key,reader.GetString(1)));
        }
        return result;
    }

    private static async Task<Dictionary<string,object?>?> ReadRowAsync(
        SqlConnection connection,string table,IReadOnlyList<string> pkOrder,IReadOnlyList<string> keyValues,
        IReadOnlyList<string> fields,CancellationToken token)
    {
        var where=string.Join(" AND ",pkOrder.Select((pk,index)=>$"[{pk}]=@k{index}"));
        var columns=string.Join(',',fields.Select(field=>$"[{field}]"));
        await using var command=new SqlCommand($"SELECT {columns} FROM dbo.[{table}] WITH (NOLOCK) WHERE {where};",connection);
        for(var i=0;i<pkOrder.Count;i++)command.Parameters.AddWithValue($"@k{i}",keyValues[i]);
        await using var reader=await command.ExecuteReaderAsync(token);
        if(!await reader.ReadAsync(token))return null;
        var row=new Dictionary<string,object?>(StringComparer.OrdinalIgnoreCase);
        for(var i=0;i<reader.FieldCount;i++)row[reader.GetName(i)]=reader.IsDBNull(i)?null:reader.GetValue(i);
        return row;
    }

    private static async Task<IReadOnlyList<Dictionary<string,object?>>> ReadRowsAsync(
        SqlConnection connection,string table,IReadOnlyList<string> pkOrder,IReadOnlyList<string> keyValues,
        IReadOnlyList<string> fields,CancellationToken token)
    {
        var where=string.Join(" AND ",pkOrder.Select((pk,index)=>$"[{pk}]=@k{index}"));
        var columns=string.Join(',',fields.Select(field=>$"[{field}]"));
        await using var command=new SqlCommand($"SELECT {columns} FROM dbo.[{table}] WITH (NOLOCK) WHERE {where} ORDER BY [{fields[0]}];",connection);
        for(var i=0;i<pkOrder.Count;i++)command.Parameters.AddWithValue($"@k{i}",keyValues[i]);
        await using var reader=await command.ExecuteReaderAsync(token);
        var rows=new List<Dictionary<string,object?>>();
        while(await reader.ReadAsync(token))
        {
            var row=new Dictionary<string,object?>(StringComparer.OrdinalIgnoreCase);
            for(var i=0;i<reader.FieldCount;i++)row[reader.GetName(i)]=reader.IsDBNull(i)?null:reader.GetValue(i);
            rows.Add(row);
        }
        return rows;
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
}
