using System.Data;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using EOS.API.Errors;
using EOS.API.Models;
using Microsoft.Data.SqlClient;

using EOS.API.Telemetry;
namespace EOS.API.Data;

/// <summary>
/// 报表查看器数据仓库：
/// - 定义：MODULES + SYSQR_DEFAULT 查询条件 + FIELDS 主表可见列（成本/保密/禁止字段过滤）；
/// - 查询：条件值经白名单字段 + 参数化编译（F_TYPE 1 范围 / 2 固定单选 / 4 固定多选；
/// F_TYPE 3 数据单选含 SQL 表达式，受控解析完成前不启用）；
/// - 排序：默认主表 SORT_FIELDS（REPORT_SORT 明细级排序待后续）。
/// 所有动态标识符来自服务端元数据，值全部参数化。
/// </summary>
public sealed class ReportRepository(DbConnectionFactory connections, ILogger<ReportRepository> logger)
{
    private static readonly Regex FieldRef = new(@"^\s*(\w+)\.(\w+)\s*$", RegexOptions.Compiled);
    private static readonly Regex SelectExpression = new(@"\{([^}]+)\}=(true|false|[+-]?\d+(?:\.\d+)?|'[^']*')\s*$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex SelectSourcePattern = new(@"^\s*select\s+(\w+)\s+C_ID\s*,\s*(\w+)\s+C_VALUE\s+from\s+(\w+)\s*$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public async Task<ReportDefinition?> GetDefinitionAsync(
        int moduleId,
        string userId,
        bool canViewCost,
        bool canViewSecrecy,
        IReadOnlySet<string> deniedFields,
        string? reportId,
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
        var (effectiveReportId,sortFields)=await ReadReportSortContextAsync(connection,moduleId,reportId,token);
        // 汇总报表（RptInteg）：数据源按报表编号取自服务端注册表，既不依赖主表也不依赖库内过程。
        var aggregate=ReportAggregateRegistry.Find(effectiveReportId);
        // 汇总报表没有 FIELDS 列定义（列由注册表声明），因此允许空 MASTER_TABLE；
        // 其余报表仍要求主表为合法标识符。
        if(!ModuleRouteValidator.IsReportUrl(url)||(!WorkbenchSql.Identifier.IsMatch(masterTable)&&aggregate is null))
        {
            logger.LogWarning("报表模块校验失败 module={ModuleId} url={Url} master={Master}",moduleId,url,masterTable);
            return null;
        }

        var conditions=await ReadConditionsAsync(connection,masterTable,moduleId,userId,token);
        // F_TYPE 3 数据单选 / 5 数据源多选：校验选项源表/列物理存在（白名单）
        foreach(var condition in conditions.Where(item=>item.SelectSource is not null))
        {
            var source=condition.SelectSource!;
            if(!WorkbenchSql.Identifier.IsMatch(source.Table)||!WorkbenchSql.Identifier.IsMatch(source.IdColumn)||!WorkbenchSql.Identifier.IsMatch(source.ValueColumn))
                conditions=conditions.Select(item=>item==condition?item with{SelectSource=null}:item).ToList();
            else
            {
                var physical=await GetPhysicalColumnsAsync(connection,source.Table,token);
                if(!physical.Any(column=>column.Equals(source.IdColumn,StringComparison.OrdinalIgnoreCase))
                   ||!physical.Any(column=>column.Equals(source.ValueColumn,StringComparison.OrdinalIgnoreCase)))
                    conditions=conditions.Select(item=>item==condition?item with{SelectSource=null}:item).ToList();
            }
        }
        // 空主表 SP 报表：无 FIELDS 列定义，列由 SP 结果集动态提供（前端/PDF 侧处理）
        IReadOnlyList<ReportColumn> columns;
        IReadOnlyList<string> pkOrder;
        if(aggregate is not null)
        {
            columns=aggregate.Columns;
            pkOrder=[];
        }
        else if(string.IsNullOrWhiteSpace(masterTable))
        {
            columns=[];
            pkOrder=[];
        }
        else
        {
            (columns,pkOrder)=await ReadColumnsAsync(connection,masterTable,canViewCost,canViewSecrecy,deniedFields,token);
        }
        return new ReportDefinition(moduleId,title,masterTable,detailTable.Length>0?detailTable:null,conditions,columns,pkOrder,sortFields,
            moduleFilter.Length==0?null:moduleFilter)
        {
            Aggregate=aggregate,
        };
    }

    /// <summary>
    /// F_TYPE 3 数据单选 / 5 数据源多选选项：按白名单表/列执行静态 SELECT（无用户输入拼接）。
    /// </summary>
    public async Task<IReadOnlyList<ReportOption>> GetConditionOptionsAsync(
        ReportDefinition definition,
        int serialNo,
        CancellationToken token)
    {
        var condition=definition.Conditions.FirstOrDefault(item=>item.SerialNo==serialNo);
        if(condition?.SelectSource is not { } source)return [];
        await using var connection=connections.Create();
        await connection.OpenAsync(token);
        return await ReadSelectSourceOptionsAsync(connection,source,token);
    }

    /// <summary>数据源选项核心读取：标识符白名单 + 物理列存在校验（fail-closed）+ 静态 SELECT。</summary>
    private static async Task<IReadOnlyList<ReportOption>> ReadSelectSourceOptionsAsync(
        SqlConnection connection, ReportSelectSource source, CancellationToken token)
    {
        if(!WorkbenchSql.Identifier.IsMatch(source.Table)||!WorkbenchSql.Identifier.IsMatch(source.IdColumn)||!WorkbenchSql.Identifier.IsMatch(source.ValueColumn))return [];
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

    /// <summary>
    /// F_TYPE 5 数据源多选的合法值集合：查询时从数据源动态取，作为 IN 白名单
    /// （与 F_TYPE 4 固定多选的静态选项白名单同语义，坏值不进 SQL）。
    /// </summary>
    private async Task<IReadOnlyDictionary<int,IReadOnlySet<string>>> LoadSelectSourceValueSetsAsync(
        ReportDefinition definition, SqlConnection connection, CancellationToken token)
    {
        var result=new Dictionary<int,IReadOnlySet<string>>();
        foreach(var condition in definition.Conditions.Where(item=>item.Type==5&&item.SelectSource is not null))
        {
            var options=await ReadSelectSourceOptionsAsync(connection,condition.SelectSource!,token);
            result[condition.SerialNo]=options.Select(option=>option.Value).ToHashSet(StringComparer.Ordinal);
        }
        return result;
    }

    public async Task<ReportQueryResult> QueryAsync(
        ReportDefinition definition,
        ReportQueryRequest request,
        int page,
        int pageSize,
        string? dataFilter,
        CancellationToken token)
    {
        using var timing = DbTimingCollector.Instance.Measure();
        if(definition.Aggregate is not null)
            return await RunAggregateAsync(definition,request.Values,request.ValuesTo,definition.SortFields,token);
        page=Math.Max(1,page);
        pageSize=Math.Clamp(pageSize,10,200);
        await using var connection=connections.Create();
        await connection.OpenAsync(token);
        var physicalColumns=await GetPhysicalColumnsAsync(connection,definition.MasterTable,token);
        var detailPhysical=definition.DetailTable is null?[]:await GetPhysicalColumnsAsync(connection,definition.DetailTable,token);
        var command=new SqlCommand();
        command.Connection=connection;
        var selectSourceValues=await LoadSelectSourceValueSetsAsync(definition,connection,token);
        var allowedFields=physicalColumns.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var predicates=BuildConditionPredicates(definition,request,physicalColumns,selectSourceValues,command);
        ApplyControlledFilter(definition.ModuleFilter,definition.MasterTable,allowedFields,predicates,command,throwOnFailure:true);
        ApplyControlledFilter(dataFilter,definition.MasterTable,allowedFields,predicates,command,throwOnFailure:true);
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
        using var timing = DbTimingCollector.Instance.Measure();
        if(definition.Aggregate is not null)
            return await RunAggregateAsync(definition,request.Values,request.ValuesTo,sortFields,token,10000);
        await using var connection=connections.Create();
        await connection.OpenAsync(token);
        var physicalColumns=await GetPhysicalColumnsAsync(connection,definition.MasterTable,token);
        var detailPhysical=definition.DetailTable is null?[]:await GetPhysicalColumnsAsync(connection,definition.DetailTable,token);
        var allowedFields=physicalColumns.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var command=new SqlCommand();
        command.Connection=connection;
        var selectSourceValues=await LoadSelectSourceValueSetsAsync(definition,connection,token);
        var predicates=BuildConditionPredicates(definition,request,physicalColumns,selectSourceValues,command);
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
    /// 报表参数值转换：按注册表/参数表声明的类型解析，解析失败返回 null（调用方落 DBNull）。
    /// </summary>
    private static object? ConvertReportValue(string dataType,string raw)
    {
        var type=dataType.ToLowerInvariant();
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

    /// <summary>
    /// 系统参数型参数的兜底值：参数行缺失或值不可解析时用它。
    /// 兜底而不是留 NULL —— 比较里出现 NULL 会让整段谓词求值为 UNKNOWN，"一行都没有"
    /// 表面上与"条件就是这么严"无法区分（近效期清单最容易踩这个：阈值取不到时应当退回默认天数，
    /// 而不是安静地报空）。
    /// </summary>
    private const int DefaultSystemParameterValue = 30;

    /// <summary>
    /// 读系统参数型参数的取值。键的形态是 `<c>&lt;归属模块&gt;|&lt;参数键&gt;</c>`
    /// （与 <see cref="SystemParameterService"/> 的源码引用登记同一格式）：归属模块是取值作用域的一部分，
    /// 少了它就会在"两张表同名键"的情形下读错行（考勤的两张参数表就是这么撞的）。
    /// 形态不合法时按系统参数作用域兜底，最后再退到默认值——**绝不留 NULL**。
    /// </summary>
    private static async Task<string> ReadSystemParameterAsync(
        SqlConnection connection,
        string systemParameterKey,
        CancellationToken token)
    {
        var parts=systemParameterKey.Split('|',2);
        var owner=parts.Length==2&&int.TryParse(parts[0],NumberStyles.Integer,CultureInfo.InvariantCulture,out var parsed)
            ? parsed
            : SystemParameterService.SystemOwner;
        var key=parts[^1].Trim();
        var value=await SystemParameterService.GetIntAsync(connection,null,owner,key,token);
        return (value??DefaultSystemParameterValue).ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// 汇总报表（RptInteg）受控执行：SQL 与输出列来自服务端注册表，参数按查询条件序号绑定并
    /// 参数化传入（值不进入 SQL 文本）；排序字段必须是注册表声明过的输出列，否则回落默认排序。
    /// </summary>
    private async Task<ReportQueryResult> RunAggregateAsync(
        ReportDefinition definition,
        IReadOnlyDictionary<int,string?> values,
        IReadOnlyDictionary<int,string?> valuesTo,
        IReadOnlyList<string> sortFields,
        CancellationToken token,
        int maxRows = 1000)
    {
        var aggregate=definition.Aggregate!;
        await using var connection=connections.Create();
        await connection.OpenAsync(token);
        await using var command=new SqlCommand(BuildAggregateSql(aggregate,sortFields),connection);
        foreach(var parameter in aggregate.Parameters)
        {
            // 取值优先级：注册表常量 > 系统参数 > 前端条件值。
            // 系统参数那一档是"报表与预警读同一个阈值"的落点——它必须能吃 SYSSS 的运行期改动，
            // 否则"改了参数报表不动"就成了一张写着参数控制、实际硬编码的报表。
            var raw=parameter.Constant;
            if(raw is null && parameter.SystemParameterKey is { Length: > 0 } systemKey)
            {
                raw=await ReadSystemParameterAsync(connection,systemKey,token);
            }
            raw ??= parameter.IsTo
                ? valuesTo.GetValueOrDefault(parameter.SerialNo)
                : values.GetValueOrDefault(parameter.SerialNo);
            var value=string.IsNullOrWhiteSpace(raw)?null:ConvertReportValue(parameter.DataType,raw!);
            command.Parameters.AddWithValue($"@{parameter.Name}",value??DBNull.Value);
        }
        await using var reader=await command.ExecuteReaderAsync(token);
        var columns=new List<string>();
        for(var i=0;i<reader.FieldCount;i++)columns.Add(reader.GetName(i));
        var rows=new List<Dictionary<string,object?>>();
        while(await reader.ReadAsync(token))
        {
            if(rows.Count>=maxRows)
                throw new PdfDataTooLargeException("报表数据超过 10,000 行上限，请缩小查询条件后再打印。");
            var row=new Dictionary<string,object?>(StringComparer.OrdinalIgnoreCase);
            for(var i=0;i<reader.FieldCount;i++)row[columns[i]]=reader.IsDBNull(i)?null:reader.GetValue(i);
            rows.Add(row);
        }
        logger.LogDebug("汇总报表查询 module={ModuleId} report={ReportId} rows={RowCount}",
            definition.ModuleId,aggregate.ReportId,rows.Count);
        return new ReportQueryResult(rows,rows.Count,1,rows.Count);
    }

    /// <summary>聚合 SQL 拼接：排序字段取注册表声明列的受控白名单，全部不合法时用注册表默认排序。</summary>
    internal static string BuildAggregateSql(ReportAggregate aggregate,IReadOnlyList<string> sortFields)
    {
        var order=aggregate.OrderBy;
        var requested=sortFields
            .Select(field=>field.Contains('.')?field.Split('.')[^1]:field)
            .Where(field=>aggregate.Columns.Any(column=>column.Key.Equals(field,StringComparison.OrdinalIgnoreCase)))
            .Select(field=>$"[{field}]")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if(requested.Count>0)order=string.Join(", ",requested);
        return $"{aggregate.Sql}\nORDER BY {order};";
    }

    private static async Task<(string? ReportId,IReadOnlyList<string> SortFields)> ReadReportSortContextAsync(
        SqlConnection connection,
        int moduleId,
        string? reportId,
        CancellationToken token)
    {
        // 按报表（REPORT_ID）解析排序/分组字段；未指定时用模块默认报表
        // （一模块多报表场景，如 18019807 人事分析表下 6 张报表各自独立数据源与列）。
        // 同时回带解析出的报表编号：汇总报表的数据源按报表编号取自服务端注册表。
        // 排序方案行是**可选**的，所以这里用锚行 + LEFT JOIN 保证"至少返回一行"——
        // 没有排序方案的报表（尤其新的汇总报表）否则回带不出编号，汇总数据源会解析不出来。
        var sql = """
            DECLARE @Rid nchar(100) = (
                SELECT TOP 1 REPORT_ID FROM dbo.REPORT WITH (NOLOCK)
                WHERE R_M_IDX=@ModuleId AND (@ReportId IS NULL OR LTRIM(RTRIM(REPORT_ID))=@ReportId)
                  AND (@ReportId IS NOT NULL OR IS_DEFAULT=1)
                ORDER BY REPORT_ID);
            SELECT LTRIM(RTRIM(ISNULL(@Rid,''))),
                   LTRIM(RTRIM(ISNULL(s.SORT_FIELDS,'')))
            FROM (SELECT 1 AS ANCHOR) anchor
            LEFT JOIN dbo.REPORT_SORT s WITH (NOLOCK)
              ON s.REPORT_ID=@Rid AND LTRIM(RTRIM(ISNULL(s.SORT_FIELDS,'')))<>''
            ORDER BY s.SERIAL_NO;
            """;
        await using var command=new SqlCommand(sql,connection);
        command.Parameters.Add("@ModuleId",SqlDbType.Int).Value=moduleId;
        command.Parameters.Add("@ReportId",SqlDbType.NChar,40).Value=(object?)reportId ?? DBNull.Value;
        await using var reader=await command.ExecuteReaderAsync(token);
        string? resolved=null;
        var result=new List<string>();
        while(await reader.ReadAsync(token))
        {
            if(resolved is null)resolved=reader.GetString(0);
            var value=reader.GetString(1);
            if(string.IsNullOrWhiteSpace(value))continue;
            result.AddRange(value.Split(',',StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries)
                .Where(field=>FieldRef.IsMatch(field)||WorkbenchSql.Identifier.IsMatch(field)));
        }
        return (string.IsNullOrWhiteSpace(resolved)?null:resolved,result);
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
        if(!WorkbenchSql.Identifier.IsMatch(column))return false;
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
    /// 查询条件编译（F_TYPE 1 范围 / 2 固定单选 / 3 数据单选 / 4 固定多选 / 5 数据源多选），
    /// 字段经白名单解析，值全部参数化；与 QueryAsync / QueryPdfAsync 共用。
    /// </summary>
    private static List<string> BuildConditionPredicates(
        ReportDefinition definition,
        ReportQueryRequest request,
        IReadOnlyList<string> physicalColumns,
        IReadOnlyDictionary<int,IReadOnlySet<string>> selectSourceValues,
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
                case 5 when value is not null:
                {
                    var allowed=condition.Type==4
                        ? condition.Options.Select(option=>option.Value).ToHashSet(StringComparer.Ordinal)
                        : selectSourceValues.GetValueOrDefault(condition.SerialNo)??new HashSet<string>(StringComparer.Ordinal);
                    var chosen=FilterMultiSelectValues(value,allowed);
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
        }
        return predicates;
    }

    /// <summary>
    /// 多选条件值（F_TYPE 4 固定多选 / 5 数据源多选）：逗号分隔实际值，
    /// 过滤合法集合（坏值丢弃，fail-closed）并去重保序。
    /// </summary>
    internal static IReadOnlyList<string> FilterMultiSelectValues(string? raw, IReadOnlySet<string> allowed)
    {
        if(raw is null)return [];
        return raw.Split(',',StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries)
            .Where(allowed.Contains)
            .Distinct()
            .ToList();
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
    /// 受控过滤应用：模块 FILTER / REPORT_FILTER / 报表级 DATA_FILTER
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
                   LTRIM(RTRIM(ISNULL(u.F_VALUE,''))),LTRIM(RTRIM(ISNULL(d.FILTER_TEMPLATE,'')))
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
            var filterTemplate=reader.GetString(8);
            var (userFrom,userTo)=SplitUserConditionValue(userValue);
            var effectiveDefault=string.IsNullOrWhiteSpace(userFrom)?defaultValue:userFrom;
            var effectiveDefaultTo=userTo;

            // FILTER_TEMPLATE 优先：结构化解析，未转换行回退 DSL 语法
            var templateParsed=ConditionTemplateParser.TryParse(filterTemplate,effectiveDefault,effectiveDefaultTo);
            if(templateParsed is not null)
            {
                result.Add(new ReportCondition(
                    serial,
                    templateParsed.Field ?? (field.Length>0?field:null),
                    desc,
                    templateParsed.Type,
                    expression.Length>0?expression:null,
                    string.IsNullOrWhiteSpace(templateParsed.DefaultValue)?null:templateParsed.DefaultValue,
                    templateParsed.ParameterName ?? (parameterName.Length>0?parameterName:null),
                    templateParsed.Options,
                    templateParsed.SelectSource,
                    string.IsNullOrWhiteSpace(templateParsed.DefaultValueTo)?null:templateParsed.DefaultValueTo));
                continue;
            }

            var options=ParseOptions(type,expression);
            ReportSelectSource? selectSource=null;
            if(type is 3 or 5)
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
              AND EXISTS (SELECT 1 FROM sys.columns c JOIN sys.objects o ON c.object_id=o.object_id AND o.type IN ('U','V') JOIN sys.schemas s ON o.schema_id=s.schema_id WHERE s.name=N'dbo' AND o.name=@Table AND c.name=f.F_ID)
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
        var pkOrder=await WorkbenchSql.GetPrimaryKeyColumnsAsync(connection,null,masterTable,token);
        return (columns,pkOrder);
    }

    private static async Task<IReadOnlyList<string>> GetPhysicalColumnsAsync(SqlConnection connection,string table,CancellationToken token)
    {
        const string sql="""
            SELECT c.name AS COLUMN_NAME FROM sys.columns c
            JOIN sys.objects o ON c.object_id=o.object_id AND o.type IN ('U','V')
            JOIN sys.schemas s ON o.schema_id=s.schema_id
            WHERE s.name=N'dbo' AND o.name=@Table;
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
        if(!WorkbenchSql.Identifier.IsMatch(field))return false;
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