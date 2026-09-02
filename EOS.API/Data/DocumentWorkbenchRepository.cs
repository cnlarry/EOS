using EOS.API.Errors;
using EOS.API.Models;
using EOS.API.Telemetry;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using System.Data;
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Text.Json.Serialization;

namespace EOS.API.Data;

public sealed record WorkbenchField(string Key, string Label, string DataType, int Width, string? Align, bool IsPrimaryKey, bool IsVisible = true, bool IsQueryable = true, string HeaderAlign = "center", string? Format = null, string? BrowseUrl = null, int? BrowseModuleId = null, bool IsVirtual = false, [property: JsonIgnore] string? VirtualExpression = null, [property: JsonIgnore] string? ConvertFunction = null, IReadOnlyList<string>? BrowseKeyFields = null);
public sealed record WorkbenchColumn(string Key, string Label, bool IsVisible, int Order, bool IsVirtual = false);
public sealed record WorkbenchColumnSettings(IReadOnlyList<WorkbenchColumn> Master, IReadOnlyList<WorkbenchColumn> Detail);
public sealed record SaveWorkbenchColumns(IReadOnlyList<string> Master, IReadOnlyList<string> Detail);
public sealed record UpdateColumnWidthsRequest(IReadOnlyDictionary<string, int> Master, IReadOnlyDictionary<string, int>? Detail);
public sealed record WorkbenchFieldSummary(string Key,string Label,bool IsVisible,bool IsDefault,bool IsQueryable,bool IsReadonly,bool IsCost,bool IsSecrecy,bool IsVirtual);
/// <summary>
/// 选择器数据源（ADR-008）：Filter=FILTER_STRUCT JSON（仅字段设置可见；普通用户表单定义不下发，
/// 运行期由 form-chooser 端点按 SerialNo 从 FIELD_DATASOURCE 取权威定义），ReturnMapping=RETURN_ITEMS JSON。
/// </summary>
public sealed record FieldChooserSource(
    bool Active,
    string? Table,
    string? Description,
    int? ModuleId,
    string? Filter,
    string? ReturnMapping,
    int? SerialNo = null);
/// <summary>工作台/表单业务按钮（解析自 MODULES.FORM_BUTTONS，如 '1=copy;2=approve;3=print'）。</summary>
public sealed record WorkbenchButton(string Action);
public sealed record WorkbenchFieldMetadata(string Key,string Label,string DataType,int Width,string? Align,string HeaderAlign,string? Format,bool IsVisible,bool IsDefault,bool IsQueryable,bool IsReadonly,bool IsRequired,bool IsCost,bool IsSecrecy,string? DefaultValue,int? VerifyIndex,string? Regex,string? Remark,string? BrowseUrl,int? BrowseModuleId,bool OnlyChoose,bool ChooseMultiple,string? ChoosePage,IReadOnlyList<FieldChooserSource> Choosers,bool IsVirtual,string? VirtualExpression,bool CanCopy,bool IsAutoIncrement,string? ConvertFunction,string? DataSourceSql,string? LastUpdatedBy,DateTime? LastUpdatedAt,int TabNo=1,int? FormOrder=null,int Span=1,bool NewLine=false,string? CellGroup=null,int CellRole=0,string? FormOptions=null);
public sealed record UpdateWorkbenchFieldMetadata(string Label,string DataType,int Width,string Align,string HeaderAlign,string? Format,bool IsVisible,bool IsDefault,bool IsQueryable,bool IsReadonly,bool IsRequired,bool IsCost,bool IsSecrecy,string? DefaultValue,int? VerifyIndex,string? Regex,string? Remark,string? BrowseUrl,int? BrowseModuleId,bool OnlyChoose,bool ChooseMultiple,string? ChoosePage,IReadOnlyList<FieldChooserSource> Choosers,bool CanCopy,WorkbenchFieldMetadata? Original,int TabNo=1,int? FormOrder=null,int Span=1,bool NewLine=false,string? CellGroup=null,int CellRole=0,string? FormOptions=null);
public sealed record WorkbenchDefinition(
    int ModuleId,
    string Title,
    string MasterTable,
    string? DetailTable,
    IReadOnlyList<WorkbenchField> MasterFields,
    IReadOnlyList<WorkbenchField> DetailFields,
    string? DefaultSort,
    bool HasAdd,
    bool HasEdit,
    bool DetailNoSave,
    IReadOnlyList<string> MasterPkOrder,
    string DetailNoFields,
    bool HasWorkflow,
    string? ModuleFilter = null,
    IReadOnlySet<string>? FilterFieldKeys = null,
    string UserId = "",
    string? ExecTag = null,
    bool HasOwnerColumn = true,
    bool HasOwnerGroupColumn = true,
    ModuleBusinessRule? BusinessRule = null,
    bool AutoApprove = false,
    [property: JsonIgnore] IReadOnlyList<string> GroupExpressions = default!,
    string? FormTabs = null,
    int? FormColumns = null,
    [property: JsonPropertyName("buttons")]
    IReadOnlyList<WorkbenchButton>? FormButtons = null,
    bool IfCopy = false,
    bool SearchMaster = false,
    bool SearchDetail = false,
    string? NewUrl = null,
    string? ModiUrl = null,
    string? HelpUrl = null,
    bool CanDelete = false,
    string? DefinitionVersion = null);
/// <summary>统一表单页签定义（解析自 MODULES.FORM_TABS，如 '1=基本资料;2=其它'）。</summary>
public sealed record FormTabDefinition(int No, string Title);
/// <summary>统一表单下拉选项（解析自 FIELDS.FORM_OPTIONS，如 'O=外含税;I=内含税'）。</summary>
public sealed record FormOptionItem(string Value, string Label);
public sealed record FormDefinition(int ModuleId, string Title, string MasterTable, string? DetailTable, bool HasAdd, bool HasEdit, string Mode, IReadOnlyList<FormFieldDefinition> MasterFields, IReadOnlyList<FormFieldDefinition> DetailFields, IReadOnlyList<string> MasterPkOrder, string DetailNoFields, string DetailDfVerify, IReadOnlyList<FormTabDefinition> Tabs = default!, int Columns = 2, IReadOnlyList<WorkbenchButton>? Buttons = null, IReadOnlyDictionary<string,string> DefaultValues = default!, bool HasWorkflow = false, bool IfCopy = false, bool SearchMaster = false, bool SearchDetail = false, bool CanDelete = false, bool CanApprove = false, bool CanDeapprove = false, bool CanEndCase = false, bool CanUnEndCase = false, bool CanFileView = false, bool CanFileUpda = false, bool CanFileEdit = false, bool CanFileDele = false, bool CanAddNew = false, bool CanEdit = false, string? HelpUrl = null, bool CanSetup = false);
public sealed record FormFieldDefinition(string Key, string Label, string DataType, int DisplayLength, string? DisplayFormat, bool IsRequired, int? VerifyIndex, string? Regex, string? DefaultValue, bool IsReadonly, bool IsVisible, bool OnlyChoose, bool ChooseMultiple, string? ChoosePage, IReadOnlyList<FieldChooserSource> Choosers, bool IsPrimaryKey, bool IsAutoIncrement, bool IsVirtual, bool IsCost, bool IsSecrecy, bool ServerFilled, int? MaxLength, int TabNo = 1, int? FormOrder = null, int Span = 1, bool NewLine = false, string? CellGroup = null, int CellRole = 0, IReadOnlyList<FormOptionItem>? Options = null, bool DisplayOnly = false, bool CanCopy = true,
    int? Precision = null, int? Scale = null);
public sealed record WorkbenchData(IReadOnlyList<Dictionary<string, object?>> Rows, int Total, int Page, int PageSize);
public sealed record WorkbenchQueryCondition(string Field, string Operator, string? Value, string? ValueTo, IReadOnlyList<string>? Values, string Logic = "and");
public sealed record WorkbenchQuery(IReadOnlyList<WorkbenchQueryCondition> Conditions);
public sealed record ExportSelectedRequest(IReadOnlyList<IReadOnlyList<string>> Keys);
public sealed record FieldSetupLookup(string Value,string Label);
public sealed record SystemKnowledgeModule(int Id, string Title);
public sealed record SystemKnowledgeField(string Table, string Field, string Description, string? DataType);
public sealed record SystemKnowledgeResult(IReadOnlyList<SystemKnowledgeModule> Modules, IReadOnlyList<SystemKnowledgeField> Fields);
public sealed record SystemModuleList(int Total, IReadOnlyList<SystemKnowledgeModule> Modules);

public sealed class DocumentWorkbenchRepository(
    DbConnectionFactory connections,
    FieldAdminRepository fieldAdmin,
    WorkbenchScopeFilter scopeFilter,
    WorkbenchDirtyMarker dirtyMarker,
    WorkbenchQueryComposer queryComposer,
    WorkbenchCommandHandler commandHandler,
    WorkbenchApprovalService approvalService,
    WorkbenchAuditWriter auditWriter,
    WorkbenchDefinitionProvider definitionProvider,
    IOptions<UnifiedFormEditorSettings> formSettings,
    ILogger<DocumentWorkbenchRepository> logger) : Features.Assistant.Tools.IWorkbenchSearchGateway
{
    private static readonly Regex BrowseUrlPlaceholder = new(@"\{([^{}]*)\}", RegexOptions.Compiled);
    private readonly IReadOnlySet<int> formEnabledModules = formSettings.Value.EnabledModuleIds.ToHashSet();

    /// <summary>
    /// 解析 MODULES.FORM_BUTTONS（如 '1=copy;2=approve;3=print'）为受控按钮列表。
    /// 格式：分号分隔的「序号=动作」；动作白名单 new/edit/delete/copy/approve/deapprove/print/export/search；
    /// 空/非法条目忽略（服务端白名单，不信任配置原文）；空配置返回 null（前端走默认按钮集）。
    /// </summary>
    private static IReadOnlyList<WorkbenchButton>? ParseFormButtons(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "new", "edit", "delete", "copy", "approve", "deapprove", "endcase", "unendcase", "print", "export", "search",
        };
        var result = new List<WorkbenchButton>();
        foreach (var part in raw.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = part.IndexOf('=');
            var action = eq >= 0 ? part[(eq + 1)..].Trim() : part.Trim();
            if (allowed.Contains(action))
                result.Add(new WorkbenchButton(action));
        }
        return result.Count > 0 ? result : null;
    }
    /// <summary>
    /// 浏览链接模板白名单校验（BROWSE_URL）。
    /// 仅允许站内相对路径（~ 开头、无外部协议），且所有 {占位符} 必须是同一表内
    /// 已授权字段（大小写不敏感）；否则返回 null，前端不渲染该浏览链接。
    /// </summary>
    internal static string? SanitizeBrowseUrl(string? rawUrl, IReadOnlySet<string> allowedFields)
    {
        if (string.IsNullOrWhiteSpace(rawUrl)) return null;
        var url = rawUrl.Trim();
        if (!url.StartsWith("~/", StringComparison.Ordinal) || url.Contains("://")) return null;
        foreach (Match match in BrowseUrlPlaceholder.Matches(url))
        {
            var token = match.Groups[1].Value.Trim();
            if (token.Length == 0 || !WorkbenchSql.Identifier.IsMatch(token) || !allowedFields.Contains(token)) return null;
        }
        return url;
    }

    public async Task<WorkbenchDefinition?> GetDefinitionAsync(int moduleId, string userId, string? execTag, bool canViewCost, bool canViewSecrecy, IReadOnlySet<string> deniedMasterFields, IReadOnlySet<string> deniedDetailFields, CancellationToken token)
    {
        using var timing = DbTimingCollector.Instance.Measure();
        await using var connection = CreateConnection();
        await connection.OpenAsync(token);
        if (definitionProvider.TryGetBaseline(moduleId, out var baseline, out var snapshotVersion)
            && !await IsDirtyAsync(connection, moduleId, token))
        {
            var fromBaseline = await BuildFromBaselineAsync(connection, baseline, snapshotVersion, moduleId, userId, execTag,
                canViewCost, canViewSecrecy, deniedMasterFields, deniedDetailFields, token);
            logger.LogDebug("工作台定义（快照）module={ModuleId} version={Version}", moduleId, snapshotVersion);
            return fromBaseline;
        }
        return await BuildFromMetadataAsync(connection, moduleId, userId, execTag, canViewCost, canViewSecrecy,
            deniedMasterFields, deniedDetailFields, snapshotVersion, token);
    }

    /// <summary>
    /// 基于已发布快照基线构建每用户定义（ADR-005 §3 运行时快照落地）：
    /// 模块级不可变元数据（表/路由/主键/过滤/表单/业务规则）来自快照；
    /// 每用户字段视图（权限过滤 + 列顺序）、FILTER 白名单与分组表达式仍按用户实时解析。
    /// </summary>
    private async Task<WorkbenchDefinition> BuildFromBaselineAsync(
        SqlConnection connection,
        WorkbenchDefinition baseline,
        string version,
        int moduleId,
        string userId,
        string? execTag,
        bool canViewCost,
        bool canViewSecrecy,
        IReadOnlySet<string> deniedMasterFields,
        IReadOnlySet<string> deniedDetailFields,
        CancellationToken token)
    {
        var master = baseline.MasterTable;
        var detail = baseline.DetailTable;
        var masterFields = await WorkbenchBrowseResolver.ResolveAsync(connection,
            await ReadFields(connection, userId, master, master, canViewCost, canViewSecrecy, deniedMasterFields, token),
            master, formEnabledModules, token);
        var detailFields = detail is null
            ? []
            : await WorkbenchBrowseResolver.ResolveAsync(connection,
                await ReadFields(connection, userId, master, detail, canViewCost, canViewSecrecy, deniedDetailFields, token),
                detail, formEnabledModules, token);
        var (_, groupExpressions) = await ReadGroupExpressionsAsync(connection, moduleId, token);
        return baseline with
        {
            MasterFields = masterFields,
            DetailFields = detailFields,
            DefaultSort = NormalizeSort(baseline.DefaultSort, master, masterFields),
            FilterFieldKeys = await ReadFilterFieldKeys(connection, master, canViewCost, canViewSecrecy, deniedMasterFields, token),
            UserId = userId.Trim(),
            ExecTag = string.IsNullOrWhiteSpace(execTag) ? "A" : execTag.Trim(),
            GroupExpressions = groupExpressions,
            DefinitionVersion = version,
        };
    }

    private async Task<WorkbenchDefinition?> BuildFromMetadataAsync(
        SqlConnection connection,
        int moduleId,
        string userId,
        string? execTag,
        bool canViewCost,
        bool canViewSecrecy,
        IReadOnlySet<string> deniedMasterFields,
        IReadOnlySet<string> deniedDetailFields,
        string? version,
        CancellationToken token)
    {
        const string sql = "SELECT M_DESC,MASTER_TABLE,DETAIL_TABLE,M_URL,SORT_FIELDS,MODI_URL,DETAIL_NO_SAVE,DETAIL_NO_FIELDS,FILTER,UPDATE_SP,AFTERSAVE_SP,AUTO_APPROVE," +
                           "GROUP1,GROUP_EXP1,GROUP2,GROUP_EXP2,GROUP3,GROUP_EXP3,GROUP4,GROUP_EXP4,GROUP5,GROUP_EXP5," +
                           "FORM_TABS,FORM_COLUMNS,FORM_BUTTONS,NEW_URL,IF_COPY,SEARCH_1,SEARCH_2,HELP_URL " +
                           "FROM dbo.MODULES WITH (NOLOCK) WHERE M_IDX=@ModuleId";
        await using var command = new SqlCommand(sql, connection); command.Parameters.Add("@ModuleId", SqlDbType.Int).Value=moduleId;
        await using var reader = await command.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token))
        {
            logger.LogDebug("工作台定义未找到 module={ModuleId}", moduleId);
            return null;
        }
        var title=reader.GetString(0).Trim(); var master=reader.IsDBNull(1)?"":reader.GetString(1).Trim();
        var detail=reader.IsDBNull(2)?null:reader.GetString(2).Trim(); if(detail is not null&&detail.Length==0)detail=null; var url=reader.IsDBNull(3)?"":reader.GetString(3);var defaultSort=reader.IsDBNull(4)?null:reader.GetString(4).Trim();
        var modiUrl=reader.IsDBNull(5)?"":reader.GetString(5).Trim();
        var detailNoSave=!reader.IsDBNull(6)&&reader.GetBoolean(6);
        var detailNoFields=reader.IsDBNull(7)?"":reader.GetString(7).Trim();
        var moduleFilter=reader.IsDBNull(8)?"":reader.GetString(8).Trim();
        var updateSproc=reader.IsDBNull(9)?"":reader.GetString(9).Trim();
        var afterSaveSproc=reader.IsDBNull(10)?"":reader.GetString(10).Trim();
        var autoApprove=!reader.IsDBNull(11)&&reader.GetBoolean(11);
        var groupExpressions = new string[5];
        for (var i = 0; i < 5; i++)
        {
            var offset = 12 + i * 2;
            var enabled = !reader.IsDBNull(offset) && reader.GetBoolean(offset);
            var expression = reader.IsDBNull(offset + 1) ? string.Empty : reader.GetString(offset + 1).Trim();
            groupExpressions[i] = enabled ? expression : string.Empty;
        }
        var formTabs = reader.IsDBNull(22) ? null : reader.GetString(22).Trim();
        var formColumns = reader.IsDBNull(23) ? (int?)null : (int)reader.GetByte(23);
        var formButtons = reader.IsDBNull(24) ? null : reader.GetString(24).Trim();
        var newUrlRaw = reader.IsDBNull(25) ? string.Empty : reader.GetString(25).Trim();
        var ifCopy = !reader.IsDBNull(26) && reader.GetBoolean(26);
        var searchMaster = !reader.IsDBNull(27) && reader.GetBoolean(27);
        var searchDetail = !reader.IsDBNull(28) && reader.GetBoolean(28);
        var helpUrl = reader.IsDBNull(29) ? null : reader.GetString(29).Trim();
        if (string.IsNullOrEmpty(helpUrl)) helpUrl = null;
        await reader.CloseAsync();
        if (!ModuleRouteValidator.IsWorkbenchUrl(url) || !WorkbenchSql.Identifier.IsMatch(master) || (detail is not null && !WorkbenchSql.Identifier.IsMatch(detail)))
        {
            logger.LogWarning("模块 {ModuleId} 未通过工作台校验 url={Url} master={Master} detail={Detail}", moduleId, url, master, detail);
            return null;
        }
        // NEW_URL/MODI_URL 决定新增/编辑路由（M86 契约）：可解析的现代动作路由下发前端，
        // 空值/非法值返回 null，由控制器按统一表单白名单回退或隐藏按钮。
        var resolvedNewUrl = ModuleRouteValidator.ResolveActionUrl(newUrlRaw, moduleId);
        var resolvedModiUrl = ModuleRouteValidator.ResolveActionUrl(modiUrl, moduleId);
        var masterFields=await WorkbenchBrowseResolver.ResolveAsync(connection,
            await ReadFields(connection,userId,master,master,canViewCost,canViewSecrecy,deniedMasterFields,token),master,formEnabledModules,token);
        var masterPkOrder=await WorkbenchSql.GetPrimaryKeyColumnsAsync(connection,null,master,token);
        // 领域规则：静态映射优先（含单号字段/冲抵表等增强配置），否则由 MODULES 元数据自动注册
        var businessRule=ModuleBusinessMap.Get(moduleId);
        if(businessRule is null)
        {
            var hasSproc=updateSproc.Length>0||afterSaveSproc.Length>0;
            if(hasSproc)
            {
                var hasAutoBillNo=await BillNoGenerator.HasAutoBillNoAsync(connection,null,moduleId,token);
                string? billNoField=null;
                string? billTypeField=null;
                if(hasAutoBillNo&&masterPkOrder.Count>=2)
                {
                    billNoField=masterPkOrder.FirstOrDefault(column=>column.Contains("NO",StringComparison.OrdinalIgnoreCase));
                    if(billNoField is not null)
                        billTypeField=masterPkOrder.First(column=>!column.Equals(billNoField,StringComparison.OrdinalIgnoreCase));
                }
                businessRule=new(moduleId,
                    afterSaveSproc.Length>0?afterSaveSproc:null,
                    updateSproc.Length>0?updateSproc:null,
                    billNoField is not null,
                    billNoField,
                    billTypeField);
                // 阶段 5：自动注册的模块若已移植 AfterSave，则用 C# 领域规则替换受控 SP
                if(DomainRuleMap.TryGet(moduleId,out var domainRule))
                    businessRule=businessRule with { DomainRule=domainRule, AfterSaveSproc=null };
                // 阶段 8（字段收敛）：未移植 AfterSave 的模块禁止静默执行元数据 SP——
                // AfterSave 置空并标记待移植（保存时拒绝）；WorkflowSproc（批核 UPDATE_SP）
                // 保留受控调用（批核 SP C# 化属二期）。
                else if(afterSaveSproc.Length>0)
                    businessRule=businessRule with { AfterSaveSproc=null, SprocPendingPorting=true };
            }
        }
        WorkbenchDefinition definition=new(moduleId,title,master,detail,masterFields,
            detail is null?[]:await WorkbenchBrowseResolver.ResolveAsync(connection,
                await ReadFields(connection,userId,master,detail,canViewCost,canViewSecrecy,deniedDetailFields,token),detail,formEnabledModules,token),NormalizeSort(defaultSort,master,masterFields),
            resolvedNewUrl is not null || resolvedModiUrl is not null,resolvedModiUrl is not null,detailNoSave,
            masterPkOrder,detailNoFields,
            businessRule?.WorkflowSproc is not null,
            string.IsNullOrWhiteSpace(moduleFilter)?null:moduleFilter,
            await ReadFilterFieldKeys(connection,master,canViewCost,canViewSecrecy,deniedMasterFields,token),
            userId.Trim(),
            string.IsNullOrWhiteSpace(execTag)?"A":execTag.Trim(),
            await WorkbenchSql.ColumnExistsAsync(connection,null,master,"OWNER",token),
            await WorkbenchSql.ColumnExistsAsync(connection,null,master,"OWNER_G",token),
            businessRule,
            autoApprove,
            groupExpressions,
            string.IsNullOrWhiteSpace(formTabs) ? null : formTabs,
            formColumns,
            ParseFormButtons(formButtons),
            ifCopy,
            searchMaster,
            searchDetail,
            resolvedNewUrl,
            resolvedModiUrl,
            helpUrl);
        logger.LogDebug("工作台定义 module={ModuleId} title={Title} master={Master} detail={Detail} masterFields={MasterFieldCount} detailFields={DetailFieldCount}",
            moduleId,title,master,detail,definition.MasterFields.Count,definition.DetailFields.Count);
        return definition with { DefinitionVersion = version };
    }

    public async Task<IReadOnlyList<FieldSetupLookup>> GetFieldSetupTablesAsync(CancellationToken token)
    {await using var connection=CreateConnection();await connection.OpenAsync(token);const string sql="SELECT LTRIM(RTRIM(T_ID)),COALESCE(NULLIF(LTRIM(RTRIM(T_DESC)),''),LTRIM(RTRIM(T_ID))) FROM dbo.TABLES WITH (NOLOCK) ORDER BY T_DESC,T_ID";await using var command=new SqlCommand(sql,connection);await using var reader=await command.ExecuteReaderAsync(token);var result=new List<FieldSetupLookup>();while(await reader.ReadAsync(token)){var value=reader.GetString(0);if(WorkbenchSql.Identifier.IsMatch(value))result.Add(new(value,reader.GetString(1)));}return result;}

    public async Task<IReadOnlyList<FieldSetupLookup>> GetFieldSetupModulesAsync(CancellationToken token)
    {await using var connection=CreateConnection();await connection.OpenAsync(token);const string sql="SELECT CONVERT(nvarchar(20),M_IDX),COALESCE(NULLIF(LTRIM(RTRIM(M_DESC)),''),CONVERT(nvarchar(20),M_IDX)) FROM dbo.MODULES WITH (NOLOCK) ORDER BY M_DESC,M_IDX";await using var command=new SqlCommand(sql,connection);await using var reader=await command.ExecuteReaderAsync(token);var result=new List<FieldSetupLookup>();while(await reader.ReadAsync(token))result.Add(new(reader.GetString(0),reader.GetString(1)));return result;}

    /// <summary>
    /// 按标题关键字查找第一个通用工作台模块（服务端白名单，不接受调用方传入任意标题）。
    /// 仅供只读助手试点使用，例如"采购订单"。
    /// </summary>
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
    /// 系统能力知识检索：按关键字匹配模块标题与字段元数据（表/字段/描述/类型）。
    /// 只返回安全元数据，不返回 VIRTUAL_EXP / CONVERT_FUNCTION / DATASOURCE_SQL 等高危表达式。
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

    /// <summary>
    /// 系统模块清单：总数 + 前 max 个模块标题（安全元数据），用于回答"系统中有多少个/有哪些模块"。
    /// 与系统知识检索一样不返回高危表达式等敏感字段。
    /// </summary>
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
    /// 批量写回列宽（DISPLAY_LENGTH）：字段键先经模块定义白名单过滤（大小写不敏感），
    /// 宽度钳制 [40,300]，单事务一次提交，避免前端逐列 GET+PUT 造成低效与中途刷新。
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
    /// 批量列宽白名单过滤：仅保留模块定义内可见字段，宽度钳制 [40,300]。
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
    public static IReadOnlyList<WorkbenchField> ResolveExportFields(
        IReadOnlyList<WorkbenchField> fields,IReadOnlyList<string>? columnKeys)
        => WorkbenchQueryComposer.ResolveExportFields(fields,columnKeys);

    /// <summary>
    /// 生成统一表单定义（按当前用户权限过滤后的录入字段视图）。
    /// mode 仅支持 new/edit（控制器已校验）；本方法不执行任何高危表达式。
    /// 明细字段与 DocumentWorkbench 子表列保持一致：列集合与顺序以工作台同源配置
    /// （用户 SYSQL_FIELDS → SYSQL_DEFAULT → FIELDS.IS_DEFAULT_FIELDS）为准，而非表单自身的
    /// SYSQL_DEFAULT 全量可见字段，对齐旧系统新增页与工作台子表共用同一列配置的行为。
    /// </summary>
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
        await using var connection=CreateConnection(); await connection.OpenAsync(token);
        var pkColumns=await WorkbenchSql.GetPrimaryKeyColumnsAsync(connection,null,definition.MasterTable,token);
        var masterRows=await ReadFormFieldRows(connection,definition.MasterTable,definition.MasterTable,token);
        var masterFields=FormFieldSelector.Select(masterRows,mode,canViewCost,canViewSecrecy,deniedMasterFields,deniedNewMasterFields,deniedModiMasterFields);
        IReadOnlyList<FormFieldDefinition> detailFields=[];
        var detailDfVerify="";
        if(definition.DetailTable is not null)
        {
            var workbenchDetail=await ReadFields(connection,userId,definition.MasterTable,definition.DetailTable,canViewCost,canViewSecrecy,deniedDetailFields,token);
            var detailRows=await ReadFormFieldRows(connection,definition.MasterTable,definition.DetailTable,token,includeVirtual:true);
            var rowsByKey=new Dictionary<string,FormFieldRow>(StringComparer.OrdinalIgnoreCase);
            foreach(var row in detailRows) rowsByKey.TryAdd(row.Key,row);
            var orderedRows=workbenchDetail
                .Where(field=>rowsByKey.ContainsKey(field.Key))
                .Select(field=>rowsByKey[field.Key])
                .ToList();
            // 必填字段（IS_VERIFY=1）必须保留在表单中：工作台列配置只决定列表展示，
            // 不决定可录入字段；用户列配置/默认列缺漏必填列会直接导致建单被拒
            // （8231b7d 起明细表单以工作台列配置为源，1505/130104 等回归）。
            var includedKeys=new HashSet<string>(orderedRows.Select(row=>row.Key),StringComparer.OrdinalIgnoreCase);
            foreach(var row in detailRows)
            {
                if(!includedKeys.Add(row.Key))continue;
                if(!row.IsRequired)continue;
                orderedRows.Add(row);
            }
            detailFields=FormFieldSelector.Select(orderedRows,mode,canViewCost,canViewSecrecy,deniedDetailFields,deniedNewDetailFields,deniedModiDetailFields);
            // 明细主键关联列（主表主键同名列，如 ORDER_TYPE/ORDER_NO）：服务端从主表带入，
            // 表单内只读展示（对齐旧系统明细隐藏主键控件、随主表联动带值的做法）
            if(detailFields.Count>0)
            {
                detailFields=detailFields
                    .Select(field=>pkColumns.Any(column=>column.Equals(field.Key,StringComparison.OrdinalIgnoreCase))
                        || field.Key.Equals("SERIAL_NO",StringComparison.OrdinalIgnoreCase)
                        ? field with { IsReadonly=true, ServerFilled=true }
                        : field)
                    .ToList();
            }
            // 主表主键关联列必须保留在明细表单定义中（服务端从主表带入，旧系统明细隐藏主键控件）：
            // 8231b7d 起明细字段以工作台列配置为源，隐藏或未默认的必填主键列会丢失，
            // 建单被 MASTER_KEY_NOT_IN_DETAIL 拒绝（1601/1505/130104 等回归）。
            // 此处从全量物理行补回缺失的主键列：只读、服务端填充，权限过滤与 Select 一致。
            var deniedForMode = mode == "edit" ? deniedModiDetailFields : deniedNewDetailFields;
            var missingPk = new List<FormFieldDefinition>();
            foreach (var column in pkColumns)
            {
                if (detailFields.Any(field => field.Key.Equals(column, StringComparison.OrdinalIgnoreCase))) continue;
                if (!rowsByKey.TryGetValue(column, out var pkRow)) continue;
                if (deniedDetailFields.Contains(column) || deniedForMode.Contains(column)) continue;
                missingPk.Add(new FormFieldDefinition(
                    pkRow.Key, pkRow.Label, pkRow.DataType, pkRow.DisplayLength, pkRow.DisplayFormat,
                    IsRequired: true, pkRow.VerifyIndex, pkRow.Regex, pkRow.DefaultValue,
                    IsReadonly: true, IsVisible: pkRow.IsVisible, OnlyChoose: false, ChooseMultiple: false, ChoosePage: null,
                    Choosers: [], IsPrimaryKey: true, IsAutoIncrement: pkRow.IsAutoIncrement, IsVirtual: pkRow.IsVirtual,
                    IsCost: pkRow.IsCost, IsSecrecy: pkRow.IsSecrecy, ServerFilled: true, pkRow.MaxLength,
                    pkRow.TabNo, pkRow.FormOrder, pkRow.Span, pkRow.NewLine,
                    string.IsNullOrWhiteSpace(pkRow.CellGroup) ? null : pkRow.CellGroup, pkRow.CellRole,
                    FormFieldSelector.ParseOptions(pkRow.Options), DisplayOnly: false,
                    Precision: pkRow.TypePrecision, Scale: pkRow.TypeScale));
            }
            if (missingPk.Count > 0) detailFields = detailFields.Concat(missingPk).ToList();
            detailDfVerify=(await WorkbenchSql.GetDfVerifyAsync(connection,null,definition.DetailTable,token))??"";
        }
        logger.LogDebug("表单定义 module={ModuleId} mode={Mode} master={MasterFieldCount} detail={DetailFieldCount}",
            definition.ModuleId,mode,masterFields.Count,detailFields.Count);
        // 查看模式：全部字段只读（对齐旧系统 state=brow），保存端点不可用
        if(mode=="view")
        {
            masterFields=masterFields.Select(field=>field with { IsReadonly=true }).ToList();
            detailFields=detailFields.Select(field=>field with { IsReadonly=true }).ToList();
        }
        var tabs = ParseFormTabs(definition.FormTabs);
        var columns = definition.FormColumns is int formColumns and > 0 ? formColumns : 2;
        var defaultValues = await BuildNewDefaultsAsync(connection,definition,masterFields,mode,token);
        return new FormDefinition(definition.ModuleId,definition.Title,definition.MasterTable,definition.DetailTable,
            definition.HasAdd,definition.HasEdit,mode,masterFields,detailFields,pkColumns,definition.DetailNoFields,detailDfVerify,
            tabs,columns,definition.FormButtons,defaultValues,definition.HasWorkflow,
            definition.IfCopy,definition.SearchMaster,definition.SearchDetail,
            canDelete,canApprove,canDeapprove,canEndCase,canUnEndCase,canFileView,canFileUpda,canFileEdit,canFileDele,
            canAddNew,canEdit,definition.HelpUrl,canSetup);
    }

    /// <summary>
    /// 新增模式默认值（对齐旧系统新增体验）：
    /// 1) 自动单号模块：默认单别 + 生成的下一单号（等价 GetDefaultBillInfo / GetNewNo）；
    /// 2) 日期字段（可编辑、非服务端持有、无 DFT_VALUE）默认今天（等价 DxCalendar DefaultDateTimeNow）。
    /// 仅 mode=new 返回；服务端生成、前端只展示，保存时仍按保存管线复查。
    /// </summary>
    private static async Task<IReadOnlyDictionary<string,string>> BuildNewDefaultsAsync(
        SqlConnection connection,
        WorkbenchDefinition definition,
        IReadOnlyList<FormFieldDefinition> masterFields,
        string mode,
        CancellationToken token)
    {
        var defaults = new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        if (mode != "new") return defaults;

        if (definition.BusinessRule is { AutoBillNo: true, BillNoField: not null, BillTypeField: not null })
        {
            if (masterFields.Any(field => field.Key.Equals(definition.BusinessRule.BillTypeField,StringComparison.OrdinalIgnoreCase)))
            {
                var billCode = await BillNoGenerator.GetDefaultBillCodeAsync(connection,null,definition.ModuleId,token);
                if (billCode is not null) defaults[definition.BusinessRule.BillTypeField] = billCode;
            }
            if (masterFields.Any(field => field.Key.Equals(definition.BusinessRule.BillNoField,StringComparison.OrdinalIgnoreCase)))
            {
                var newNo = await BillNoGenerator.GenerateAsync(connection,null,definition.ModuleId,
                    definition.MasterTable,definition.BusinessRule.BillNoField,definition.BusinessRule.BillTypeField,token);
                if (newNo is not null) defaults[definition.BusinessRule.BillNoField] = newNo;
            }
        }

        var today = DateTime.Today.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture);
        foreach (var field in masterFields)
        {
            if (field is { IsVisible: true, IsReadonly: false, ServerFilled: false, IsVirtual: false, DisplayOnly: false }
                && field.DataType.Contains("date",StringComparison.OrdinalIgnoreCase)
                && string.IsNullOrEmpty(field.DefaultValue))
                defaults[field.Key] = today;
        }
        // When a defaulted code field (bill type + number) has a companion name field in the same
        // cell group, backfill the name from the chooser's return mapping so the form shows the
        // name immediately without a manual re-selection.
        foreach (var field in masterFields)
        {
            if (field.CellRole != 1 || string.IsNullOrWhiteSpace(field.CellGroup)) continue;
            if (!defaults.TryGetValue(field.Key, out var defaultValue) || string.IsNullOrWhiteSpace(defaultValue)) continue;
            var source = field.Choosers.FirstOrDefault(item => item.Active && !string.IsNullOrWhiteSpace(item.Table));
            if (source is null) continue;
            var companions = masterFields
                .Where(item => item.CellRole == 2 && string.Equals(item.CellGroup, field.CellGroup, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (companions.Count == 0) continue;
            await FillChooserNameDefaultsAsync(connection, field, source, companions, defaultValue, defaults, token);
        }
        return defaults;
    }

    /// <summary>
    /// 新增默认值回填：按编号字段的默认值查询选择器来源表，回填同组名称 companion（仅物理列；
    /// 虚拟列需 JOIN 的场景由选择器运行期处理）。任一校验失败即跳过（fail-closed，不抛错）。
    /// </summary>
    private static async Task FillChooserNameDefaultsAsync(
        SqlConnection connection,
        FormFieldDefinition main,
        FieldChooserSource source,
        IReadOnlyList<FormFieldDefinition> companions,
        string value,
        Dictionary<string, string> defaults,
        CancellationToken token)
    {
        var table = source.Table!.Trim();
        if (!WorkbenchSql.Identifier.IsMatch(table) || !await WorkbenchSql.TableExistsAsync(connection, table, token))
            return;

        var mapping = ChooserReturnItems.Parse(source.ReturnMapping) ?? [];
        var keyColumn = mapping.FirstOrDefault(pair =>
            string.Equals(FormFieldSelector.NormalizeChooserTarget(pair.Target), main.Key, StringComparison.OrdinalIgnoreCase))?.Column;
        if (string.IsNullOrWhiteSpace(keyColumn)) keyColumn = main.Key;
        if (!WorkbenchSql.Identifier.IsMatch(keyColumn)) return;

        var selected = new List<(FormFieldDefinition Field, string Column)>();
        foreach (var companion in companions)
        {
            var column = mapping.FirstOrDefault(pair =>
                string.Equals(FormFieldSelector.NormalizeChooserTarget(pair.Target), companion.Key, StringComparison.OrdinalIgnoreCase))?.Column;
            if (string.IsNullOrWhiteSpace(column)) column = companion.Key;
            if (!WorkbenchSql.Identifier.IsMatch(column)) return;
            selected.Add((companion, column));
        }
        if (selected.Count == 0) return;

        var columns = selected.Select(item => item.Column).Append(keyColumn)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (!await WorkbenchSql.ColumnsExistAsync(connection, table, columns, token)) return;

        var select = string.Join(",", columns.Select(column => $"[{column}]"));
        try
        {
            await using var command = new SqlCommand(
                $"SELECT TOP 1 {select} FROM dbo.[{table}] WITH (NOLOCK) WHERE [{keyColumn}]=@Value;", connection);
            command.Parameters.Add("@Value", SqlDbType.NVarChar, 256).Value = value;
            await using var reader = await command.ExecuteReaderAsync(token);
            if (!await reader.ReadAsync(token)) return;
            foreach (var (field, column) in selected)
            {
                var ordinal = reader.GetOrdinal(column);
                if (reader.IsDBNull(ordinal)) continue;
                defaults[field.Key] = Convert.ToString(reader.GetValue(ordinal), CultureInfo.InvariantCulture) ?? string.Empty;
            }
        }
        catch (Exception ex) when (ex is SqlException or InvalidOperationException)
        {
            // 纯体验增强的默认值回填：来源表查询异常（列变更/锁等待等）时降级为空回填，
            // 不让 form-definition 新增模式因选填名称回填而整体 500（fail-closed）。
        }
    }

    private static async Task<IReadOnlyList<FormFieldRow>> ReadFormFieldRows(SqlConnection connection,string masterTable,string targetTable,CancellationToken token,bool includeVirtual=false)
    {
        const string sql="""
            SELECT LTRIM(RTRIM(f.F_ID)) AS F_ID,COALESCE(NULLIF(LTRIM(RTRIM(f.F_DESC)),''),LTRIM(RTRIM(f.F_ID))) AS F_DESC,
                   COALESCE(NULLIF(LTRIM(RTRIM(f.F_TYPE)),''),'nvarchar') AS F_TYPE,COALESCE(f.DISPLAY_LENGTH,100) AS DISPLAY_LENGTH,
                   f.DISPLAY_FORMAT,CAST(COALESCE(f.IS_VERIFY,0) AS bit) AS IS_VERIFY,f.VERIFY_INDEX,f.REGEX,f.DFT_VALUE,
                   CAST(COALESCE(f.IS_READONLY,0) AS bit) AS IS_READONLY,CAST(COALESCE(f.IS_VISIBLE,1) AS bit) AS IS_VISIBLE,
                   CAST(COALESCE(f.ONLY_CHOOSE,0) AS bit) AS ONLY_CHOOSE,CAST(COALESCE(f.CHOOSE_MULTI,0) AS bit) AS CHOOSE_MULTI,
                   f.CHOOSE_PAGE,
                   CAST(COALESCE(f.IS_VIRTUAL,0) AS bit) AS IS_VIRTUAL,CAST(COALESCE(f.IS_COST,0) AS bit) AS IS_COST,
                   CAST(COALESCE(f.IS_SECRECY,0) AS bit) AS IS_SECRECY,CAST(COALESCE(f.IS_AUTOINC,0) AS bit) AS IS_AUTOINC,
                   CAST(COALESCE(f.CAN_COPY,1) AS bit) AS CAN_COPY,
                   d.F_IDX,CAST(CASE WHEN pk.COLUMN_NAME IS NULL THEN 0 ELSE 1 END AS bit) AS IS_PK,
                    CASE WHEN col.COLUMN_NAME IS NULL THEN NULL
                         WHEN col.CHARACTER_LENGTH_FLAG = 0 THEN NULL
                         WHEN col.MAX_LENGTH = -1 THEN NULL
                         WHEN col.CHARACTER_LENGTH_FLAG = 2 THEN col.MAX_LENGTH / 2
                         ELSE col.MAX_LENGTH END AS MAX_LENGTH,
                   CAST(COALESCE(f.FORM_TAB_NO,1) AS int) AS FORM_TAB_NO,
                   f.FORM_ORDER AS FORM_ORDER,
                   CAST(COALESCE(f.FORM_SPAN,1) AS int) AS FORM_SPAN,
                   CAST(COALESCE(f.FORM_NEW_LINE,0) AS bit) AS FORM_NEW_LINE,
                   LTRIM(RTRIM(COALESCE(f.FORM_CELL_GROUP,''))) AS FORM_CELL_GROUP,
                   CAST(COALESCE(f.FORM_CELL_ROLE,0) AS int) AS FORM_CELL_ROLE,
                   f.FORM_OPTIONS AS FORM_OPTIONS,
                   col.TYPE_PRECISION AS TYPE_PRECISION,col.TYPE_SCALE AS TYPE_SCALE,
                   CAST(CASE WHEN col.COLUMN_NAME IS NULL THEN 0 ELSE 1 END AS bit) AS IS_PHYSICAL
            FROM dbo.FIELDS f WITH (NOLOCK)
            LEFT JOIN (SELECT T_ID,T_ID_R,LTRIM(RTRIM(F_ID)) AS F_ID,MIN(F_IDX) AS F_IDX
                       FROM dbo.SYSQL_DEFAULT WITH (NOLOCK)
                       GROUP BY T_ID,T_ID_R,LTRIM(RTRIM(F_ID))) d
              ON d.T_ID=@MasterTable AND d.T_ID_R=@TargetTable AND d.F_ID=LTRIM(RTRIM(f.F_ID))
            LEFT JOIN (SELECT c.name AS COLUMN_NAME,c.max_length AS MAX_LENGTH,CAST(t.precision AS int) AS TYPE_PRECISION,CAST(t.scale AS int) AS TYPE_SCALE,
                        CASE WHEN t.user_type_id IN (231,239) THEN 2
                             WHEN t.user_type_id IN (167,175,35,99) THEN 1
                             ELSE 0 END AS CHARACTER_LENGTH_FLAG
                       FROM sys.columns c
                       JOIN sys.objects o ON c.object_id=o.object_id AND o.type IN ('U','V')
                       JOIN sys.schemas s ON o.schema_id=s.schema_id
                       JOIN sys.types t ON c.user_type_id=t.user_type_id
                       WHERE s.name=N'dbo' AND o.name=@TargetTable) col
              ON col.COLUMN_NAME=f.F_ID
            LEFT JOIN (SELECT c.name AS COLUMN_NAME
                       FROM sys.indexes i
                       JOIN sys.index_columns ic ON i.object_id=ic.object_id AND i.index_id=ic.index_id
                       JOIN sys.columns c ON ic.object_id=c.object_id AND ic.column_id=c.column_id
                       JOIN sys.tables t2 ON i.object_id=t2.object_id
                       JOIN sys.schemas s2 ON t2.schema_id=s2.schema_id
                       WHERE s2.name=N'dbo' AND t2.name=@TargetTable AND i.is_primary_key=1) pk
              ON pk.COLUMN_NAME=f.F_ID
            WHERE f.T_ID=@TargetTable
              AND (COALESCE(f.IS_VIRTUAL,0)=@IncludeVirtual OR col.COLUMN_NAME IS NOT NULL OR LTRIM(RTRIM(COALESCE(f.FORM_CELL_GROUP,'')))<>'')
            ORDER BY CASE WHEN f.FORM_ORDER IS NULL THEN 1 ELSE 0 END,COALESCE(f.FORM_ORDER,d.F_IDX,COALESCE(f.VERIFY_INDEX,999)),f.F_ID;
            """;
        await using var command=new SqlCommand(sql,connection);
        command.Parameters.Add("@MasterTable",SqlDbType.VarChar,100).Value=masterTable;
        command.Parameters.Add("@TargetTable",SqlDbType.VarChar,100).Value=targetTable;
        command.Parameters.Add("@IncludeVirtual",SqlDbType.Bit).Value=includeVirtual;
        // Chooser data sources read from FIELD_DATASOURCE (aggregated by field), not embedded in FIELDS columns
        var choosersByField = await ReadChoosersByFieldAsync(connection, targetTable, token);
        await using var reader=await command.ExecuteReaderAsync(token);
        var rows=new List<FormFieldRow>();
        while(await reader.ReadAsync(token))
        {
            var row=ReadFormFieldRow(reader);
            rows.Add(choosersByField.TryGetValue(row.Key, out var choosers) && choosers.Count>0
                ? row with { Choosers = choosers }
                : row);
        }
        return rows;
    }

    private static FormFieldRow ReadFormFieldRow(SqlDataReader reader)
        => new(
            reader.GetString(reader.GetOrdinal("F_ID")).Trim(),
            reader.GetString(reader.GetOrdinal("F_DESC")).Trim(),
            reader.GetString(reader.GetOrdinal("F_TYPE")).Trim(),
            reader.GetInt32(reader.GetOrdinal("DISPLAY_LENGTH")),
            reader.GetNullableString("DISPLAY_FORMAT"),
            reader.GetBoolean(reader.GetOrdinal("IS_VERIFY")),
            reader.GetNullableInt32("VERIFY_INDEX"),
            reader.GetNullableString("REGEX"),
            reader.GetNullableString("DFT_VALUE"),
            reader.GetBoolean(reader.GetOrdinal("IS_READONLY")),
            reader.GetBoolean(reader.GetOrdinal("IS_VISIBLE")),
            reader.GetBoolean(reader.GetOrdinal("ONLY_CHOOSE")),
            reader.GetBoolean(reader.GetOrdinal("CHOOSE_MULTI")),
            reader.GetNullableString("CHOOSE_PAGE"),
            [],
            reader.GetBoolean(reader.GetOrdinal("IS_VIRTUAL")),
            reader.GetBoolean(reader.GetOrdinal("IS_COST")),
            reader.GetBoolean(reader.GetOrdinal("IS_SECRECY")),
            reader.GetBoolean(reader.GetOrdinal("IS_AUTOINC")),
            reader.GetBoolean(reader.GetOrdinal("CAN_COPY")),
            reader.GetBoolean(reader.GetOrdinal("IS_PK")),
            reader.GetNullableInt32("MAX_LENGTH"),
            reader.GetInt32(reader.GetOrdinal("FORM_TAB_NO")),
            reader.GetNullableInt32("FORM_ORDER"),
            reader.GetInt32(reader.GetOrdinal("FORM_SPAN")),
            reader.GetBoolean(reader.GetOrdinal("FORM_NEW_LINE")),
            reader.GetNullableString("FORM_CELL_GROUP"),
            reader.GetInt32(reader.GetOrdinal("FORM_CELL_ROLE")),
            reader.GetNullableString("FORM_OPTIONS"),
            reader.GetBoolean(reader.GetOrdinal("IS_PHYSICAL")),
            reader.GetNullableInt32("TYPE_PRECISION"),
            reader.GetNullableInt32("TYPE_SCALE"));

    /// <summary>读取目标表全部字段的选择器数据源（FIELD_DATASOURCE，按 SERIAL_NO 排序）。</summary>
    private static async Task<Dictionary<string, IReadOnlyList<FormChooserRow>>> ReadChoosersByFieldAsync(
        SqlConnection connection,
        string targetTable,
        CancellationToken token)
    {
        const string sql = """
            SELECT LTRIM(RTRIM(c.F_ID)) AS F_ID,c.SERIAL_NO,CAST(COALESCE(c.ACTIVE_TAG,0) AS bit) AS ACTIVE_TAG,
                   LTRIM(RTRIM(ISNULL(c.SOURCE_T_ID,''))) AS SOURCE_T_ID,
                   LTRIM(RTRIM(ISNULL(c.SOURCE_DESC,''))) AS SOURCE_DESC,c.SOURCE_M_IDX,
                   c.RETURN_ITEMS,c.FILTER_STRUCT
            FROM dbo.FIELD_DATASOURCE c WITH (NOLOCK)
            WHERE c.T_ID=@TargetTable
            ORDER BY c.SERIAL_NO;
            """;
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@TargetTable", SqlDbType.VarChar, 100).Value = targetTable;
        await using var reader = await command.ExecuteReaderAsync(token);
        var result = new Dictionary<string, IReadOnlyList<FormChooserRow>>(StringComparer.OrdinalIgnoreCase);
        while (await reader.ReadAsync(token))
        {
            var field = reader.GetString(0).Trim();
            var table = reader.GetString(3).Trim();
            if (table.Length == 0)
            {
                continue;
            }
            var row = new FormChooserRow(
                reader.GetBoolean(reader.GetOrdinal("ACTIVE_TAG")),
                table,
                reader.GetString(4),
                reader.IsDBNull(reader.GetOrdinal("SOURCE_M_IDX")) ? null : reader.GetInt32(reader.GetOrdinal("SOURCE_M_IDX")),
                reader.IsDBNull(reader.GetOrdinal("RETURN_ITEMS")) ? null : reader.GetString(reader.GetOrdinal("RETURN_ITEMS")),
                reader.IsDBNull(reader.GetOrdinal("FILTER_STRUCT")) ? null : reader.GetString(reader.GetOrdinal("FILTER_STRUCT")),
                reader.GetInt32(reader.GetOrdinal("SERIAL_NO")));
            if (!result.TryGetValue(field, out var list))
            {
                list = new List<FormChooserRow>();
                result[field] = list;
            }
            ((List<FormChooserRow>)list).Add(row);
        }
        return result;
    }

    #region 记录读取与保存（M2 核心写操作）

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




    /// <summary>
    /// 选择器数据源（M4/ADR-008）：表名与来源定义来自服务端 FIELD_DATASOURCE（客户端仅传字段 key + serialNo），
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
        await using var connection=CreateConnection(); await connection.OpenAsync(token);
        var all=await ReadChooserColumnRows(connection,table,token);
        if(all.Count==0)return null;
        var allowedFields=all.Select(row=>row.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var columnTypes=all.ToDictionary(row=>row.Key,row=>row.DataType,StringComparer.OrdinalIgnoreCase);
        logger.LogInformation("选择器过滤 table={Table} filter={Filter}", table, filterStruct is null ? null : filterStruct.ToJson());
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
        var hasOwner = await WorkbenchSql.ColumnExistsAsync(connection, null, table, "OWNER", token);
        var hasOwnerGroup = await WorkbenchSql.ColumnExistsAsync(connection, null, table, "OWNER_G", token);
        if (!scopeFilter.TryBuildChooserScopePredicate(table, moduleFilter, moduleMasterTable, dataFilter, execTag,
                userId, hasOwner, hasOwnerGroup, allowedFields, out var basePredicate, out var baseParameters))
        {
            logger.LogWarning("选择器数据范围无法构建（fail-closed）table={Table} module={Module}", table, scopeModuleId);
            return new FormChooserResult([], [], 0);
        }
        var scopeParameters = new List<object>(baseParameters);
        string? scopePredicate = string.IsNullOrWhiteSpace(basePredicate) ? null : basePredicate;
        var joins = new List<string>();
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
                var joinClause = ChooserJoinCatalog.BuildJoinClause(catalog, compiled.Joins);
                if (joinClause is null)
                {
                    logger.LogWarning("选择器 JOIN 重建失败（fail-closed）table={Table}", table);
                    return new FormChooserResult([], [], 0);
                }
                joins.Add(joinClause);
            }
        }
        // 显示列：回填映射列优先（保证主键/名称可见），其余按 SYSQL_DEFAULT 顺序，
        // 保留审计/状态列（对齐旧系统 Chooser.aspx 网格列，如建立人/批核状态等）
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
        if(missingColumns.Count>0)
        {
            var virtualFields=await ReadChooserVirtualFieldsAsync(connection,table,missingColumns,token);
            if(virtualFields.Count>0)
            {
                var resolution=await new VirtualColumnResolver(connection).ResolveAsync(table,virtualFields,token);
                if(resolution.ResolvedKeys.Count>0)
                {
                    virtualSelect=string.Join(",",resolution.SelectFragments);
                    virtualJoin=resolution.JoinFragment;
                    foreach(var field in virtualFields)
                    {
                        if(!resolution.ResolvedKeys.Contains(field.Key,StringComparer.OrdinalIgnoreCase))continue;
                        columns.Add(new FormChooserColumn(field.Key,field.Label,field.DataType,null));
                    }
                }
            }
        }
        var (rows,total)=await ReadChooserRowsAsync(connection,table,columns,keyword,filterField,scopePredicate,scopeParameters,joins,conditions,allowedFields,sortField,sortDirection,page,pageSize,virtualSelect,virtualJoin,token);
        return new FormChooserResult(columns,rows,total);
    }

    /// <summary>
    /// form-chooser 端点权威数据源解析（ADR-008 §2/§7）：按字段 + serialNo 从 FIELD_DATASOURCE 读取
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
        await using var connection = CreateConnection();
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
        SqlConnection connection,string table,IReadOnlyList<FormChooserColumn> columns,string? keyword,string? filterField,string? scopePredicate,IReadOnlyList<object> scopeParameters,IReadOnlyList<string> joins,IReadOnlyList<UnifiedChooserCondition>? conditions,IReadOnlySet<string> allowedFields,string? sortField,string? sortDirection,int page,int pageSize,string? virtualSelect,string? virtualJoin,CancellationToken token)
    {
        var select=string.Join(',',columns.Select(column=>$"[{table}].[{column.Key}]"));
        if(!string.IsNullOrWhiteSpace(virtualSelect))select+=","+virtualSelect;
        var predicates=new List<string>();
        if(!string.IsNullOrWhiteSpace(keyword))
        {
            // 指定字段 → 单列模糊；未指定（全部）→ 跨文本列 OR LIKE（对齐旧选择器 droFieldList=全部）
            if(!string.IsNullOrWhiteSpace(filterField))
                predicates.Add($"[{table}].[{filterField}] LIKE @kw");
            else
            {
                var textColumns=columns.Where(column=>IsTextLike(column.DataType)).ToList();
                if(textColumns.Count>0)
                    predicates.Add("("+string.Join(" OR ",textColumns.Select(column=>$"[{table}].[{column.Key}] LIKE @kw"))+")");
            }
        }
        if(!string.IsNullOrWhiteSpace(scopePredicate))predicates.Add($"({scopePredicate})");
        await using var command=new SqlCommand{Connection=connection};
        if(!string.IsNullOrWhiteSpace(keyword))command.Parameters.AddWithValue("@kw",$"%{keyword.Trim()}%");
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
        // 排序字段必须在显示列白名单内（服务端校验），否则回退首列；方向仅 asc/desc
        var sortColumn = !string.IsNullOrWhiteSpace(sortField)
            ? columns.FirstOrDefault(column=>column.Key.Equals(sortField,StringComparison.OrdinalIgnoreCase))?.Key
            : null;
        sortColumn ??= columns[0].Key;
        var dir = string.Equals(sortDirection,"desc",StringComparison.OrdinalIgnoreCase) ? "DESC" : "ASC";
        page=Math.Max(1,page);
        pageSize=Math.Clamp(pageSize,10,100);
        var sql=$"SELECT COUNT_BIG(1) {countFrom}{where}; SELECT {select} {from}{where} ORDER BY [{table}].[{sortColumn}] {dir} OFFSET @Offset ROWS FETCH NEXT @PageSize ROWS ONLY;";
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




    /// <summary>解析 MODULES.FORM_TABS（如 '1=基本资料;2=其它'），非法项跳过并按键号升序。</summary>
    private static IReadOnlyList<FormTabDefinition> ParseFormTabs(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return [];
        var tabs = new List<FormTabDefinition>();
        foreach (var part in raw.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var eq = part.IndexOf('=');
            if (eq <= 0) continue;
            if (!int.TryParse(part[..eq].Trim(), out var no)) continue;
            var title = part[(eq + 1)..].Trim();
            if (title.Length == 0) continue;
            tabs.Add(new FormTabDefinition(no, title));
        }
        return tabs.OrderBy(tab => tab.No).ToList();
    }
    #endregion

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
    /// 列设置中剔除无法受控解析的虚拟字段（与 ReadFields 口径一致，避免列编辑器中
    /// 出现"勾选但列表不渲染"的列）。
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

    /// <summary>
    /// MODULES.FILTER / DATA_FILTER 字段白名单：主表全部物理存在、非虚拟字段
    /// （含隐藏字段；旧系统过滤表达式常引用隐藏字段如 IF_SHOW，若仅按可见字段会误拒整个模块；
    /// 不受用户列选择影响，因为过滤器是服务端行级数据范围，字段不在用户列配置里不代表
    /// 不能用于过滤）；仍受禁止字段/成本/保密权限约束。
    /// </summary>
    private static async Task<IReadOnlySet<string>> ReadFilterFieldKeys(SqlConnection connection,string targetTable,bool canViewCost,bool canViewSecrecy,IReadOnlySet<string> deniedFields,CancellationToken token)
    {
        const string sql="""
            SELECT LTRIM(RTRIM(f.F_ID)),CAST(COALESCE(f.IS_COST,0) AS bit),CAST(COALESCE(f.IS_SECRECY,0) AS bit)
            FROM dbo.FIELDS f WITH (NOLOCK)
            WHERE f.T_ID=@TargetTable AND COALESCE(f.IS_VIRTUAL,0)=0
              AND EXISTS (SELECT 1 FROM sys.columns c
                          JOIN sys.objects o ON c.object_id=o.object_id AND o.type IN ('U','V')
                          JOIN sys.schemas s ON o.schema_id=s.schema_id
                          WHERE s.name=N'dbo' AND o.name=@TargetTable AND c.name=f.F_ID)
            ORDER BY f.F_ID;
            """;
        await using var command=new SqlCommand(sql,connection);command.Parameters.Add("@TargetTable",SqlDbType.NVarChar,100).Value=targetTable;
        await using var reader=await command.ExecuteReaderAsync(token);
        var result=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while(await reader.ReadAsync(token))
        {
            var key=reader.GetString(0).Trim();
            if(WorkbenchSql.Identifier.IsMatch(key)&&!deniedFields.Contains(key)&&(canViewCost||!reader.GetBoolean(1))&&(canViewSecrecy||!reader.GetBoolean(2)))
                result.Add(key);
        }
        return result;
    }

    /// <summary>模块已编辑未发布（脏）时回退实时元数据构建，避免快照消费造成放量/字段维护立即生效语义变化。</summary>
    private static async Task<bool> IsDirtyAsync(SqlConnection connection, int moduleId, CancellationToken token)
    {
        const string sql = "SELECT 1 FROM dbo.WORKBENCH_MODULE_DIRTY WITH (NOLOCK) WHERE MODULE_ID=@ModuleId AND DIRTY_TAG=1;";
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@ModuleId", SqlDbType.Int).Value = moduleId;
        return await command.ExecuteScalarAsync(token) is not null;
    }

    /// <summary>读取模块分组表达式（GROUP1..5/GROUP_EXP1..5；快照 JSON 不含高危表达式，运行时实时读取）。</summary>
    private static async Task<(bool[] Enabled, string[] Expressions)> ReadGroupExpressionsAsync(
        SqlConnection connection, int moduleId, CancellationToken token)
    {
        const string sql = """
            SELECT ISNULL(GROUP1,0),ISNULL(GROUP_EXP1,''),ISNULL(GROUP2,0),ISNULL(GROUP_EXP2,''),
                   ISNULL(GROUP3,0),ISNULL(GROUP_EXP3,''),ISNULL(GROUP4,0),ISNULL(GROUP_EXP4,''),
                   ISNULL(GROUP5,0),ISNULL(GROUP_EXP5,'')
            FROM dbo.MODULES WITH (NOLOCK) WHERE M_IDX=@ModuleId;
            """;
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@ModuleId", SqlDbType.Int).Value = moduleId;
        await using var reader = await command.ExecuteReaderAsync(token);
        var enabled = new bool[5];
        var expressions = new string[5];
        if (await reader.ReadAsync(token))
        {
            for (var i = 0; i < 5; i++)
            {
                enabled[i] = reader.GetBoolean(i * 2);
                expressions[i] = reader.IsDBNull(i * 2 + 1) ? string.Empty : reader.GetString(i * 2 + 1).Trim();
            }
        }
        return (enabled, expressions);
    }

    private static async Task<IReadOnlyList<WorkbenchField>> ReadFields(SqlConnection connection,string userId,string masterTable,string targetTable,bool canViewCost,bool canViewSecrecy,IReadOnlySet<string> deniedFields,CancellationToken token)
    {
        const string sql="""
            WITH UserFields AS (
              SELECT LTRIM(RTRIM(F_ID)) F_ID,F_IDX FROM dbo.SYSQL_FIELDS WITH (NOLOCK)
              WHERE USER_ID=@UserId AND T_ID=@MasterTable AND T_ID_R=@TargetTable
            ), HasConfig AS (SELECT CASE WHEN EXISTS(SELECT 1 FROM UserFields) THEN 1 ELSE 0 END Value)
            SELECT f.F_ID,COALESCE(NULLIF(LTRIM(RTRIM(f.F_DESC)),''),f.F_ID),COALESCE(f.F_TYPE,'nvarchar'),
                   COALESCE(f.DISPLAY_LENGTH,100),NULLIF(LTRIM(RTRIM(f.ITEM_ALIGN)),''),CAST(CASE WHEN EXISTS(SELECT 1 FROM sys.indexes i2 JOIN sys.index_columns ic2 ON i2.object_id=ic2.object_id AND i2.index_id=ic2.index_id JOIN sys.columns c2 ON ic2.object_id=c2.object_id AND ic2.column_id=c2.column_id JOIN sys.tables t3 ON i2.object_id=t3.object_id JOIN sys.schemas s3 ON t3.schema_id=s3.schema_id WHERE s3.name=N'dbo' AND t3.name=@TargetTable AND i2.is_primary_key=1 AND c2.name=f.F_ID) THEN 1 ELSE 0 END AS bit),CAST(COALESCE(f.IS_QUERY,1) AS bit),CAST(COALESCE(f.IS_COST,0) AS bit),CAST(COALESCE(f.IS_SECRECY,0) AS bit),COALESCE(NULLIF(f.HEADER_ALIGN,''),'center'),f.DISPLAY_FORMAT,f.BROWSE_URL,f.BROWSE_M_IDX,CAST(COALESCE(f.IS_VIRTUAL,0) AS bit),f.VIRTUAL_EXP,f.CONVERT_FUNCTION
            FROM dbo.FIELDS f WITH (NOLOCK) CROSS JOIN HasConfig h LEFT JOIN UserFields u ON u.F_ID=f.F_ID
            WHERE f.T_ID=@TargetTable AND COALESCE(f.IS_VISIBLE,1)=1
              AND (COALESCE(f.IS_VIRTUAL,0)=1 OR EXISTS (SELECT 1 FROM sys.columns c
                          JOIN sys.objects o ON c.object_id=o.object_id AND o.type IN ('U','V')
                          JOIN sys.schemas s ON o.schema_id=s.schema_id
                          WHERE s.name=N'dbo' AND o.name=@TargetTable AND c.name=f.F_ID))
              AND (EXISTS(SELECT 1 FROM sys.indexes i2 JOIN sys.index_columns ic2 ON i2.object_id=ic2.object_id AND i2.index_id=ic2.index_id JOIN sys.columns c2 ON ic2.object_id=c2.object_id AND ic2.column_id=c2.column_id JOIN sys.tables t3 ON i2.object_id=t3.object_id JOIN sys.schemas s3 ON t3.schema_id=s3.schema_id WHERE s3.name=N'dbo' AND t3.name=@TargetTable AND i2.is_primary_key=1 AND c2.name=f.F_ID) OR (h.Value=1 AND u.F_ID IS NOT NULL) OR (h.Value=0 AND COALESCE(f.IS_DEFAULT_FIELDS,0)=1))
            ORDER BY CASE WHEN EXISTS(SELECT 1 FROM sys.indexes i2 JOIN sys.index_columns ic2 ON i2.object_id=ic2.object_id AND i2.index_id=ic2.index_id JOIN sys.columns c2 ON ic2.object_id=c2.object_id AND ic2.column_id=c2.column_id JOIN sys.tables t3 ON i2.object_id=t3.object_id JOIN sys.schemas s3 ON t3.schema_id=s3.schema_id WHERE s3.name=N'dbo' AND t3.name=@TargetTable AND i2.is_primary_key=1 AND c2.name=f.F_ID) AND u.F_ID IS NULL THEN 0 ELSE 1 END,COALESCE(u.F_IDX,COALESCE(f.VERIFY_INDEX,999)),f.F_ID OPTION (OPTIMIZE FOR UNKNOWN);
            """;
        await using var command=new SqlCommand(sql,connection);command.Parameters.Add("@UserId",SqlDbType.NChar,10).Value=userId.Trim();command.Parameters.Add("@MasterTable",SqlDbType.VarChar,100).Value=masterTable;command.Parameters.Add("@TargetTable",SqlDbType.VarChar,100).Value=targetTable;
        var fields=new List<WorkbenchField>();
        await using(var reader=await command.ExecuteReaderAsync(token))
        {
            while(await reader.ReadAsync(token)){var key=reader.GetString(0).Trim();if(WorkbenchSql.Identifier.IsMatch(key)&&!deniedFields.Contains(key)&&(canViewCost||!reader.GetBoolean(7))&&(canViewSecrecy||!reader.GetBoolean(8))){var isVirtual=reader.GetBoolean(13);var virtualExpression=reader.IsDBNull(14)?null:reader.GetString(14).Trim();var convertFunction=reader.IsDBNull(15)?null:reader.GetString(15).Trim();fields.Add(new(key,reader.GetString(1).Trim(),reader.GetString(2).Trim(),Math.Clamp(reader.GetInt32(3),40,300),reader.IsDBNull(4)?null:reader.GetString(4).Trim(),reader.GetBoolean(5),true,isVirtual?false:reader.GetBoolean(6),reader.GetString(9),reader.IsDBNull(10)?null:reader.GetString(10),reader.IsDBNull(11)?null:reader.GetString(11),reader.IsDBNull(12)?null:reader.GetInt32(12),isVirtual,virtualExpression,convertFunction));}}
        }
        var virtualFields=fields.Where(field=>field.IsVirtual).ToList();
        if(virtualFields.Count>0)
        {
            var resolution=await new VirtualColumnResolver(connection).ResolveAsync(targetTable,virtualFields,token);
            if(resolution.UnresolvedKeys.Count>0)
            {
                var dropped=resolution.UnresolvedKeys.ToHashSet(StringComparer.OrdinalIgnoreCase);
                fields=fields.Where(field=>!dropped.Contains(field.Key)).ToList();
            }
        }
        var allowedFields=fields.Select(field=>field.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        for(var i=0;i<fields.Count;i++){var field=fields[i];var safe=SanitizeBrowseUrl(field.BrowseUrl,allowedFields);if(safe!=field.BrowseUrl)fields[i]=field with{BrowseUrl=safe};}
        return fields;
    }



    private static string? NormalizeSort(string? value,string table,IReadOnlyList<WorkbenchField> fields)
    {
        if(string.IsNullOrWhiteSpace(value))return null;var allowed=fields.Where(field=>!field.IsVirtual).Select(field=>field.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);var result=new List<string>();
        foreach(var part in value.Split(',',StringSplitOptions.TrimEntries|StringSplitOptions.RemoveEmptyEntries)){var tokens=Regex.Split(part.Trim(),"\\s+");if(tokens.Length is <1 or >2)return null;var identifier=tokens[0].Split('.');if(identifier.Length==2&&!identifier[0].Equals(table,StringComparison.OrdinalIgnoreCase))return null;var field=identifier[^1].Trim('[',']');if(!WorkbenchSql.Identifier.IsMatch(field)||!allowed.Contains(field))return null;var direction=tokens.Length==2&&tokens[1].Equals("DESC",StringComparison.OrdinalIgnoreCase)?" DESC":tokens.Length==1||tokens[1].Equals("ASC",StringComparison.OrdinalIgnoreCase)?" ASC":null;if(direction is null)return null;result.Add($"[{field}]{direction}");}
        return result.Count==0?null:string.Join(',',result);
    }




    /// <summary>选择器关键字过滤的文本类字段判定（与查询组件 IsTextLike 同口径）。</summary>
    private static bool IsTextLike(string dataType)
    {
        var type = dataType.ToLowerInvariant();
        return type.Contains("char") || type.Contains("text") || type.Contains("date") || type.Contains("time")
            || type is "idcard" or "url" or "email" or "phoneno" or "zipcode";
    }

    private SqlConnection CreateConnection()=>connections.Create();
    /// <summary>
}
