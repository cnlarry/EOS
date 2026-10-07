using System.Data;
using EOS.API.Models;
using EOS.API.Telemetry;
using Microsoft.Data.SqlClient;

namespace EOS.API.Data;

/// <summary>
/// Resolves chooser data sources and option rows for form field pickers. Applies data
/// scope and structured filter conditions, resolves virtual return columns through the
/// controlled resolver, and reads paged rows with whitelisted sorting.
/// </summary>
public sealed class WorkbenchChooserService(
    DbConnectionFactory connections,
    WorkbenchScopeFilter scopeFilter,
    ILogger<WorkbenchChooserService> logger)
{




    /// <summary>
    /// 选择器数据源：表名与来源定义来自服务端 FIELD_DATASOURCE（客户端仅传字段 key + serialNo），
    /// 显示列按权限过滤（成本/保密/禁止查看），DATA_FILTER 受限解析可应用时应用，
    /// FILTER_STRUCT 经受控编译器参数化；任一无法安全编译即返回空列表（不泄漏数据，fail-closed）。
    /// </summary>
    public async Task<FormChooserResult?> GetChooserOptionsAsync(
        string table,
        string? keyword,
        string? filterField,
        IReadOnlyList<ChooserReturnItem>? returnItems,
        IReadOnlyDictionary<string, string>? masterValues,
        IReadOnlyDictionary<string, string>? detailValues,
        IReadOnlyList<UnifiedChooserCondition>? conditions,
        bool canViewCost,
        bool canViewSecrecy,
        IReadOnlySet<string> deniedFields,
        string? dataFilter,
        ChooserFilterStruct? filterStruct,
        string? sortField,
        string? sortDirection,
        int page,
        int pageSize,
        string? execTag,
        int? scopeModuleId,
        int formModuleId,
        string userId,
        CancellationToken token)
    {
        using var timing = DbTimingCollector.Instance.Measure();
        if(!WorkbenchSql.Identifier.IsMatch(table))return null;
        await using var connection=connections.Create(); await connection.OpenAsync(token);
        var all=await ReadChooserColumnRows(connection,table,token);
        if(all.Count==0)return null;
        var allowedFields=all.Select(row=>row.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var columnTypes=all.ToDictionary(row=>row.Key,row=>row.DataType,StringComparer.OrdinalIgnoreCase);
        logger.LogDebug("选择器过滤 table={Table} filter={Filter}", table, filterStruct is null ? null : filterStruct.ToJson());
        // Data scope: module FILTER (only when source table matches module master table)
        // + DATA_FILTER + EXEC_TAG, combined with the chooser's own FILTER_STRUCT.
        // If any component cannot be safely compiled, returns empty (fail-closed).
        string? moduleFilter = null;
        string? moduleMasterTable = null;
        if (scopeModuleId is int scopeId)
        {
            const string moduleSql = "SELECT LTRIM(RTRIM(ISNULL(FILTER,''))),LTRIM(RTRIM(ISNULL(MASTER_TABLE,''))) FROM dbo.MODULES WITH (NOLOCK) WHERE M_IDX=@ModuleId;";
            await using var moduleCommand = new SqlCommand(moduleSql, connection);
            moduleCommand.Parameters.Add("@ModuleId", SqlDbType.Int).Value = scopeId;
            await using var moduleReader = await moduleCommand.ExecuteReaderAsync(token);
            if (await moduleReader.ReadAsync(token))
            {
                moduleFilter = moduleReader.GetString(0);
                moduleMasterTable = moduleReader.GetString(1);
            }
        }
        if (!scopeFilter.TryBuildChooserScopePredicate(table, moduleFilter, moduleMasterTable, dataFilter, execTag,
                allowedFields, out var basePredicate, out var baseParameters))
        {
            logger.LogWarning("选择器数据范围无法构建（fail-closed）table={Table} module={Module}", table, scopeModuleId);
            return new FormChooserResult([], [], 0);
        }
        var scopeParameters = new List<object>(baseParameters);
        string? scopePredicate = string.IsNullOrWhiteSpace(basePredicate) ? null : basePredicate;
        var joins = new List<string>();
        // 过滤条件 JOIN 段实际拼进去的别名：虚拟列解析随后拼同一个 FROM，重复别名会让 SQL 报错
        IReadOnlySet<string> filterJoinAliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (filterStruct is null)
        {
            // FILTER_STRUCT=NULL means the condition is pending migration; fail-closed empty,
            // so "unconditional" and "pending rebuild" are not conflated
            logger.LogWarning("选择器过滤条件待重建（FILTER_STRUCT 为空）table={Table}", table);
            return new FormChooserResult([], [], 0);
        }
        if (filterStruct is not null && filterStruct.Items.Count > 0)
        {
            // Cross-table JOIN whitelist from TABLES.QUERY_RELATION (controlled resolution, in-process cache)
            var catalog = await ChooserJoinCatalog.GetAsync(connection, table, token);
            if (catalog.Error is not null)
            {
                logger.LogWarning("选择器跨表目录解析失败（fail-closed）table={Table} error={Error}", table, catalog.Error);
                return new FormChooserResult([], [], 0);
            }
            var compiled = ChooserFilterCompiler.Compile(filterStruct, table, catalog.Aliases, columnTypes);
            if (compiled is null)
            {
                logger.LogWarning("选择器过滤无法编译 table={Table} filter={Filter}", table, filterStruct.ToJson());
                return new FormChooserResult([], [], 0);
            }
            // 裸字段必须在源表可解析字段集内（跨表字段走 JOIN 白名单 + 物理列校验）
            var bareFields = new List<string>();
            CollectBareFields(filterStruct, bareFields);
            foreach (var field in bareFields)
            {
                if (!allowedFields.Contains(field))
                {
                    logger.LogWarning("选择器过滤字段不在白名单 table={Table} field={Field}", table, field);
                    return new FormChooserResult([], [], 0);
                }
            }
            if (!await ValidateChooserForeignColumnsAsync(connection, catalog, compiled.ForeignColumns, token))
            {
                logger.LogWarning("选择器 JOIN 校验失败 table={Table} filter={Filter}", table, filterStruct.ToJson());
                return new FormChooserResult([], [], 0);
            }
            // 运行期模板绑定（{m.X}/{d.X}/{module}）→ SqlParameter；@cfN 重编号为 @df{offset+N} 与作用域参数连续
            var boundParameters = compiled.Parameters
                .Select(parameter => (object)ChooserFilterCompiler.BindRuntimeValue(parameter, masterValues, detailValues, formModuleId))
                .ToList();
            var renumbered = RenumberChooserParameters(compiled.Predicate, scopeParameters.Count);
            scopePredicate = scopePredicate is null
                ? renumbered
                : $"({scopePredicate}) AND ({renumbered})";
            scopeParameters.AddRange(boundParameters);
            if (compiled.Joins.Count > 0)
            {
                var joinClause = ChooserJoinCatalog.BuildJoinClause(catalog, compiled.Joins, out filterJoinAliases);
                if (joinClause is null)
                {
                    logger.LogWarning("选择器 JOIN 重建失败（fail-closed）table={Table}", table);
                    return new FormChooserResult([], [], 0);
                }
                joins.Add(joinClause);
            }
        }
        // 显示列：回填映射列优先（保证主键/名称可见），其余按 SYSQL_DEFAULT 顺序，
        // 保留审计/状态列
        var preferred = (returnItems ?? [])
            .Select(pair => pair.Column.Trim())
            .Where(column => all.Any(row => row.Key.Equals(column,StringComparison.OrdinalIgnoreCase)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var selected=ChooserColumnSelector.Select(all,canViewCost,canViewSecrecy,deniedFields,max: all.Count)
            .ToList();
        var orderIndex = selected.ToDictionary(column=>column.Key,column=>all.First(row=>row.Key.Equals(column.Key,StringComparison.OrdinalIgnoreCase)).OrderIndex,StringComparer.OrdinalIgnoreCase);
        var columns=preferred
            .Select(key=>selected.FirstOrDefault(column=>column.Key.Equals(key,StringComparison.OrdinalIgnoreCase)))
            .OfType<FormChooserColumn>()
            .Concat(selected
                .Where(column=>!preferred.Any(key=>key.Equals(column.Key,StringComparison.OrdinalIgnoreCase)))
                .OrderBy(column=>orderIndex[column.Key]))
            .Take(30)
            .ToList();
        if(columns.Count==0)return new FormChooserResult([],[],0);
        // 过滤字段白名单校验：不在显示列内则忽略（回退为跨列模糊搜索）
        if(!string.IsNullOrWhiteSpace(filterField) && !columns.Any(column=>column.Key.Equals(filterField,StringComparison.OrdinalIgnoreCase)))
            filterField=null;
        // Virtual columns referenced by the return mapping (e.g. CLIENT.SALES_NAME=SYSDN.EMP_NAME)
        // are resolved through the controlled VirtualColumnResolver (QUERY_RELATION whitelist JOIN),
        // so selecting a row also brings back display names, not just the code.
        string? virtualSelect=null;
        string? virtualJoin=null;
        var returnColumns=(returnItems ?? [])
            .Select(pair=>pair.Column.Trim())
            .Where(column=>WorkbenchSql.Identifier.IsMatch(column))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var missingColumns=returnColumns
            .Where(column=>!all.Any(row=>row.Key.Equals(column,StringComparison.OrdinalIgnoreCase)))
            .ToList();
        var resolvedVirtualKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if(missingColumns.Count>0)
        {
            var virtualFields=await ReadChooserVirtualFieldsAsync(connection,table,missingColumns,token);
            if(virtualFields.Count>0)
            {
                var resolution=await new VirtualColumnResolver(connection).ResolveAsync(table,virtualFields,token,alreadyJoined:filterJoinAliases);
                if(resolution.ResolvedKeys.Count>0)
                {
                    virtualSelect=string.Join(",",resolution.SelectFragments);
                    virtualJoin=resolution.JoinFragment;
                    foreach(var field in virtualFields)
                    {
                        if(!resolution.ResolvedKeys.Contains(field.Key,StringComparer.OrdinalIgnoreCase))continue;
                        // 虚拟列仅作为返回给前端的显示列（经 virtualSelect 别名取数），
                        // 不能进入基表物理 SELECT（基表无此列，拼入会导致 Invalid column name）
                        columns.Add(new FormChooserColumn(field.Key,field.Label,field.DataType,null));
                        resolvedVirtualKeys.Add(field.Key);
                    }
                }
            }
        }
        // 物理 SELECT 列 = 显示列剔除虚拟列（虚拟列片段已由 virtualSelect 携带）
        var physicalColumns=resolvedVirtualKeys.Count>0
            ? columns.Where(column=>!resolvedVirtualKeys.Contains(column.Key)).ToList()
            : columns;
        var (rows,total)=await ReadChooserRowsAsync(connection,table,physicalColumns,keyword,filterField,scopePredicate,scopeParameters,joins,conditions,allowedFields,sortField,sortDirection,page,pageSize,virtualSelect,virtualJoin,resolvedVirtualKeys,token);
        return new FormChooserResult(columns,rows,total);
    }

    /// <summary>
    /// form-chooser 端点权威数据源解析：按字段 + serialNo 从 FIELD_DATASOURCE 读取
    /// FILTER_STRUCT / RETURN_ITEMS（仅服务端持有；普通用户表单定义不下发过滤条件）。
    /// serialNo 为空时取首个启用来源（向后兼容单来源调用）。
    /// </summary>
    public async Task<FieldChooserSource?> GetChooserSourceAsync(
        string masterTable,
        string? detailTable,
        string fieldKey,
        int? serialNo,
        CancellationToken token)
    {
        const string sql = """
            SELECT c.SERIAL_NO,CAST(COALESCE(c.ACTIVE_TAG,0) AS bit) AS ACTIVE_TAG,
                   LTRIM(RTRIM(ISNULL(c.SOURCE_T_ID,''))) AS SOURCE_T_ID,
                   LTRIM(RTRIM(ISNULL(c.SOURCE_DESC,''))) AS SOURCE_DESC,c.SOURCE_M_IDX,
                   c.RETURN_ITEMS,c.FILTER_STRUCT
            FROM dbo.FIELD_DATASOURCE c WITH (NOLOCK)
            WHERE LTRIM(RTRIM(c.F_ID))=@FieldKey
              AND c.T_ID IN (SELECT value FROM STRING_SPLIT(@Tables, N','))
            ORDER BY c.SERIAL_NO;
            """;
        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@FieldKey", SqlDbType.NVarChar, 100).Value = fieldKey.Trim();
        var tables = detailTable is { Length: > 0 }
            ? $"{masterTable},{detailTable}"
            : masterTable;
        command.Parameters.Add("@Tables", SqlDbType.NVarChar, 220).Value = tables;
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            var active = reader.GetBoolean(reader.GetOrdinal("ACTIVE_TAG"));
            var table = reader.GetString(reader.GetOrdinal("SOURCE_T_ID")).Trim();
            var currentSerial = reader.GetInt32(reader.GetOrdinal("SERIAL_NO"));
            if (serialNo is int requested && requested != currentSerial)
            {
                continue;
            }
            // 未启用来源一律不下发（无论是否指定 serialNo，防绕过 ACTIVE_TAG 门）
            if (table.Length == 0 || !active)
            {
                continue;
            }
            return new FieldChooserSource(
                active,
                table,
                reader.GetString(reader.GetOrdinal("SOURCE_DESC")),
                reader.IsDBNull(reader.GetOrdinal("SOURCE_M_IDX")) ? null : reader.GetInt32(reader.GetOrdinal("SOURCE_M_IDX")),
                reader.IsDBNull(reader.GetOrdinal("FILTER_STRUCT")) ? null : reader.GetString(reader.GetOrdinal("FILTER_STRUCT")),
                reader.IsDBNull(reader.GetOrdinal("RETURN_ITEMS")) ? null : reader.GetString(reader.GetOrdinal("RETURN_ITEMS")),
                currentSerial);
        }
        return null;
    }

    /// <summary>
    /// 校验选择器跨表引用列物理存在：引用别名经 catalog 映射到物理表后查 sys.columns，否则拒绝。
    /// 源表自身的裸字段由 allowedFields（FIELDS 注册集）覆盖，不在此处。
    /// </summary>
    private static async Task<bool> ValidateChooserForeignColumnsAsync(
        SqlConnection connection,
        ChooserSourceJoins catalog,
        IReadOnlyList<(string Table,string Column)> foreignColumns,
        CancellationToken token)
    {
        if (foreignColumns.Count == 0)
        {
            return true;
        }
        var aliasToTable = catalog.Joins
            .GroupBy(join => join.Alias, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().Table, StringComparer.OrdinalIgnoreCase);
        var checks = new List<(string Table, string Column)>();
        foreach (var (alias, column) in foreignColumns)
        {
            if (alias.Equals(catalog.SourceTable, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if (!aliasToTable.TryGetValue(alias, out var physicalTable))
            {
                return false;
            }
            checks.Add((physicalTable, column));
        }
        foreach (var group in checks.GroupBy(item => item.Table, StringComparer.OrdinalIgnoreCase))
        {
            var columns = group.Select(item => item.Column).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (!await WorkbenchSql.ColumnsExistAsync(connection, group.Key, columns, token))
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>递归收集主查询裸字段（无表前缀的列引用），供源表 FIELDS 白名单校验。</summary>
    private static void CollectBareFields(ChooserFilterStruct filter, ICollection<string> fields)
    {
        foreach (var item in filter.Items)
        {
            if (item.Group is not null)
            {
                CollectBareFields(item.Group, fields);
            }
            if (item.Subquery?.Filter is { Count: > 0 })
            {
                foreach (var subItem in item.Subquery.Filter)
                {
                    CollectBareFields(new ChooserFilterStruct("AND", [subItem]), fields);
                }
            }
            if (!string.IsNullOrWhiteSpace(item.Field) && !item.Field.Contains('.', StringComparison.Ordinal))
            {
                fields.Add(item.Field.Trim());
            }
            CollectBareExpression(item.Left, fields);
            CollectBareExpression(item.Right, fields);
        }
    }

    private static void CollectBareExpression(ChooserFilterExpression? expr, ICollection<string> fields)
    {
        if (expr is null)
        {
            return;
        }
        if (expr.Kind.Equals("column", StringComparison.OrdinalIgnoreCase)
            && string.IsNullOrWhiteSpace(expr.Table)
            && !string.IsNullOrWhiteSpace(expr.Column)
            && !expr.Column.Contains('.', StringComparison.Ordinal))
        {
            fields.Add(expr.Column.Trim());
        }
        CollectBareExpression(expr.Left, fields);
        CollectBareExpression(expr.Right, fields);
    }

    /// <summary>把编译器输出参数 @cfN 重编号为 @df{offset+N}，与作用域参数（@df0..offset-1）连续绑定。</summary>
    private static string RenumberChooserParameters(string predicate, int offset)
    {
        return System.Text.RegularExpressions.Regex.Replace(
            predicate,
            "@cf(\\d+)",
            match => $"@df{offset + int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)}");
    }

    private static async Task<IReadOnlyList<FormChooserColumnRow>> ReadChooserColumnRows(SqlConnection connection,string table,CancellationToken token)
    {
        const string sql="""
            SELECT LTRIM(RTRIM(f.F_ID)),COALESCE(NULLIF(LTRIM(RTRIM(f.F_DESC)),''),LTRIM(RTRIM(f.F_ID))),
                   COALESCE(NULLIF(LTRIM(RTRIM(f.F_TYPE)),''),'nvarchar'),CAST(COALESCE(f.IS_COST,0) AS bit),CAST(COALESCE(f.IS_SECRECY,0) AS bit),CAST(COALESCE(f.IS_VISIBLE,1) AS bit) AS IS_VISIBLE,
                   CAST(COALESCE(d.F_IDX,COALESCE(f.VERIFY_INDEX,999)) AS int) AS ORDER_IDX,
                   f.DISPLAY_FORMAT
            FROM dbo.FIELDS f WITH (NOLOCK)
            LEFT JOIN (SELECT T_ID,T_ID_R,LTRIM(RTRIM(F_ID)) AS F_ID,MIN(F_IDX) AS F_IDX
                       FROM dbo.SYSQL_DEFAULT WITH (NOLOCK)
                       GROUP BY T_ID,T_ID_R,LTRIM(RTRIM(F_ID))) d
              ON d.T_ID=@Table AND d.T_ID_R=@Table AND d.F_ID=LTRIM(RTRIM(f.F_ID))
            WHERE f.T_ID=@Table AND COALESCE(f.IS_VIRTUAL,0)=0
              AND EXISTS (SELECT 1 FROM sys.columns c JOIN sys.objects o ON c.object_id=o.object_id AND o.type IN ('U','V') JOIN sys.schemas s ON o.schema_id=s.schema_id WHERE s.name=N'dbo' AND o.name=@Table AND c.name=f.F_ID)
            ORDER BY CASE WHEN d.F_IDX IS NULL THEN 1 ELSE 0 END,COALESCE(d.F_IDX,COALESCE(f.VERIFY_INDEX,999)),f.F_ID;
            """;
        await using var command=new SqlCommand(sql,connection);command.Parameters.Add("@Table",SqlDbType.NVarChar,100).Value=table;
        await using var reader=await command.ExecuteReaderAsync(token);
        var rows=new List<FormChooserColumnRow>();
        while(await reader.ReadAsync(token))rows.Add(new(reader.GetString(0),reader.GetString(1),reader.GetString(2),reader.GetBoolean(3),reader.GetBoolean(4),reader.GetInt32(reader.GetOrdinal("ORDER_IDX")),reader.GetBoolean(reader.GetOrdinal("IS_VISIBLE")),reader.IsDBNull(reader.GetOrdinal("DISPLAY_FORMAT"))?null:reader.GetString(reader.GetOrdinal("DISPLAY_FORMAT"))));
        return rows;
    }

    /// <summary>读取回填映射引用的虚拟列定义（FIELDS.IS_VIRTUAL=1 + VIRTUAL_EXP），供 VirtualColumnResolver 解析。</summary>
    private static async Task<IReadOnlyList<WorkbenchField>> ReadChooserVirtualFieldsAsync(
        SqlConnection connection,string table,IReadOnlyList<string> columns,CancellationToken token)
    {
        if(columns.Count==0)return [];
        var placeholders=string.Join(",",columns.Select((_,i)=>$"@C{i}"));
        var sql=$"""
            SELECT LTRIM(RTRIM(f.F_ID)),COALESCE(NULLIF(LTRIM(RTRIM(f.F_DESC)),''),LTRIM(RTRIM(f.F_ID))),
                   COALESCE(NULLIF(LTRIM(RTRIM(f.F_TYPE)),''),'nvarchar'),LTRIM(RTRIM(ISNULL(f.VIRTUAL_EXP,'')))
            FROM dbo.FIELDS f WITH (NOLOCK)
            WHERE f.T_ID=@Table AND COALESCE(f.IS_VIRTUAL,0)=1 AND f.F_ID IN ({placeholders});
            """;
        await using var command=new SqlCommand(sql,connection);
        command.Parameters.Add("@Table",SqlDbType.NVarChar,128).Value=table;
        for(var i=0;i<columns.Count;i++)command.Parameters.Add($"@C{i}",SqlDbType.NVarChar,128).Value=columns[i];
        var result=new List<WorkbenchField>();
        await using var reader=await command.ExecuteReaderAsync(token);
        while(await reader.ReadAsync(token))
        {
            var key=reader.GetString(0).Trim();
            var label=reader.GetString(1).Trim();
            var dataType=reader.GetString(2).Trim();
            var virtualExp=reader.IsDBNull(3)?null:reader.GetString(3).Trim();
            if(string.IsNullOrWhiteSpace(virtualExp))continue;
            result.Add(new WorkbenchField(key,label,dataType,100,null,false,IsVirtual:true,VirtualExpression:virtualExp));
        }
        return result;
    }

    private static async Task<(IReadOnlyList<IReadOnlyDictionary<string,object?>> Rows,int Total)> ReadChooserRowsAsync(
        SqlConnection connection,string table,IReadOnlyList<FormChooserColumn> columns,string? keyword,string? filterField,string? scopePredicate,IReadOnlyList<object> scopeParameters,IReadOnlyList<string> joins,IReadOnlyList<UnifiedChooserCondition>? conditions,IReadOnlySet<string> allowedFields,string? sortField,string? sortDirection,int page,int pageSize,string? virtualSelect,string? virtualJoin,IReadOnlySet<string> virtualKeys,CancellationToken token)
    {
        var select=string.Join(',',columns.Select(column=>$"[{table}].[{column.Key}]"));
        if(!string.IsNullOrWhiteSpace(virtualSelect))select+=","+virtualSelect;
        var predicates=new List<string>();
        if(!string.IsNullOrWhiteSpace(keyword))
        {
            // 关键字里的 % _ [ 是 LIKE 通配符：按 ESCAPE 规则转义，否则用户搜 "50%" 会命中一切
            // 指定字段 → 单列模糊；未指定（全部）→ 跨文本列 OR LIKE
            if(!string.IsNullOrWhiteSpace(filterField))
                predicates.Add($"[{table}].[{filterField}] LIKE @kw ESCAPE '\\'");
            else
            {
                var textColumns=columns.Where(column=>IsTextLike(column.DataType)).ToList();
                if(textColumns.Count>0)
                    predicates.Add("("+string.Join(" OR ",textColumns.Select(column=>$"[{table}].[{column.Key}] LIKE @kw ESCAPE '\\'"))+")");
            }
        }
        if(!string.IsNullOrWhiteSpace(scopePredicate))predicates.Add($"({scopePredicate})");
        await using var command=new SqlCommand{Connection=connection};
        if(!string.IsNullOrWhiteSpace(keyword))command.Parameters.AddWithValue("@kw",$"%{EscapeLikePattern(keyword.Trim())}%");
        for(var i=0;i<scopeParameters.Count;i++)command.Parameters.AddWithValue($"@df{i}",scopeParameters[i]??DBNull.Value);
        if(conditions is { Count: > 0 })
        {
            var expressions=allowedFields.ToDictionary(field=>field,field=>$"[{table}].[{field}]",StringComparer.OrdinalIgnoreCase);
            var conditionPredicate=ChooserConditionBuilder.Build(conditions,expressions,command);
            if(conditionPredicate is not null)predicates.Add(conditionPredicate);
        }
        var where=predicates.Count>0?" WHERE "+string.Join(" AND ",predicates):"";
        var from=$"FROM dbo.[{table}] WITH (NOLOCK)";
        if(joins.Count>0)from+=" "+string.Join(" ",joins);
        if(!string.IsNullOrWhiteSpace(virtualJoin))from+=virtualJoin;
        var countFrom=$"FROM dbo.[{table}] WITH (NOLOCK)";
        if(joins.Count>0)countFrom+=" "+string.Join(" ",joins);
        // 排序字段必须在显示列白名单内（服务端校验），否则回退首列；方向仅 asc/desc。
        // 虚拟列（由 VirtualColumnResolver 解析出的显示列）不在物理列里，按 SELECT 别名排序
        // （SQL Server 允许 ORDER BY 使用选择列表别名）——否则点它的表头会静默按首列排。
        var requested = !string.IsNullOrWhiteSpace(sortField)
            ? columns.FirstOrDefault(column=>column.Key.Equals(sortField,StringComparison.OrdinalIgnoreCase))?.Key
              ?? (virtualKeys.Contains(sortField) ? sortField : null)
            : null;
        var sortColumn = requested ?? columns[0].Key;
        var sortExpression = virtualKeys.Contains(sortColumn) ? $"[{sortColumn}]" : $"[{table}].[{sortColumn}]";
        var dir = string.Equals(sortDirection,"desc",StringComparison.OrdinalIgnoreCase) ? "DESC" : "ASC";
        page=Math.Max(1,page);
        pageSize=Math.Clamp(pageSize,10,100);
        var sql=$"SELECT COUNT_BIG(1) {countFrom}{where}; SELECT {select} {from}{where} ORDER BY {sortExpression} {dir} OFFSET @Offset ROWS FETCH NEXT @PageSize ROWS ONLY;";
        command.CommandText=sql;
        command.Parameters.Add("@Offset",SqlDbType.Int).Value=(page-1)*pageSize;
        command.Parameters.Add("@PageSize",SqlDbType.Int).Value=pageSize;
        await using var reader=await command.ExecuteReaderAsync(token);
        await reader.ReadAsync(token);
        var total=Convert.ToInt32(reader.GetInt64(0));
        await reader.NextResultAsync(token);
        var result=new List<IReadOnlyDictionary<string,object?>>();
        while(await reader.ReadAsync(token))
        {
            var row=new Dictionary<string,object?>(StringComparer.OrdinalIgnoreCase);
            for(var i=0;i<reader.FieldCount;i++)
            {
                var value=reader.IsDBNull(i)?null:reader.GetValue(i);
                row[reader.GetName(i)]=value is string text?text.Trim():value;
            }
            result.Add(row);
        }
        return (result,total);
    }




    /// <summary>选择器关键字过滤的文本类字段判定（与查询组件 IsTextLike 同口径）。</summary>
    internal static bool IsTextLike(string dataType)
    {
        var type = dataType.ToLowerInvariant();
        return type.Contains("char") || type.Contains("text") || type.Contains("date") || type.Contains("time")
            || type is "idcard" or "url" or "email" or "phoneno" or "zipcode";
    }

    /// <summary>LIKE 模式转义：把用户输入里的通配符按 ESCAPE '\' 规则转义（配合 LIKE ... ESCAPE '\'）。</summary>
    internal static string EscapeLikePattern(string value) =>
        value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_").Replace("[", "\\[");
}
