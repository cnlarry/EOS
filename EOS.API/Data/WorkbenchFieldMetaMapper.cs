using EOS.API.Models;
using Microsoft.Data.SqlClient;
using System.Data;

namespace EOS.API.Data;

/// <summary>
/// Maps workbench field metadata and column settings: field-maintenance summaries and details,
/// field and column-width write-backs, and user (SYSQL_FIELDS) vs system default (SYSQL_DEFAULT)
/// column configuration. Field keys pass module-definition/registry whitelists, widths and counts
/// are bounded, and values are parameterized.
/// </summary>
public sealed class WorkbenchFieldMetaMapper(
    DbConnectionFactory connections,
    FieldAdminRepository fieldAdmin,
    WorkbenchDirtyMarker dirtyMarker,
    WorkbenchAuditWriter auditWriter,
    ILogger<WorkbenchFieldMetaMapper> logger)
{
    private SqlConnection CreateConnection()=>connections.Create();
    public async Task<IReadOnlyList<FieldSetupLookup>> GetFieldSetupTablesAsync(CancellationToken token)
    {await using var connection=CreateConnection();await connection.OpenAsync(token);const string sql="SELECT LTRIM(RTRIM(T_ID)),COALESCE(NULLIF(LTRIM(RTRIM(T_DESC)),''),LTRIM(RTRIM(T_ID))) FROM dbo.TABLES WITH (NOLOCK) ORDER BY T_DESC,T_ID";await using var command=new SqlCommand(sql,connection);await using var reader=await command.ExecuteReaderAsync(token);var result=new List<FieldSetupLookup>();while(await reader.ReadAsync(token)){var value=reader.GetString(0);if(WorkbenchSql.Identifier.IsMatch(value))result.Add(new(value,reader.GetString(1)));}return result;}

    public async Task<IReadOnlyList<FieldSetupLookup>> GetFieldSetupModulesAsync(CancellationToken token)
    {await using var connection=CreateConnection();await connection.OpenAsync(token);const string sql="SELECT CONVERT(nvarchar(20),M_IDX),COALESCE(NULLIF(LTRIM(RTRIM(M_DESC)),''),CONVERT(nvarchar(20),M_IDX)) FROM dbo.MODULES WITH (NOLOCK) ORDER BY M_DESC,M_IDX";await using var command=new SqlCommand(sql,connection);await using var reader=await command.ExecuteReaderAsync(token);var result=new List<FieldSetupLookup>();while(await reader.ReadAsync(token))result.Add(new(reader.GetString(0),reader.GetString(1)));return result;}


    public async Task<IReadOnlyList<WorkbenchFieldSummary>> GetFieldSummariesAsync(WorkbenchDefinition definition,bool detail,CancellationToken token)
    {
        var table=detail?definition.DetailTable:definition.MasterTable;if(table is null)return [];
        await using var connection=CreateConnection();await connection.OpenAsync(token);const string sql="""
            SELECT LTRIM(RTRIM(F_ID)),COALESCE(NULLIF(LTRIM(RTRIM(F_DESC)),''),LTRIM(RTRIM(F_ID))),CAST(COALESCE(IS_VISIBLE,1) AS bit),CAST(COALESCE(IS_DEFAULT_FIELDS,0) AS bit),CAST(COALESCE(IS_QUERY,1) AS bit),CAST(COALESCE(IS_READONLY,0) AS bit),CAST(COALESCE(IS_COST,0) AS bit),CAST(COALESCE(IS_SECRECY,0) AS bit),CAST(COALESCE(IS_VIRTUAL,0) AS bit)
            FROM dbo.FIELDS WITH (NOLOCK) WHERE T_ID=@Table ORDER BY COALESCE(VERIFY_INDEX,999),F_ID;
            """;
        await using var command=new SqlCommand(sql,connection);command.Parameters.Add("@Table",SqlDbType.NVarChar,100).Value=table;await using var reader=await command.ExecuteReaderAsync(token);var result=new List<WorkbenchFieldSummary>();while(await reader.ReadAsync(token)){var key=reader.GetString(0);if(WorkbenchSql.Identifier.IsMatch(key))result.Add(new(key,reader.GetString(1),reader.GetBoolean(2),reader.GetBoolean(3),reader.GetBoolean(4),reader.GetBoolean(5),reader.GetBoolean(6),reader.GetBoolean(7),reader.GetBoolean(8)));}return result;
    }

    public async Task<WorkbenchFieldMetadata?> GetFieldMetadataAsync(WorkbenchDefinition definition,bool detail,string fieldKey,CancellationToken token)
    {
        var table=detail?definition.DetailTable:definition.MasterTable;if(table is null||!WorkbenchSql.Identifier.IsMatch(fieldKey))return null;
        var metadata=await fieldAdmin.GetMetadataAsync(table,fieldKey.Trim(),token);
        return metadata is null?null:MapMetadata(metadata);
    }

    private static WorkbenchFieldMetadata MapMetadata(FieldAdminMetadata metadata)
    {
        var input=metadata.Field;
        return new(metadata.FieldId,input.Label,input.DataType,input.Width,input.Align,input.HeaderAlign,
            input.Format,input.IsVisible,input.IsDefault,input.IsQueryable,input.IsReadonly,input.IsRequired,
            input.IsCost,input.IsSecrecy,input.DefaultValue,input.VerifyIndex,input.Regex,input.Remark,
            input.BrowseUrl,input.BrowseModuleId,input.OnlyChoose,input.ChooseMultiple,input.ChoosePage,
            input.Choosers.Select(MapChooser).ToArray(),metadata.IsVirtual,metadata.VirtualExpression,input.CanCopy,
            metadata.IsAutoIncrement,metadata.ConvertFunction,metadata.DataSourceSql,metadata.LastUpdatedBy,metadata.LastUpdatedAt,
            input.TabNo,input.FormOrder,input.Span,input.NewLine,input.CellGroup,input.CellRole,input.Options);
    }

    private static FieldChooserSource MapChooser(FieldAdminChooser source)=>
        new(source.Active,source.Table,source.Description,source.ModuleId,source.Filter,source.ReturnMapping,source.SerialNo);

    public async Task UpdateFieldMetadataAsync(WorkbenchDefinition definition,bool detail,string fieldKey,UpdateWorkbenchFieldMetadata update,string updatedBy,CancellationToken token)
    {
        var table=detail?definition.DetailTable:definition.MasterTable;if(table is null||!WorkbenchSql.Identifier.IsMatch(fieldKey))throw new ArgumentException("字段无效。");
        var input=MapInput(update);
        var original=update.Original is null?null:MapInput(update.Original);
        await fieldAdmin.UpdateAsync(table,fieldKey.Trim(),input,original,updatedBy,token);
        await auditWriter.WriteBestEffortAsync(definition.ModuleId,$"{table}.{fieldKey.Trim()}","UPDATE","更新字段设置",updatedBy,"WORKBENCH_METADATA",result:1,null,token);
    }

    /// <summary>
    /// Batch writes column widths (DISPLAY_LENGTH): keys are filtered by the module-definition
    /// whitelist (case-insensitive), widths clamp to [40,300], and all updates commit in one
    /// transaction to avoid per-column round trips and mid-save refreshes.
    /// </summary>
    public async Task UpdateColumnWidthsAsync(WorkbenchDefinition definition,UpdateColumnWidthsRequest request,string updatedBy,CancellationToken token)
    {
        var masterAllowed=definition.MasterFields.Select(field=>field.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var detailAllowed=definition.DetailFields.Select(field=>field.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var masterWidths=FilterColumnWidths(request.Master,masterAllowed);
        var detailWidths=request.Detail is null?new Dictionary<string,int>(StringComparer.OrdinalIgnoreCase):FilterColumnWidths(request.Detail,detailAllowed);
        if(masterWidths.Count==0&&detailWidths.Count==0)return;

        await using var connection=CreateConnection();
        await connection.OpenAsync(token);
        await using var transaction=(SqlTransaction)await connection.BeginTransactionAsync(token);
        const string sql="""
            UPDATE dbo.FIELDS SET DISPLAY_LENGTH=@Width,LAST_UPDATE_BY=@UpdatedBy,LAST_UPDATE_DATE=GETDATE()
            WHERE T_ID=@TableId AND LTRIM(RTRIM(F_ID))=@FieldId;
            """;
        var groups=new List<(string? TableId,IReadOnlyDictionary<string,int> Widths)>
        {
            (definition.MasterTable,masterWidths),
            (definition.DetailTable,detailWidths),
        };
        foreach(var (tableId,widths) in groups)
        {
            if(tableId is null)continue;
            foreach(var (fieldKey,width) in widths)
            {
                await using var command=new SqlCommand(sql,connection,transaction);
                command.Parameters.Add("@TableId",SqlDbType.NVarChar,100).Value=tableId;
                command.Parameters.Add("@FieldId",SqlDbType.NVarChar,100).Value=fieldKey;
                command.Parameters.Add("@Width",SqlDbType.Int).Value=width;
                command.Parameters.Add("@UpdatedBy",SqlDbType.NVarChar,50).Value=updatedBy;
                await command.ExecuteNonQueryAsync(token);
            }
        }
        await dirtyMarker.MarkDirtyAsync(connection, transaction, definition.ModuleId, updatedBy, token);
        await auditWriter.WriteEventAsync(connection, transaction, definition.ModuleId,
            $"column-widths:{definition.MasterTable}", "UPDATE", "更新列宽", updatedBy, "WORKBENCH_METADATA", result: 1, null, token);
        await transaction.CommitAsync(token);
        logger.LogInformation("批量保存列宽 module={ModuleId} master={MasterCount} detail={DetailCount}",definition.ModuleId,masterWidths.Count,detailWidths.Count);
    }

    /// <summary>
    /// Whitelist filter for batch column widths: keeps fields visible in the module definition and
    /// clamps widths to [40,300].
    /// </summary>
    internal static IReadOnlyDictionary<string,int> FilterColumnWidths(IReadOnlyDictionary<string,int> input,IReadOnlySet<string> allowed)
    {
        var result=new Dictionary<string,int>(StringComparer.OrdinalIgnoreCase);
        foreach(var (key,rawWidth) in input)
        {
            var field=key.Trim();
            if(field.Length==0||!WorkbenchSql.Identifier.IsMatch(field)||!allowed.Contains(field))continue;
            result[field]=Math.Clamp(rawWidth,40,300);
        }
        return result;
    }

    private static FieldAdminInput MapInput(UpdateWorkbenchFieldMetadata update)=>new(
        update.Label,update.DataType,update.Width,update.Align,update.HeaderAlign,update.Format,
        update.IsVisible,update.IsDefault,update.IsQueryable,update.IsReadonly,update.IsRequired,update.IsCost,update.IsSecrecy,
        update.DefaultValue,update.VerifyIndex,update.Regex,update.Remark,update.BrowseUrl,update.BrowseModuleId,
        update.OnlyChoose,update.ChooseMultiple,update.ChoosePage,
        update.Choosers.Select(MapInputChooser).ToArray(),update.CanCopy,
        update.TabNo,update.FormOrder,update.Span,update.NewLine,update.CellGroup,update.CellRole,update.FormOptions);

    private static FieldAdminInput MapInput(WorkbenchFieldMetadata metadata)=>new(
        metadata.Label,metadata.DataType,metadata.Width,metadata.Align,metadata.HeaderAlign,metadata.Format,
        metadata.IsVisible,metadata.IsDefault,metadata.IsQueryable,metadata.IsReadonly,metadata.IsRequired,metadata.IsCost,metadata.IsSecrecy,
        metadata.DefaultValue,metadata.VerifyIndex,metadata.Regex,metadata.Remark,metadata.BrowseUrl,metadata.BrowseModuleId,
        metadata.OnlyChoose,metadata.ChooseMultiple,metadata.ChoosePage,
        metadata.Choosers.Select(MapInputChooser).ToArray(),metadata.CanCopy,
        metadata.TabNo,metadata.FormOrder,metadata.Span,metadata.NewLine,metadata.CellGroup,metadata.CellRole,metadata.FormOptions);

    private static FieldAdminChooser MapInputChooser(FieldChooserSource source)=>
        new(source.Active,source.Table,source.Description,source.ModuleId,source.Filter,source.ReturnMapping,source.SerialNo);

    public async Task<WorkbenchColumnSettings> GetDefaultColumnSettingsAsync(WorkbenchDefinition definition,string userId,CancellationToken token)
    {
        await using var connection=CreateConnection();await connection.OpenAsync(token);
        var master=await ReadDefaultColumnSettings(connection,definition.MasterTable,definition.MasterTable,token);var detail=definition.DetailTable is null?[]:await ReadDefaultColumnSettings(connection,definition.MasterTable,definition.DetailTable,token);return new(master,detail);
    }

    public async Task<WorkbenchColumnSettings> GetColumnSettingsAsync(WorkbenchDefinition definition,string userId,CancellationToken token)
    {
        await using var connection=CreateConnection();await connection.OpenAsync(token);
        return new(await ReadColumnSettings(connection,userId,definition.MasterTable,definition.MasterTable,token),definition.DetailTable is null?[]:await ReadColumnSettings(connection,userId,definition.MasterTable,definition.DetailTable,token));
    }

    public async Task<(WorkbenchColumnSettings Current,WorkbenchColumnSettings Defaults)> GetColumnEditorSettingsAsync(WorkbenchDefinition definition,string userId,CancellationToken token)
    {
        await using var connection=CreateConnection();await connection.OpenAsync(token);var current=new WorkbenchColumnSettings(await ReadColumnSettings(connection,userId,definition.MasterTable,definition.MasterTable,token),definition.DetailTable is null?[]:await ReadColumnSettings(connection,userId,definition.MasterTable,definition.DetailTable,token));var defaults=new WorkbenchColumnSettings(await ReadDefaultColumnSettings(connection,definition.MasterTable,definition.MasterTable,token),definition.DetailTable is null?[]:await ReadDefaultColumnSettings(connection,definition.MasterTable,definition.DetailTable,token));return(current,defaults);
    }

    public async Task SaveColumnSettingsAsync(WorkbenchDefinition definition,string userId,SaveWorkbenchColumns settings,CancellationToken token)
    {
        await using var connection=CreateConnection();await connection.OpenAsync(token);await using var transaction=(SqlTransaction)await connection.BeginTransactionAsync(token);
        await SaveColumns(connection,transaction,userId,definition.MasterTable,definition.MasterTable,settings.Master,token);
        if(definition.DetailTable is not null)await SaveColumns(connection,transaction,userId,definition.MasterTable,definition.DetailTable,settings.Detail,token);
        await transaction.CommitAsync(token);
        logger.LogInformation("保存列配置 userId={UserId} module={ModuleId} master={Master} masterFields={MasterFieldCount} detailFields={DetailFieldCount}",
            userId,definition.ModuleId,definition.MasterTable,settings.Master.Count,settings.Detail.Count);
    }

    public async Task ResetColumnSettingsAsync(WorkbenchDefinition definition,string userId,CancellationToken token)
    {
        await using var connection=CreateConnection();await connection.OpenAsync(token);const string sql="DELETE FROM dbo.SYSQL_FIELDS WHERE USER_ID=@UserId AND T_ID=@MasterTable";
        await using var command=new SqlCommand(sql,connection);command.Parameters.Add("@UserId",SqlDbType.NChar,10).Value=userId.Trim();command.Parameters.Add("@MasterTable",SqlDbType.VarChar,100).Value=definition.MasterTable;await command.ExecuteNonQueryAsync(token);
        logger.LogInformation("重置列配置 userId={UserId} module={ModuleId} master={Master}", userId,definition.ModuleId,definition.MasterTable);
    }

    private static async Task<IReadOnlyList<WorkbenchColumn>> ReadDefaultColumnSettings(SqlConnection connection,string masterTable,string targetTable,CancellationToken token)
    {
        const string sql="""
            SELECT LTRIM(RTRIM(f.F_ID)),COALESCE(NULLIF(LTRIM(RTRIM(f.F_DESC)),''),LTRIM(RTRIM(f.F_ID))),CAST(CASE WHEN d.F_ID IS NULL THEN 0 ELSE 1 END AS bit),COALESCE(d.F_IDX,COALESCE(f.VERIFY_INDEX,999)),CAST(COALESCE(f.IS_VIRTUAL,0) AS bit)
            FROM dbo.FIELDS f WITH (NOLOCK) LEFT JOIN dbo.SYSQL_DEFAULT d WITH (NOLOCK) ON d.T_ID=@MasterTable AND d.T_ID_R=@TargetTable AND LTRIM(RTRIM(d.F_ID))=LTRIM(RTRIM(f.F_ID))
            WHERE f.T_ID=@TargetTable AND COALESCE(f.IS_VISIBLE,1)=1 AND COALESCE(f.IS_COST,0)=0 AND COALESCE(f.IS_SECRECY,0)=0 ORDER BY CASE WHEN d.F_ID IS NULL THEN 1 ELSE 0 END,COALESCE(d.F_IDX,COALESCE(f.VERIFY_INDEX,999)),f.F_ID;
            """;
        await using var command=new SqlCommand(sql,connection);command.Parameters.Add("@MasterTable",SqlDbType.VarChar,100).Value=masterTable;command.Parameters.Add("@TargetTable",SqlDbType.VarChar,100).Value=targetTable;
        var result=new List<WorkbenchColumn>();
        var seen=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using(var reader=await command.ExecuteReaderAsync(token))
        {
            var order=0;
            while(await reader.ReadAsync(token)){var key=reader.GetString(0);if(WorkbenchSql.Identifier.IsMatch(key)&&seen.Add(key))result.Add(new(key,reader.GetString(1),reader.GetBoolean(2),++order,reader.GetBoolean(4)));}
        }
        return await DropUnresolvableVirtualColumnsAsync(connection,targetTable,result,token);
    }

    private static async Task<IReadOnlyList<WorkbenchColumn>> ReadColumnSettings(SqlConnection connection,string userId,string masterTable,string targetTable,CancellationToken token,bool defaultsOnly=false)
    {
        const string sql="""
            WITH UserFields AS (SELECT LTRIM(RTRIM(F_ID)) F_ID,F_IDX FROM dbo.SYSQL_FIELDS WITH (NOLOCK) WHERE USER_ID=@UserId AND T_ID=@MasterTable AND T_ID_R=@TargetTable),
            HasConfig AS (SELECT CASE WHEN EXISTS(SELECT 1 FROM UserFields) THEN 1 ELSE 0 END Value)
            SELECT LTRIM(RTRIM(f.F_ID)),COALESCE(NULLIF(LTRIM(RTRIM(f.F_DESC)),''),LTRIM(RTRIM(f.F_ID))),CAST(CASE WHEN (h.Value=1 AND u.F_ID IS NOT NULL) OR (h.Value=0 AND COALESCE(f.IS_DEFAULT_FIELDS,0)=1) THEN 1 ELSE 0 END AS bit),COALESCE(u.F_IDX,COALESCE(f.VERIFY_INDEX,999)),CAST(COALESCE(f.IS_VIRTUAL,0) AS bit)
            FROM dbo.FIELDS f WITH (NOLOCK) CROSS JOIN HasConfig h LEFT JOIN UserFields u ON u.F_ID=LTRIM(RTRIM(f.F_ID))
            WHERE f.T_ID=@TargetTable AND COALESCE(f.IS_VISIBLE,1)=1 ORDER BY CASE WHEN u.F_IDX IS NULL THEN 1 ELSE 0 END,COALESCE(u.F_IDX,COALESCE(f.VERIFY_INDEX,999)),f.F_ID OPTION (OPTIMIZE FOR UNKNOWN);
            """;
        await using var command=new SqlCommand(sql,connection);command.Parameters.Add("@UserId",SqlDbType.NChar,10).Value=userId.Trim();command.Parameters.Add("@MasterTable",SqlDbType.VarChar,100).Value=masterTable;command.Parameters.Add("@TargetTable",SqlDbType.VarChar,100).Value=targetTable;
        var result=new List<WorkbenchColumn>();
        var seen=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using(var reader=await command.ExecuteReaderAsync(token))
        {
            var order=0;
            while(await reader.ReadAsync(token)){var key=reader.GetString(0);if(WorkbenchSql.Identifier.IsMatch(key)&&seen.Add(key))result.Add(new(key,reader.GetString(1),reader.GetBoolean(2),++order,reader.GetBoolean(4)));}
        }
        return await DropUnresolvableVirtualColumnsAsync(connection,targetTable,result,token);
    }

    /// <summary>
    /// Removes virtual fields that cannot be resolved under controlled parsing from column
    /// settings (same policy as field reading, so the column editor never shows a checked column
    /// that the list cannot render).
    /// </summary>
    private static async Task<IReadOnlyList<WorkbenchColumn>> DropUnresolvableVirtualColumnsAsync(
        SqlConnection connection,
        string targetTable,
        IReadOnlyList<WorkbenchColumn> columns,
        CancellationToken token)
    {
        var virtualColumns=columns.Where(column=>column.IsVirtual).ToList();
        if(virtualColumns.Count==0)return columns;
        var fields=new List<WorkbenchField>();
        await using var command=new SqlCommand(
            "SELECT LTRIM(RTRIM(F_ID)),COALESCE(NULLIF(LTRIM(RTRIM(F_DESC)),''),F_ID),COALESCE(F_TYPE,'nvarchar'),ISNULL(VIRTUAL_EXP,'') " +
            "FROM dbo.FIELDS WITH (NOLOCK) WHERE T_ID=@Table AND COALESCE(IS_VISIBLE,1)=1 AND COALESCE(IS_VIRTUAL,0)=1",
            connection);
        command.Parameters.Add("@Table",SqlDbType.NVarChar,100).Value=targetTable;
        await using(var reader=await command.ExecuteReaderAsync(token))
        {
            while(await reader.ReadAsync(token))
            {
                var key=reader.GetString(0).Trim();
                if(WorkbenchSql.Identifier.IsMatch(key))
                    fields.Add(new(key,reader.GetString(1).Trim(),reader.GetString(2).Trim(),100,"left",false,IsVirtual:true,VirtualExpression:reader.GetString(3).Trim()));
            }
        }
        var resolution=await new VirtualColumnResolver(connection).ResolveAsync(targetTable,fields,token);
        var dropped=resolution.UnresolvedKeys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return columns.Where(column=>!dropped.Contains(column.Key)).ToList();
    }

    private static async Task SaveColumns(SqlConnection connection,SqlTransaction transaction,string userId,string masterTable,string targetTable,IReadOnlyList<string> fields,CancellationToken token)
    {
        if(fields.Count>200||fields.Distinct(StringComparer.OrdinalIgnoreCase).Count()!=fields.Count)throw new ArgumentException("字段配置无效。");
        var allowed=await ReadAllowedFieldKeys(connection,transaction,targetTable,token);if(fields.Any(field=>!allowed.Contains(field)))throw new ArgumentException("字段配置包含无效字段。");
        await using(var delete=new SqlCommand("DELETE FROM dbo.SYSQL_FIELDS WHERE USER_ID=@UserId AND T_ID=@MasterTable AND T_ID_R=@TargetTable",connection,transaction)){delete.Parameters.Add("@UserId",SqlDbType.NChar,10).Value=userId.Trim();delete.Parameters.Add("@MasterTable",SqlDbType.VarChar,100).Value=masterTable;delete.Parameters.Add("@TargetTable",SqlDbType.VarChar,100).Value=targetTable;await delete.ExecuteNonQueryAsync(token);}
        for(var i=0;i<fields.Count;i++){await using var insert=new SqlCommand("INSERT INTO dbo.SYSQL_FIELDS (USER_ID,T_ID,T_ID_R,F_ID,F_IDX) VALUES (@UserId,@MasterTable,@TargetTable,@Field,@Index)",connection,transaction);insert.Parameters.Add("@UserId",SqlDbType.NChar,10).Value=userId.Trim();insert.Parameters.Add("@MasterTable",SqlDbType.VarChar,100).Value=masterTable;insert.Parameters.Add("@TargetTable",SqlDbType.VarChar,100).Value=targetTable;insert.Parameters.Add("@Field",SqlDbType.NVarChar,100).Value=fields[i];insert.Parameters.Add("@Index",SqlDbType.Int).Value=i+1;await insert.ExecuteNonQueryAsync(token);}
    }

    private static async Task<HashSet<string>> ReadAllowedFieldKeys(SqlConnection connection,SqlTransaction transaction,string targetTable,CancellationToken token)
    {await using var command=new SqlCommand("SELECT LTRIM(RTRIM(F_ID)) FROM dbo.FIELDS WHERE T_ID=@TargetTable AND COALESCE(IS_VISIBLE,1)=1",connection,transaction);command.Parameters.Add("@TargetTable",SqlDbType.NVarChar,100).Value=targetTable;await using var reader=await command.ExecuteReaderAsync(token);var result=new HashSet<string>(StringComparer.OrdinalIgnoreCase);while(await reader.ReadAsync(token)){var key=reader.GetString(0);if(WorkbenchSql.Identifier.IsMatch(key))result.Add(key);}return result;}

}
