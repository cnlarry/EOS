using System.Data;
using EOS.API.Data.Query;
using EOS.API.Models;
using Microsoft.Data.SqlClient;

using EOS.API.Telemetry;
namespace EOS.API.Data;

/// <summary>
/// 通用查询中心：
/// 检索面来自 MODULES.SEARCH_1/SEARCH_2（标记为可搜主表 / 副表的业务模块）——
/// 按模块提供可搜索表，字段 + 值（或关键字）受控查询，结果元数据网格。
/// 动态标识符全部来自服务端元数据（FIELDS + sys.columns），值参数化。
///
/// 可见性与统一工作台**同一真源**：
/// - 模块清单 = 检索位 ∩ 当前用户可浏览模块（判定复用 <see cref="NavigationRepository"/>，
///   不在这里另写一套权限 SQL）；
/// - 行范围由 <see cref="WorkbenchScopeFilter"/> 落地（模块 FILTER + DATA_FILTER + EXEC_TAG），
///   明细表经主表关联键推导——用户在查询中心看到的行，不多于他在该模块列表里能看到的。
/// </summary>
public sealed class SearchCenterRepository(
    DbConnectionFactory connections,
    NavigationRepository navigation,
    WorkbenchScopeFilter scopeFilter,
    ILogger<SearchCenterRepository> logger)
{

    public async Task<IReadOnlyList<SearchableModule>> GetModulesAsync(string userId, CancellationToken token)
    {
        await using var connection=connections.Create();
        await connection.OpenAsync(token);
        // MASTER_TABLE 在库内可空：直接 GetString 读 NULL 列会抛 SqlNullValueException，
        // 把"启用了通用查询但没配主表"的一个模块放大成整个模块清单 500——必须兜住 NULL。
        // （菜单保存已保证"无主表则 SEARCH_1/2 落 0"，这里兜的是直写库/历史数据。）
        const string sql="""
            SELECT M_IDX,LTRIM(RTRIM(M_DESC)),LTRIM(RTRIM(ISNULL(MASTER_TABLE,''))),LTRIM(RTRIM(ISNULL(DETAIL_TABLE,''))),
                   COALESCE(SEARCH_1,0),COALESCE(SEARCH_2,0)
            FROM dbo.MODULES WITH (NOLOCK)
            WHERE COALESCE(SEARCH_1,0)=1 OR COALESCE(SEARCH_2,0)=1
            ORDER BY M_IDX;
            """;
        await using var command=new SqlCommand(sql,connection);
        var result=new List<SearchableModule>();
        await using(var reader=await command.ExecuteReaderAsync(token))
        {
            while(await reader.ReadAsync(token))
                result.Add(new SearchableModule(reader.GetInt32(0),reader.GetString(1),reader.GetString(2),
                    reader.GetString(3).Length>0?reader.GetString(3):null,
                    reader.GetInt32(4)==1,reader.GetInt32(5)==1));
        }
        // 检索位只说明"这个模块可被检索"，能不能看还要过模块权限：同一用户在别处的可见性，
        // 不能因为在查询中心换了入口就放宽。
        var browsable=(await navigation.GetForUserAsync(userId,token)).Select(module=>module.Id).ToHashSet();
        return result.Where(module=>browsable.Contains(module.ModuleId)).ToList();
    }

    public async Task<SearchDefinition?> GetDefinitionAsync(
        WorkbenchDefinition workbench,
        string table,
        bool canViewCost,
        bool canViewSecrecy,
        IReadOnlySet<string> deniedFields,
        CancellationToken token)
    {
        if(!WorkbenchSql.Identifier.IsMatch(table))return null;
        await using var connection=connections.Create();
        await connection.OpenAsync(token);
        // 检索位是"这个模块准不准被检索"的唯一开关：调用方绕过 /modules 直接 POST 也要校验。
        const string moduleSql="""
            SELECT COALESCE(SEARCH_1,0),COALESCE(SEARCH_2,0)
            FROM dbo.MODULES WITH (NOLOCK) WHERE M_IDX=@ModuleId;
            """;
        await using var moduleCommand=new SqlCommand(moduleSql,connection);
        moduleCommand.Parameters.Add("@ModuleId",SqlDbType.Int).Value=workbench.ModuleId;
        var search1=false;
        var search2=false;
        await using(var reader=await moduleCommand.ExecuteReaderAsync(token))
        {
            if(!await reader.ReadAsync(token))return null;
            search1=reader.GetInt32(0)==1;
            search2=reader.GetInt32(1)==1;
        }
        var isMaster=table.Equals(workbench.MasterTable,StringComparison.OrdinalIgnoreCase);
        var isDetail=workbench.DetailTable is { Length: > 0 } detailTable
            && table.Equals(detailTable,StringComparison.OrdinalIgnoreCase);
        if(!isMaster&&!isDetail)return null;
        if(isMaster&&!search1)return null;
        if(isDetail&&!search2)return null;
        // 显示列**直接用工作台装配好的字段**：顺序、个人列宽（`SYS_USER_COLUMN.F_WIDTH`）、
        // 对齐、显示格式都在 builder 里叠加过，查询中心不再自己读 FIELDS 拼一套。
        // 可搜字段仍用 FIELDS 全集（含被隐藏的列），否则「结果列」里没法把它们加回来。
        var metas=(await ReadFieldsAsync(connection,table,canViewCost,canViewSecrecy,deniedFields,token))
            .ToDictionary(field=>field.Key,StringComparer.OrdinalIgnoreCase);
        var display=(isMaster?workbench.MasterFields:workbench.DetailFields)
            .Where(field=>!field.IsVirtual&&metas.ContainsKey(field.Key))
            .Select(field=>new SearchField(field.Key,field.Label,field.DataType,
                field.Width,field.Align,field.HeaderAlign,field.Format,
                metas[field.Key].NumericRole))
            .ToList();
        var pkOrder=isMaster
            ?workbench.MasterPkOrder
            :await WorkbenchSql.GetPrimaryKeyColumnsAsync(connection,null,table,token);
        return new SearchDefinition(workbench.ModuleId,workbench.Title,table,
            metas.Values.ToList(),display,pkOrder,workbench.MasterPkOrder);
    }

    public async Task<SearchQueryResult> QueryAsync(
        SearchDefinition definition,
        WorkbenchDefinition workbench,
        string? dataFilter,
        SearchQueryRequest request,
        int page,
        int pageSize,
        CancellationToken token)
    {
        using var timing = DbTimingCollector.Instance.Measure();
        page=Math.Max(1,page);
        pageSize=PageWindow.NormalizePageSize(pageSize);
        await using var connection=connections.Create();
        await connection.OpenAsync(token);
        var predicates=new List<string>();
        var command=new SqlCommand();
        command.Connection=connection;
        // 行范围与统一工作台同源：查主表走 ApplyScope，查明细走 ApplyDetailScope（主表关联键 EXISTS）。
        // 先加范围再加用户条件，保证范围谓词不会被调用方参数名或空条件挤掉。
        var isDetail=!string.Equals(definition.Table,workbench.MasterTable,StringComparison.OrdinalIgnoreCase);
        // 条件/关键字/排序的字段白名单与统一工作台同一份：明细查明细字段，主表查主表字段。
        var tableFields=isDetail?workbench.DetailFields:workbench.MasterFields;
        if(isDetail)
        {
            var keyColumns=await WorkbenchSql.ResolveDetailKeyColumnsAsync(connection,workbench,definition.Table,token);
            scopeFilter.ApplyDetailScope(workbench,dataFilter,definition.Table,keyColumns,predicates,command);
        }
        else
        {
            scopeFilter.ApplyScope(workbench,dataFilter,null,null,predicates,command);
        }
        if(request.Conditions is { Count: > 0 })
        {
            // 条件解析复用工作台的那一份（字段白名单 / 虚拟字段 / 可查询性 / 算子统一），不另写。
            WorkbenchQueryComposer.AddQueryPredicates(new WorkbenchQuery(request.Conditions),tableFields,predicates,command);
        }
        if(!string.IsNullOrWhiteSpace(request.Keyword))
        {
            WorkbenchQueryComposer.AddKeywordPredicates(request.Keyword,tableFields,predicates,command);
        }
        var where=predicates.Count>0?" WHERE "+string.Join(" AND ",predicates):"";
        var selected=definition.Columns.Take(40).Select(column=>column.Key).ToList();
        if(isDetail)
        {
            // 明细结果要能定位回所属单据：把主表主键列并入投影（仅当明细表物理存在该列）。
            var physical=await WorkbenchSql.GetPhysicalColumnsAsync(connection,null,definition.Table,token);
            foreach(var key in definition.MasterPkOrder)
            {
                if(physical.Contains(key)&&!selected.Contains(key,StringComparer.OrdinalIgnoreCase))
                {
                    selected.Add(key);
                }
            }
        }
        if(selected.Count==0)return new([],0,page,pageSize);
        var order=ResolveOrder(definition,workbench,tableFields,isDetail,request);
        command.CommandText=$"""
            SELECT COUNT_BIG(1) FROM dbo.[{definition.Table}] WITH (NOLOCK){where};
            SELECT {string.Join(',',selected.Select(column=>$"[{column}]"))} FROM dbo.[{definition.Table}] WITH (NOLOCK){where}
            ORDER BY {order} OFFSET @Offset ROWS FETCH NEXT @PageSize ROWS ONLY;
            """;
        command.Parameters.Add("@Offset",SqlDbType.Int).Value=PageWindow.ResolveOffset(page,pageSize);
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

    /// <summary>
    /// 排序：优先用调用方给的字段（经工作台同一份白名单与去重校验）；
    /// 字段无效或该表没有可用主键时回退到当前表主键，保证永远有确定顺序（否则分页会跳行）。
    /// </summary>
    private static string ResolveOrder(
        SearchDefinition definition,
        WorkbenchDefinition workbench,
        IReadOnlyList<WorkbenchField> tableFields,
        bool isDetail,
        SearchQueryRequest request)
    {
        try
        {
            var order=WorkbenchQueryComposer.ResolveOrder(workbench,tableFields,tableFields,isDetail,
                request.SortFields,request.SortDirections);
            if(!string.IsNullOrWhiteSpace(order))return order;
        }
        catch(ArgumentException)
        {
            // 排序字段不在白名单里：按"没有指定排序"处理，不把整个查询打成失败。
        }
        if(definition.PkOrder.Count>0)
        {
            return string.Join(',',definition.PkOrder.Select(pk=>$"[{pk}]"));
        }
        return string.Join(',',definition.Columns.Take(1).Select(column=>$"[{column.Key}]"));
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
                   COALESCE(f.IS_VISIBLE,1),LTRIM(RTRIM(ISNULL(f.DISPLAY_FORMAT,''))),LTRIM(RTRIM(ISNULL(f.NUMERIC_ROLE,''))),
                   COALESCE(f.DISPLAY_LENGTH,0),LTRIM(RTRIM(ISNULL(f.ITEM_ALIGN,''))),LTRIM(RTRIM(ISNULL(f.HEADER_ALIGN,'')))
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
            var numericRole=reader.GetString(7);
            var align=reader.GetString(9);
            var headerAlign=reader.GetString(10);
            result.Add(new SearchField(key,reader.GetString(1),reader.GetString(2),
                reader.GetInt32(8),
                string.IsNullOrWhiteSpace(align)?null:align.Trim(),
                string.IsNullOrWhiteSpace(headerAlign)?null:headerAlign.Trim(),
                string.IsNullOrWhiteSpace(displayFormat)?null:displayFormat.Trim(),
                string.IsNullOrWhiteSpace(numericRole)?null:numericRole.Trim()));
        }
        return result;
    }

}
