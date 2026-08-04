using Microsoft.Data.SqlClient;
using System.Data;
using System.Text.RegularExpressions;

namespace EOS.API.Data;

public sealed record WorkbenchField(string Key, string Label, string DataType, int Width, string Align, bool IsPrimaryKey, bool IsVisible = true, bool IsQueryable = true, string HeaderAlign = "center", string? Format = null, string? BrowseUrl = null, int? BrowseModuleId = null);
public sealed record WorkbenchColumn(string Key, string Label, bool IsVisible, int Order);
public sealed record WorkbenchColumnSettings(IReadOnlyList<WorkbenchColumn> Master, IReadOnlyList<WorkbenchColumn> Detail);
public sealed record SaveWorkbenchColumns(IReadOnlyList<string> Master, IReadOnlyList<string> Detail);
public sealed record WorkbenchFieldSummary(string Key,string Label,bool IsVisible,bool IsDefault,bool IsQueryable,bool IsReadonly,bool IsCost,bool IsSecrecy,bool IsVirtual);
public sealed record FieldChooserSource(bool Active,string? Table,string? Description,int? ModuleId,string? Filter,string? ReturnMapping);
public sealed record WorkbenchFieldMetadata(string Key,string Label,string DataType,int Width,string Align,string HeaderAlign,string? Format,bool IsVisible,bool IsDefault,bool IsQueryable,bool IsReadonly,bool IsRequired,bool IsCost,bool IsSecrecy,string? DefaultValue,int? VerifyIndex,string? Regex,string? Remark,string? BrowseUrl,int? BrowseModuleId,bool OnlyChoose,bool ChooseMultiple,string? ChoosePage,IReadOnlyList<FieldChooserSource> Choosers,bool IsVirtual,string? VirtualExpression,bool CanCopy,bool IsAutoIncrement,string? ConvertFunction,string? DataSourceSql,string? LastUpdatedBy,DateTime? LastUpdatedAt);
public sealed record UpdateWorkbenchFieldMetadata(string Label,string DataType,int Width,string Align,string HeaderAlign,string? Format,bool IsVisible,bool IsDefault,bool IsQueryable,bool IsReadonly,bool IsRequired,bool IsCost,bool IsSecrecy,string? DefaultValue,int? VerifyIndex,string? Regex,string? Remark,string? BrowseUrl,int? BrowseModuleId,bool OnlyChoose,bool ChooseMultiple,string? ChoosePage,IReadOnlyList<FieldChooserSource> Choosers);
public sealed record WorkbenchDefinition(int ModuleId, string Title, string MasterTable, string? DetailTable, IReadOnlyList<WorkbenchField> MasterFields, IReadOnlyList<WorkbenchField> DetailFields, string? DefaultSort);
public sealed record WorkbenchData(IReadOnlyList<Dictionary<string, object?>> Rows, int Total, int Page, int PageSize);
public sealed record WorkbenchQueryCondition(string Field, string Operator, string? Value, string? ValueTo, IReadOnlyList<string>? Values, string Logic = "and");
public sealed record WorkbenchQuery(IReadOnlyList<WorkbenchQueryCondition> Conditions);
public sealed record FieldSetupLookup(string Value,string Label);

public sealed class DocumentWorkbenchRepository(IConfiguration configuration)
{
    private static readonly Regex Identifier = new("^[A-Za-z_][A-Za-z0-9_]{0,127}$", RegexOptions.Compiled);

    public async Task<WorkbenchDefinition?> GetDefinitionAsync(int moduleId, string userId, bool canViewCost, bool canViewSecrecy, IReadOnlySet<string> deniedMasterFields, IReadOnlySet<string> deniedDetailFields, CancellationToken token)
    {
        await using var connection = CreateConnection(); await connection.OpenAsync(token);
        const string sql = "SELECT M_DESC,MASTER_TABLE,DETAIL_TABLE,M_URL,SORT_FIELDS FROM dbo.MODULES WITH (NOLOCK) WHERE M_IDX=@ModuleId";
        await using var command = new SqlCommand(sql, connection); command.Parameters.Add("@ModuleId", SqlDbType.Int).Value=moduleId;
        await using var reader = await command.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token)) return null;
        var title=reader.GetString(0).Trim(); var master=reader.IsDBNull(1)?"":reader.GetString(1).Trim();
        var detail=reader.IsDBNull(2)?null:reader.GetString(2).Trim(); var url=reader.IsDBNull(3)?"":reader.GetString(3);var defaultSort=reader.IsDBNull(4)?null:reader.GetString(4).Trim();
        await reader.CloseAsync();
        if (!IsWorkbenchUrl(url) || !Identifier.IsMatch(master) || (detail is not null && !Identifier.IsMatch(detail))) return null;
        var masterFields=await ReadFields(connection,userId,master,master,canViewCost,canViewSecrecy,deniedMasterFields,token);
        return new(moduleId,title,master,detail,masterFields,
            detail is null?[]:await ReadFields(connection,userId,master,detail,canViewCost,canViewSecrecy,deniedDetailFields,token),NormalizeSort(defaultSort,master,masterFields));
    }

    public async Task<IReadOnlyList<FieldSetupLookup>> GetFieldSetupTablesAsync(CancellationToken token)
    {await using var connection=CreateConnection();await connection.OpenAsync(token);const string sql="SELECT LTRIM(RTRIM(T_ID)),COALESCE(NULLIF(LTRIM(RTRIM(T_DESC)),''),LTRIM(RTRIM(T_ID))) FROM dbo.TABLES WITH (NOLOCK) ORDER BY T_DESC,T_ID";await using var command=new SqlCommand(sql,connection);await using var reader=await command.ExecuteReaderAsync(token);var result=new List<FieldSetupLookup>();while(await reader.ReadAsync(token)){var value=reader.GetString(0);if(Identifier.IsMatch(value))result.Add(new(value,reader.GetString(1)));}return result;}

    public async Task<IReadOnlyList<FieldSetupLookup>> GetFieldSetupModulesAsync(CancellationToken token)
    {await using var connection=CreateConnection();await connection.OpenAsync(token);const string sql="SELECT CONVERT(nvarchar(20),M_IDX),COALESCE(NULLIF(LTRIM(RTRIM(M_DESC)),''),CONVERT(nvarchar(20),M_IDX)) FROM dbo.MODULES WITH (NOLOCK) ORDER BY M_DESC,M_IDX";await using var command=new SqlCommand(sql,connection);await using var reader=await command.ExecuteReaderAsync(token);var result=new List<FieldSetupLookup>();while(await reader.ReadAsync(token))result.Add(new(reader.GetString(0),reader.GetString(1)));return result;}

    public async Task<IReadOnlyList<WorkbenchFieldSummary>> GetFieldSummariesAsync(WorkbenchDefinition definition,bool detail,CancellationToken token)
    {
        var table=detail?definition.DetailTable:definition.MasterTable;if(table is null)return [];
        await using var connection=CreateConnection();await connection.OpenAsync(token);const string sql="""
            SELECT LTRIM(RTRIM(F_ID)),COALESCE(NULLIF(LTRIM(RTRIM(F_DESC)),''),LTRIM(RTRIM(F_ID))),CAST(COALESCE(IS_VISIBLE,1) AS bit),CAST(COALESCE(IS_DEFAULT_FIELDS,0) AS bit),CAST(COALESCE(IS_QUERY,1) AS bit),CAST(COALESCE(IS_READONLY,0) AS bit),CAST(COALESCE(IS_COST,0) AS bit),CAST(COALESCE(IS_SECRECY,0) AS bit),CAST(COALESCE(IS_VIRTUAL,0) AS bit)
            FROM dbo.FIELDS WITH (NOLOCK) WHERE T_ID=@Table ORDER BY COALESCE(VERIFY_INDEX,999),F_ID;
            """;
        await using var command=new SqlCommand(sql,connection);command.Parameters.Add("@Table",SqlDbType.NVarChar,100).Value=table;await using var reader=await command.ExecuteReaderAsync(token);var result=new List<WorkbenchFieldSummary>();while(await reader.ReadAsync(token)){var key=reader.GetString(0);if(Identifier.IsMatch(key))result.Add(new(key,reader.GetString(1),reader.GetBoolean(2),reader.GetBoolean(3),reader.GetBoolean(4),reader.GetBoolean(5),reader.GetBoolean(6),reader.GetBoolean(7),reader.GetBoolean(8)));}return result;
    }

    public async Task<WorkbenchFieldMetadata?> GetFieldMetadataAsync(WorkbenchDefinition definition,bool detail,string fieldKey,CancellationToken token)
    {
        var table=detail?definition.DetailTable:definition.MasterTable;if(table is null||!Identifier.IsMatch(fieldKey))return null;
        await using var connection=CreateConnection();await connection.OpenAsync(token);const string sql="""
            SELECT LTRIM(RTRIM(F_ID)),COALESCE(NULLIF(LTRIM(RTRIM(F_DESC)),''),LTRIM(RTRIM(F_ID))),COALESCE(F_TYPE,'nvarchar'),COALESCE(DISPLAY_LENGTH,100),COALESCE(NULLIF(ITEM_ALIGN,''),'left'),COALESCE(NULLIF(HEADER_ALIGN,''),'center'),DISPLAY_FORMAT,CAST(COALESCE(IS_VISIBLE,1) AS bit),CAST(COALESCE(IS_DEFAULT_FIELDS,0) AS bit),CAST(COALESCE(IS_QUERY,1) AS bit),CAST(COALESCE(IS_READONLY,0) AS bit),CAST(COALESCE(IS_VERIFY,0) AS bit),CAST(COALESCE(IS_COST,0) AS bit),CAST(COALESCE(IS_SECRECY,0) AS bit),DFT_VALUE,VERIFY_INDEX,REGEX,F_REMARK,BROWSE_URL,BROWSE_M_IDX,CAST(COALESCE(ONLY_CHOOSE,0) AS bit),CAST(COALESCE(CHOOSE_MULTI,0) AS bit),CHOOSE_PAGE,CAST(COALESCE(CHOOSE_ACTIVE1,0) AS bit),CHOOSE_T_ID1,CHOOSE_T_DESC1,CHOOSE_M_IDX1,CHOOSE_FILTER1,CHOOSE_RETURNVAL1,CAST(COALESCE(CHOOSE_ACTIVE2,0) AS bit),CHOOSE_T_ID2,CHOOSE_T_DESC2,CHOOSE_M_IDX2,CHOOSE_FILTER2,CHOOSE_RETURNVAL2,CAST(COALESCE(CHOOSE_ACTIVE3,0) AS bit),CHOOSE_T_ID3,CHOOSE_T_DESC3,CHOOSE_M_IDX3,CHOOSE_FILTER3,CHOOSE_RETURNVAL3,CAST(COALESCE(CHOOSE_ACTIVE4,0) AS bit),CHOOSE_T_ID4,CHOOSE_T_DESC4,CHOOSE_M_IDX4,CHOOSE_FILTER4,CHOOSE_RETURNVAL4,CAST(COALESCE(IS_VIRTUAL,0) AS bit),VIRTUAL_EXP,CAST(COALESCE(CAN_COPY,1) AS bit),CAST(COALESCE(IS_AUTOINC,0) AS bit),CONVERT_FUNCTION,DATASOURCE_SQL,LAST_UPDATE_BY,LAST_UPDATE_DATE
            FROM dbo.FIELDS WITH (NOLOCK) WHERE T_ID=@Table AND LTRIM(RTRIM(F_ID))=@Field;
            """;
        await using var command=new SqlCommand(sql,connection);command.Parameters.Add("@Table",SqlDbType.NVarChar,100).Value=table;command.Parameters.Add("@Field",SqlDbType.NVarChar,100).Value=fieldKey.Trim();await using var reader=await command.ExecuteReaderAsync(token);if(!await reader.ReadAsync(token))return null;var key=reader.GetString(0);return Identifier.IsMatch(key)?new(key,reader.GetString(1),reader.GetString(2),Math.Clamp(reader.GetInt32(3),40,300),reader.GetString(4),reader.GetString(5),reader.IsDBNull(6)?null:reader.GetString(6),reader.GetBoolean(7),reader.GetBoolean(8),reader.GetBoolean(9),reader.GetBoolean(10),reader.GetBoolean(11),reader.GetBoolean(12),reader.GetBoolean(13),reader.IsDBNull(14)?null:reader.GetString(14),reader.IsDBNull(15)?null:reader.GetInt32(15),reader.IsDBNull(16)?null:reader.GetString(16),reader.IsDBNull(17)?null:reader.GetString(17),reader.IsDBNull(18)?null:reader.GetString(18),reader.IsDBNull(19)?null:reader.GetInt32(19),reader.GetBoolean(20),reader.GetBoolean(21),reader.IsDBNull(22)?null:reader.GetString(22),[ReadChooser(reader,23),ReadChooser(reader,29),ReadChooser(reader,35),ReadChooser(reader,41)],reader.GetBoolean(47),reader.IsDBNull(48)?null:reader.GetString(48),reader.GetBoolean(49),reader.GetBoolean(50),reader.IsDBNull(51)?null:reader.GetString(51),reader.IsDBNull(52)?null:reader.GetString(52),reader.IsDBNull(53)?null:reader.GetString(53),reader.IsDBNull(54)?null:reader.GetDateTime(54)):null;
    }

    private static FieldChooserSource ReadChooser(SqlDataReader reader,int offset)=>new(reader.GetBoolean(offset),reader.IsDBNull(offset+1)?null:reader.GetString(offset+1),reader.IsDBNull(offset+2)?null:reader.GetString(offset+2),reader.IsDBNull(offset+3)?null:reader.GetInt32(offset+3),reader.IsDBNull(offset+4)?null:reader.GetString(offset+4),reader.IsDBNull(offset+5)?null:reader.GetString(offset+5));

    public async Task UpdateFieldMetadataAsync(WorkbenchDefinition definition,bool detail,string fieldKey,UpdateWorkbenchFieldMetadata update,string userId,CancellationToken token)
    {
        var table=detail?definition.DetailTable:definition.MasterTable;if(table is null||!Identifier.IsMatch(fieldKey))throw new ArgumentException("字段无效。");
        var types=new HashSet<string>(["nvarchar","varchar","nchar","char","int","bigint","smallint","tinyint","decimal","numeric","float","real","money","smallmoney","date","datetime","datetime2","smalldatetime","time","bit"],StringComparer.OrdinalIgnoreCase);if(!types.Contains(update.DataType)||update.Label.Trim().Length is 0 or >300||update.Width is <40 or >300||update.Align is not ("left" or "center" or "right")||update.HeaderAlign is not ("left" or "center" or "right")||(update.Format?.Length??0)>50||(update.DefaultValue?.Length??0)>200||(update.Regex?.Length??0)>300||(update.Remark?.Length??0)>500||update.VerifyIndex is <0 or >9999||(update.BrowseUrl?.Length??0)>1000||(update.ChoosePage?.Length??0)>500||update.Choosers.Count!=4||update.Choosers.Any(item=>(item.Table?.Length??0)>300||(item.Description?.Length??0)>50||(item.Filter?.Length??0)>1000||(item.ReturnMapping?.Length??0)>8000))throw new ArgumentException("字段设置无效。");
        if(!string.IsNullOrWhiteSpace(update.BrowseUrl)&&(!Uri.TryCreate(update.BrowseUrl,UriKind.Relative,out _)||update.BrowseUrl.TrimStart().StartsWith("//")))throw new ArgumentException("查看详情 URL 仅允许站内相对路径。");
        await using var connection=CreateConnection();await connection.OpenAsync(token);const string sql="""
            UPDATE dbo.FIELDS SET F_DESC=@Label,DISPLAY_LENGTH=@Width,ITEM_ALIGN=@Align,HEADER_ALIGN=@HeaderAlign,DISPLAY_FORMAT=@Format,IS_VISIBLE=@Visible,IS_DEFAULT_FIELDS=@Default,IS_QUERY=@Queryable,IS_READONLY=@Readonly,IS_VERIFY=@Required,IS_COST=@Cost,IS_SECRECY=@Secrecy,DFT_VALUE=@DefaultValue,VERIFY_INDEX=@VerifyIndex,REGEX=@Regex,F_REMARK=@Remark,BROWSE_URL=@BrowseUrl,BROWSE_M_IDX=@BrowseModuleId,ONLY_CHOOSE=@OnlyChoose,CHOOSE_MULTI=@ChooseMultiple,CHOOSE_PAGE=@ChoosePage,CHOOSE_ACTIVE1=@Active1,CHOOSE_T_ID1=@Table1,CHOOSE_T_DESC1=@Description1,CHOOSE_M_IDX1=@Module1,CHOOSE_FILTER1=@Filter1,CHOOSE_RETURNVAL1=@Return1,CHOOSE_ACTIVE2=@Active2,CHOOSE_T_ID2=@Table2,CHOOSE_T_DESC2=@Description2,CHOOSE_M_IDX2=@Module2,CHOOSE_FILTER2=@Filter2,CHOOSE_RETURNVAL2=@Return2,CHOOSE_ACTIVE3=@Active3,CHOOSE_T_ID3=@Table3,CHOOSE_T_DESC3=@Description3,CHOOSE_M_IDX3=@Module3,CHOOSE_FILTER3=@Filter3,CHOOSE_RETURNVAL3=@Return3,CHOOSE_ACTIVE4=@Active4,CHOOSE_T_ID4=@Table4,CHOOSE_T_DESC4=@Description4,CHOOSE_M_IDX4=@Module4,CHOOSE_FILTER4=@Filter4,CHOOSE_RETURNVAL4=@Return4,LAST_UPDATE_BY=@UserId,LAST_UPDATE_DATE=GETDATE() WHERE T_ID=@Table AND F_ID=@Field AND COALESCE(IS_VIRTUAL,0)=0;
            """;
        await using var command=new SqlCommand(sql,connection);command.Parameters.Add("@Label",SqlDbType.NVarChar,300).Value=update.Label.Trim();command.Parameters.Add("@Width",SqlDbType.Int).Value=update.Width;command.Parameters.Add("@Align",SqlDbType.NVarChar,50).Value=update.Align;command.Parameters.Add("@HeaderAlign",SqlDbType.NVarChar,50).Value=update.HeaderAlign;command.Parameters.Add("@Format",SqlDbType.NVarChar,50).Value=(object?)update.Format??DBNull.Value;command.Parameters.Add("@Visible",SqlDbType.Bit).Value=update.IsVisible;command.Parameters.Add("@Default",SqlDbType.Bit).Value=update.IsDefault;command.Parameters.Add("@Queryable",SqlDbType.Bit).Value=update.IsQueryable;command.Parameters.Add("@Readonly",SqlDbType.Bit).Value=update.IsReadonly;command.Parameters.Add("@Required",SqlDbType.Bit).Value=update.IsRequired;command.Parameters.Add("@Cost",SqlDbType.Bit).Value=update.IsCost;command.Parameters.Add("@Secrecy",SqlDbType.Bit).Value=update.IsSecrecy;command.Parameters.Add("@DefaultValue",SqlDbType.NVarChar,200).Value=(object?)update.DefaultValue??DBNull.Value;command.Parameters.Add("@VerifyIndex",SqlDbType.Int).Value=(object?)update.VerifyIndex??DBNull.Value;command.Parameters.Add("@Regex",SqlDbType.NVarChar,300).Value=(object?)update.Regex??DBNull.Value;command.Parameters.Add("@Remark",SqlDbType.NVarChar,500).Value=(object?)update.Remark??DBNull.Value;command.Parameters.Add("@BrowseUrl",SqlDbType.VarChar,1000).Value=(object?)update.BrowseUrl??DBNull.Value;command.Parameters.Add("@BrowseModuleId",SqlDbType.Int).Value=(object?)update.BrowseModuleId??DBNull.Value;command.Parameters.Add("@OnlyChoose",SqlDbType.Bit).Value=update.OnlyChoose;command.Parameters.Add("@ChooseMultiple",SqlDbType.Bit).Value=update.ChooseMultiple;command.Parameters.Add("@ChoosePage",SqlDbType.NVarChar,500).Value=(object?)update.ChoosePage??DBNull.Value;for(var i=0;i<4;i++){var source=update.Choosers[i];var n=i+1;command.Parameters.Add($"@Active{n}",SqlDbType.Bit).Value=source.Active;command.Parameters.Add($"@Table{n}",SqlDbType.NVarChar,300).Value=(object?)source.Table??DBNull.Value;command.Parameters.Add($"@Description{n}",SqlDbType.NVarChar,50).Value=(object?)source.Description??DBNull.Value;command.Parameters.Add($"@Module{n}",SqlDbType.Int).Value=(object?)source.ModuleId??DBNull.Value;command.Parameters.Add($"@Filter{n}",SqlDbType.NVarChar,1000).Value=(object?)source.Filter??DBNull.Value;command.Parameters.Add($"@Return{n}",SqlDbType.VarChar,8000).Value=(object?)source.ReturnMapping??DBNull.Value;}command.Parameters.Add("@UserId",SqlDbType.NVarChar,50).Value=userId;command.Parameters.Add("@Table",SqlDbType.NVarChar,100).Value=table;command.Parameters.Add("@Field",SqlDbType.NVarChar,100).Value=fieldKey;if(await command.ExecuteNonQueryAsync(token)!=1)throw new ArgumentException("字段不存在或不可设置。");
    }

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
    }

    public async Task ResetColumnSettingsAsync(WorkbenchDefinition definition,string userId,CancellationToken token)
    {
        await using var connection=CreateConnection();await connection.OpenAsync(token);const string sql="DELETE FROM dbo.SYSQL_FIELDS WHERE USER_ID=@UserId AND T_ID=@MasterTable";
        await using var command=new SqlCommand(sql,connection);command.Parameters.Add("@UserId",SqlDbType.NChar,10).Value=userId.Trim();command.Parameters.Add("@MasterTable",SqlDbType.NVarChar,100).Value=definition.MasterTable;await command.ExecuteNonQueryAsync(token);
    }

    public async Task<WorkbenchData> GetRowsAsync(WorkbenchDefinition definition, bool detail, IReadOnlyDictionary<string,string> keys, int page, int pageSize, CancellationToken token, WorkbenchQuery? query=null, string? sortField=null, string? sortDirection=null)
    {
        var table=detail?definition.DetailTable:definition.MasterTable; var fields=detail?definition.DetailFields:definition.MasterFields;
        page=Math.Max(1,page); pageSize=Math.Clamp(pageSize,10,100);
        if (table is null || fields.Count==0) return new([],0,page,pageSize);
        var selected=fields.Take(30).ToList(); var predicates=new List<string>();
        await using var connection=CreateConnection(); await connection.OpenAsync(token); await using var command=new SqlCommand(); command.Connection=connection;
        if (detail) foreach(var key in definition.MasterFields.Where(field=>field.IsPrimaryKey)) if(keys.TryGetValue(key.Key,out var value) && definition.DetailFields.Any(field=>field.Key.Equals(key.Key,StringComparison.OrdinalIgnoreCase))) { var name=$"@k{predicates.Count}"; predicates.Add($"[{key.Key}]={name}"); command.Parameters.AddWithValue(name,value); }
        if(detail && predicates.Count==0) return new([],0,page,pageSize);
        if (!detail && query is not null) AddQueryPredicates(query, definition.MasterFields, predicates, command);
        var where=predicates.Count>0?" WHERE "+string.Join(" AND ",predicates):"";
        var order=ResolveOrder(definition,fields,selected,detail,sortField,sortDirection);
        command.CommandText=$"SELECT COUNT_BIG(1) FROM dbo.[{table}] WITH (NOLOCK){where}; SELECT {string.Join(',',selected.Select(field=>$"[{field.Key}]"))} FROM dbo.[{table}] WITH (NOLOCK){where} ORDER BY {order} OFFSET @Offset ROWS FETCH NEXT @PageSize ROWS ONLY;";
        command.Parameters.Add("@Offset",SqlDbType.Int).Value=(page-1)*pageSize;command.Parameters.Add("@PageSize",SqlDbType.Int).Value=pageSize;
        await using var reader=await command.ExecuteReaderAsync(token); await reader.ReadAsync(token);var total=Convert.ToInt32(reader.GetInt64(0));await reader.NextResultAsync(token); var rows=new List<Dictionary<string,object?>>();
        while(await reader.ReadAsync(token)){var row=new Dictionary<string,object?>(StringComparer.OrdinalIgnoreCase); for(var i=0;i<reader.FieldCount;i++) row[reader.GetName(i)]=reader.IsDBNull(i)?null:reader.GetValue(i); rows.Add(row);} return new(rows,total,page,pageSize);
    }

    private static async Task<IReadOnlyList<WorkbenchColumn>> ReadDefaultColumnSettings(SqlConnection connection,string masterTable,string targetTable,CancellationToken token)
    {
        const string sql="""
            SELECT LTRIM(RTRIM(f.F_ID)),COALESCE(NULLIF(LTRIM(RTRIM(f.F_DESC)),''),LTRIM(RTRIM(f.F_ID))),CAST(CASE WHEN d.F_ID IS NULL THEN 0 ELSE 1 END AS bit),COALESCE(d.F_IDX,COALESCE(f.VERIFY_INDEX,999))
            FROM dbo.FIELDS f WITH (NOLOCK) LEFT JOIN dbo.SYSQL_DEFAULT d WITH (NOLOCK) ON d.T_ID=@MasterTable AND d.T_ID_R=@TargetTable AND LTRIM(RTRIM(d.F_ID))=LTRIM(RTRIM(f.F_ID))
            WHERE f.T_ID=@TargetTable AND COALESCE(f.IS_VISIBLE,1)=1 AND COALESCE(f.IS_VIRTUAL,0)=0 AND COALESCE(f.IS_COST,0)=0 AND COALESCE(f.IS_SECRECY,0)=0 ORDER BY CASE WHEN d.F_ID IS NULL THEN 1 ELSE 0 END,COALESCE(d.F_IDX,COALESCE(f.VERIFY_INDEX,999)),f.F_ID;
            """;
        await using var command=new SqlCommand(sql,connection);command.Parameters.Add("@MasterTable",SqlDbType.NVarChar,100).Value=masterTable;command.Parameters.Add("@TargetTable",SqlDbType.NVarChar,100).Value=targetTable;await using var reader=await command.ExecuteReaderAsync(token);var result=new List<WorkbenchColumn>();var order=0;while(await reader.ReadAsync(token)){var key=reader.GetString(0);if(Identifier.IsMatch(key))result.Add(new(key,reader.GetString(1),reader.GetBoolean(2),++order));}return result;
    }

    private static async Task<IReadOnlyList<WorkbenchColumn>> ReadColumnSettings(SqlConnection connection,string userId,string masterTable,string targetTable,CancellationToken token,bool defaultsOnly=false)
    {
        const string sql="""
            WITH UserFields AS (SELECT LTRIM(RTRIM(F_ID)) F_ID,F_IDX FROM dbo.SYSQL_FIELDS WITH (NOLOCK) WHERE USER_ID=@UserId AND T_ID=@MasterTable AND T_ID_R=@TargetTable),
            HasConfig AS (SELECT CASE WHEN EXISTS(SELECT 1 FROM UserFields) THEN 1 ELSE 0 END Value)
            SELECT LTRIM(RTRIM(f.F_ID)),COALESCE(NULLIF(LTRIM(RTRIM(f.F_DESC)),''),LTRIM(RTRIM(f.F_ID))),CAST(CASE WHEN (h.Value=1 AND u.F_ID IS NOT NULL) OR (h.Value=0 AND COALESCE(f.IS_DEFAULT_FIELDS,0)=1) THEN 1 ELSE 0 END AS bit),COALESCE(u.F_IDX,COALESCE(f.VERIFY_INDEX,999))
            FROM dbo.FIELDS f WITH (NOLOCK) CROSS JOIN HasConfig h LEFT JOIN UserFields u ON u.F_ID=LTRIM(RTRIM(f.F_ID))
            WHERE f.T_ID=@TargetTable AND COALESCE(f.IS_VISIBLE,1)=1 AND COALESCE(f.IS_VIRTUAL,0)=0 ORDER BY CASE WHEN u.F_IDX IS NULL THEN 1 ELSE 0 END,COALESCE(u.F_IDX,COALESCE(f.VERIFY_INDEX,999)),f.F_ID;
            """;
        await using var command=new SqlCommand(sql,connection);command.Parameters.Add("@UserId",SqlDbType.NChar,10).Value=userId.Trim();command.Parameters.Add("@MasterTable",SqlDbType.NVarChar,100).Value=masterTable;command.Parameters.Add("@TargetTable",SqlDbType.NVarChar,100).Value=targetTable;
        await using var reader=await command.ExecuteReaderAsync(token);var result=new List<WorkbenchColumn>();var order=0;while(await reader.ReadAsync(token)){var key=reader.GetString(0);if(Identifier.IsMatch(key))result.Add(new(key,reader.GetString(1),reader.GetBoolean(2),++order));}return result;
    }

    private static async Task SaveColumns(SqlConnection connection,SqlTransaction transaction,string userId,string masterTable,string targetTable,IReadOnlyList<string> fields,CancellationToken token)
    {
        if(fields.Count>200||fields.Distinct(StringComparer.OrdinalIgnoreCase).Count()!=fields.Count)throw new ArgumentException("字段配置无效。");
        var allowed=await ReadAllowedFieldKeys(connection,transaction,targetTable,token);if(fields.Any(field=>!allowed.Contains(field)))throw new ArgumentException("字段配置包含无效字段。");
        await using(var delete=new SqlCommand("DELETE FROM dbo.SYSQL_FIELDS WHERE USER_ID=@UserId AND T_ID=@MasterTable AND T_ID_R=@TargetTable",connection,transaction)){delete.Parameters.Add("@UserId",SqlDbType.NChar,10).Value=userId.Trim();delete.Parameters.Add("@MasterTable",SqlDbType.NVarChar,100).Value=masterTable;delete.Parameters.Add("@TargetTable",SqlDbType.NVarChar,100).Value=targetTable;await delete.ExecuteNonQueryAsync(token);}
        for(var i=0;i<fields.Count;i++){await using var insert=new SqlCommand("INSERT INTO dbo.SYSQL_FIELDS (USER_ID,T_ID,T_ID_R,F_ID,F_IDX) VALUES (@UserId,@MasterTable,@TargetTable,@Field,@Index)",connection,transaction);insert.Parameters.Add("@UserId",SqlDbType.NChar,10).Value=userId.Trim();insert.Parameters.Add("@MasterTable",SqlDbType.NVarChar,100).Value=masterTable;insert.Parameters.Add("@TargetTable",SqlDbType.NVarChar,100).Value=targetTable;insert.Parameters.Add("@Field",SqlDbType.NVarChar,100).Value=fields[i];insert.Parameters.Add("@Index",SqlDbType.Int).Value=i+1;await insert.ExecuteNonQueryAsync(token);}
    }

    private static async Task<HashSet<string>> ReadAllowedFieldKeys(SqlConnection connection,SqlTransaction transaction,string targetTable,CancellationToken token)
    {await using var command=new SqlCommand("SELECT LTRIM(RTRIM(F_ID)) FROM dbo.FIELDS WHERE T_ID=@TargetTable AND COALESCE(IS_VISIBLE,1)=1 AND COALESCE(IS_VIRTUAL,0)=0",connection,transaction);command.Parameters.Add("@TargetTable",SqlDbType.NVarChar,100).Value=targetTable;await using var reader=await command.ExecuteReaderAsync(token);var result=new HashSet<string>(StringComparer.OrdinalIgnoreCase);while(await reader.ReadAsync(token)){var key=reader.GetString(0);if(Identifier.IsMatch(key))result.Add(key);}return result;}

    private static async Task<IReadOnlyList<WorkbenchField>> ReadFields(SqlConnection connection,string userId,string masterTable,string targetTable,bool canViewCost,bool canViewSecrecy,IReadOnlySet<string> deniedFields,CancellationToken token)
    {
        const string sql="""
            WITH UserFields AS (
              SELECT LTRIM(RTRIM(F_ID)) F_ID,F_IDX FROM dbo.SYSQL_FIELDS WITH (NOLOCK)
              WHERE USER_ID=@UserId AND T_ID=@MasterTable AND T_ID_R=@TargetTable
            ), HasConfig AS (SELECT CASE WHEN EXISTS(SELECT 1 FROM UserFields) THEN 1 ELSE 0 END Value)
            SELECT f.F_ID,COALESCE(NULLIF(LTRIM(RTRIM(f.F_DESC)),''),f.F_ID),COALESCE(f.F_TYPE,'nvarchar'),
                   COALESCE(f.DISPLAY_LENGTH,100),COALESCE(NULLIF(f.ITEM_ALIGN,''),'left'),CAST(CASE WHEN EXISTS(SELECT 1 FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS tc INNER JOIN INFORMATION_SCHEMA.KEY_COLUMN_USAGE ku ON ku.CONSTRAINT_NAME=tc.CONSTRAINT_NAME AND ku.CONSTRAINT_SCHEMA=tc.CONSTRAINT_SCHEMA WHERE tc.CONSTRAINT_TYPE='PRIMARY KEY' AND ku.TABLE_SCHEMA='dbo' AND ku.TABLE_NAME=@TargetTable AND ku.COLUMN_NAME=f.F_ID) THEN 1 ELSE 0 END AS bit),CAST(COALESCE(f.IS_QUERY,1) AS bit),CAST(COALESCE(f.IS_COST,0) AS bit),CAST(COALESCE(f.IS_SECRECY,0) AS bit),COALESCE(NULLIF(f.HEADER_ALIGN,''),'center'),f.DISPLAY_FORMAT,f.BROWSE_URL,f.BROWSE_M_IDX
            FROM dbo.FIELDS f WITH (NOLOCK) CROSS JOIN HasConfig h LEFT JOIN UserFields u ON u.F_ID=f.F_ID
            WHERE f.T_ID=@TargetTable AND COALESCE(f.IS_VISIBLE,1)=1 AND COALESCE(f.IS_VIRTUAL,0)=0
              AND (EXISTS(SELECT 1 FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS tc INNER JOIN INFORMATION_SCHEMA.KEY_COLUMN_USAGE ku ON ku.CONSTRAINT_NAME=tc.CONSTRAINT_NAME AND ku.CONSTRAINT_SCHEMA=tc.CONSTRAINT_SCHEMA WHERE tc.CONSTRAINT_TYPE='PRIMARY KEY' AND ku.TABLE_SCHEMA='dbo' AND ku.TABLE_NAME=@TargetTable AND ku.COLUMN_NAME=f.F_ID) OR (h.Value=1 AND u.F_ID IS NOT NULL) OR (h.Value=0 AND COALESCE(f.IS_DEFAULT_FIELDS,0)=1))
            ORDER BY CASE WHEN EXISTS(SELECT 1 FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS tc INNER JOIN INFORMATION_SCHEMA.KEY_COLUMN_USAGE ku ON ku.CONSTRAINT_NAME=tc.CONSTRAINT_NAME AND ku.CONSTRAINT_SCHEMA=tc.CONSTRAINT_SCHEMA WHERE tc.CONSTRAINT_TYPE='PRIMARY KEY' AND ku.TABLE_SCHEMA='dbo' AND ku.TABLE_NAME=@TargetTable AND ku.COLUMN_NAME=f.F_ID) AND u.F_ID IS NULL THEN 0 ELSE 1 END,COALESCE(u.F_IDX,COALESCE(f.VERIFY_INDEX,999)),f.F_ID;
            """;
        await using var command=new SqlCommand(sql,connection);command.Parameters.Add("@UserId",SqlDbType.NChar,10).Value=userId.Trim();command.Parameters.Add("@MasterTable",SqlDbType.NVarChar,100).Value=masterTable;command.Parameters.Add("@TargetTable",SqlDbType.NVarChar,100).Value=targetTable;
        await using var reader=await command.ExecuteReaderAsync(token);var fields=new List<WorkbenchField>();while(await reader.ReadAsync(token)){var key=reader.GetString(0).Trim();if(Identifier.IsMatch(key)&&!deniedFields.Contains(key)&&(canViewCost||!reader.GetBoolean(7))&&(canViewSecrecy||!reader.GetBoolean(8)))fields.Add(new(key,reader.GetString(1).Trim(),reader.GetString(2).Trim(),Math.Clamp(reader.GetInt32(3),40,300),reader.GetString(4).Trim(),reader.GetBoolean(5),true,reader.GetBoolean(6),reader.GetString(9),reader.IsDBNull(10)?null:reader.GetString(10),reader.IsDBNull(11)?null:reader.GetString(11),reader.IsDBNull(12)?null:reader.GetInt32(12)));}return fields;
    }
    private static string ResolveOrder(WorkbenchDefinition definition,IReadOnlyList<WorkbenchField> fields,IReadOnlyList<WorkbenchField> selected,bool detail,string? sortField,string? sortDirection)
    {
        if(!string.IsNullOrWhiteSpace(sortField)){var field=fields.FirstOrDefault(item=>item.Key.Equals(sortField,StringComparison.OrdinalIgnoreCase))??throw new ArgumentException("排序字段无效。");var direction=sortDirection?.Equals("desc",StringComparison.OrdinalIgnoreCase)==true?"DESC":"ASC";return $"[{field.Key}] {direction}";}
        if(!detail&&!string.IsNullOrWhiteSpace(definition.DefaultSort))return definition.DefaultSort;
        var keys=fields.Where(field=>field.IsPrimaryKey).ToList();if(keys.Count==0)keys=[selected[0]];return string.Join(',',keys.Select(field=>$"[{field.Key}]"));
    }

    private static string? NormalizeSort(string? value,string table,IReadOnlyList<WorkbenchField> fields)
    {
        if(string.IsNullOrWhiteSpace(value))return null;var allowed=fields.Select(field=>field.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);var result=new List<string>();
        foreach(var part in value.Split(',',StringSplitOptions.TrimEntries|StringSplitOptions.RemoveEmptyEntries)){var tokens=Regex.Split(part.Trim(),"\\s+");if(tokens.Length is <1 or >2)return null;var identifier=tokens[0].Split('.');if(identifier.Length==2&&!identifier[0].Equals(table,StringComparison.OrdinalIgnoreCase))return null;var field=identifier[^1].Trim('[',']');if(!Identifier.IsMatch(field)||!allowed.Contains(field))return null;var direction=tokens.Length==2&&tokens[1].Equals("DESC",StringComparison.OrdinalIgnoreCase)?" DESC":tokens.Length==1||tokens[1].Equals("ASC",StringComparison.OrdinalIgnoreCase)?" ASC":null;if(direction is null)return null;result.Add($"[{field}]{direction}");}
        return result.Count==0?null:string.Join(',',result);
    }

    private static void AddQueryPredicates(WorkbenchQuery query,IReadOnlyList<WorkbenchField> fields,List<string> predicates,SqlCommand command)
    {
        if(query.Conditions.Count>20)throw new ArgumentException("查询条件不能超过 20 个。");
        var queryPredicates=new List<string>();
        foreach(var condition in query.Conditions)
        {
            var field=fields.FirstOrDefault(item=>item.IsQueryable&&item.Key.Equals(condition.Field,StringComparison.OrdinalIgnoreCase))??throw new ArgumentException($"无效查询字段：{condition.Field}");
            var name=$"@q{command.Parameters.Count}"; var column=$"[{field.Key}]"; var op=condition.Operator.ToLowerInvariant(); string expression;
            switch(op)
            {
                case "eq": case "ne": case "gt": case "gte": case "lt": case "lte":
                    var sqlOperator=op switch { "eq"=>"=", "ne"=>"<>", "gt"=>">", "gte"=>">=", "lt"=>"<", _=>"<=" }; expression=$"{column} {sqlOperator} {name}"; command.Parameters.AddWithValue(name,condition.Value??""); break;
                case "contains": case "notcontains": case "startswith": case "endswith":
                    expression=$"{column} {(op=="notcontains"?"NOT LIKE":"LIKE")} {name}"; var value=condition.Value??""; command.Parameters.AddWithValue(name,op is "contains" or "notcontains"?$"%{value}%":op=="startswith"?$"{value}%":$"%{value}"); break;
                case "empty": expression=$"({column} IS NULL OR {column}='')"; break;
                case "notempty": expression=$"({column} IS NOT NULL AND {column}<>'')"; break;
                case "between": expression=$"{column} BETWEEN {name} AND {name}b"; command.Parameters.AddWithValue(name,condition.Value??""); command.Parameters.AddWithValue(name+"b",condition.ValueTo??""); break;
                default: throw new ArgumentException($"无效查询运算符：{condition.Operator}");
            }
            queryPredicates.Add((queryPredicates.Count>0&&condition.Logic.Equals("or",StringComparison.OrdinalIgnoreCase)?"OR ":"AND ")+expression);
        }
        if(queryPredicates.Count>0){queryPredicates[0]=queryPredicates[0][4..];predicates.Add("("+string.Join(' ',queryPredicates)+")");}
    }
    private SqlConnection CreateConnection()=>new(configuration.GetConnectionString("ErpDatabase")??throw new InvalidOperationException("ConnectionStrings:ErpDatabase 未配置。"));
    private static bool IsWorkbenchUrl(string url){var value=url.Trim().Replace('\\','/');var query=value.IndexOfAny(['?','#']);if(query>=0)value=value[..query];while(value.StartsWith("~/")||value.StartsWith('/'))value=value.TrimStart('~','/');return value.Equals("comm/view_frame.aspx",StringComparison.OrdinalIgnoreCase);}
}
