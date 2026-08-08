using System.Data;
using System.Text;
using System.Text.RegularExpressions;
using EOS.API.Models;
using Microsoft.Data.SqlClient;

namespace EOS.API.Data;

/// <summary>
/// 报表查看器数据仓库（旧 RptList2 的受控等价）：
/// - 定义：MODULES + SYSQR_DEFAULT 查询条件 + FIELDS 主表可见列（成本/保密/禁止字段过滤）；
/// - 查询：条件值经白名单字段 + 参数化编译（F_TYPE 1 范围 / 2 固定单选 / 4 固定多选；
///   F_TYPE 3 数据单选含 SQL 表达式，受控解析完成前不启用）；
/// - 排序：默认主表 SORT_FIELDS（REPORT_SORT 明细级排序待后续）。
/// 所有动态标识符来自服务端元数据，值全部参数化。
/// </summary>
public sealed class ReportRepository(DbConnectionFactory connections, ILogger<ReportRepository> logger)
{
    private static readonly Regex Identifier = new("^[A-Za-z_][A-Za-z0-9_]{0,127}$", RegexOptions.Compiled);
    private static readonly Regex FieldRef = new(@"^\s*(\w+)\.(\w+)\s*$", RegexOptions.Compiled);
    private static readonly Regex SelectExpression = new(@"\{([^}]+)\}=(true|false|[+-]?\d+(?:\.\d+)?|'[^']*')\s*$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public async Task<ReportDefinition?> GetDefinitionAsync(
        int moduleId,
        string userId,
        bool canViewCost,
        bool canViewSecrecy,
        IReadOnlySet<string> deniedFields,
        CancellationToken token)
    {
        await using var connection=connections.Create();
        await connection.OpenAsync(token);
        const string moduleSql="""
            SELECT LTRIM(RTRIM(M_DESC)),LTRIM(RTRIM(MASTER_TABLE)),LTRIM(RTRIM(ISNULL(M_URL,'')))
            FROM dbo.MODULES WITH (NOLOCK) WHERE M_IDX=@ModuleId;
            """;
        await using var moduleCommand=new SqlCommand(moduleSql,connection);
        moduleCommand.Parameters.Add("@ModuleId",SqlDbType.Int).Value=moduleId;
        await using var reader=await moduleCommand.ExecuteReaderAsync(token);
        if(!await reader.ReadAsync(token))return null;
        var title=reader.GetString(0);
        var masterTable=reader.GetString(1);
        var url=reader.GetString(2);
        await reader.DisposeAsync();
        if(!IsReportUrl(url)||!Identifier.IsMatch(masterTable))
        {
            logger.LogWarning("报表模块校验失败 module={ModuleId} url={Url} master={Master}",moduleId,url,masterTable);
            return null;
        }

        var conditions=await ReadConditionsAsync(connection,masterTable,moduleId,token);
        var (columns,pkOrder)=await ReadColumnsAsync(connection,masterTable,canViewCost,canViewSecrecy,deniedFields,token);
        return new ReportDefinition(moduleId,title,masterTable,conditions,columns,pkOrder);
    }

    public async Task<ReportQueryResult> QueryAsync(
        ReportDefinition definition,
        ReportQueryRequest request,
        int page,
        int pageSize,
        CancellationToken token)
    {
        page=Math.Max(1,page);
        pageSize=Math.Clamp(pageSize,10,200);
        await using var connection=connections.Create();
        await connection.OpenAsync(token);
        var physicalColumns=await GetPhysicalColumnsAsync(connection,definition.MasterTable,token);
        var predicates=new List<string>();
        var command=new SqlCommand();
        command.Connection=connection;
        foreach(var condition in definition.Conditions)
        {
            var value=request.Values.GetValueOrDefault(condition.SerialNo);
            var valueTo=request.ValuesTo.GetValueOrDefault(condition.SerialNo);
            if(!TryResolveField(condition.Field,definition.MasterTable,physicalColumns,out var field))continue;
            switch(condition.Type)
            {
                case 1 when !string.IsNullOrWhiteSpace(value):
                    predicates.Add($"[{field}] >= @rc{command.Parameters.Count}");
                    command.Parameters.AddWithValue($"@rc{command.Parameters.Count}",value);
                    if(!string.IsNullOrWhiteSpace(valueTo))
                    {
                        predicates.Add($"[{field}] <= @rc{command.Parameters.Count}");
                        command.Parameters.AddWithValue($"@rc{command.Parameters.Count}",valueTo);
                    }
                    break;
                case 2 when !string.IsNullOrWhiteSpace(value):
                    if(!TryParseSelectOption(value,definition.MasterTable,physicalColumns,out var selectField,out var selectValue))continue;
                    predicates.Add($"[{selectField}] = @rc{command.Parameters.Count}");
                    command.Parameters.AddWithValue($"@rc{command.Parameters.Count}",NormalizeConstant(selectValue));
                    break;
                case 4 when value is not null:
                    var chosen=value.Split(',',StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries)
                        .Where(item=>condition.Options.Any(option=>option.Value==item)).ToList();
                    if(chosen.Count==0)continue;
                    var placeholders=new List<string>();
                    foreach(var item in chosen)
                    {
                        placeholders.Add($"@rc{command.Parameters.Count}");
                        command.Parameters.AddWithValue($"@rc{command.Parameters.Count}",item);
                    }
                    predicates.Add($"[{field}] IN ({string.Join(',',placeholders)})");
                    break;
            }
        }
        var where=predicates.Count>0?" WHERE "+string.Join(" AND ",predicates):"";
        var selected=definition.Columns.Take(40).Select(column=>$"[{column.Key}]").ToList();
        if(selected.Count==0)return new([],0,page,pageSize);
        command.CommandText=$"""
            SELECT COUNT_BIG(1) FROM dbo.[{definition.MasterTable}] WITH (NOLOCK){where};
            SELECT {string.Join(',',selected)} FROM dbo.[{definition.MasterTable}] WITH (NOLOCK){where}
            ORDER BY {string.Join(',',definition.MasterPkOrder.Select((pk,index)=>$"[{pk}]"))}
            OFFSET @Offset ROWS FETCH NEXT @PageSize ROWS ONLY;
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
        logger.LogDebug("报表查询 module={ModuleId} master={Master} total={Total} page={Page}",definition.ModuleId,definition.MasterTable,total,page);
        return new ReportQueryResult(rows,total,page,pageSize);
    }

    private static bool IsReportUrl(string url)
    {
        var value=url.Trim().Replace('\\','/');
        return value.StartsWith("~/RPT/",StringComparison.OrdinalIgnoreCase)
            ||value.StartsWith("RPT/",StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<IReadOnlyList<ReportCondition>> ReadConditionsAsync(
        SqlConnection connection,
        string masterTable,
        int moduleId,
        CancellationToken token)
    {
        const string sql="""
            SELECT SERIAL_NO,LTRIM(RTRIM(ISNULL(F_ID,''))),LTRIM(RTRIM(ISNULL(F_DESC,''))),ISNULL(F_TYPE,0),
                   LTRIM(RTRIM(ISNULL(F_EXPR,''))),LTRIM(RTRIM(ISNULL(F_VALUE,''))),LTRIM(RTRIM(ISNULL(PARA_NAME,'')))
            FROM dbo.SYSQR_DEFAULT WITH (NOLOCK)
            WHERE M_IDX=@ModuleId ORDER BY SERIAL_NO;
            """;
        await using var command=new SqlCommand(sql,connection);
        command.Parameters.Add("@ModuleId",SqlDbType.Int).Value=moduleId;
        await using var reader=await command.ExecuteReaderAsync(token);
        var result=new List<ReportCondition>();
        while(await reader.ReadAsync(token))
        {
            var serial=Convert.ToInt32(reader.GetValue(0));
            var field=reader.GetString(1);
            var desc=reader.GetString(2);
            var type=Convert.ToInt32(reader.GetValue(3));
            var expression=reader.GetString(4);
            var defaultValue=reader.GetString(5);
            var parameterName=reader.GetString(6);
            var options=ParseOptions(type,expression);
            result.Add(new ReportCondition(serial,field.Length>0?field:null,desc,type,
                expression.Length>0?expression:null,defaultValue.Length>0?defaultValue:null,
                parameterName.Length>0?parameterName:null,options));
        }
        return result;
    }

    private static IReadOnlyList<ReportOption> ParseOptions(int type,string expression)
    {
        var result=new List<ReportOption>();
        if(string.IsNullOrWhiteSpace(expression))return result;
        foreach(var item in expression.Split(';',StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries))
        {
            var separator=item.IndexOf(':');
            if(separator<=0)continue;
            result.Add(new ReportOption(item[..separator].Trim(),item[(separator+1)..].Trim()));
        }
        return result;
    }

    private static async Task<(IReadOnlyList<ReportColumn> Columns,IReadOnlyList<string> PkOrder)> ReadColumnsAsync(
        SqlConnection connection,
        string masterTable,
        bool canViewCost,
        bool canViewSecrecy,
        IReadOnlySet<string> deniedFields,
        CancellationToken token)
    {
        const string sql="""
            SELECT LTRIM(RTRIM(f.F_ID)),COALESCE(NULLIF(LTRIM(RTRIM(f.F_DESC)),''),LTRIM(RTRIM(f.F_ID))),
                   COALESCE(NULLIF(LTRIM(RTRIM(f.F_TYPE)),''),'nvarchar'),COALESCE(f.IS_COST,0),COALESCE(f.IS_SECRECY,0)
            FROM dbo.FIELDS f WITH (NOLOCK)
            WHERE f.T_ID=@Table AND COALESCE(f.IS_VISIBLE,1)=1 AND COALESCE(f.IS_VIRTUAL,0)=0
              AND EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS c WHERE c.TABLE_SCHEMA='dbo' AND c.TABLE_NAME=@Table AND c.COLUMN_NAME=f.F_ID)
            ORDER BY COALESCE(f.VERIFY_INDEX,999),f.F_ID;
            """;
        await using var command=new SqlCommand(sql,connection);
        command.Parameters.Add("@Table",SqlDbType.NVarChar,100).Value=masterTable;
        await using var reader=await command.ExecuteReaderAsync(token);
        var columns=new List<ReportColumn>();
        while(await reader.ReadAsync(token))
        {
            var key=reader.GetString(0);
            if(deniedFields.Contains(key))continue;
            if(Convert.ToBoolean(reader.GetValue(3))&&!canViewCost)continue;
            if(Convert.ToBoolean(reader.GetValue(4))&&!canViewSecrecy)continue;
            columns.Add(new ReportColumn(key,reader.GetString(1),reader.GetString(2)));
        }
        await reader.DisposeAsync();
        var pkOrder=await GetPrimaryKeyColumnsAsync(connection,masterTable,token);
        return (columns,pkOrder);
    }

    private static async Task<IReadOnlyList<string>> GetPhysicalColumnsAsync(SqlConnection connection,string table,CancellationToken token)
    {
        const string sql="""
            SELECT COLUMN_NAME FROM INFORMATION_SCHEMA.COLUMNS
            WHERE TABLE_SCHEMA='dbo' AND TABLE_NAME=@Table;
            """;
        await using var command=new SqlCommand(sql,connection);
        command.Parameters.Add("@Table",SqlDbType.NVarChar,100).Value=table;
        await using var reader=await command.ExecuteReaderAsync(token);
        var result=new List<string>();
        while(await reader.ReadAsync(token))result.Add(reader.GetString(0));
        return result;
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

    private static bool TryResolveField(string? raw,string masterTable,IReadOnlyList<string> physicalColumns,out string field)
    {
        field=string.Empty;
        if(string.IsNullOrWhiteSpace(raw))return false;
        var match=FieldRef.Match(raw);
        if(!match.Success)return false;
        if(!match.Groups[1].Value.Equals(masterTable,StringComparison.OrdinalIgnoreCase))return false;
        field=match.Groups[2].Value;
        if(!Identifier.IsMatch(field))return false;
        foreach(var column in physicalColumns)
            if(column.Equals(field,StringComparison.OrdinalIgnoreCase))return true;
        return false;
    }

    private static bool TryParseSelectOption(string expression,string masterTable,IReadOnlyList<string> physicalColumns,out string field,out string constant)
    {
        field=string.Empty;
        constant=string.Empty;
        var match=SelectExpression.Match(expression);
        if(!match.Success)return false;
        if(!TryResolveField(match.Groups[1].Value,masterTable,physicalColumns,out field))return false;
        constant=match.Groups[2].Value;
        return true;
    }

    private static object NormalizeConstant(string constant)
    {
        if(constant.Equals("true",StringComparison.OrdinalIgnoreCase))return true;
        if(constant.Equals("false",StringComparison.OrdinalIgnoreCase))return false;
        if(decimal.TryParse(constant,System.Globalization.NumberStyles.Any,System.Globalization.CultureInfo.InvariantCulture,out var numeric))
            return numeric;
        if(constant.Length>=2&&constant[0]=='\''&&constant[^1]=='\'')
            return constant[1..^1];
        return constant;
    }
}
