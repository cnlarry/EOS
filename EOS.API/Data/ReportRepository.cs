using System.Data;
using System.Text;
using System.Text.RegularExpressions;
using EOS.API.Errors;
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
    private static readonly Regex SpReference = new(@"\{([A-Za-z_][A-Za-z0-9_]*(?:\.[A-Za-z_][A-Za-z0-9_]*)?)\}", RegexOptions.Compiled);
    private static readonly Regex SelectSourcePattern = new(@"^\s*select\s+(\w+)\s+C_ID\s*,\s*(\w+)\s+C_VALUE\s+from\s+(\w+)\s*$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

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
            SELECT LTRIM(RTRIM(M_DESC)),LTRIM(RTRIM(MASTER_TABLE)),LTRIM(RTRIM(ISNULL(M_URL,''))),
                   LTRIM(RTRIM(ISNULL(DETAIL_TABLE,''))),LTRIM(RTRIM(ISNULL(FILTER,'')))
            FROM dbo.MODULES WITH (NOLOCK) WHERE M_IDX=@ModuleId;
            """;
        await using var moduleCommand=new SqlCommand(moduleSql,connection);
        moduleCommand.Parameters.Add("@ModuleId",SqlDbType.Int).Value=moduleId;
        await using var reader=await moduleCommand.ExecuteReaderAsync(token);
        if(!await reader.ReadAsync(token))return null;
        var title=reader.GetString(0);
        var masterTable=reader.GetString(1);
        var url=reader.GetString(2);
        var detailTable=reader.GetString(3);
        var moduleFilter=reader.GetString(4);
        await reader.DisposeAsync();
        if(!IsReportUrl(url)||!Identifier.IsMatch(masterTable))
        {
            logger.LogWarning("报表模块校验失败 module={ModuleId} url={Url} master={Master}",moduleId,url,masterTable);
            return null;
        }

        var conditions=await ReadConditionsAsync(connection,masterTable,moduleId,userId,token);
        // F_TYPE 3 数据单选：校验选项源表/列物理存在（白名单）
        foreach(var condition in conditions.Where(item=>item.SelectSource is not null))
        {
            var source=condition.SelectSource!;
            if(!Identifier.IsMatch(source.Table)||!Identifier.IsMatch(source.IdColumn)||!Identifier.IsMatch(source.ValueColumn))
                conditions=conditions.Select(item=>item==condition?item with{SelectSource=null}:item).ToList();
            else
            {
                var physical=await GetPhysicalColumnsAsync(connection,source.Table,token);
                if(!physical.Any(column=>column.Equals(source.IdColumn,StringComparison.OrdinalIgnoreCase))
                   ||!physical.Any(column=>column.Equals(source.ValueColumn,StringComparison.OrdinalIgnoreCase)))
                    conditions=conditions.Select(item=>item==condition?item with{SelectSource=null}:item).ToList();
            }
        }
        var (columns,pkOrder)=await ReadColumnsAsync(connection,masterTable,canViewCost,canViewSecrecy,deniedFields,token);
        var sortFields=await ReadDefaultSortFieldsAsync(connection,moduleId,token);
        var spName=sortFields.Select(field=>SpReference.Match(field))
            .Where(match=>match.Success)
            .Select(match=>match.Groups[1].Value.Split('.')[0])
            .FirstOrDefault(name=>name.StartsWith("P_RPT_",StringComparison.OrdinalIgnoreCase));
        IReadOnlyList<ReportSpParameter> spParameters=[];
        if(spName is not null)
        {
            spParameters=await ReadSpParametersAsync(connection,spName,token);
            if(await StoredProcedureExistsAsync(connection,spName,token)&&spParameters.Count==0)
                spParameters=[]; // 无参 SP 也允许
            if(!await StoredProcedureExistsAsync(connection,spName,token))spName=null;
        }
        return new ReportDefinition(moduleId,title,masterTable,detailTable.Length>0?detailTable:null,conditions,columns,pkOrder,sortFields,spName,spParameters,
            moduleFilter.Length==0?null:moduleFilter);
    }

    /// <summary>
    /// F_TYPE 3 数据单选选项：按白名单表/列执行静态 SELECT（无用户输入拼接）。
    /// </summary>
    public async Task<IReadOnlyList<ReportOption>> GetConditionOptionsAsync(
        ReportDefinition definition,
        int serialNo,
        CancellationToken token)
    {
        var condition=definition.Conditions.FirstOrDefault(item=>item.SerialNo==serialNo);
        if(condition?.SelectSource is not { } source)return [];
        if(!Identifier.IsMatch(source.Table)||!Identifier.IsMatch(source.IdColumn)||!Identifier.IsMatch(source.ValueColumn))return [];
        await using var connection=connections.Create();
        await connection.OpenAsync(token);
        var physical=await GetPhysicalColumnsAsync(connection,source.Table,token);
        if(!physical.Any(column=>column.Equals(source.IdColumn,StringComparison.OrdinalIgnoreCase))
           ||!physical.Any(column=>column.Equals(source.ValueColumn,StringComparison.OrdinalIgnoreCase)))
            return [];
        await using var command=new SqlCommand(
            $"SELECT [{source.IdColumn}],[{source.ValueColumn}] FROM dbo.[{source.Table}] WITH (NOLOCK);",connection);
        await using var reader=await command.ExecuteReaderAsync(token);
        var result=new List<ReportOption>();
        while(await reader.ReadAsync(token))
            result.Add(new ReportOption(Convert.ToString(reader.GetValue(1))??"",Convert.ToString(reader.GetValue(0))??""));
        return result;
    }

    public async Task<ReportQueryResult> QueryAsync(
        ReportDefinition definition,
        ReportQueryRequest request,
        int page,
        int pageSize,
        CancellationToken token)
    {
        if(definition.SpName is not null)
            return await RunSpAsync(definition,request.Values,token);
        page=Math.Max(1,page);
        pageSize=Math.Clamp(pageSize,10,200);
        await using var connection=connections.Create();
        await connection.OpenAsync(token);
        var physicalColumns=await GetPhysicalColumnsAsync(connection,definition.MasterTable,token);
        var detailPhysical=definition.DetailTable is null?[]:await GetPhysicalColumnsAsync(connection,definition.DetailTable,token);
        var command=new SqlCommand();
        command.Connection=connection;
        var predicates=BuildConditionPredicates(definition,request,physicalColumns,command);
        var where=predicates.Count>0?" WHERE "+string.Join(" AND ",predicates):"";
        var selected=BuildSelectedColumns(definition);
        if(selected.Count==0)return new([],0,page,pageSize);
        var join=string.Empty;
        if(definition.DetailTable is not null&&definition.MasterPkOrder.Count>0
           &&definition.SortFields.Any(field=>TryResolveSortField(field,definition,physicalColumns,detailPhysical,out _,out _)))
            join=BuildDetailJoin(definition);
        var orderBy=ResolveOrderBy(definition,physicalColumns,detailPhysical,join.Length>0);
        command.CommandText=$"""
            SELECT COUNT_BIG(1) FROM dbo.[{definition.MasterTable}] m WITH (NOLOCK){join}{where};
            SELECT {string.Join(',',selected.Select(column=>$"m.[{column}]"))} FROM dbo.[{definition.MasterTable}] m WITH (NOLOCK){join}{where}
            ORDER BY {orderBy}
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

    /// <summary>
    /// 报表 PDF 全量查询（不翻页）：
    /// - 查询条件沿用白名单参数化编译（与 QueryAsync 同边界）；
    /// - 模块 FILTER 必须可受控解析（失败抛 403，拒绝返回未过滤数据）；
    /// - REPORT_FILTER / 报表级 DATA_FILTER 仅当受控解析成功才应用，否则跳过并记日志；
    /// - 排序/分组字段全部来自服务端（REPORT_SORT 白名单）经物理列校验；
    /// - 结果超过 10,000 行抛 PdfDataTooLargeException（422）。
    /// </summary>
    public async Task<ReportQueryResult> QueryPdfAsync(
        ReportDefinition definition,
        ReportQueryRequest request,
        string? moduleFilter,
        string? reportFilter,
        string? rightsDataFilter,
        IReadOnlyList<string> sortFields,
        IReadOnlyList<string> groupFields,
        CancellationToken token)
    {
        if(definition.SpName is not null)
            return await RunSpAsync(definition,request.Values,token,10000);
        await using var connection=connections.Create();
        await connection.OpenAsync(token);
        var physicalColumns=await GetPhysicalColumnsAsync(connection,definition.MasterTable,token);
        var detailPhysical=definition.DetailTable is null?[]:await GetPhysicalColumnsAsync(connection,definition.DetailTable,token);
        var allowedFields=physicalColumns.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var command=new SqlCommand();
        command.Connection=connection;
        var predicates=BuildConditionPredicates(definition,request,physicalColumns,command);
        ApplyControlledFilter(moduleFilter,definition.MasterTable,allowedFields,predicates,command,throwOnFailure:true);
        ApplyControlledFilter(reportFilter,definition.MasterTable,allowedFields,predicates,command,throwOnFailure:true);
        ApplyControlledFilter(rightsDataFilter,definition.MasterTable,allowedFields,predicates,command,throwOnFailure:true);
        var where=predicates.Count>0?" WHERE "+string.Join(" AND ",predicates):"";
        var selected=BuildSelectedColumns(definition);
        // 分组字段并入选中列（最多 45 列）：分组表头/小计需要真实分组值
        foreach(var field in groupFields)
        {
            var name=field.Contains('.')?field.Split('.')[^1]:field;
            if(!selected.Contains(name,StringComparer.OrdinalIgnoreCase))
            {
                selected.Add(name);
                if(selected.Count>=45)break;
            }
        }
        if(selected.Count==0)return new([],0,0,0);
        var hasJoin=definition.DetailTable is not null&&definition.MasterPkOrder.Count>0
            &&groupFields.Concat(sortFields).Any(field=>
                TryResolveSortField(field,definition,physicalColumns,detailPhysical,out _,out var needsDetail)&&needsDetail);
        var join=hasJoin?BuildDetailJoin(definition):string.Empty;
        var orderBy=ResolvePrintOrderBy(definition,physicalColumns,detailPhysical,join.Length>0,groupFields,sortFields);
        command.CommandText=$"""
            SELECT {string.Join(',',selected.Select(column=>$"m.[{column}]"))}
            FROM dbo.[{definition.MasterTable}] m WITH (NOLOCK){join}{where}
            ORDER BY {orderBy};
            """;
        await using var reader=await command.ExecuteReaderAsync(token);
        var rows=new List<Dictionary<string,object?>>();
        while(await reader.ReadAsync(token))
        {
            if(rows.Count>=10000)
                throw new PdfDataTooLargeException("报表数据超过 10,000 行上限，请缩小查询条件后再打印。");
            var row=new Dictionary<string,object?>(StringComparer.OrdinalIgnoreCase);
            for(var i=0;i<reader.FieldCount;i++)row[reader.GetName(i)]=reader.IsDBNull(i)?null:reader.GetValue(i);
            rows.Add(row);
        }
        logger.LogDebug("报表 PDF 查询 module={ModuleId} master={Master} rows={RowCount}",
            definition.ModuleId,definition.MasterTable,rows.Count);
        return new ReportQueryResult(rows,rows.Count,1,rows.Count);
    }

    /// <summary>
    /// 汇总报表 SP 受控执行（RptInteg，如库存日报 P_RPT_INV_PRO_DEPOT_1）：
    /// SP 名来自 REPORT_SORT 花括号引用且必须以 P_RPT_ 开头并在 sys.objects 存在；
    /// 参数名来自 sys.parameters 白名单，值按参数类型转换，执行读取首个结果集。
    /// </summary>
    private async Task<ReportQueryResult> RunSpAsync(
        ReportDefinition definition,
        IReadOnlyDictionary<int,string?> values,
        CancellationToken token,
        int maxRows = 1000)
    {
        await using var connection=connections.Create();
        await connection.OpenAsync(token);
        await using var command=new SqlCommand(definition.SpName!,connection)
        {
            CommandType=CommandType.StoredProcedure,
        };
        // 全部参数显式传入：缺失/空白按 DBNull（等价"不过滤"），避免 SQL 报"未提供参数"。
        for(var i=0;i<definition.SpParameters.Count;i++)
        {
            var spec=definition.SpParameters[i];
            var raw=values.GetValueOrDefault(i+1);
            if(string.IsNullOrWhiteSpace(raw))
            {
                command.Parameters.AddWithValue($"@{spec.Name}",DBNull.Value);
                continue;
            }
            var value=ConvertParameter(spec,raw);
            command.Parameters.AddWithValue($"@{spec.Name}",value??DBNull.Value);
        }
        await using var reader=await command.ExecuteReaderAsync(token);
        var columns=new List<string>();
        for(var i=0;i<reader.FieldCount;i++)columns.Add(reader.GetName(i));
        var rows=new List<Dictionary<string,object?>>();
        var count=0;
        while(await reader.ReadAsync(token))
        {
            if(count>=maxRows)
                throw new PdfDataTooLargeException("报表数据超过 10,000 行上限，请缩小查询条件后再打印。");
            var row=new Dictionary<string,object?>(StringComparer.OrdinalIgnoreCase);
            for(var i=0;i<reader.FieldCount;i++)row[columns[i]]=reader.IsDBNull(i)?null:reader.GetValue(i);
            rows.Add(row);
            count++;
        }
        return new ReportQueryResult(rows,count,1,count);
    }

    private static object? ConvertParameter(ReportSpParameter parameter,string raw)
    {
        var type=parameter.DataType.ToLowerInvariant();
        try
        {
            if(type.Contains("datetime",StringComparison.Ordinal)||type.Contains("date",StringComparison.Ordinal))
                return DateTime.Parse(raw,System.Globalization.CultureInfo.InvariantCulture);
            if(type.Contains("int",StringComparison.Ordinal))
                return int.Parse(raw,System.Globalization.CultureInfo.InvariantCulture);
            if(type.Contains("float",StringComparison.Ordinal)||type.Contains("real",StringComparison.Ordinal)
               ||type.Contains("decimal",StringComparison.Ordinal)||type.Contains("numeric",StringComparison.Ordinal))
                return decimal.Parse(raw,System.Globalization.CultureInfo.InvariantCulture);
            if(type.Contains("bit",StringComparison.Ordinal))
                return raw.Equals("true",StringComparison.OrdinalIgnoreCase)||raw=="1";
            return raw;
        }
        catch{return null;}
    }

    private static async Task<IReadOnlyList<ReportSpParameter>> ReadSpParametersAsync(
        SqlConnection connection,
        string spName,
        CancellationToken token)
    {
        const string sql="""
            SELECT p.name,TYPE_NAME(p.user_type_id),p.max_length
            FROM sys.parameters p WHERE p.object_id=OBJECT_ID(@SpName) ORDER BY p.parameter_id;
            """;
        await using var command=new SqlCommand(sql,connection);
        command.Parameters.Add("@SpName",SqlDbType.NVarChar,200).Value=spName;
        await using var reader=await command.ExecuteReaderAsync(token);
        var result=new List<ReportSpParameter>();
        while(await reader.ReadAsync(token))
            result.Add(new ReportSpParameter(reader.GetString(0).TrimStart('@'),reader.GetString(1),Convert.ToInt32(reader.GetValue(2))));
        return result;
    }

    private static async Task<bool> StoredProcedureExistsAsync(SqlConnection connection,string spName,CancellationToken token)
    {
        const string sql="SELECT 1 FROM sys.objects WHERE object_id=OBJECT_ID(@Name) AND type='P';";
        await using var command=new SqlCommand(sql,connection);
        command.Parameters.Add("@Name",SqlDbType.NVarChar,200).Value=spName;
        return await command.ExecuteScalarAsync(token) is not null;
    }

    private static async Task<IReadOnlyList<string>> ReadDefaultSortFieldsAsync(
        SqlConnection connection,
        int moduleId,
        CancellationToken token)
    {
        const string sql="""
            SELECT LTRIM(RTRIM(ISNULL(s.SORT_FIELDS,'')))
            FROM dbo.REPORT_SORT s WITH (NOLOCK)
            WHERE s.REPORT_ID IN (SELECT REPORT_ID FROM dbo.REPORT WITH (NOLOCK)
                                  WHERE R_M_IDX=@ModuleId AND IS_DEFAULT=1)
              AND LTRIM(RTRIM(ISNULL(s.SORT_FIELDS,'')))<>''
            ORDER BY s.SERIAL_NO;
            """;
        await using var command=new SqlCommand(sql,connection);
        command.Parameters.Add("@ModuleId",SqlDbType.Int).Value=moduleId;
        await using var reader=await command.ExecuteReaderAsync(token);
        var result=new List<string>();
        while(await reader.ReadAsync(token))
        {
            var value=reader.GetString(0);
            if(string.IsNullOrWhiteSpace(value))continue;
            result.AddRange(value.Split(',',StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries)
                .Where(field=>FieldRef.IsMatch(field)||SpReference.IsMatch(field)));
        }
        return result;
    }

    private static bool TryResolveSortField(
        string raw,
        ReportDefinition definition,
        IReadOnlyList<string> masterPhysical,
        IReadOnlyList<string> detailPhysical,
        out string columnExpression,
        out bool needsDetail)
    {
        columnExpression=string.Empty;
        needsDetail=false;
        var match=FieldRef.Match(raw);
        if(!match.Success)return false;
        var table=match.Groups[1].Value;
        var column=match.Groups[2].Value;
        if(!Identifier.IsMatch(column))return false;
        if(table.Equals(definition.MasterTable,StringComparison.OrdinalIgnoreCase))
        {
            foreach(var item in masterPhysical)
                if(item.Equals(column,StringComparison.OrdinalIgnoreCase)){columnExpression=$"[{column}]";return true;}
            return false;
        }
        if(definition.DetailTable is not null&&table.Equals(definition.DetailTable,StringComparison.OrdinalIgnoreCase))
        {
            foreach(var item in detailPhysical)
                if(item.Equals(column,StringComparison.OrdinalIgnoreCase)){columnExpression=$"d.[{column}]";needsDetail=true;return true;}
            return false;
        }
        return false;
    }

    /// <summary>
    /// 查询条件编译（F_TYPE 1 范围 / 2 固定单选 / 3 数据单选 / 4 固定多选），
    /// 字段经白名单解析，值全部参数化；与 QueryAsync / QueryPdfAsync 共用。
    /// </summary>
    private static List<string> BuildConditionPredicates(
        ReportDefinition definition,
        ReportQueryRequest request,
        IReadOnlyList<string> physicalColumns,
        SqlCommand command)
    {
        var predicates=new List<string>();
        foreach(var condition in definition.Conditions)
        {
            var value=request.Values.GetValueOrDefault(condition.SerialNo);
            var valueTo=request.ValuesTo.GetValueOrDefault(condition.SerialNo);
            if(!TryResolveField(condition.Field,definition.MasterTable,physicalColumns,out var field))continue;
            switch(condition.Type)
            {
                case 1 when !string.IsNullOrWhiteSpace(value):
                    predicates.Add($"m.[{field}] >= @rc{command.Parameters.Count}");
                    command.Parameters.AddWithValue($"@rc{command.Parameters.Count}",value);
                    if(!string.IsNullOrWhiteSpace(valueTo))
                    {
                        predicates.Add($"m.[{field}] <= @rc{command.Parameters.Count}");
                        command.Parameters.AddWithValue($"@rc{command.Parameters.Count}",valueTo);
                    }
                    break;
                case 2 when !string.IsNullOrWhiteSpace(value):
                    if(!TryParseSelectOption(value,definition.MasterTable,physicalColumns,out var selectField,out var selectValue))continue;
                    predicates.Add($"m.[{selectField}] = @rc{command.Parameters.Count}");
                    command.Parameters.AddWithValue($"@rc{command.Parameters.Count}",NormalizeConstant(selectValue));
                    break;
                case 3 when !string.IsNullOrWhiteSpace(value):
                    predicates.Add($"m.[{field}] = @rc{command.Parameters.Count}");
                    command.Parameters.AddWithValue($"@rc{command.Parameters.Count}",value);
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
                    predicates.Add($"m.[{field}] IN ({string.Join(',',placeholders)})");
                    break;
            }
        }
        return predicates;
    }

    /// <summary>主键列优先（报表须可辨识行），再取其余可见列，上限 40 列。</summary>
    internal static List<string> BuildSelectedColumns(ReportDefinition definition) =>
        definition.MasterPkOrder
            .Where(pk=>definition.Columns.Any(column=>column.Key.Equals(pk,StringComparison.OrdinalIgnoreCase)))
            .Concat(definition.Columns
                .Select(column=>column.Key)
                .Where(key=>!definition.MasterPkOrder.Any(pk=>pk.Equals(key,StringComparison.OrdinalIgnoreCase))))
            .Take(40)
            .ToList();

    private static string BuildDetailJoin(ReportDefinition definition)
    {
        var on=string.Join(" AND ",definition.MasterPkOrder.Select(pk=>$"m.[{pk}]=d.[{pk}]"));
        return $" LEFT JOIN dbo.[{definition.DetailTable}] d WITH (NOLOCK) ON {on}";
    }

    /// <summary>
    /// 受控过滤应用（M85 定稿）：模块 FILTER / REPORT_FILTER / 报表级 DATA_FILTER
    /// 一律白名单化，解析失败即拒绝（403），不静默跳过；参数名按谓词序号重命名避免冲突。
    /// </summary>
    private static void ApplyControlledFilter(
        string? filter,
        string masterTable,
        IReadOnlySet<string> allowedFields,
        ICollection<string> predicates,
        SqlCommand command,
        bool throwOnFailure)
    {
        if(string.IsNullOrWhiteSpace(filter))return;
        if(!DataFilterParser.TryParse(filter,masterTable,allowedFields,out var predicate,out var parameters))
        {
            if(throwOnFailure)
                throw new DataFilterUnsupportedException("该模块的数据过滤条件尚不支持，已拒绝生成 PDF。");
            return;
        }
        var renamed=Regex.Replace(predicate,"@df(\\d+)",
            match=>$"@pf{predicates.Count}_{int.Parse(match.Groups[1].Value)}");
        for(var i=0;i<parameters.Count;i++)
            command.Parameters.AddWithValue($"@pf{predicates.Count}_{i}",parameters[i]);
        predicates.Add(renamed);
    }

    /// <summary>PDF 排序：分组字段优先（分组需连续），随后排序字段；全部经物理列白名单。</summary>
    private static string ResolvePrintOrderBy(
        ReportDefinition definition,
        IReadOnlyList<string> masterPhysical,
        IReadOnlyList<string> detailPhysical,
        bool hasJoin,
        IReadOnlyList<string> groupFields,
        IReadOnlyList<string> sortFields)
    {
        var orders=new List<string>();
        foreach(var field in groupFields.Concat(sortFields))
        {
            if(!TryResolveSortField(field,definition,masterPhysical,detailPhysical,out var expression,out var needsDetail))continue;
            if(needsDetail&&!hasJoin)continue;
            if(!orders.Contains(expression,StringComparer.OrdinalIgnoreCase))orders.Add(expression);
        }
        return orders.Count==0
            ? string.Join(',',definition.MasterPkOrder.Select(pk=>$"[{pk}]"))
            : string.Join(',',orders);
    }

    private static string ResolveOrderBy(
        ReportDefinition definition,
        IReadOnlyList<string> masterPhysical,
        IReadOnlyList<string> detailPhysical,
        bool hasJoin)
    {
        var orders=new List<string>();
        foreach(var field in definition.SortFields)
        {
            if(!TryResolveSortField(field,definition,masterPhysical,detailPhysical,out var expression,out var needsDetail))continue;
            if(needsDetail&&!hasJoin)continue;
            orders.Add(expression);
        }
        if(orders.Count==0)
            return string.Join(',',definition.MasterPkOrder.Select(pk=>$"[{pk}]"));
        return string.Join(',',orders);
    }

    private static bool IsReportUrl(string url)
    {
        var value=url.Trim().Replace('\\','/');
        return value.Equals("/reports",StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("/reports/",StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<IReadOnlyList<ReportCondition>> ReadConditionsAsync(
        SqlConnection connection,
        string masterTable,
        int moduleId,
        string userId,
        CancellationToken token)
    {
        const string sql="""
            SELECT d.SERIAL_NO,LTRIM(RTRIM(ISNULL(d.F_ID,''))),LTRIM(RTRIM(ISNULL(d.F_DESC,''))),ISNULL(d.F_TYPE,0),
                   LTRIM(RTRIM(ISNULL(d.F_EXPR,''))),LTRIM(RTRIM(ISNULL(d.F_VALUE,''))),LTRIM(RTRIM(ISNULL(d.PARA_NAME,''))),
                   LTRIM(RTRIM(ISNULL(u.F_VALUE,'')))
            FROM dbo.SYSQR_DEFAULT d WITH (NOLOCK)
            LEFT JOIN dbo.SYSQR_USER u WITH (NOLOCK)
              ON u.M_IDX=d.M_IDX AND u.SERIAL_NO=d.SERIAL_NO AND u.USER_ID=@UserId
            WHERE d.M_IDX=@ModuleId ORDER BY d.SERIAL_NO;
            """;
        await using var command=new SqlCommand(sql,connection);
        command.Parameters.Add("@ModuleId",SqlDbType.Int).Value=moduleId;
        command.Parameters.Add("@UserId",SqlDbType.NChar,10).Value=userId.Trim();
        await using var reader=await command.ExecuteReaderAsync(token);
        var result=new List<ReportCondition>();
        while(await reader.ReadAsync(token))
        {
            var serial=int.TryParse(Convert.ToString(reader.GetValue(0)), out var parsedSerial)?parsedSerial:0;
            var field=reader.GetString(1);
            var desc=reader.GetString(2);
            var type=int.TryParse(Convert.ToString(reader.GetValue(3)), out var parsedType)?parsedType:0;
            var expression=reader.GetString(4);
            var defaultValue=reader.GetString(5);
            var parameterName=reader.GetString(6);
            var userValue=reader.GetString(7);
            var (userFrom,userTo)=SplitUserConditionValue(userValue);
            var effectiveDefault=string.IsNullOrWhiteSpace(userFrom)?defaultValue:userFrom;
            var effectiveDefaultTo=userTo;
            var options=ParseOptions(type,expression);
            ReportSelectSource? selectSource=null;
            if(type==3)
            {
                var match=SelectSourcePattern.Match(expression);
                if(match.Success)
                    selectSource=new ReportSelectSource(match.Groups[3].Value,match.Groups[1].Value,match.Groups[2].Value);
            }
            result.Add(new ReportCondition(serial,field.Length>0?field:null,desc,type,
                expression.Length>0?expression:null,
                string.IsNullOrWhiteSpace(effectiveDefault)?null:effectiveDefault,
                parameterName.Length>0?parameterName:null,options,selectSource,effectiveDefaultTo));
        }
        return result;
    }

    /// <summary>SYSQR_USER.F_VALUE 解析（范围条件存 "from☆to"，其余存单值）。</summary>
    internal static (string? From,string? To) SplitUserConditionValue(string? raw)
    {
        if(string.IsNullOrWhiteSpace(raw))return(null,null);
        var value=raw.Trim();
        var separator=value.IndexOf('☆');
        if(separator<0)return(value.Length==0?null:value,null);
        var from=value[..separator].Trim();
        var to=value[(separator+1)..].Trim();
        return(from.Length==0?null:from,to.Length==0?null:to);
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
                   COALESCE(NULLIF(LTRIM(RTRIM(f.F_TYPE)),''),'nvarchar'),COALESCE(f.IS_COST,0),COALESCE(f.IS_SECRECY,0),
                   LTRIM(RTRIM(ISNULL(f.DISPLAY_FORMAT,'')))
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
            var displayFormat=reader.GetString(5);
            columns.Add(new ReportColumn(key,reader.GetString(1),reader.GetString(2),
                string.IsNullOrWhiteSpace(displayFormat)?null:displayFormat.Trim()));
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
