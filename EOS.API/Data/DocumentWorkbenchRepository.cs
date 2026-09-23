using EOS.API.Models;
using Microsoft.Data.SqlClient;
using System.Data;

namespace EOS.API.Data;

/// <summary>
/// Controller-facing workbench facade. Definition/form building is delegated to
/// WorkbenchDefinitionBuilder, field metadata and column settings to WorkbenchFieldMetaMapper,
/// row queries and exports to WorkbenchQueryComposer, and record writes and approvals to
/// WorkbenchCommandHandler / WorkbenchApprovalService. This type also implements the read-only
/// assistant search gateway over safe module/field metadata.
/// </summary>
public sealed class DocumentWorkbenchRepository(
    DbConnectionFactory connections,
    WorkbenchDefinitionBuilder definitionBuilder,
    WorkbenchFieldMetaMapper fieldMetaMapper,
    WorkbenchQueryComposer queryComposer,
    WorkbenchCommandHandler commandHandler,
    WorkbenchApprovalService approvalService,
    DocumentActions.DocumentActionRegistry documentActions,
    DocumentActions.DocumentActionAuthorization documentActionAuthorization,
    ILogger<DocumentWorkbenchRepository> logger) : Features.Assistant.Tools.IWorkbenchSearchGateway
{
    private SqlConnection CreateConnection()=>connections.Create();

    /// <summary>Resolves the per-user workbench definition from snapshot baseline when clean, or from live metadata.</summary>
    public async Task<WorkbenchDefinition?> GetDefinitionAsync(int moduleId, string userId, string? execTag, bool canViewCost, bool canViewSecrecy, IReadOnlySet<string> deniedMasterFields, IReadOnlySet<string> deniedDetailFields, CancellationToken token)
        => await definitionBuilder.GetDefinitionAsync(moduleId, userId, execTag, canViewCost, canViewSecrecy, deniedMasterFields, deniedDetailFields, token);

    /// <summary>Builds the unified form definition (permission-filtered entry field view for the requested mode).</summary>
    public async Task<FormDefinition?> GetFormDefinitionAsync(
        WorkbenchDefinition definition,
        string userId,
        string mode,
        bool canViewCost,
        bool canViewSecrecy,
        IReadOnlySet<string> deniedMasterFields,
        IReadOnlySet<string> deniedDetailFields,
        IReadOnlySet<string> deniedNewMasterFields,
        IReadOnlySet<string> deniedNewDetailFields,
        IReadOnlySet<string> deniedModiMasterFields,
        IReadOnlySet<string> deniedModiDetailFields,
        CancellationToken token,
        bool canAddNew = false,
        bool canEdit = false,
        bool canDelete = false,
        bool canApprove = false,
        bool canDeapprove = false,
        bool canEndCase = false,
        bool canUnEndCase = false,
        bool canFileView = false,
        bool canFileUpda = false,
        bool canFileEdit = false,
        bool canFileDele = false,
        bool canSetup = false)
    {
        var form = await definitionBuilder.GetFormDefinitionAsync(definition, userId, mode, canViewCost, canViewSecrecy,
            deniedMasterFields, deniedDetailFields, deniedNewMasterFields, deniedNewDetailFields,
            deniedModiMasterFields, deniedModiDetailFields, token, canAddNew, canEdit, canDelete, canApprove,
            canDeapprove, canEndCase, canUnEndCase, canFileView, canFileUpda, canFileEdit, canFileDele, canSetup);
        return form is null ? null : form with { UserActions = await BuildUserActionsAsync(definition, userId, token) };
    }

    /// <summary>
    /// 按钮元数据随定义下发：只有该用户获授权的操作才出现在名单里（前端据此渲染，不硬编码操作）。
    /// 未授权即不下发——"配了没人能用"是正常状态，此时按钮对所有人都不显示。
    /// </summary>
    private async Task<IReadOnlyList<DocumentActionMetadata>> BuildUserActionsAsync(
        WorkbenchDefinition definition, string userId, CancellationToken token)
    {
        var configured = DocumentActions.DocumentActionConfigs.Parse(definition.BusinessActions);
        if (configured.Count == 0)
        {
            return [];
        }
        var authorized = await documentActionAuthorization.AuthorizedKeysAsync(
            userId, definition.ModuleId, configured.Select(config => config.Key).ToList(), token);
        return DocumentActions.DocumentActionMetadataFactory.Build(configured, authorized, documentActions);
    }

    public async Task<IReadOnlyList<FieldSetupLookup>> GetFieldSetupTablesAsync(CancellationToken token)
        => await fieldMetaMapper.GetFieldSetupTablesAsync(token);

    public async Task<IReadOnlyList<FieldSetupLookup>> GetFieldSetupModulesAsync(CancellationToken token)
        => await fieldMetaMapper.GetFieldSetupModulesAsync(token);

    public async Task<IReadOnlyList<WorkbenchFieldSummary>> GetFieldSummariesAsync(WorkbenchDefinition definition,bool detail,CancellationToken token)
        => await fieldMetaMapper.GetFieldSummariesAsync(definition, detail, token);

    public async Task<WorkbenchFieldMetadata?> GetFieldMetadataAsync(WorkbenchDefinition definition,bool detail,string fieldKey,CancellationToken token)
        => await fieldMetaMapper.GetFieldMetadataAsync(definition, detail, fieldKey, token);

    public async Task UpdateFieldMetadataAsync(WorkbenchDefinition definition,bool detail,string fieldKey,UpdateWorkbenchFieldMetadata update,string updatedBy,CancellationToken token)
        => await fieldMetaMapper.UpdateFieldMetadataAsync(definition, detail, fieldKey, update, updatedBy, token);

    public async Task UpdateColumnWidthsAsync(WorkbenchDefinition definition,UpdateColumnWidthsRequest request,string updatedBy,CancellationToken token)
        => await fieldMetaMapper.UpdateColumnWidthsAsync(definition, request, updatedBy, token);

    public async Task<WorkbenchColumnSettings> GetDefaultColumnSettingsAsync(WorkbenchDefinition definition,string userId,CancellationToken token)
        => await fieldMetaMapper.GetDefaultColumnSettingsAsync(definition, userId, token);

    public async Task<WorkbenchColumnSettings> GetColumnSettingsAsync(WorkbenchDefinition definition,string userId,CancellationToken token)
        => await fieldMetaMapper.GetColumnSettingsAsync(definition, userId, token);

    public async Task<(WorkbenchColumnSettings Current,WorkbenchColumnSettings Defaults)> GetColumnEditorSettingsAsync(WorkbenchDefinition definition,string userId,CancellationToken token)
        => await fieldMetaMapper.GetColumnEditorSettingsAsync(definition, userId, token);

    public async Task SaveColumnSettingsAsync(WorkbenchDefinition definition,string userId,SaveWorkbenchColumns settings,CancellationToken token)
        => await fieldMetaMapper.SaveColumnSettingsAsync(definition, userId, settings, token);

    public async Task ResetColumnSettingsAsync(WorkbenchDefinition definition,string userId,CancellationToken token)
        => await fieldMetaMapper.ResetColumnSettingsAsync(definition, userId, token);

    public async Task<WorkbenchData> GetRowsAsync(WorkbenchDefinition definition, bool detail, IReadOnlyDictionary<string,string> keys, int page, int pageSize, CancellationToken token, WorkbenchQuery? query=null, string? keyword=null, string? sortField=null, string? sortDirection=null, int? groupIndex=null, string? groupValue=null, string? dataFilter=null)
        => await queryComposer.GetRowsAsync(definition,detail,keys,page,pageSize,token,query,keyword,sortField,sortDirection,groupIndex,groupValue,dataFilter);

    public async Task<IReadOnlyList<Dictionary<string,object?>>> GetExportRowsAsync(
        WorkbenchDefinition definition,WorkbenchQuery? query,string? keyword,CancellationToken token,
        string? sortField=null,string? sortDirection=null,int? groupIndex=null,string? groupValue=null,
        IReadOnlyList<WorkbenchField>? exportFields=null,string? dataFilter=null)
        => await queryComposer.GetExportRowsAsync(definition,query,keyword,token,sortField,sortDirection,groupIndex,groupValue,exportFields,dataFilter);

    public async Task<IReadOnlyList<Dictionary<string,object?>>> GetExportRowsByKeysAsync(
        WorkbenchDefinition definition,IReadOnlyList<IReadOnlyList<string>> keys,CancellationToken token,
        int? groupIndex=null,string? groupValue=null,IReadOnlyList<WorkbenchField>? exportFields=null,string? dataFilter=null)
        => await queryComposer.GetExportRowsByKeysAsync(definition,keys,token,groupIndex,groupValue,exportFields,dataFilter);

    /// <summary>Picks export columns from the definition fields by a whitelisted key list.</summary>
    public static IReadOnlyList<WorkbenchField> ResolveExportFields(
        IReadOnlyList<WorkbenchField> fields,IReadOnlyList<string>? columnKeys)
        => WorkbenchQueryComposer.ResolveExportFields(fields,columnKeys);

    public async Task<RecordReadResult> GetRecordAsync(
        WorkbenchDefinition definition,FormDefinition form,IReadOnlyList<string> keyValues,string? dataFilter,CancellationToken token)
        => await commandHandler.GetRecordAsync(definition,form,keyValues,dataFilter,token);

    public async Task<RecordSaveResult> CreateRecordAsync(
        WorkbenchDefinition definition,FormDefinition form,SaveRecordRequest request,string employeeName,string userId,string? dataFilter,CancellationToken token)
        => await commandHandler.CreateRecordAsync(definition,form,request,employeeName,userId,dataFilter,token);

    public async Task<RecordSaveResult> UpdateRecordAsync(
        WorkbenchDefinition definition,FormDefinition form,IReadOnlyList<string> keyValues,SaveRecordRequest request,string employeeName,string userId,string? dataFilter,CancellationToken token)
        => await commandHandler.UpdateRecordAsync(definition,form,keyValues,request,employeeName,userId,dataFilter,token);

    public async Task<RecordSaveResult> DeleteRecordAsync(
        WorkbenchDefinition definition,FormDefinition form,IReadOnlyList<string> keyValues,string userId,string? dataFilter,CancellationToken token,string? idempotencyKey=null)
        => await commandHandler.DeleteRecordAsync(definition,form,keyValues,userId,dataFilter,idempotencyKey,token);

    public async Task<RecordSaveResult> WorkflowAsync(
        WorkbenchDefinition definition,IReadOnlyList<string> keyValues,bool approve,string employeeName,string userId,CancellationToken token,string? idempotencyKey=null,string? message=null)
        => await approvalService.WorkflowAsync(definition,keyValues,approve,employeeName,userId,idempotencyKey,token,message);

    public async Task<RecordSaveResult> FinishAsync(
        WorkbenchDefinition definition,IReadOnlyList<string> keyValues,bool finish,string employeeName,string userId,CancellationToken token,string? idempotencyKey=null)
        => await approvalService.FinishAsync(definition,keyValues,finish,employeeName,userId,idempotencyKey,token);

    /// <summary>Finds the first generic workbench module whose title matches the keyword (server-side whitelist only).</summary>
    public async Task<int?> FindGenericModuleIdByTitleAsync(string titleKeyword, CancellationToken token)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(token);
        const string sql = """
            SELECT TOP 10 m.M_IDX, ISNULL(m.M_URL,'')
            FROM dbo.MODULES m WITH (NOLOCK)
            WHERE m.M_DESC LIKE @Keyword
              AND NULLIF(LTRIM(RTRIM(m.M_DESC)),'') IS NOT NULL
            ORDER BY m.SORT_IDX, m.M_IDX;
            """;
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@Keyword", SqlDbType.NVarChar, 100).Value = "%" + titleKeyword + "%";
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            var moduleId = reader.GetInt32(0);
            var url = reader.IsDBNull(1) ? "" : reader.GetString(1);
            if (ModuleRouteValidator.IsWorkbenchUrl(url))
            {
                logger.LogDebug("只读助手找到通用模块 titleKeyword={Keyword} module={ModuleId}", titleKeyword, moduleId);
                return moduleId;
            }
        }

        return null;
    }

    /// <summary>
    /// Knowledge search over module titles and field metadata (table/field/description/type).
    /// Only safe metadata is returned; high-risk expressions are never exposed.
    /// </summary>
    public async Task<SystemKnowledgeResult> SearchSystemKnowledgeAsync(string keyword, int max, CancellationToken token)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(token);

        var like = "%" + keyword.Trim() + "%";
        var modules = new List<SystemKnowledgeModule>();
        var fields = new List<SystemKnowledgeField>();

        await using (var command = new SqlCommand("""
            SELECT TOP (@Max) m.M_IDX, LTRIM(RTRIM(m.M_DESC))
            FROM dbo.MODULES m WITH (NOLOCK)
            WHERE m.M_DESC LIKE @Keyword AND NULLIF(LTRIM(RTRIM(m.M_DESC)),'') IS NOT NULL
            ORDER BY m.SORT_IDX, m.M_IDX;
            """, connection))
        {
            command.Parameters.Add("@Max", SqlDbType.Int).Value = max;
            command.Parameters.Add("@Keyword", SqlDbType.NVarChar, 100).Value = like;
            await using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
            {
                modules.Add(new(reader.GetInt32(0), reader.GetString(1)));
            }
        }

        await using (var command = new SqlCommand("""
            SELECT TOP (@Max) LTRIM(RTRIM(f.T_ID)), LTRIM(RTRIM(f.F_ID)),
                   LTRIM(RTRIM(COALESCE(f.F_DESC,''))), LTRIM(RTRIM(COALESCE(f.F_TYPE,'')))
            FROM dbo.FIELDS f WITH (NOLOCK)
            WHERE f.F_DESC LIKE @Keyword OR f.F_ID LIKE @Keyword OR f.T_ID LIKE @Keyword
            ORDER BY f.T_ID, f.F_ID;
            """, connection))
        {
            command.Parameters.Add("@Max", SqlDbType.Int).Value = max;
            command.Parameters.Add("@Keyword", SqlDbType.NVarChar, 100).Value = like;
            await using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
            {
                fields.Add(new(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    string.IsNullOrWhiteSpace(reader.GetString(3)) ? null : reader.GetString(3)));
            }
        }

        logger.LogDebug("系统知识检索 keyword={Keyword} modules={Modules} fields={Fields}",
            keyword, modules.Count, fields.Count);
        return new SystemKnowledgeResult(modules, fields);
    }

    /// <summary>Lists module titles (count plus the first max titles) as safe metadata for assistant use.</summary>
    public async Task<SystemModuleList> ListModulesAsync(int max, CancellationToken token)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(token);

        int total;
        await using (var countCommand = new SqlCommand("""
            SELECT COUNT(*) FROM dbo.MODULES WITH (NOLOCK)
            WHERE NULLIF(LTRIM(RTRIM(M_DESC)),'') IS NOT NULL;
            """, connection))
        {
            total = Convert.ToInt32(await countCommand.ExecuteScalarAsync(token));
        }

        var modules = new List<SystemKnowledgeModule>();
        await using (var command = new SqlCommand("""
            SELECT TOP (@Max) M_IDX, LTRIM(RTRIM(M_DESC))
            FROM dbo.MODULES WITH (NOLOCK)
            WHERE NULLIF(LTRIM(RTRIM(M_DESC)),'') IS NOT NULL
            ORDER BY SORT_IDX, M_IDX;
            """, connection))
        {
            command.Parameters.Add("@Max", SqlDbType.Int).Value = max;
            await using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
            {
                modules.Add(new(reader.GetInt32(0), reader.GetString(1)));
            }
        }

        logger.LogDebug("系统模块清单 total={Total} listed={Listed}", total, modules.Count);
        return new SystemModuleList(total, modules);
    }

    /// <summary>Returns generic workbench modules for assistant-side permission filtering.</summary>
    public async Task<IReadOnlyList<SystemKnowledgeModule>> ListAssistantModulesAsync(string? keyword, CancellationToken token)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(token);
        const string sql = """
            SELECT m.M_IDX, LTRIM(RTRIM(m.M_DESC))
            FROM dbo.MODULES m WITH (NOLOCK)
            WHERE NULLIF(LTRIM(RTRIM(m.M_DESC)), '') IS NOT NULL
              AND NULLIF(LTRIM(RTRIM(m.MASTER_TABLE)), '') IS NOT NULL
              AND LTRIM(RTRIM(ISNULL(m.M_URL, ''))) = '/workbench'
              AND (@Keyword = '' OR m.M_DESC LIKE @LikeKeyword)
            ORDER BY m.SORT_IDX, m.M_IDX;
            """;
        await using var command = new SqlCommand(sql, connection);
        var normalizedKeyword = keyword?.Trim() ?? string.Empty;
        command.Parameters.Add("@Keyword", SqlDbType.NVarChar, 100).Value = normalizedKeyword;
        command.Parameters.Add("@LikeKeyword", SqlDbType.NVarChar, 202).Value = "%" + normalizedKeyword + "%";
        await using var reader = await command.ExecuteReaderAsync(token);
        var modules = new List<SystemKnowledgeModule>();
        while (await reader.ReadAsync(token))
        {
            modules.Add(new(reader.GetInt32(0), reader.GetString(1)));
        }

        return modules;
    }
}
