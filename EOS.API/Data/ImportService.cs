using System.Data;
using System.Text;
using EOS.API.Models;
using Microsoft.Data.SqlClient;

namespace EOS.API.Data;

/// <summary>
/// 基本资料导入：
/// - 表/字段全部来自服务端元数据（TABLES + FIELDS + INFORMATION_SCHEMA 主键），
/// 不接受客户端任意表名/字段名；
/// - 行数据参数化批量写入（事务），审计列服务端填充，主键必填校验；
/// - CSV 解析支持引号与换行；逐行错误收集，不中断整体。
/// </summary>
public sealed class ImportService(DbConnectionFactory connections, ILogger<ImportService> logger)
{

    public async Task<IReadOnlyList<ImportTable>> GetTablesAsync(CancellationToken token)
    {
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
              AND EXISTS (SELECT 1 FROM dbo.FIELDS f WHERE f.T_ID=t.T_ID)
            ORDER BY t.T_DESC,t.T_ID;
            """;
        await using var command=new SqlCommand(sql,connection);
        await using var reader=await command.ExecuteReaderAsync(token);
        var tables=new List<(string Id,string Desc)>();
        while(await reader.ReadAsync(token))
        {
            var table=reader.GetString(0);
            if(!WorkbenchSql.Identifier.IsMatch(table))continue;
            tables.Add((table,reader.GetString(1)));
        }
        await reader.DisposeAsync();
        var result=new List<ImportTable>();
        foreach(var (table,desc) in tables)
        {
            var pks=await WorkbenchSql.GetPrimaryKeyColumnsAsync(connection,null,table,token);
            if(pks.Count==0)continue;
            result.Add(new ImportTable(table,desc,pks));
        }
        return result;
    }

    public async Task<ImportDefinition?> GetDefinitionAsync(string table,CancellationToken token)
    {
        if(!WorkbenchSql.Identifier.IsMatch(table))return null;
        await using var connection=connections.Create();
        await connection.OpenAsync(token);
        if(!await WorkbenchSql.TableExistsAsync(connection,table,token))return null;
        var pks=await WorkbenchSql.GetPrimaryKeyColumnsAsync(connection,null,table,token);
        if(pks.Count==0)return null;
        const string sql="""
            SELECT LTRIM(RTRIM(f.F_ID)),COALESCE(NULLIF(LTRIM(RTRIM(f.F_DESC)),''),LTRIM(RTRIM(f.F_ID))),
                   COALESCE(NULLIF(LTRIM(RTRIM(f.F_TYPE)),''),'nvarchar'),COALESCE(f.IS_VERIFY,0),COALESCE(f.IS_VIRTUAL,0)
            FROM dbo.FIELDS f WITH (NOLOCK)
            WHERE f.T_ID=@Table AND COALESCE(f.IS_VIRTUAL,0)=0
              AND EXISTS (SELECT 1 FROM sys.columns c JOIN sys.objects o ON c.object_id=o.object_id AND o.type IN ('U','V') JOIN sys.schemas s ON o.schema_id=s.schema_id WHERE s.name=N'dbo' AND o.name=@Table AND c.name=f.F_ID)
            ORDER BY COALESCE(f.VERIFY_INDEX,999),f.F_ID;
            """;
        await using var command=new SqlCommand(sql,connection);
        command.Parameters.Add("@Table",SqlDbType.NVarChar,100).Value=table;
        await using var reader=await command.ExecuteReaderAsync(token);
        var fields=new List<ImportField>();
        while(await reader.ReadAsync(token))
        {
            var key=reader.GetString(0);
            if(ImportService.IsAuditColumn(key))continue;
            fields.Add(new ImportField(key,reader.GetString(1),reader.GetString(2),
                Convert.ToBoolean(reader.GetValue(3)),pks.Any(pk=>pk.Equals(key,StringComparison.OrdinalIgnoreCase))));
        }
        return new ImportDefinition(table,fields,pks);
    }

    public static bool IsAuditColumn(string field) => RecordPayloadValidator.IsAuditColumn(field);

    public static ImportPreview ParseCsv(string csvText)
    {
        var rows=ParseCsvRows(csvText);
        if(rows.Count==0)return new ImportPreview([],[],0);
        var columns=rows[0].Select((column,index)=>column.Trim().Length>0?column.Trim():$"列{index+1}").ToList();
        var dataRows=rows.Skip(1).Take(5000).ToList();
        return new ImportPreview(columns,dataRows,dataRows.Count);
    }

    public async Task<ImportExecuteResult> ExecuteAsync(
        ImportExecuteRequest request,
        string employeeName,
        string userId,
        CancellationToken token)
    {
        if(!WorkbenchSql.Identifier.IsMatch(request.Table))return new(0,request.Rows.Count,[new(0,"表名不合法。")]);
        var definition=await GetDefinitionAsync(request.Table,token);
        if(definition is null)return new(0,request.Rows.Count,[new(0,"表不存在或缺少主键。")]);
        var fieldMap=definition.Fields.ToDictionary(field=>field.Key,StringComparer.OrdinalIgnoreCase);
        // 审计列不参与用户映射，但允许服务端填充写入
        foreach(var audit in new[]{"CREATE_PERSON","CREATE_DATE","LAST_UPDATE_BY","LAST_UPDATE_DATE"})
            fieldMap.TryAdd(audit,new ImportField(audit,audit,"nvarchar",false,false));
        // 数据归属三列同样服务端持有：导入行归属导入人（与统一表单新建覆盖回填同口径）
        foreach(var owned in new[]{"OWNER","OWNER_G","CI"})
            fieldMap.TryAdd(owned,new ImportField(owned,owned,"nvarchar",false,false));
        var columns=new List<ImportColumn>();
        foreach(var mapping in request.Mapping)
        {
            if(string.IsNullOrWhiteSpace(mapping.MappedField))continue;
            if(!fieldMap.TryGetValue(mapping.MappedField,out _))return new(0,request.Rows.Count,[new(0,$"字段不在白名单内：{mapping.MappedField}")]);
            columns.Add(mapping);
        }
        if(columns.Count==0)return new(0,request.Rows.Count,[new(0,"未选择可导入字段。")]);
        var missingPrimary=definition.PrimaryKeys.Where(pk=>!columns.Any(column=>column.MappedField!.Equals(pk,StringComparison.OrdinalIgnoreCase))).ToList();
        if(missingPrimary.Count>0)return new(0,request.Rows.Count,[new(0,$"主键字段未映射：{string.Join(',',missingPrimary)}")]);

        await using var connection=connections.Create();
        await connection.OpenAsync(token);
        await using var transaction=(SqlTransaction)await connection.BeginTransactionAsync(token);
        var inserted=0;
        var errors=new List<ImportRowError>();
        var now=DateTime.Now;
        for(var rowIndex=0;rowIndex<request.Rows.Count;rowIndex++)
        {
            var row=request.Rows[rowIndex];
            try
            {
                var values=new Dictionary<string,object?>(StringComparer.OrdinalIgnoreCase);
                var errorMessage=new StringBuilder();
                for(var i=0;i<columns.Count;i++)
                {
                    var value=row.ElementAtOrDefault(i)?.Trim()??"";
                    if(value.Length==0)
                    {
                        if(fieldMap[columns[i].MappedField!].IsRequired&&!fieldMap[columns[i].MappedField!].IsPrimaryKey)
                            errorMessage.Append($"{columns[i].MappedField} 不能为空；");
                        continue;
                    }
                    if(!TryConvert(fieldMap[columns[i].MappedField!].DataType,value,out var converted))
                    {
                        errorMessage.Append($"{columns[i].MappedField} 格式不正确；");
                        continue;
                    }
                    values[columns[i].MappedField!]=converted;
                }
                if(errorMessage.Length>0)
                {
                    errors.Add(new ImportRowError(rowIndex+2,errorMessage.ToString().TrimEnd('；')));
                    continue;
                }
                foreach(var pk in definition.PrimaryKeys)
                    if(!values.ContainsKey(pk)||values[pk] is null)
                    {
                        errors.Add(new ImportRowError(rowIndex+2,$"主键 {pk} 不能为空。"));
                        break;
                    }
                if(errors.Count>0&&errors[^1].RowNumber==rowIndex+2)continue;
                values["CREATE_PERSON"]=employeeName;
                values["CREATE_DATE"]=now;
                values["LAST_UPDATE_BY"]=employeeName;
                values["LAST_UPDATE_DATE"]=now;
                // 归属列仅目标表真实存在时回填（ definition 已做物理存在校验），缺列即跳过。
                if(definition.Fields.Any(field=>field.Key.Equals("OWNER",StringComparison.OrdinalIgnoreCase)))
                    values["OWNER"]=userId;
                if(definition.Fields.Any(field=>field.Key.Equals("OWNER_G",StringComparison.OrdinalIgnoreCase)))
                {
                    var primaryGroup=await WorkbenchSql.GetPrimaryGroupAsync(connection,transaction,userId,token);
                    if(primaryGroup is not null)values["OWNER_G"]=primaryGroup;
                }
                if(definition.Fields.Any(field=>field.Key.Equals("CI",StringComparison.OrdinalIgnoreCase)))
                {
                    var company=await WorkbenchSql.GetUserCompanyAsync(connection,transaction,userId,token);
                    if(company is null)
                    {
                        logger.LogWarning("导入用户无公司归属，回填默认公司 userId={UserId} table={Table}",userId,request.Table);
                        company=WorkflowStates.DefaultCompanyId;
                    }
                    values["CI"]=company;
                }
                var insertColumns=values.Keys.Where(key=>fieldMap.ContainsKey(key)).ToList();
                var sql=$"INSERT INTO dbo.[{request.Table}] ({string.Join(',',insertColumns.Select(column=>$"[{column}]"))}) VALUES ({string.Join(',',insertColumns.Select((_,index)=>$"@v{index}"))});";
                await using var command=new SqlCommand(sql,connection,transaction);
                for(var i=0;i<insertColumns.Count;i++)
                    command.Parameters.AddWithValue($"@v{i}",NormalizeValue(values[insertColumns[i]]));
                await command.ExecuteNonQueryAsync(token);
                inserted++;
            }
            catch(SqlException ex)
            {
                errors.Add(new ImportRowError(rowIndex+2,ex.Message));
            }
        }
        await transaction.CommitAsync(token);
        logger.LogInformation("数据导入完成 table={Table} inserted={Inserted} failed={Failed}",request.Table,inserted,errors.Count);
        return new ImportExecuteResult(inserted,errors.Count,errors);
    }

    private static bool TryConvert(string dataType,string raw,out object? value)
    {
        value=null;
        var type=dataType.ToLowerInvariant();
        try
        {
            if(type.Contains("bit",StringComparison.Ordinal))
                value=raw.Equals("true",StringComparison.OrdinalIgnoreCase)||raw=="1"||raw=="是";
            else if(type.Contains("datetime",StringComparison.Ordinal)||type.Contains("smalldatetime",StringComparison.Ordinal)
                    ||type.Contains("date",StringComparison.Ordinal))
                value=DateTime.Parse(raw,System.Globalization.CultureInfo.InvariantCulture);
            else if(type.Contains("int",StringComparison.Ordinal))
                value=int.Parse(raw,System.Globalization.CultureInfo.InvariantCulture);
            else if(type.Contains("float",StringComparison.Ordinal)||type.Contains("real",StringComparison.Ordinal))
                value=double.Parse(raw,System.Globalization.CultureInfo.InvariantCulture);
            else if(type.Contains("decimal",StringComparison.Ordinal)||type.Contains("numeric",StringComparison.Ordinal)
                    ||type.Contains("money",StringComparison.Ordinal))
                value=decimal.Parse(raw,System.Globalization.CultureInfo.InvariantCulture);
            else
                value=raw;
            return true;
        }
        catch{return false;}
    }

    private static object NormalizeValue(object? value)=>value??DBNull.Value;

    private static List<List<string>> ParseCsvRows(string csvText)
    {
        var rows=new List<List<string>>();
        var row=new List<string>();
        var field=new StringBuilder();
        var inQuotes=false;
        foreach(var ch in csvText)
        {
            if(inQuotes)
            {
                if(ch=='"')
                {
                    if(field.Length>0&&field[^1]=='"'){field.Length--;field.Append('"');}
                    else inQuotes=false;
                }
                else field.Append(ch);
            }
            else
            {
                switch(ch)
                {
                    case '"':inQuotes=true;break;
                    case ',':row.Add(field.ToString());field.Clear();break;
                    case '\n':row.Add(field.ToString());field.Clear();rows.Add(row);row=[];break;
                    case '\r':break;
                    default:field.Append(ch);break;
                }
            }
        }
        if(field.Length>0||row.Count>0){row.Add(field.ToString());rows.Add(row);}
        return rows;
    }
}
