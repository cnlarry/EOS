using EOS.API.Errors;
using EOS.API.Models;
using Microsoft.Data.SqlClient;
using System.Data;
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Text.Json.Serialization;

namespace EOS.API.Data;

public sealed record WorkbenchField(string Key, string Label, string DataType, int Width, string? Align, bool IsPrimaryKey, bool IsVisible = true, bool IsQueryable = true, string HeaderAlign = "center", string? Format = null, string? BrowseUrl = null, int? BrowseModuleId = null, bool IsVirtual = false, [property: JsonIgnore] string? VirtualExpression = null);
public sealed record WorkbenchColumn(string Key, string Label, bool IsVisible, int Order, bool IsVirtual = false);
public sealed record WorkbenchColumnSettings(IReadOnlyList<WorkbenchColumn> Master, IReadOnlyList<WorkbenchColumn> Detail);
public sealed record SaveWorkbenchColumns(IReadOnlyList<string> Master, IReadOnlyList<string> Detail);
public sealed record UpdateColumnWidthsRequest(IReadOnlyDictionary<string, int> Master, IReadOnlyDictionary<string, int>? Detail);
public sealed record WorkbenchFieldSummary(string Key,string Label,bool IsVisible,bool IsDefault,bool IsQueryable,bool IsReadonly,bool IsCost,bool IsSecrecy,bool IsVirtual);
public sealed record FieldChooserSource(bool Active,string? Table,string? Description,int? ModuleId,string? Filter,string? ReturnMapping);
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
    string? FormButtons = null);
/// <summary>统一表单页签定义（解析自 MODULES.FORM_TABS，如 '1=基本资料;2=其它'）。</summary>
public sealed record FormTabDefinition(int No, string Title);
/// <summary>统一表单下拉选项（解析自 FIELDS.FORM_OPTIONS，如 'O=外含税;I=内含税'）。</summary>
public sealed record FormOptionItem(string Value, string Label);
public sealed record FormDefinition(int ModuleId, string Title, string MasterTable, string? DetailTable, bool HasAdd, bool HasEdit, string Mode, IReadOnlyList<FormFieldDefinition> MasterFields, IReadOnlyList<FormFieldDefinition> DetailFields, IReadOnlyList<string> MasterPkOrder, string DetailNoFields, string DetailDfVerify, IReadOnlyList<FormTabDefinition> Tabs = default!, int Columns = 2, string? Buttons = null, IReadOnlyDictionary<string,string> DefaultValues = default!);
public sealed record FormFieldDefinition(string Key, string Label, string DataType, int DisplayLength, string? DisplayFormat, bool IsRequired, int? VerifyIndex, string? Regex, string? DefaultValue, bool IsReadonly, bool IsVisible, bool OnlyChoose, bool ChooseMultiple, string? ChoosePage, IReadOnlyList<FieldChooserSource> Choosers, bool IsPrimaryKey, bool IsAutoIncrement, bool IsVirtual, bool IsCost, bool IsSecrecy, bool ServerFilled, int? MaxLength, int TabNo = 1, int? FormOrder = null, int Span = 1, bool NewLine = false, string? CellGroup = null, int CellRole = 0, IReadOnlyList<FormOptionItem>? Options = null, bool DisplayOnly = false);
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
    ControlledSprocInvoker controlledSprocs,
    WorkflowEngine workflowEngine,
    DomainRuleService domainRules,
    ILogger<DocumentWorkbenchRepository> logger)
{
    private static readonly Regex Identifier = new("^[A-Za-z_][A-Za-z0-9_]{0,127}$", RegexOptions.Compiled);
    private static readonly Regex BrowseUrlPlaceholder = new(@"\{([^{}]*)\}", RegexOptions.Compiled);
    /// <summary>
    /// 选择器 CHOOSE_FILTER 跨表 JOIN 白名单：外键表名 → 与查询表同名的关联列。
    /// 旧系统过滤器常引用 CLIENT_PRICE_M/PRODUCT 等表（如"仅客户计价 + 启用料号"），
    /// 此处仅允许登记在册的表 + 固定关联键，其余跨表引用一律拒绝。
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> ChooserJoinTables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["PRODUCT"] = "PRO_NO",
        ["CLIENT_PRICE_M"] = "CLIENT_ID",
        ["CLIENT"] = "CLIENT_ID",
        ["SUPPLIER"] = "SUPPLIER_ID",
    };
    private static readonly Regex MasterValuePlaceholderQuoted = new(@"'\{m\.([A-Za-z_][A-Za-z0-9_]*)\}'", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex MasterValuePlaceholder = new(@"\{m\.([A-Za-z_][A-Za-z0-9_]*)\}", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex DetailValuePlaceholderQuoted = new(@"'\{d\.([A-Za-z_][A-Za-z0-9_]*)\}'", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex DetailValuePlaceholder = new(@"\{d\.([A-Za-z_][A-Za-z0-9_]*)\}", RegexOptions.Compiled | RegexOptions.IgnoreCase);
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
            if (token.Length == 0 || !Identifier.IsMatch(token) || !allowedFields.Contains(token)) return null;
        }
        return url;
    }

    public async Task<WorkbenchDefinition?> GetDefinitionAsync(int moduleId, string userId, string? execTag, bool canViewCost, bool canViewSecrecy, IReadOnlySet<string> deniedMasterFields, IReadOnlySet<string> deniedDetailFields, CancellationToken token)
    {
        await using var connection = CreateConnection(); await connection.OpenAsync(token);
        const string sql = "SELECT M_DESC,MASTER_TABLE,DETAIL_TABLE,M_URL,SORT_FIELDS,MODI_URL,DETAIL_NO_SAVE,DETAIL_NO_FIELDS,FILTER,UPDATE_SP,AFTERSAVE_SP,AUTO_APPROVE," +
                           "GROUP1,GROUP_EXP1,GROUP2,GROUP_EXP2,GROUP3,GROUP_EXP3,GROUP4,GROUP_EXP4,GROUP5,GROUP_EXP5," +
                           "FORM_TABS,FORM_COLUMNS,FORM_BUTTONS " +
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
        var modiUrl=reader.IsDBNull(5)?"":reader.GetString(5).Trim(); var hasEdit=!string.IsNullOrWhiteSpace(modiUrl);
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
        await reader.CloseAsync();
        if (!IsWorkbenchUrl(url) || !Identifier.IsMatch(master) || (detail is not null && !Identifier.IsMatch(detail)))
        {
            logger.LogWarning("模块 {ModuleId} 未通过工作台校验 url={Url} master={Master} detail={Detail}", moduleId, url, master, detail);
            return null;
        }
        var masterFields=await ReadFields(connection,userId,master,master,canViewCost,canViewSecrecy,deniedMasterFields,token);
        var masterPkOrder=await GetPrimaryKeyColumnsAsync(connection,null,master,token);
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
            }
        }
        WorkbenchDefinition definition=new(moduleId,title,master,detail,masterFields,
            detail is null?[]:await ReadFields(connection,userId,master,detail,canViewCost,canViewSecrecy,deniedDetailFields,token),NormalizeSort(defaultSort,master,masterFields),hasEdit,hasEdit,detailNoSave,
            masterPkOrder,detailNoFields,
            businessRule?.WorkflowSproc is not null,
            string.IsNullOrWhiteSpace(moduleFilter)?null:moduleFilter,
            await ReadFilterFieldKeys(connection,master,canViewCost,canViewSecrecy,deniedMasterFields,token),
            userId.Trim(),
            string.IsNullOrWhiteSpace(execTag)?"A":execTag.Trim(),
            await ColumnExistsAsync(connection,null,master,"OWNER",token),
            await ColumnExistsAsync(connection,null,master,"OWNER_G",token),
            businessRule,
            autoApprove,
            groupExpressions,
            string.IsNullOrWhiteSpace(formTabs) ? null : formTabs,
            formColumns,
            string.IsNullOrWhiteSpace(formButtons) ? null : formButtons);
        logger.LogDebug("工作台定义 module={ModuleId} title={Title} master={Master} detail={Detail} masterFields={MasterFieldCount} detailFields={DetailFieldCount}",
            moduleId,title,master,detail,definition.MasterFields.Count,definition.DetailFields.Count);
        return definition;
    }

    public async Task<IReadOnlyList<FieldSetupLookup>> GetFieldSetupTablesAsync(CancellationToken token)
    {await using var connection=CreateConnection();await connection.OpenAsync(token);const string sql="SELECT LTRIM(RTRIM(T_ID)),COALESCE(NULLIF(LTRIM(RTRIM(T_DESC)),''),LTRIM(RTRIM(T_ID))) FROM dbo.TABLES WITH (NOLOCK) ORDER BY T_DESC,T_ID";await using var command=new SqlCommand(sql,connection);await using var reader=await command.ExecuteReaderAsync(token);var result=new List<FieldSetupLookup>();while(await reader.ReadAsync(token)){var value=reader.GetString(0);if(Identifier.IsMatch(value))result.Add(new(value,reader.GetString(1)));}return result;}

    public async Task<IReadOnlyList<FieldSetupLookup>> GetFieldSetupModulesAsync(CancellationToken token)
    {await using var connection=CreateConnection();await connection.OpenAsync(token);const string sql="SELECT CONVERT(nvarchar(20),M_IDX),COALESCE(NULLIF(LTRIM(RTRIM(M_DESC)),''),CONVERT(nvarchar(20),M_IDX)) FROM dbo.MODULES WITH (NOLOCK) ORDER BY M_DESC,M_IDX";await using var command=new SqlCommand(sql,connection);await using var reader=await command.ExecuteReaderAsync(token);var result=new List<FieldSetupLookup>();while(await reader.ReadAsync(token))result.Add(new(reader.GetString(0),reader.GetString(1)));return result;}

    /// <summary>
    /// 应用模块级行过滤（MODULES.FILTER，如 1206 成品资料 PRODUCT.PRO_TYPE=1）。
    /// 仅接受受限解析器支持的谓词（白名单主表字段 + 比较运算符 + 常量，参数化）；
    /// 无法安全解析时抛 DataFilterUnsupportedException（403），拒绝返回未过滤数据。
    /// </summary>
    private static void ApplyModuleFilter(
        WorkbenchDefinition definition,
        ICollection<string> predicates,
        SqlCommand command)
    {
        if(string.IsNullOrWhiteSpace(definition.ModuleFilter))return;
        // 过滤器字段白名单 = 全量可见字段（不受用户列选择影响）：
        // 用户保存的列配置可能不含过滤器引用的字段，若以可见列为准会误拒整个模块查询。
        var allowedFields=definition.FilterFieldKeys
            ?? definition.MasterFields.Select(field=>field.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if(!DataFilterParser.TryParse(definition.ModuleFilter,definition.MasterTable,allowedFields,
               out var predicate,out var parameters))
            throw new DataFilterUnsupportedException("该模块的数据过滤条件尚不支持，已拒绝查询。");
        predicates.Add(predicate);
        // 解析器生成的谓词按顺序引用 @df0..@dfN，参数名与值一一对应
        var parameterNames=Regex.Matches(predicate,"@df\\d+")
            .Select(match=>match.Value)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(name=>int.Parse(name[3..]))
            .ToList();
        for(var i=0;i<parameterNames.Count;i++)
            command.Parameters.AddWithValue(parameterNames[i],parameters[i]);
    }

    /// <summary>
    /// 应用菜单分组筛选（GROUP_EXP&lt;groupIndex&gt; = groupValue）。
    /// 与 MODULES.FILTER 同边界：表达式经 GroupExpressionParser 受控编译
    /// （白名单主表字段），值参数化；不可解析抛 GroupExpressionUnsupportedException（403）。
    /// </summary>
    private static void ApplyGroupFilter(
        WorkbenchDefinition definition,
        int? groupIndex,
        string? groupValue,
        ICollection<string> predicates,
        SqlCommand command)
    {
        if (groupIndex is null || string.IsNullOrWhiteSpace(groupValue)) return;
        if (groupIndex is < 1 or > 5)
            throw new GroupExpressionUnsupportedException("分组序号无效，已拒绝查询。");
        var expression = definition.GroupExpressions[groupIndex.Value - 1];
        if (string.IsNullOrWhiteSpace(expression))
            throw new GroupExpressionUnsupportedException("该模块未启用此分组表达式，已拒绝查询。");
        var allowedFields = definition.FilterFieldKeys
            ?? definition.MasterFields.Select(field => field.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!GroupExpressionParser.TryCompile(expression, definition.MasterTable, allowedFields, out var compiled))
            throw new GroupExpressionUnsupportedException("分组表达式超出受控子集，已拒绝查询。");
        var parameterName = $"@gf{predicates.Count}";
        predicates.Add($"({compiled})={parameterName}");
        command.Parameters.AddWithValue(parameterName, groupValue);
    }

    /// <summary>
    /// 执行范围（EXEC_TAG）行级过滤，对齐旧 DxQueryButton.OtherCondition：
    /// B 仅本人（OWNER=当前用户）、C 本人及下级（含 f_get_underling）、
    /// D 本人所属组（OWNER_G）、E 本人及下级所属组；Z/A 无附加范围。
    /// 表缺 OWNER/OWNER_G 列时拒绝查询（不返回越权数据）；值全部参数化。
    /// </summary>
    private static void ApplyExecTagScope(
        WorkbenchDefinition definition,
        ICollection<string> predicates,
        SqlCommand command)
    {
        var tag=(definition.ExecTag??"Z").Trim().ToUpperInvariant();
        if(tag is "Z" or "A" or "")return;
        var ownerParameter="@execOwner";
        switch(tag)
        {
            case "B":
                if(!definition.HasOwnerColumn)
                    throw new DataFilterUnsupportedException("该模块不支持按执行范围过滤。");
                predicates.Add($"[OWNER]={ownerParameter}");
                command.Parameters.AddWithValue(ownerParameter,definition.UserId);
                break;
            case "C":
                if(!definition.HasOwnerColumn)
                    throw new DataFilterUnsupportedException("该模块不支持按执行范围过滤。");
                predicates.Add($"([OWNER]={ownerParameter} OR [OWNER] IN (SELECT USER_ID FROM dbo.f_get_underling({ownerParameter})))");
                command.Parameters.AddWithValue(ownerParameter,definition.UserId);
                break;
            case "D":
                if(!definition.HasOwnerGroupColumn)
                    throw new DataFilterUnsupportedException("该模块不支持按执行范围过滤。");
                predicates.Add($"[OWNER_G] IN (SELECT G_IDX FROM dbo.SYSDG_USER WITH (NOLOCK) WHERE USER_ID={ownerParameter})");
                command.Parameters.AddWithValue(ownerParameter,definition.UserId);
                break;
            case "E":
                if(!definition.HasOwnerGroupColumn)
                    throw new DataFilterUnsupportedException("该模块不支持按执行范围过滤。");
                predicates.Add($"[OWNER_G] IN (SELECT G_IDX FROM dbo.SYSDG_USER WITH (NOLOCK) WHERE USER_ID IN (SELECT {ownerParameter} UNION ALL SELECT USER_ID FROM dbo.f_get_underling({ownerParameter})))");
                command.Parameters.AddWithValue(ownerParameter,definition.UserId);
                break;
        }
    }

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
            if (IsWorkbenchUrl(url))
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
        await using var command=new SqlCommand(sql,connection);command.Parameters.Add("@Table",SqlDbType.NVarChar,100).Value=table;await using var reader=await command.ExecuteReaderAsync(token);var result=new List<WorkbenchFieldSummary>();while(await reader.ReadAsync(token)){var key=reader.GetString(0);if(Identifier.IsMatch(key))result.Add(new(key,reader.GetString(1),reader.GetBoolean(2),reader.GetBoolean(3),reader.GetBoolean(4),reader.GetBoolean(5),reader.GetBoolean(6),reader.GetBoolean(7),reader.GetBoolean(8)));}return result;
    }

    public async Task<WorkbenchFieldMetadata?> GetFieldMetadataAsync(WorkbenchDefinition definition,bool detail,string fieldKey,CancellationToken token)
    {
        var table=detail?definition.DetailTable:definition.MasterTable;if(table is null||!Identifier.IsMatch(fieldKey))return null;
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
        new(source.Active,source.Table,source.Description,source.ModuleId,source.Filter,source.ReturnMapping);

    public async Task UpdateFieldMetadataAsync(WorkbenchDefinition definition,bool detail,string fieldKey,UpdateWorkbenchFieldMetadata update,string updatedBy,CancellationToken token)
    {
        var table=detail?definition.DetailTable:definition.MasterTable;if(table is null||!Identifier.IsMatch(fieldKey))throw new ArgumentException("字段无效。");
        var input=MapInput(update);
        var original=update.Original is null?null:MapInput(update.Original);
        await fieldAdmin.UpdateAsync(table,fieldKey.Trim(),input,original,updatedBy,token);
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
            if(field.Length==0||!Identifier.IsMatch(field)||!allowed.Contains(field))continue;
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
        new(source.Active,source.Table,source.Description,source.ModuleId,source.Filter,source.ReturnMapping);

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

    public async Task<WorkbenchData> GetRowsAsync(WorkbenchDefinition definition, bool detail, IReadOnlyDictionary<string,string> keys, int page, int pageSize, CancellationToken token, WorkbenchQuery? query=null, string? keyword=null, string? sortField=null, string? sortDirection=null, int? groupIndex=null, string? groupValue=null)
    {
        var table=detail?definition.DetailTable:definition.MasterTable; var fields=detail?definition.DetailFields:definition.MasterFields;
        page=Math.Max(1,page); pageSize=Math.Clamp(pageSize,10,100);
        if (table is null || fields.Count==0) return new([],0,page,pageSize);
        var selected=fields.Take(30).ToList();
        // 行标识必须稳定：物理主键列无论是否可见/是否被截断，都强制包含在返回行中。
        // MasterPkOrder 来自 INFORMATION_SCHEMA 主键约束（服务端白名单），不信任前端提交。
        foreach(var pk in definition.MasterPkOrder)
        {
            if(selected.Any(field=>field.Key.Equals(pk,StringComparison.OrdinalIgnoreCase)))continue;
            var field=fields.FirstOrDefault(item=>item.Key.Equals(pk,StringComparison.OrdinalIgnoreCase));
            if(field is null&&Identifier.IsMatch(pk))field=new WorkbenchField(pk,pk,"nvarchar",100,"left",true,false,false);
            if(field is not null)selected.Add(field);
        }
        var predicates=new List<string>();
        var stopwatch=Stopwatch.StartNew();
        await using var connection=CreateConnection(); await connection.OpenAsync(token); await using var command=new SqlCommand(); command.Connection=connection;
        if (detail) foreach(var key in definition.MasterPkOrder) if(keys.TryGetValue(key,out var value) && definition.DetailFields.Any(field=>field.Key.Equals(key,StringComparison.OrdinalIgnoreCase))) { var name=$"@k{predicates.Count}"; predicates.Add($"[{key}]={name}"); command.Parameters.AddWithValue(name,value); }
        if(detail && predicates.Count==0)
        {
            logger.LogDebug("子表查询缺少主表关联键，跳过 detail={Detail} table={Table}", detail, table);
            return new([],0,page,pageSize);
        }
        if (!detail && query is not null) AddQueryPredicates(query, definition.MasterFields, predicates, command);
        if (!detail && !string.IsNullOrWhiteSpace(keyword)) AddKeywordPredicates(keyword, fields, predicates, command);
        if (!detail) ApplyModuleFilter(definition, predicates, command);
        if (!detail) ApplyGroupFilter(definition, groupIndex, groupValue, predicates, command);
        if (!detail) ApplyExecTagScope(definition, predicates, command);
        var where=predicates.Count>0?" WHERE "+string.Join(" AND ",predicates):"";
        var order=ResolveOrder(definition,fields,selected,detail,sortField,sortDirection);
        var selection=await BuildListSelectionAsync(connection,table,selected,token);
        command.CommandText=selection.HasVirtual
            ? $"SELECT COUNT_BIG(1) FROM dbo.[{table}] WITH (NOLOCK){where}; SELECT {selection.OuterColumns} FROM (SELECT {selection.InnerColumns} FROM dbo.[{table}] WITH (NOLOCK){where}) AS [__base]{selection.JoinFragment} ORDER BY {QualifyOrder(order)} OFFSET @Offset ROWS FETCH NEXT @PageSize ROWS ONLY;"
            : $"SELECT COUNT_BIG(1) FROM dbo.[{table}] WITH (NOLOCK){where}; SELECT {selection.InnerColumns} FROM dbo.[{table}] WITH (NOLOCK){where} ORDER BY {order} OFFSET @Offset ROWS FETCH NEXT @PageSize ROWS ONLY;";
        command.Parameters.Add("@Offset",SqlDbType.Int).Value=(page-1)*pageSize;command.Parameters.Add("@PageSize",SqlDbType.Int).Value=pageSize;
        await using var reader=await command.ExecuteReaderAsync(token); await reader.ReadAsync(token);var total=Convert.ToInt32(reader.GetInt64(0));await reader.NextResultAsync(token); var rows=new List<Dictionary<string,object?>>();
        while(await reader.ReadAsync(token)){var row=new Dictionary<string,object?>(StringComparer.OrdinalIgnoreCase); for(var i=0;i<reader.FieldCount;i++) row[reader.GetName(i)]=reader.IsDBNull(i)?null:reader.GetValue(i); rows.Add(row);}
        logger.LogDebug("工作台查询完成 detail={Detail} table={Table} page={Page} pageSize={PageSize} total={Total} returned={Returned} elapsedMs={ElapsedMs:F0}",
            detail,table,page,pageSize,total,rows.Count,stopwatch.Elapsed.TotalMilliseconds);
        return new(rows,total,page,pageSize);
    }

    public async Task<IReadOnlyList<Dictionary<string,object?>>> GetExportRowsAsync(WorkbenchDefinition definition,WorkbenchQuery? query,string? keyword,CancellationToken token,string? sortField=null,string? sortDirection=null,int? groupIndex=null,string? groupValue=null)
    {
        var table=definition.MasterTable; var fields=definition.MasterFields;
        if (table is null || fields.Count==0) return [];
        const int maxExportRows=100000;
        var selected=fields.Take(30).ToList(); var predicates=new List<string>();
        var stopwatch=Stopwatch.StartNew();
        await using var connection=CreateConnection(); await connection.OpenAsync(token); await using var command=new SqlCommand(); command.Connection=connection;
        if (query is not null) AddQueryPredicates(query, definition.MasterFields, predicates, command);
        if (!string.IsNullOrWhiteSpace(keyword)) AddKeywordPredicates(keyword, fields, predicates, command);
        ApplyModuleFilter(definition, predicates, command);
        ApplyGroupFilter(definition, groupIndex, groupValue, predicates, command);
        ApplyExecTagScope(definition, predicates, command);
        var where=predicates.Count>0?" WHERE "+string.Join(" AND ",predicates):"";
        var order=ResolveOrder(definition,fields,selected,false,sortField,sortDirection);
        var selection=await BuildListSelectionAsync(connection,table,selected,token);
        command.CommandText=selection.HasVirtual
            ? $"SELECT TOP {maxExportRows} {selection.OuterColumns} FROM (SELECT {selection.InnerColumns} FROM dbo.[{table}] WITH (NOLOCK){where}) AS [__base]{selection.JoinFragment} ORDER BY {QualifyOrder(order)};"
            : $"SELECT TOP {maxExportRows} {selection.InnerColumns} FROM dbo.[{table}] WITH (NOLOCK){where} ORDER BY {order};";
        await using var reader=await command.ExecuteReaderAsync(token); var rows=new List<Dictionary<string,object?>>();
        while(await reader.ReadAsync(token)){var row=new Dictionary<string,object?>(StringComparer.OrdinalIgnoreCase); for(var i=0;i<reader.FieldCount;i++) row[reader.GetName(i)]=reader.IsDBNull(i)?null:reader.GetValue(i); rows.Add(row);}
        logger.LogDebug("工作台导出完成 table={Table} returned={RowCount} elapsedMs={ElapsedMs:F0}", table,rows.Count,stopwatch.Elapsed.TotalMilliseconds);
        return rows;
    }

    /// <summary>
    /// 按主键集合导出（导出所选行）：keys 为「主键值数组」列表，顺序与 masterPkOrder 一致。
    /// 全部条件参数化，字段沿用权限过滤后的定义白名单。
    /// </summary>
    public async Task<IReadOnlyList<Dictionary<string,object?>>> GetExportRowsByKeysAsync(WorkbenchDefinition definition,IReadOnlyList<IReadOnlyList<string>> keys,CancellationToken token,int? groupIndex=null,string? groupValue=null)
    {
        var table=definition.MasterTable;var fields=definition.MasterFields;
        if(table is null||fields.Count==0||keys.Count==0)return [];
        var pks=definition.MasterPkOrder
            .Select(key=>fields.FirstOrDefault(field=>field.Key.Equals(key,StringComparison.OrdinalIgnoreCase))?.Key)
            .Where(key=>key is not null).Cast<string>().ToList();
        if(pks.Count==0)return [];
        var selected=fields.Take(30).ToList();
        var filterPredicates=new List<string>();
        await using var connection=CreateConnection();await connection.OpenAsync(token);await using var command=new SqlCommand();command.Connection=connection;
        ApplyModuleFilter(definition, filterPredicates, command);
        ApplyGroupFilter(definition, groupIndex, groupValue, filterPredicates, command);
        ApplyExecTagScope(definition, filterPredicates, command);
        var orParts=new List<string>();
        for(var rowIndex=0;rowIndex<keys.Count;rowIndex++)
        {
            var row=keys[rowIndex];
            if(row.Count!=pks.Count)continue;
            var andParts=new List<string>();
            for(var i=0;i<pks.Count;i++)
            {
                var name=$"@k{rowIndex}_{i}";
                andParts.Add($"[{pks[i]}]={name}");
                command.Parameters.AddWithValue(name,row[i]??"");
            }
            orParts.Add("("+string.Join(" AND ",andParts)+")");
        }
        if(orParts.Count==0)return [];
        var filterWhere=filterPredicates.Count>0?" AND "+string.Join(" AND ",filterPredicates):"";
        var selection=await BuildListSelectionAsync(connection,table,selected,token);
        command.CommandText=selection.HasVirtual
            ? $"SELECT {selection.OuterColumns} FROM (SELECT {selection.InnerColumns} FROM dbo.[{table}] WITH (NOLOCK) WHERE {string.Join(" OR ",orParts)}{filterWhere}) AS [__base]{selection.JoinFragment};"
            : $"SELECT {selection.InnerColumns} FROM dbo.[{table}] WITH (NOLOCK) WHERE {string.Join(" OR ",orParts)}{filterWhere};";
        await using var reader=await command.ExecuteReaderAsync(token);
        var rows=new List<Dictionary<string,object?>>();
        while(await reader.ReadAsync(token)){var row=new Dictionary<string,object?>(StringComparer.OrdinalIgnoreCase);for(var i=0;i<reader.FieldCount;i++)row[reader.GetName(i)]=reader.IsDBNull(i)?null:reader.GetValue(i);rows.Add(row);}
        return rows;
    }

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
        CancellationToken token)
    {
        await using var connection=CreateConnection(); await connection.OpenAsync(token);
        var pkColumns=await GetPrimaryKeyColumnsAsync(connection,null,definition.MasterTable,token);
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
            detailDfVerify=(await GetDfVerifyAsync(connection,null,definition.DetailTable,token))??"";
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
            tabs,columns,definition.FormButtons,defaultValues);
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
        return defaults;
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
                   CAST(COALESCE(f.CHOOSE_ACTIVE1,0) AS bit) AS CHOOSE_ACTIVE1,f.CHOOSE_T_ID1,f.CHOOSE_T_DESC1,f.CHOOSE_M_IDX1,f.CHOOSE_RETURNVAL1,
                   f.CHOOSE_FILTER1,
                   CAST(COALESCE(f.CHOOSE_ACTIVE2,0) AS bit) AS CHOOSE_ACTIVE2,f.CHOOSE_T_ID2,f.CHOOSE_T_DESC2,f.CHOOSE_M_IDX2,f.CHOOSE_RETURNVAL2,
                   f.CHOOSE_FILTER2,
                   CAST(COALESCE(f.CHOOSE_ACTIVE3,0) AS bit) AS CHOOSE_ACTIVE3,f.CHOOSE_T_ID3,f.CHOOSE_T_DESC3,f.CHOOSE_M_IDX3,f.CHOOSE_RETURNVAL3,
                   f.CHOOSE_FILTER3,
                   CAST(COALESCE(f.CHOOSE_ACTIVE4,0) AS bit) AS CHOOSE_ACTIVE4,f.CHOOSE_T_ID4,f.CHOOSE_T_DESC4,f.CHOOSE_M_IDX4,f.CHOOSE_RETURNVAL4,
                   f.CHOOSE_FILTER4,
                   CAST(COALESCE(f.IS_VIRTUAL,0) AS bit) AS IS_VIRTUAL,CAST(COALESCE(f.IS_COST,0) AS bit) AS IS_COST,
                   CAST(COALESCE(f.IS_SECRECY,0) AS bit) AS IS_SECRECY,CAST(COALESCE(f.IS_AUTOINC,0) AS bit) AS IS_AUTOINC,
                   d.F_IDX,CAST(CASE WHEN pk.COLUMN_NAME IS NULL THEN 0 ELSE 1 END AS bit) AS IS_PK,
                   col.CHARACTER_MAXIMUM_LENGTH AS MAX_LENGTH,
                   CAST(COALESCE(f.FORM_TAB_NO,1) AS int) AS FORM_TAB_NO,
                   f.FORM_ORDER AS FORM_ORDER,
                   CAST(COALESCE(f.FORM_SPAN,1) AS int) AS FORM_SPAN,
                   CAST(COALESCE(f.FORM_NEW_LINE,0) AS bit) AS FORM_NEW_LINE,
                   LTRIM(RTRIM(COALESCE(f.FORM_CELL_GROUP,''))) AS FORM_CELL_GROUP,
                   CAST(COALESCE(f.FORM_CELL_ROLE,0) AS int) AS FORM_CELL_ROLE,
                   f.FORM_OPTIONS AS FORM_OPTIONS,
                   CAST(CASE WHEN col.COLUMN_NAME IS NULL THEN 0 ELSE 1 END AS bit) AS IS_PHYSICAL
            FROM dbo.FIELDS f WITH (NOLOCK)
            LEFT JOIN (SELECT T_ID,T_ID_R,LTRIM(RTRIM(F_ID)) AS F_ID,MIN(F_IDX) AS F_IDX
                       FROM dbo.SYSQL_DEFAULT WITH (NOLOCK)
                       GROUP BY T_ID,T_ID_R,LTRIM(RTRIM(F_ID))) d
              ON d.T_ID=@MasterTable AND d.T_ID_R=@TargetTable AND d.F_ID=LTRIM(RTRIM(f.F_ID))
            LEFT JOIN INFORMATION_SCHEMA.COLUMNS col
              ON col.TABLE_SCHEMA='dbo' AND col.TABLE_NAME=@TargetTable AND col.COLUMN_NAME=f.F_ID
            LEFT JOIN (SELECT ku.COLUMN_NAME FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS tc
                       INNER JOIN INFORMATION_SCHEMA.KEY_COLUMN_USAGE ku
                         ON ku.CONSTRAINT_NAME=tc.CONSTRAINT_NAME AND ku.CONSTRAINT_SCHEMA=tc.CONSTRAINT_SCHEMA
                       WHERE tc.CONSTRAINT_TYPE='PRIMARY KEY' AND ku.TABLE_SCHEMA='dbo' AND ku.TABLE_NAME=@TargetTable) pk
              ON pk.COLUMN_NAME=f.F_ID
            WHERE f.T_ID=@TargetTable
              AND (COALESCE(f.IS_VIRTUAL,0)=@IncludeVirtual OR col.COLUMN_NAME IS NOT NULL OR LTRIM(RTRIM(COALESCE(f.FORM_CELL_GROUP,'')))<>'')
            ORDER BY CASE WHEN f.FORM_ORDER IS NULL THEN 1 ELSE 0 END,COALESCE(f.FORM_ORDER,d.F_IDX,COALESCE(f.VERIFY_INDEX,999)),f.F_ID;
            """;
        await using var command=new SqlCommand(sql,connection);
        command.Parameters.Add("@MasterTable",SqlDbType.VarChar,100).Value=masterTable;
        command.Parameters.Add("@TargetTable",SqlDbType.VarChar,100).Value=targetTable;
        command.Parameters.Add("@IncludeVirtual",SqlDbType.Bit).Value=includeVirtual;
        await using var reader=await command.ExecuteReaderAsync(token);
        var rows=new List<FormFieldRow>();
        while(await reader.ReadAsync(token)) rows.Add(ReadFormFieldRow(reader));
        return rows;
    }

    private static FormFieldRow ReadFormFieldRow(SqlDataReader reader)
    {
        var choosers=new List<FormChooserRow>(4)
        {
            ReadChooser(reader,1),
            ReadChooser(reader,2),
            ReadChooser(reader,3),
            ReadChooser(reader,4),
        };
        return new FormFieldRow(
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
            choosers,
            reader.GetBoolean(reader.GetOrdinal("IS_VIRTUAL")),
            reader.GetBoolean(reader.GetOrdinal("IS_COST")),
            reader.GetBoolean(reader.GetOrdinal("IS_SECRECY")),
            reader.GetBoolean(reader.GetOrdinal("IS_AUTOINC")),
            reader.GetBoolean(reader.GetOrdinal("IS_PK")),
            reader.GetNullableInt32("MAX_LENGTH"),
            reader.GetInt32(reader.GetOrdinal("FORM_TAB_NO")),
            reader.GetNullableInt32("FORM_ORDER"),
            reader.GetInt32(reader.GetOrdinal("FORM_SPAN")),
            reader.GetBoolean(reader.GetOrdinal("FORM_NEW_LINE")),
            reader.GetNullableString("FORM_CELL_GROUP"),
            reader.GetInt32(reader.GetOrdinal("FORM_CELL_ROLE")),
            reader.GetNullableString("FORM_OPTIONS"),
            reader.GetBoolean(reader.GetOrdinal("IS_PHYSICAL")));
    }

    private static FormChooserRow ReadChooser(SqlDataReader reader,int index)
    {
        var table=reader.GetNullableString($"CHOOSE_T_ID{index}")?.Trim();
        return new FormChooserRow(
            reader.GetBoolean(reader.GetOrdinal($"CHOOSE_ACTIVE{index}")),
            table,
            reader.GetNullableString($"CHOOSE_T_DESC{index}")?.Trim(),
            reader.GetNullableInt32($"CHOOSE_M_IDX{index}"),
            reader.GetNullableString($"CHOOSE_RETURNVAL{index}"),
            reader.GetNullableString($"CHOOSE_FILTER{index}")?.Trim());
    }

    #region 记录读取与保存（M2 核心写操作）

    public async Task<RecordReadResult> GetRecordAsync(
        WorkbenchDefinition definition,
        FormDefinition form,
        IReadOnlyList<string> keyValues,
        string? dataFilter,
        CancellationToken token)
    {
        await using var connection=CreateConnection(); await connection.OpenAsync(token);
        var pkColumns=await GetPrimaryKeyColumnsAsync(connection,null,definition.MasterTable,token);
        if(pkColumns.Count!=keyValues.Count)return new(RecordAccessStatus.KeyMismatch,null);
        var masterFields=form.MasterFields.Where(field=>!field.DisplayOnly).Select(field=>field.Key).Concat(pkColumns).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var current=await ReadRowAsync(connection,null,definition.MasterTable,pkColumns,keyValues,masterFields,token);
        if(current is null)return new(RecordAccessStatus.NotFound,null);
        if(!string.IsNullOrWhiteSpace(dataFilter))
        {
            if(!DataFilterParser.TryParse(dataFilter,definition.MasterTable,ScopeFields(form.MasterFields,pkColumns),out var predicate,out var parameters))
                return new(RecordAccessStatus.FilterUnsupported,null);
            if(!await RecordInScopeAsync(connection,null,definition.MasterTable,pkColumns,keyValues,predicate,parameters,token))
                return new(RecordAccessStatus.OutOfScope,null);
        }
        await ResolveChooserDisplaysAsync(connection,form.MasterFields,current,token);
        var detailRows=new List<IReadOnlyDictionary<string,object?>>();
        if(definition.DetailTable is not null&&form.DetailFields.Count>0)
        {
            var detailFields=form.DetailFields.Where(field=>!field.DisplayOnly).Select(field=>field.Key).Concat(pkColumns).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            detailRows.AddRange(await ReadRowsAsync(connection,null,definition.DetailTable,pkColumns,keyValues,detailFields,token));
        }
        return new(RecordAccessStatus.Ok,new RecordBundle(current,detailRows));
    }

    public async Task<RecordSaveResult> CreateRecordAsync(
        WorkbenchDefinition definition,
        FormDefinition form,
        SaveRecordRequest request,
        string employeeName,
        string userId,
        string? dataFilter,
        CancellationToken token)
    {
        await using var connection=CreateConnection(); await connection.OpenAsync(token);
        await using var transaction=(SqlTransaction)await connection.BeginTransactionAsync(token);
        var pkColumns=await GetPrimaryKeyColumnsAsync(connection,transaction,definition.MasterTable,token);
        if(pkColumns.Count==0)return RecordSaveResult.Failed(RecordAccessStatus.ValidationFailed,"NO_PRIMARY_KEY","模块主表缺少主键定义。");
        var masterIdentity=await GetIdentityColumnsAsync(connection,transaction,definition.MasterTable,token);

        var validation=RecordPayloadValidator.ValidateSubmitted(form.MasterFields,request.Values);
        if(validation.Errors.Count>0)return RecordSaveResult.Failed(RecordAccessStatus.ValidationFailed,"VALIDATION_FAILED","数据校验未通过。",validation.Errors);
        var values=new Dictionary<string,object?>(validation.Converted,StringComparer.OrdinalIgnoreCase);
        RecordPayloadValidator.ApplyDefaults(form.MasterFields,values);
        FormDefaultRules.Apply(definition.ModuleId,form.MasterFields,values);

        // 领域规则：自动单号 + 默认单别（等价旧 GetNewBillNo / GetDefaultBillInfo）
        var businessRule=definition.BusinessRule;
        if(businessRule is { AutoBillNo: true, BillNoField: not null, BillTypeField: not null })
        {
            var existingNo=values.GetValueOrDefault(businessRule.BillNoField);
            if(existingNo is null||string.IsNullOrWhiteSpace(ValueToString(existingNo)))
            {
                var newNo=await BillNoGenerator.GenerateAsync(connection,transaction,definition.ModuleId,
                    definition.MasterTable,businessRule.BillNoField,businessRule.BillTypeField,token);
                if(newNo is not null)values[businessRule.BillNoField]=newNo;
            }
            var existingType=values.GetValueOrDefault(businessRule.BillTypeField);
            if(existingType is null||string.IsNullOrWhiteSpace(ValueToString(existingType)))
            {
                var billCode=await BillNoGenerator.GetDefaultBillCodeAsync(connection,transaction,definition.ModuleId,token);
                if(billCode is not null)values[businessRule.BillTypeField]=billCode;
            }
        }

        var finalErrors=RecordPayloadValidator.CheckRequiredAndRegex(form.MasterFields,values);
        if(finalErrors.Count>0)return RecordSaveResult.Failed(RecordAccessStatus.ValidationFailed,"VALIDATION_FAILED","数据校验未通过。",finalErrors);
        var fillErrors=await FillServerColumnsAsync(connection,transaction,definition.MasterTable,form.MasterFields,values,employeeName,true,token);
        if(fillErrors.Count>0)return RecordSaveResult.Failed(RecordAccessStatus.ValidationFailed,"SERVER_FILL_MISSING","存在服务端必填字段未登记填充规则。",fillErrors);

        foreach(var pk in pkColumns)
        {
            if(masterIdentity.Contains(pk))continue;
            if(!values.ContainsKey(pk)||values[pk] is null)
                return RecordSaveResult.Failed(RecordAccessStatus.ValidationFailed,"REQUIRED_FIELD_MISSING",$"主键字段 {pk} 不能为空。",[new FieldError(pk,"主键字段不能为空。","REQUIRED_FIELD_MISSING")]);
        }

        var keyValues=pkColumns.Select(column=>ValueToString(values.GetValueOrDefault(column))).ToList();
        if(!string.IsNullOrWhiteSpace(dataFilter))
        {
            if(!DataFilterParser.TryParse(dataFilter,definition.MasterTable,ScopeFields(form.MasterFields,pkColumns),out var predicate,out var parameters))
                return RecordSaveResult.Failed(RecordAccessStatus.FilterUnsupported,"DATA_FILTER_UNSUPPORTED","当前数据过滤条件尚不支持，已拒绝执行。");
            if(!await RecordInScopeAsync(connection,transaction,definition.MasterTable,pkColumns,keyValues,predicate,parameters,token))
                return RecordSaveResult.Failed(RecordAccessStatus.OutOfScope,"RECORD_OUT_OF_SCOPE","目标记录不在当前用户数据范围内。");
        }

        var insertFields=form.MasterFields.Where(field=>!field.IsVirtual&&values.ContainsKey(field.Key)&&!masterIdentity.Contains(field.Key)).ToList();
        if(insertFields.Count==0)return RecordSaveResult.Failed(RecordAccessStatus.ValidationFailed,"NO_WRITABLE_FIELDS","没有可写入的字段。");
        var identityValue=await InsertRowAsync(connection,transaction,definition.MasterTable,insertFields,values,masterIdentity,token);
        if(identityValue is not null&&masterIdentity.Count>0)
        {
            values[masterIdentity[0]]=identityValue;
            keyValues=pkColumns.Select(column=>ValueToString(values.GetValueOrDefault(column))).ToList();
        }

        var detailErrors=await SaveDetailsAsync(connection,transaction,definition,form,pkColumns,keyValues,values,request.Details??[],employeeName,true,token);
        if(detailErrors is not null)return RecordSaveResult.Failed(RecordAccessStatus.ValidationFailed,"VALIDATION_FAILED","明细数据校验未通过。",detailErrors);
        await SavePrepayOffsetsAsync(connection,transaction,businessRule,pkColumns,keyValues,request.PrepayOffsets,token);
        if(businessRule?.DomainRule is { } domainRule)
        {
            var domainResult=await domainRules.RunAfterSaveAsync(domainRule,connection,transaction,definition,pkColumns,keyValues,token);
            if(!domainResult.Success)
                return RecordSaveResult.Failed(RecordAccessStatus.ValidationFailed,"BUSINESS_VALIDATION_FAILED",
                    domainResult.Message??"保存后业务校验未通过。");
        }
        else if(businessRule?.AfterSaveSproc is { } afterSaveSproc)
        {
            var sprocResult=await controlledSprocs.RunAfterSaveAsync(definition.ModuleId,afterSaveSproc,pkColumns,keyValues,connection,transaction,token);
            if(!sprocResult.Success)
                return RecordSaveResult.Failed(RecordAccessStatus.ValidationFailed,"BUSINESS_VALIDATION_FAILED",
                    sprocResult.Message??"保存后业务校验未通过。");
        }
        await RecalculateMasterAmountsAsync(connection,transaction,definition,token);
        await WriteAuditAsync(connection,transaction,definition.ModuleId,string.Join(',',keyValues),"INSERT","新增记录",userId,token);
        await transaction.CommitAsync(token);
        logger.LogInformation("统一表单新增 module={ModuleId} master={Master} key={Key}",definition.ModuleId,definition.MasterTable,string.Join(',',keyValues));
        if(definition.AutoApprove)
        {
            // 自动批核模块：新增成功后立即进入批核状态（CONFIRM_TAG=1/SYSTEM + P_WF_* 副作用 + 审计）。
            // 在保存事务提交后执行——批核业务 SP 自带事务（自动提交），不能嵌套在保存事务内，
            // 与现有批核端点（WorkflowAsync）同结构；失败时回滚确认状态、记录保持已建未确认。
            var autoResult=await AutoApproveAsync(connection,definition,keyValues,userId,token);
            if(autoResult.Status!=RecordAccessStatus.Ok)
                logger.LogWarning("自动批核失败 module={ModuleId} key={Key} code={Code} message={Message}",
                    definition.ModuleId,string.Join(',',keyValues),autoResult.ErrorCode,autoResult.ErrorMessage);
        }
        return RecordSaveResult.Success(keyValues);
    }

    /// <summary>
    /// 自动批核（MODULES.AUTO_APPROVE=1）：新增后数据自动为批核状态，无需再点批核。
    /// 等价批核端点无流程路径：CONFIRM_TAG=1/CONFIRM_PERSON='SYSTEM'/CONFIRM_DATE=GETDATE() +
    /// P_WF_&lt;DOC&gt; 业务副作用（WorkflowSproc 存在时）+ APPROVE 审计；守卫防重复。
    /// 主表无 CONFIRM_TAG 的模块跳过（无确认语义）。
    /// </summary>
    private async Task<RecordSaveResult> AutoApproveAsync(
        SqlConnection connection,
        WorkbenchDefinition definition,
        IReadOnlyList<string> keyValues,
        string userId,
        CancellationToken token)
    {
        if(!await ColumnExistsAsync(connection,null,definition.MasterTable,"CONFIRM_TAG",token))
            return RecordSaveResult.Success(keyValues);
        var keyCondition=ControlledSprocInvoker.BuildKeyCondition(definition.MasterPkOrder,keyValues);
        var originalState=await ReadConfirmStateAsync(connection,definition.MasterTable,keyCondition,token);
        if(originalState is null)
            return RecordSaveResult.Failed(RecordAccessStatus.NotFound,"RECORD_NOT_FOUND","记录不存在。");
        if(originalState.Value.Tag==true)
            return RecordSaveResult.Success(keyValues);
        var confirmSql=$"UPDATE dbo.[{definition.MasterTable}] SET CONFIRM_PERSON=@ConfirmPerson,CONFIRM_DATE=GETDATE(),CONFIRM_TAG=1 WHERE ISNULL(CONFIRM_TAG,0)=0 AND {keyCondition};";
        await using(var confirmCommand=new SqlCommand(confirmSql,connection))
        {
            confirmCommand.Parameters.Add("@ConfirmPerson",SqlDbType.NVarChar,50).Value="SYSTEM";
            if(await confirmCommand.ExecuteNonQueryAsync(token)==0)
                return RecordSaveResult.Success(keyValues);
        }
        if(definition.BusinessRule?.WorkflowSproc is { } sproc)
        {
            var result=await controlledSprocs.RunWorkflowAsync(definition.ModuleId,sproc,definition.MasterPkOrder,keyValues,true,token);
            if(!result.Success)
            {
                await RestoreConfirmStateAsync(connection,definition.MasterTable,keyCondition,originalState.Value,token);
                return RecordSaveResult.Failed(RecordAccessStatus.ValidationFailed,"WORKFLOW_FAILED",
                    result.Message??"自动批核失败。");
            }
        }
        await WriteAuditAsync(connection,null,definition.ModuleId,string.Join(',',keyValues),"APPROVE","自动批核",userId,token);
        logger.LogInformation("自动批核 module={ModuleId} key={Key} executor={User}",definition.ModuleId,string.Join(',',keyValues),userId);
        return RecordSaveResult.Success(keyValues);
    }

    public async Task<RecordSaveResult> UpdateRecordAsync(
        WorkbenchDefinition definition,
        FormDefinition form,
        IReadOnlyList<string> keyValues,
        SaveRecordRequest request,
        string employeeName,
        string userId,
        string? dataFilter,
        CancellationToken token)
    {
        await using var connection=CreateConnection(); await connection.OpenAsync(token);
        await using var transaction=(SqlTransaction)await connection.BeginTransactionAsync(token);
        var pkColumns=await GetPrimaryKeyColumnsAsync(connection,transaction,definition.MasterTable,token);
        if(pkColumns.Count!=keyValues.Count)return RecordSaveResult.Failed(RecordAccessStatus.KeyMismatch,"RECORD_KEY_MISMATCH","主键数量与模块主键不匹配。");
        var masterFields=form.MasterFields.Where(field=>!field.DisplayOnly).Select(field=>field.Key).Concat(pkColumns).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var current=await ReadRowAsync(connection,transaction,definition.MasterTable,pkColumns,keyValues,masterFields,token);
        if(current is null)return RecordSaveResult.Failed(RecordAccessStatus.NotFound,"RECORD_NOT_FOUND","记录不存在。");
        if(!string.IsNullOrWhiteSpace(dataFilter))
        {
            if(!DataFilterParser.TryParse(dataFilter,definition.MasterTable,ScopeFields(form.MasterFields,pkColumns),out var predicate,out var parameters))
                return RecordSaveResult.Failed(RecordAccessStatus.FilterUnsupported,"DATA_FILTER_UNSUPPORTED","当前数据过滤条件尚不支持，已拒绝执行。");
            if(!await RecordInScopeAsync(connection,transaction,definition.MasterTable,pkColumns,keyValues,predicate,parameters,token))
                return RecordSaveResult.Failed(RecordAccessStatus.OutOfScope,"RECORD_OUT_OF_SCOPE","目标记录不在当前用户数据范围内。");
        }

        if(request.Original is not null)
        {
            var writable=form.MasterFields.Where(field=>!field.IsReadonly&&!field.IsVirtual&&!field.ServerFilled).ToList();
            string? conflictingField=null;
            foreach(var (key,raw) in request.Original)
            {
                var field=writable.FirstOrDefault(item=>item.Key.Equals(key,StringComparison.OrdinalIgnoreCase));
                if(field is null||!RecordPayloadValidator.TryConvert(field.DataType,raw,out var expected))continue;
                if(!ValuesEqual(expected,current.GetValueOrDefault(field.Key)))
                {
                    logger.LogDebug("并发冲突诊断 field={Field} expected={Expected} current={Current}", field.Key, expected, current.GetValueOrDefault(field.Key));
                    conflictingField??=field.Key;
                }
            }
            if(conflictingField is not null)
                return RecordSaveResult.Failed(RecordAccessStatus.ConcurrentModified,"CONCURRENT_MODIFIED","字段内容已被他人修改，请刷新后重试！",[new FieldError(conflictingField,"字段内容已被他人修改，请刷新后重试！","CONCURRENT_MODIFIED")]);
        }

        var validation=RecordPayloadValidator.ValidateSubmitted(form.MasterFields,request.Values);
        if(validation.Errors.Count>0)return RecordSaveResult.Failed(RecordAccessStatus.ValidationFailed,"VALIDATION_FAILED","数据校验未通过。",validation.Errors);
        var merged=new Dictionary<string,object?>(current,StringComparer.OrdinalIgnoreCase);
        foreach(var (key,value) in validation.Converted)merged[key]=value;
        var finalErrors=RecordPayloadValidator.CheckRequiredAndRegex(form.MasterFields,merged);
        if(finalErrors.Count>0)return RecordSaveResult.Failed(RecordAccessStatus.ValidationFailed,"VALIDATION_FAILED","数据校验未通过。",finalErrors);

        var sets=new List<(string Column,object? Value)>();
        foreach(var (key,value) in validation.Converted)
        {
            if(pkColumns.Contains(key,StringComparer.OrdinalIgnoreCase))continue;
            sets.Add((key,value));
        }
        if(await ColumnExistsAsync(connection,transaction,definition.MasterTable,"LAST_UPDATE_BY",token))sets.Add(("LAST_UPDATE_BY",employeeName));
        if(await ColumnExistsAsync(connection,transaction,definition.MasterTable,"LAST_UPDATE_DATE",token))sets.Add(("LAST_UPDATE_DATE",DateTime.Now));
        if(sets.Count>0)
        {
            var setSql=string.Join(',',sets.Select((item,index)=>$"[{item.Column}]=@s{index}"));
            var where=string.Join(" AND ",pkColumns.Select((column,index)=>$"[{column}]=@k{index}"));
            await using var command=new SqlCommand($"UPDATE dbo.[{definition.MasterTable}] SET {setSql} WHERE {where};",connection,transaction);
            for(var i=0;i<sets.Count;i++)command.Parameters.AddWithValue($"@s{i}",NormalizeDbValue(sets[i].Value));
            AddKeyParameters(command,pkColumns,keyValues);
            var affected=await command.ExecuteNonQueryAsync(token);
            if(affected==0)return RecordSaveResult.Failed(RecordAccessStatus.NotFound,"RECORD_NOT_FOUND","记录不存在。");
        }

        var detailErrors=await SaveDetailsAsync(connection,transaction,definition,form,pkColumns,keyValues,merged,request.Details??[],employeeName,false,token);
        if(detailErrors is not null)return RecordSaveResult.Failed(RecordAccessStatus.ValidationFailed,"VALIDATION_FAILED","明细数据校验未通过。",detailErrors);
        var businessRule=definition.BusinessRule;
        await SavePrepayOffsetsAsync(connection,transaction,businessRule,pkColumns,keyValues,request.PrepayOffsets,token);
        if(businessRule?.DomainRule is { } domainRule)
        {
            var domainResult=await domainRules.RunAfterSaveAsync(domainRule,connection,transaction,definition,pkColumns,keyValues,token);
            if(!domainResult.Success)
                return RecordSaveResult.Failed(RecordAccessStatus.ValidationFailed,"BUSINESS_VALIDATION_FAILED",
                    domainResult.Message??"保存后业务校验未通过。");
        }
        else if(businessRule?.AfterSaveSproc is { } afterSaveSproc)
        {
            var sprocResult=await controlledSprocs.RunAfterSaveAsync(definition.ModuleId,afterSaveSproc,pkColumns,keyValues,connection,transaction,token);
            if(!sprocResult.Success)
                return RecordSaveResult.Failed(RecordAccessStatus.ValidationFailed,"BUSINESS_VALIDATION_FAILED",
                    sprocResult.Message??"保存后业务校验未通过。");
        }
        await RecalculateMasterAmountsAsync(connection,transaction,definition,token);
        var changes = new List<string>();
        foreach (var (key,value) in validation.Converted)
        {
            var field=form.MasterFields.FirstOrDefault(item=>item.Key.Equals(key,StringComparison.OrdinalIgnoreCase));
            var oldValue=current.GetValueOrDefault(key);
            if(field is not null&&!ValuesEqual(oldValue,value))
                changes.Add($"{field.Label}：{ValueToString(oldValue)}-->{ValueToString(value)}<BR>");
        }
        await WriteAuditAsync(connection,transaction,definition.ModuleId,string.Join(',',keyValues),"UPDATE",
            changes.Count>0?string.Join("",changes):"修改记录",userId,token);
        await transaction.CommitAsync(token);
        logger.LogInformation("统一表单修改 module={ModuleId} master={Master} key={Key}",definition.ModuleId,definition.MasterTable,string.Join(',',keyValues));
        return RecordSaveResult.Success(keyValues);
    }

    public async Task<RecordSaveResult> DeleteRecordAsync(
        WorkbenchDefinition definition,
        FormDefinition form,
        IReadOnlyList<string> keyValues,
        string userId,
        string? dataFilter,
        CancellationToken token)
    {
        await using var connection=CreateConnection(); await connection.OpenAsync(token);
        await using var transaction=(SqlTransaction)await connection.BeginTransactionAsync(token);
        var pkColumns=await GetPrimaryKeyColumnsAsync(connection,transaction,definition.MasterTable,token);
        if(pkColumns.Count!=keyValues.Count)return RecordSaveResult.Failed(RecordAccessStatus.KeyMismatch,"RECORD_KEY_MISMATCH","主键数量与模块主键不匹配。");
        if(!await RowExistsAsync(connection,transaction,definition.MasterTable,pkColumns,keyValues,token))
            return RecordSaveResult.Failed(RecordAccessStatus.NotFound,"RECORD_NOT_FOUND","记录不存在。");
        if(!string.IsNullOrWhiteSpace(dataFilter))
        {
            if(!DataFilterParser.TryParse(dataFilter,definition.MasterTable,ScopeFields(form.MasterFields,pkColumns),out var predicate,out var parameters))
                return RecordSaveResult.Failed(RecordAccessStatus.FilterUnsupported,"DATA_FILTER_UNSUPPORTED","当前数据过滤条件尚不支持，已拒绝执行。");
            if(!await RecordInScopeAsync(connection,transaction,definition.MasterTable,pkColumns,keyValues,predicate,parameters,token))
                return RecordSaveResult.Failed(RecordAccessStatus.OutOfScope,"RECORD_OUT_OF_SCOPE","目标记录不在当前用户数据范围内。");
        }
        if(definition.DetailTable is not null)
            await DeleteDetailRowsAsync(connection,transaction,definition.DetailTable,pkColumns,keyValues,token);
        var where=string.Join(" AND ",pkColumns.Select((column,index)=>$"[{column}]=@k{index}"));
        await using var command=new SqlCommand($"DELETE FROM dbo.[{definition.MasterTable}] WHERE {where};",connection,transaction);
        AddKeyParameters(command,pkColumns,keyValues);
        var affected=await command.ExecuteNonQueryAsync(token);
        if(affected==0)return RecordSaveResult.Failed(RecordAccessStatus.NotFound,"RECORD_NOT_FOUND","记录不存在。");
        await WriteAuditAsync(connection,transaction,definition.ModuleId,string.Join(',',keyValues),"DELETE","删除记录",userId,token);
        await transaction.CommitAsync(token);
        logger.LogInformation("统一表单删除 module={ModuleId} master={Master} key={Key}",definition.ModuleId,definition.MasterTable,string.Join(',',keyValues));
        return RecordSaveResult.Success(keyValues);
    }

    /// <summary>
    /// 单据批核/解批（旧 P_WF_&lt;DOC&gt; 的受控调用）。
    /// 仅对 ModuleBusinessMap 登记的模块开放；成功返回主键，失败返回业务消息。
    /// </summary>
    public async Task<RecordSaveResult> WorkflowAsync(
        WorkbenchDefinition definition,
        IReadOnlyList<string> keyValues,
        bool approve,
        string employeeName,
        string userId,
        CancellationToken token)
    {
        var rule=definition.BusinessRule;
        if(rule?.WorkflowSproc is not { } sproc)
            return RecordSaveResult.Failed(RecordAccessStatus.NotFound,"WORKFLOW_NOT_SUPPORTED","该模块不支持批核操作。");
        await using var connection=CreateConnection();
        await connection.OpenAsync(token);
        var keyCondition=ControlledSprocInvoker.BuildKeyCondition(definition.MasterPkOrder,keyValues);
        // 自动批核模块（MODULES.AUTO_APPROVE=1）：新增即已确认（SYSTEM），显式批核幂等返回成功，
        // 且不进入流程送审（用户语义：自动批核模块不走新增、审核模式）。
        if(approve && definition.AutoApprove)
        {
            var state=await ReadConfirmStateAsync(connection,definition.MasterTable,keyCondition,token);
            if(state is null)
                return RecordSaveResult.Failed(RecordAccessStatus.NotFound,"RECORD_NOT_FOUND","记录不存在。");
            if(state.Value.Tag==true)
                return RecordSaveResult.Success(keyValues);
            // 未确认（理论不发生）时继续走直接确认路径。
        }
        // 有流程定义的模块：批核即"送审"（启动审批链），单据保持未确认；
        // 无流程模块保持直接批核（对齐旧系统 P_WF_APPROVE_NOFLOW 语义）。
        else if(approve && await WorkflowEngine.HasFlowAsync(connection,definition.ModuleId,token))
            return await workflowEngine.StartFlowAsync(definition,keyValues,employeeName,token);
        // 对齐旧系统 P_WF_APPROVE_NOFLOW：先更新主表确认状态（带守卫），
        // 再执行 P_WF_<DOC> 业务存储过程。业务 SP 自动提交运行——
        // 部分 SP（如 P_WF_SAMPLE_PRO→P_WF_RUN 流程链）内部自带 BEGIN TRAN/COMMIT，
        // 外层再包事务会导致"事务计数不匹配"（EXECUTE 后 BEGIN/COMMIT 数目失衡）。
        // 守卫语义：批核仅允许 CONFIRM_TAG=0（未批核），解批仅允许 CONFIRM_TAG=1；
        // 业务 SP 失败时回滚确认状态（SP 自身副作用无法回滚，与旧系统嵌套事务一致）。
        var originalState=await ReadConfirmStateAsync(connection,definition.MasterTable,keyCondition,token);
        if(originalState is null)
            return RecordSaveResult.Failed(RecordAccessStatus.NotFound,"RECORD_NOT_FOUND","记录不存在。");
        var confirmSql=approve
            ? $"UPDATE dbo.[{definition.MasterTable}] SET CONFIRM_PERSON=@ConfirmPerson,CONFIRM_DATE=GETDATE(),CONFIRM_TAG=1 WHERE ISNULL(CONFIRM_TAG,0)=0 AND {keyCondition};"
            : $"UPDATE dbo.[{definition.MasterTable}] SET CONFIRM_PERSON=@ConfirmPerson,CONFIRM_DATE=GETDATE(),CONFIRM_TAG=0 WHERE CONFIRM_TAG=1 AND {keyCondition};";
        await using (var confirmCommand=new SqlCommand(confirmSql,connection))
        {
            confirmCommand.Parameters.Add("@ConfirmPerson",SqlDbType.NVarChar,50).Value=employeeName.Trim();
            var affected=await confirmCommand.ExecuteNonQueryAsync(token);
            if(affected==0)
            {
                return RecordSaveResult.Failed(RecordAccessStatus.ValidationFailed,"WORKFLOW_STATE_CONFLICT",
                    approve?"记录不存在或已批核，无法重复批核。":"记录不存在或未批核，无法解批。");
            }
        }
        var result=await controlledSprocs.RunWorkflowAsync(definition.ModuleId,sproc,definition.MasterPkOrder,keyValues,approve,token);
        if(!result.Success)
        {
            await RestoreConfirmStateAsync(connection,definition.MasterTable,keyCondition,originalState.Value,token);
            return RecordSaveResult.Failed(RecordAccessStatus.ValidationFailed,"WORKFLOW_FAILED",
                result.Message??(approve?"批核失败。":"解批失败。"));
        }
        logger.LogInformation("统一表单{Action} module={ModuleId} key={Key}",approve?"批核":"解批",definition.ModuleId,string.Join(',',keyValues));
        await WriteAuditAsync(connection,null,definition.ModuleId,string.Join(',',keyValues),
            approve?"APPROVE":"DEAPPROVE",approve?"批核":"解批",userId,token);
        return RecordSaveResult.Success(keyValues);
    }

    private static async Task<(bool? Tag,string? Person,DateTime? Date)?> ReadConfirmStateAsync(
        SqlConnection connection,string table,string keyCondition,CancellationToken token)
    {
        var sql=$"SELECT CONFIRM_TAG,CONFIRM_PERSON,CONFIRM_DATE FROM dbo.[{table}] WITH (NOLOCK) WHERE {keyCondition};";
        await using var command=new SqlCommand(sql,connection);
        await using var reader=await command.ExecuteReaderAsync(token);
        if(!await reader.ReadAsync(token))return null;
        var tag=reader.IsDBNull(0)?(bool?)null:reader.GetBoolean(0);
        var person=reader.IsDBNull(1)?null:reader.GetString(1);
        var date=reader.IsDBNull(2)?(DateTime?)null:reader.GetDateTime(2);
        return (tag,person,date);
    }

    private static async Task RestoreConfirmStateAsync(
        SqlConnection connection,string table,string keyCondition,(bool? Tag,string? Person,DateTime? Date) state,CancellationToken token)
    {
        var sql=$"UPDATE dbo.[{table}] SET CONFIRM_TAG=@Tag,CONFIRM_PERSON=@Person,CONFIRM_DATE=@Date WHERE {keyCondition};";
        await using var command=new SqlCommand(sql,connection);
        command.Parameters.Add("@Tag",SqlDbType.Bit).Value=(object?)state.Tag??DBNull.Value;
        command.Parameters.Add("@Person",SqlDbType.NVarChar,50).Value=(object?)state.Person??DBNull.Value;
        command.Parameters.Add("@Date",SqlDbType.DateTime).Value=(object?)state.Date??DBNull.Value;
        await command.ExecuteNonQueryAsync(token);
    }

    /// <summary>
    /// 选择器数据源（M4）：表名来自服务端表单定义（客户端仅传字段 key），
    /// 显示列按权限过滤（成本/保密/禁止查看），DATA_FILTER 受限解析可应用时应用，
    /// 无法安全解析时返回空列表（不泄漏数据）。
    /// </summary>
    public async Task<FormChooserResult?> GetChooserOptionsAsync(
        string table,
        string? keyword,
        string? filterField,
        string? returnMapping,
        IReadOnlyDictionary<string, string>? masterValues,
        IReadOnlyDictionary<string, string>? detailValues,
        IReadOnlyList<UnifiedChooserCondition>? conditions,
        bool canViewCost,
        bool canViewSecrecy,
        IReadOnlySet<string> deniedFields,
        string? dataFilter,
        string? chooseFilter,
        string? sortField,
        string? sortDirection,
        int page,
        int pageSize,
        CancellationToken token)
    {
        if(!Identifier.IsMatch(table))return null;
        await using var connection=CreateConnection(); await connection.OpenAsync(token);
        var all=await ReadChooserColumnRows(connection,table,token);
        if(all.Count==0)return null;
        var allowedFields=all.Select(row=>row.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var columnTypes=all.ToDictionary(row=>row.Key,row=>row.DataType,StringComparer.OrdinalIgnoreCase);
        // {m.FIELD}/{d.FIELD} 旧系统模板：替换为当前主表/明细字段值（SQL 字面量转义后由解析器参数化，不拼接原始值）
        var substitutedFilter = SubstituteTemplateValues(chooseFilter, masterValues, detailValues);
        logger.LogInformation("选择器过滤 table={Table} filter={Filter} substituted={Substituted}", table, chooseFilter, substitutedFilter);
        // 合并两类过滤：用户数据范围（DATA_FILTER）+ 字段选择器过滤（CHOOSE_FILTER）。
        // 全部经受限解析器（白名单字段 + 参数化）；任一无法安全解析即返回空选项（不泄漏数据）。
        string? scopePredicate=null;
        var scopeParameters=new List<object>();
        var joins=new List<string>();
        var nextIndex=0;
        foreach(var (raw,required) in new[]{ (dataFilter,false), (substitutedFilter,true) })
        {
            if(string.IsNullOrWhiteSpace(raw))continue;
            if(!DataFilterParser.TryParseWithJoins(raw,table,allowedFields,ChooserJoinTables,columnTypes,out var parsed,out var parsedParameters,out var parsedJoins,out var foreignColumns))
            {
                logger.LogWarning("选择器过滤无法解析 table={Table} filter={Filter}",table,raw);
                return new FormChooserResult([],[],0);
            }
            if(!await ValidateChooserJoinsAsync(connection,table,parsedJoins,foreignColumns,token))
            {
                logger.LogWarning("选择器 JOIN 校验失败 table={Table} filter={Filter}",table,raw);
                return new FormChooserResult([],[],0);
            }
            var renumbered=RenumberFilterParameters(parsed,parsedParameters,nextIndex);
            scopePredicate=scopePredicate is null
                ? renumbered.Predicate
                : $"({scopePredicate}) AND ({renumbered.Predicate})";
            scopeParameters.AddRange(renumbered.Parameters);
            nextIndex+=renumbered.Parameters.Count;
            foreach(var joinTable in parsedJoins.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var joinKey=ChooserJoinTables[joinTable];
                joins.Add($"LEFT JOIN dbo.[{joinTable}] ON dbo.[{joinTable}].[{joinKey}] = dbo.[{table}].[{joinKey}]");
            }
        }
        // 显示列：回填映射列优先（保证主键/名称可见），其余按 SYSQL_DEFAULT 顺序，
        // 保留审计/状态列（对齐旧系统 Chooser.aspx 网格列，如建立人/批核状态等）
        var preferred = FormFieldSelector.ParseReturnMapping(returnMapping)
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
        var (rows,total)=await ReadChooserRowsAsync(connection,table,columns,keyword,filterField,scopePredicate,scopeParameters,joins,conditions,allowedFields,sortField,sortDirection,page,pageSize,token);
        return new FormChooserResult(columns,rows,total);
    }

    /// <summary>
    /// 把旧系统 {m.FIELD} 主表值 / {d.FIELD} 明细值模板替换为 SQL 字面量（单引号转义，随后由解析器参数化绑定）。
    /// 自带引号与不带引号两种都覆盖；值缺失时替换为空串（条件不命中，安全）。
    /// </summary>
    private static string? SubstituteTemplateValues(
        string? filter,
        IReadOnlyDictionary<string,string>? masterValues,
        IReadOnlyDictionary<string,string>? detailValues)
    {
        if(string.IsNullOrWhiteSpace(filter))return filter;
        string Escape(IReadOnlyDictionary<string,string>? source, string field)
        {
            if(source is not null && source.TryGetValue(field,out var value)) return value.Replace("'","''");
            return "";
        }
        if(masterValues is not null)
        {
            filter=MasterValuePlaceholderQuoted.Replace(filter,match=>"'"+Escape(masterValues,match.Groups[1].Value)+"'");
            filter=MasterValuePlaceholder.Replace(filter,match=>"'"+Escape(masterValues,match.Groups[1].Value)+"'");
        }
        if(detailValues is not null)
        {
            filter=DetailValuePlaceholderQuoted.Replace(filter,match=>"'"+Escape(detailValues,match.Groups[1].Value)+"'");
            filter=DetailValuePlaceholder.Replace(filter,match=>"'"+Escape(detailValues,match.Groups[1].Value)+"'");
        }
        return filter;
    }

    /// <summary>校验选择器跨表 JOIN：关联键（查询表侧 + 外键表侧）与引用列均须物理存在，否则拒绝。</summary>
    private static async Task<bool> ValidateChooserJoinsAsync(
        SqlConnection connection,
        string table,
        IReadOnlyList<string> joinTables,
        IReadOnlyList<(string Table,string Column)> foreignColumns,
        CancellationToken token)
    {
        var checks=new List<(string Table,string Column)>();
        foreach(var joinTable in joinTables.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if(!ChooserJoinTables.TryGetValue(joinTable,out var joinKey))return false;
            checks.Add((table,joinKey));
            checks.Add((joinTable,joinKey));
        }
        checks.AddRange(foreignColumns);
        foreach(var group in checks.GroupBy(item=>item.Table,StringComparer.OrdinalIgnoreCase))
        {
            var columns=group.Select(item=>item.Column).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if(!await ColumnsExistAsync(connection,group.Key,columns,token))return false;
        }
        return true;
    }

    /// <summary>把解析器生成的 @dfN 参数名重编号，避免多段过滤合并时参数名冲突。</summary>
    private static (string Predicate,IReadOnlyList<object> Parameters) RenumberFilterParameters(
        string predicate,IReadOnlyList<object> parameters,int startIndex)
    {
        var names=Regex.Matches(predicate,"@df\\d+").Select(match=>match.Value)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(name=>int.Parse(name[3..]))
            .ToList();
        if(names.Count==0)return (predicate,parameters);
        var map=names.Select((name,index)=>(name,$"@df{startIndex+index}"))
            .ToDictionary(item=>item.name,item=>item.Item2);
        return (Regex.Replace(predicate,"@df\\d+",match=>map[match.Value]),parameters);
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
              AND EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS c WHERE c.TABLE_SCHEMA='dbo' AND c.TABLE_NAME=@Table AND c.COLUMN_NAME=f.F_ID)
            ORDER BY CASE WHEN d.F_IDX IS NULL THEN 1 ELSE 0 END,COALESCE(d.F_IDX,COALESCE(f.VERIFY_INDEX,999)),f.F_ID;
            """;
        await using var command=new SqlCommand(sql,connection);command.Parameters.Add("@Table",SqlDbType.NVarChar,100).Value=table;
        await using var reader=await command.ExecuteReaderAsync(token);
        var rows=new List<FormChooserColumnRow>();
        while(await reader.ReadAsync(token))rows.Add(new(reader.GetString(0),reader.GetString(1),reader.GetString(2),reader.GetBoolean(3),reader.GetBoolean(4),reader.GetInt32(reader.GetOrdinal("ORDER_IDX")),reader.GetBoolean(reader.GetOrdinal("IS_VISIBLE")),reader.IsDBNull(reader.GetOrdinal("DISPLAY_FORMAT"))?null:reader.GetString(reader.GetOrdinal("DISPLAY_FORMAT"))));
        return rows;
    }

    private static async Task<(IReadOnlyList<IReadOnlyDictionary<string,object?>> Rows,int Total)> ReadChooserRowsAsync(
        SqlConnection connection,string table,IReadOnlyList<FormChooserColumn> columns,string? keyword,string? filterField,string? scopePredicate,IReadOnlyList<object> scopeParameters,IReadOnlyList<string> joins,IReadOnlyList<UnifiedChooserCondition>? conditions,IReadOnlySet<string> allowedFields,string? sortField,string? sortDirection,int page,int pageSize,CancellationToken token)
    {
        var select=string.Join(',',columns.Select(column=>$"[{table}].[{column.Key}]"));
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
        // 排序字段必须在显示列白名单内（服务端校验），否则回退首列；方向仅 asc/desc
        var sortColumn = !string.IsNullOrWhiteSpace(sortField)
            ? columns.FirstOrDefault(column=>column.Key.Equals(sortField,StringComparison.OrdinalIgnoreCase))?.Key
            : null;
        sortColumn ??= columns[0].Key;
        var dir = string.Equals(sortDirection,"desc",StringComparison.OrdinalIgnoreCase) ? "DESC" : "ASC";
        page=Math.Max(1,page);
        pageSize=Math.Clamp(pageSize,10,100);
        var sql=$"SELECT COUNT_BIG(1) FROM dbo.[{table}] WITH (NOLOCK){where}; SELECT {select} {from}{where} ORDER BY [{table}].[{sortColumn}] {dir} OFFSET @Offset ROWS FETCH NEXT @PageSize ROWS ONLY;";
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

    private async Task<IReadOnlyList<FieldError>?> SaveDetailsAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        WorkbenchDefinition definition,
        FormDefinition form,
        IReadOnlyList<string> pkColumns,
        IReadOnlyList<string> keyValues,
        IReadOnlyDictionary<string,object?> masterValues,
        IReadOnlyList<IReadOnlyDictionary<string,string?>> details,
        string employeeName,
        bool isNew,
        CancellationToken token)
    {
        if(definition.DetailTable is null||form.DetailFields.Count==0)return null;
        if(details.Count==0)
        {
            if(definition.DetailNoSave)return [new FieldError("","该模块无明细资料不可保存。","DETAIL_REQUIRED")];
            await DeleteDetailRowsAsync(connection,transaction,definition.DetailTable,pkColumns,keyValues,token);
            return null;
        }

        if(!string.IsNullOrWhiteSpace(definition.DetailNoFields))
        {
            var missing=definition.DetailNoFields.Split(';',StringSplitOptions.TrimEntries|StringSplitOptions.RemoveEmptyEntries)
                .Where(field=>!masterValues.TryGetValue(field,out var value)||value is null||value is string text&&string.IsNullOrWhiteSpace(text))
                .ToList();
            if(missing.Count>0)
                return missing.Select(field=>new FieldError(field,"新增明细前必须填写该主表字段。","DETAIL_NO_FIELDS_MISSING")).ToList();
        }

        var detailIdentity=await GetIdentityColumnsAsync(connection,transaction,definition.DetailTable,token);
        var dfVerify=await GetDfVerifyAsync(connection,transaction,definition.DetailTable,token);
        var dfFields=dfVerify?.Split(';',StringSplitOptions.TrimEntries|StringSplitOptions.RemoveEmptyEntries)??[];
        var errors=new List<FieldError>();
        var rows=new List<Dictionary<string,object?>>();
        for(var rowIndex=0;rowIndex<details.Count;rowIndex++)
        {
            var validation=RecordPayloadValidator.ValidateSubmitted(form.DetailFields,details[rowIndex]);
            errors.AddRange(validation.Errors);
            var row=new Dictionary<string,object?>(validation.Converted,StringComparer.OrdinalIgnoreCase);
            RecordPayloadValidator.ApplyDefaults(form.DetailFields,row);
            // 明细中未提供的字段，若主表存在同名值（如 CURR_ID/CURR_RATE/TAX_ID），
            // 由服务端从主表带入——对齐旧系统明细隐藏控件随主表联动带值的行为。
            // 客户端显式提供的可编辑明细值（如 PRO_NO/PRICE）保持优先。
            foreach(var detailField in form.DetailFields)
            {
                if(row.ContainsKey(detailField.Key))continue;
                if(masterValues.TryGetValue(detailField.Key,out var masterValue)&&masterValue is not null)
                    row[detailField.Key]=masterValue;
            }
            for(var i=0;i<pkColumns.Count;i++)
            {
                var pkField=form.DetailFields.FirstOrDefault(field=>field.Key.Equals(pkColumns[i],StringComparison.OrdinalIgnoreCase));
                if(pkField is null)
                {
                    errors.Add(new FieldError(pkColumns[i],"明细表缺少主表关联字段。","MASTER_KEY_NOT_IN_DETAIL"));
                    continue;
                }
                if(RecordPayloadValidator.TryConvert(pkField.DataType,keyValues[i],out var keyValue))row[pkColumns[i]]=keyValue;
            }
            // 服务端复算明细金额（对齐旧系统前端 calc_row_amount，金额以服务端为权威）
            RecalculateDetailAmounts(form.DetailFields,row,masterValues);
            rows.Add(row);
        }
        RecordPayloadValidator.AssignSerialNumbers(rows, form.DetailFields);
        // 应收货款单（170101）等引用单据的明细：金额/数量从送货（退货）单明细带出
        await FillReferencedAmountsAsync(connection,transaction,form.DetailFields,rows,token);
        foreach(var row in rows)errors.AddRange(RecordPayloadValidator.CheckRequiredAndRegex(form.DetailFields,row));
        if(errors.Count>0)return errors;

        if(dfFields.Length>0)
        {
            var groups=rows.GroupBy(row=>string.Join('\u0001',dfFields.Select(field=>Convert.ToString(row.GetValueOrDefault(field)??string.Empty,System.Globalization.CultureInfo.InvariantCulture))));
            var duplicate=groups.FirstOrDefault(group=>group.Count()>1);
            if(duplicate is not null)
                return [new FieldError(string.Join(';',dfFields),$"明细表资料重复：{string.Join(';',dfFields)}。","DF_VERIFY_DUPLICATE")];
        }

        await DeleteDetailRowsAsync(connection,transaction,definition.DetailTable,pkColumns,keyValues,token);
        foreach(var row in rows)
        {
            var fillErrors=await FillServerColumnsAsync(connection,transaction,definition.DetailTable,form.DetailFields,row,employeeName,isNew,token);
            if(fillErrors.Count>0)return fillErrors;
            var insertFields=form.DetailFields.Where(field=>!field.IsVirtual&&row.ContainsKey(field.Key)&&!detailIdentity.Contains(field.Key)).ToList();
            if(insertFields.Count==0)continue;
            await InsertRowAsync(connection,transaction,definition.DetailTable,insertFields,row,detailIdentity,token);
        }
        return null;
    }

    /// <summary>
    /// 引用单据金额带出：明细含 S_R_TYPE/S_R_NO/S_R_SERIAL_NO（送/退货引用）时，
    /// 从 COP_SEND_D / COP_RETURN_D 读取 QTY/AMOUNT/AMOUNT_TAX/TAX_SUM 填充，
    /// 对齐旧系统对账引用带出行为；未找到引用行时保留客户端值。
    /// 表名/列名为服务端常量，引用键值来自客户端但仅作参数化等值匹配。
    /// </summary>
    private static async Task FillReferencedAmountsAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        IReadOnlyList<FormFieldDefinition> fields,
        IReadOnlyList<Dictionary<string,object?>> rows,
        CancellationToken token)
    {
        var hasRef = fields.Any(field=>field.Key.Equals("S_R_TYPE",StringComparison.OrdinalIgnoreCase))
            &&fields.Any(field=>field.Key.Equals("S_R_NO",StringComparison.OrdinalIgnoreCase))
            &&fields.Any(field=>field.Key.Equals("S_R_SERIAL_NO",StringComparison.OrdinalIgnoreCase));
        if(!hasRef)return;
        var fieldSet=fields.Select(field=>field.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach(var row in rows)
        {
            if(!row.TryGetValue("S_R_TYPE",out var rawType)||!row.TryGetValue("S_R_NO",out var rawNo)
               ||!row.TryGetValue("S_R_SERIAL_NO",out var rawSerial))continue;
            var type=Convert.ToString(rawType)??""; var no=Convert.ToString(rawNo)??"";
            var serial=Convert.ToString(rawSerial)??"";
            if(type.Length==0||no.Length==0||serial.Length==0)continue;
            const string sql="""
                SELECT TOP 1 QTY,AMOUNT,AMOUNT_TAX,TAX_SUM
                FROM dbo.COP_SEND_D WITH (NOLOCK)
                WHERE SEND_TYPE=@t AND SEND_NO=@n AND SERIAL_NO=@s;
                """;
            await using var command=new SqlCommand(sql,connection,transaction);
            command.Parameters.Add("@t",SqlDbType.NVarChar,50).Value=type;
            command.Parameters.Add("@n",SqlDbType.NVarChar,50).Value=no;
            command.Parameters.Add("@s",SqlDbType.Int).Value=int.TryParse(serial,out var parsed)?parsed:0;
            await using var reader=await command.ExecuteReaderAsync(token);
            if(!await reader.ReadAsync(token))continue;
            void Fill(string column)
            {
                if(!fieldSet.Contains(column))return;
                row[column]=reader.IsDBNull(reader.GetOrdinal(column))?null:reader.GetValue(reader.GetOrdinal(column));
            }
            Fill("QTY");Fill("AMOUNT");Fill("AMOUNT_TAX");Fill("TAX_SUM");
        }
    }

    /// <summary>
    /// 服务端复算明细行金额（AMOUNT/TAX_SUM/AMOUNT_TAX），覆盖客户端提交值，
    /// 保证后续 AfterSave/主表汇总使用一致金额。仅当明细表单存在金额列时才计算。
    /// </summary>
    private static void RecalculateDetailAmounts(
        IReadOnlyList<FormFieldDefinition> fields,
        IDictionary<string,object?> row,
        IReadOnlyDictionary<string,object?> masterValues)
    {
        var hasAmount=fields.Any(field=>field.Key.Equals("AMOUNT",StringComparison.OrdinalIgnoreCase));
        var hasAmountTax=fields.Any(field=>field.Key.Equals("AMOUNT_TAX",StringComparison.OrdinalIgnoreCase));
        var hasTaxSum=fields.Any(field=>field.Key.Equals("TAX_SUM",StringComparison.OrdinalIgnoreCase));
        if(!hasAmount&&!hasAmountTax&&!hasTaxSum)return;
        var qty=GetDecimal(row,"QTY");
        var price=GetDecimal(row,"PRICE");
        // 无数量/单价的明细行（如预收/预付单按订单冲抵金额）不参与金额重算，
        // 保留客户端提交的 AMOUNT（旧系统同样由用户录入冲抵金额）。
        if(qty is null&&price is null)return;
        var result=AmountCalculator.Calculate(
            qty,
            price,
            GetDecimal(row,"TAX_RATE")??GetDecimal(masterValues,"TAX_RATE")??0m,
            row.TryGetValue("TAX_TYPE",out var taxType)
                ?Convert.ToString(taxType)
                :masterValues.TryGetValue("TAX_TYPE",out var masterTaxType)?Convert.ToString(masterTaxType):null,
            GetDecimal(row,"REBATE")??100m);
        if(hasAmount)row["AMOUNT"]=result.Amount;
        if(hasTaxSum)row["TAX_SUM"]=result.TaxSum;
        if(hasAmountTax)row["AMOUNT_TAX"]=result.AmountTax;
    }

    private static decimal? GetDecimal(IReadOnlyDictionary<string,object?> row,string key)
    {
        return GetDecimal((IDictionary<string,object?>)row,key);
    }

    private static decimal? GetDecimal(IDictionary<string,object?> row,string key)
    {
        if(!row.TryGetValue(key,out var value)||value is null)return null;
        return value switch
        {
            decimal d=>d,
            double dbl=>Convert.ToDecimal(dbl),
            float f=>Convert.ToDecimal(f),
            int i=>i,
            long l=>l,
            _=>decimal.TryParse(Convert.ToString(value,System.Globalization.CultureInfo.InvariantCulture),
                System.Globalization.NumberStyles.Any,System.Globalization.CultureInfo.InvariantCulture,out var parsed)?parsed:null,
        };
    }

    /// <summary>
    /// 主表金额汇总与余额字段初始化：
    /// - 明细表存在 AMOUNT/AMOUNT_TAX/TAX_SUM 时，主表同名列按主键聚合更新；
    /// - 预收/预付主表 PREPAY_AMOUNT 为 NULL 时初始化为 AMOUNT（对齐旧系统余额语义）；
    /// - 应收主表 RECEIVE_AMOUNT 为 NULL 时初始化为 0（批核时累加）。
    /// 列名/表名均来自服务端元数据与 INFORMATION_SCHEMA 存在性校验。
    /// </summary>
    private static async Task RecalculateMasterAmountsAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        WorkbenchDefinition definition,
        CancellationToken token)
    {
        if(definition.DetailTable is null||definition.MasterPkOrder.Count<2)return;
        var typeColumn=definition.MasterPkOrder[0];
        var noColumn=definition.MasterPkOrder[1];
        foreach(var column in new[]{"AMOUNT","AMOUNT_TAX","TAX_SUM"})
        {
            if(!await ColumnExistsAsync(connection,transaction,definition.DetailTable,column,token))continue;
            if(!await ColumnExistsAsync(connection,transaction,definition.MasterTable,column,token))continue;
            await using var command=new SqlCommand(
                $"""
                UPDATE m SET [{column}]=d.[{column}]
                FROM dbo.[{definition.MasterTable}] m
                INNER JOIN (SELECT [{typeColumn}],[{noColumn}],SUM([{column}]) AS [{column}]
                            FROM dbo.[{definition.DetailTable}]
                            GROUP BY [{typeColumn}],[{noColumn}]) d
                  ON m.[{typeColumn}]=d.[{typeColumn}] AND m.[{noColumn}]=d.[{noColumn}];
                """,connection,transaction);
            await command.ExecuteNonQueryAsync(token);
        }
        // 余额字段初始化（仅 NULL 时填充，编辑已有余额不覆盖）
        if(await ColumnExistsAsync(connection,transaction,definition.MasterTable,"PREPAY_AMOUNT",token)
           &&await ColumnExistsAsync(connection,transaction,definition.MasterTable,"AMOUNT",token))
        {
            await using var prepay=new SqlCommand(
                $"UPDATE dbo.[{definition.MasterTable}] SET PREPAY_AMOUNT=ISNULL(PREPAY_AMOUNT,ISNULL(AMOUNT,0));",
                connection,transaction);
            await prepay.ExecuteNonQueryAsync(token);
        }
        if(await ColumnExistsAsync(connection,transaction,definition.MasterTable,"RECEIVE_AMOUNT",token))
        {
            await using var receive=new SqlCommand(
                $"UPDATE dbo.[{definition.MasterTable}] SET RECEIVE_AMOUNT=ISNULL(RECEIVE_AMOUNT,0);",
                connection,transaction);
            await receive.ExecuteNonQueryAsync(token);
        }
    }

    private static async Task<int?> InsertRowAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string table,
        IReadOnlyList<FormFieldDefinition> insertFields,
        IReadOnlyDictionary<string,object?> values,
        IReadOnlyList<string> identityColumns,
        CancellationToken token)
    {
        var columns=string.Join(',',insertFields.Select(field=>$"[{field.Key}]"));
        var parameters=string.Join(',',insertFields.Select((_,index)=>$"@v{index}"));
        var sql=$"INSERT INTO dbo.[{table}] ({columns}) VALUES ({parameters});";
        await using var command=new SqlCommand(sql,connection,transaction);
        for(var i=0;i<insertFields.Count;i++)command.Parameters.AddWithValue($"@v{i}",NormalizeDbValue(values[insertFields[i].Key]));
        if(identityColumns.Count>0)
        {
            command.CommandText+=" SELECT SCOPE_IDENTITY();";
            return Convert.ToInt32(await command.ExecuteScalarAsync(token));
        }
        await command.ExecuteNonQueryAsync(token);
        return null;
    }

    private async Task<IReadOnlyList<FieldError>> FillServerColumnsAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string table,
        IReadOnlyList<FormFieldDefinition> fields,
        IDictionary<string,object?> values,
        string employeeName,
        bool isNew,
        CancellationToken token)
    {
        var now=DateTime.Now;
        var audit=isNew
            ? new (string Name,object Value)[]{("CREATE_PERSON",employeeName),("CREATE_DATE",now),("LAST_UPDATE_BY",employeeName),("LAST_UPDATE_DATE",now)}
            : new (string Name,object Value)[]{("LAST_UPDATE_BY",employeeName),("LAST_UPDATE_DATE",now)};
        foreach(var item in audit)
            if(await ColumnExistsAsync(connection,transaction,table,item.Name,token))values[item.Name]=item.Value;
        var errors=new List<FieldError>();
        foreach(var field in fields.Where(field=>field.ServerFilled&&!values.ContainsKey(field.Key)&&!RecordPayloadValidator.IsAuditColumn(field.Key)))
        {
            // 隐藏必填字段（serverFilled）优先取字段默认值（DFT_VALUE，服务端元数据）；
            // 无默认值时跳过并登记日志（不再硬失败阻塞保存，缺口以技术债跟踪逐表修正元数据）。
            if(!string.IsNullOrWhiteSpace(field.DefaultValue)&&RecordPayloadValidator.TryConvert(field.DataType,field.DefaultValue,out var defaulted))
            {
                values[field.Key]=defaulted;
                continue;
            }
            logger.LogWarning("服务端必填字段无填充规则且无默认值 table={Table} field={Field}",table,field.Key);
        }
        return errors;
    }

    /// <summary>
    /// 保存收款/付款单的预收/预付冲抵关联表（COP_RECEIPT_PREPAY / PUR_PAY_PREPAY）。
    /// 表名来自 ModuleBusinessMap 白名单，主键列来自服务端元数据，值全部参数化。
    /// 先删除该单据的旧冲抵行再写入新行（与明细重建语义一致）。
    /// </summary>
    private static async Task SavePrepayOffsetsAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        ModuleBusinessRule? rule,
        IReadOnlyList<string> pkColumns,
        IReadOnlyList<string> keyValues,
        IReadOnlyList<PrepayOffsetRequest>? offsets,
        CancellationToken token)
    {
        if(rule?.PrepayOffsetTable is not { } table||pkColumns.Count<2)return;
        var typeColumn=pkColumns[0];
        var noColumn=pkColumns[1];
        await using var delete=new SqlCommand(
            $"DELETE FROM dbo.[{table}] WHERE [{typeColumn}]=@t AND [{noColumn}]=@n",connection,transaction);
        delete.Parameters.Add("@t",SqlDbType.NVarChar,50).Value=keyValues[0];
        delete.Parameters.Add("@n",SqlDbType.NVarChar,50).Value=keyValues[1];
        await delete.ExecuteNonQueryAsync(token);
        if(offsets is null)return;
        var serial=1;
        foreach(var offset in offsets)
        {
            await using var insert=new SqlCommand(
                $"""
                INSERT INTO dbo.[{table}] ([{typeColumn}],[{noColumn}],SERIAL_NO,PREPAY_TYPE,PREPAY_NO,AMOUNT,PREPAY_AMOUNT)
                VALUES (@t,@n,@serial,@pt,@pn,@amount,@prepayAmount);
                """,connection,transaction);
            insert.Parameters.Add("@t",SqlDbType.NVarChar,50).Value=keyValues[0];
            insert.Parameters.Add("@n",SqlDbType.NVarChar,50).Value=keyValues[1];
            insert.Parameters.Add("@serial",SqlDbType.Int).Value=serial;
            insert.Parameters.Add("@pt",SqlDbType.NVarChar,50).Value=offset.Type;
            insert.Parameters.Add("@pn",SqlDbType.NVarChar,50).Value=offset.No;
            insert.Parameters.Add("@amount",SqlDbType.Decimal).Value=(object?)offset.Amount??DBNull.Value;
            insert.Parameters.Add("@prepayAmount",SqlDbType.Decimal).Value=offset.PrepayAmount;
            await insert.ExecuteNonQueryAsync(token);
            serial++;
        }
    }

    private static async Task<IReadOnlyList<string>> GetPrimaryKeyColumnsAsync(SqlConnection connection,SqlTransaction? transaction,string table,CancellationToken token)
    {
        const string sql="""
            SELECT ku.COLUMN_NAME FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS tc
            INNER JOIN INFORMATION_SCHEMA.KEY_COLUMN_USAGE ku
              ON ku.CONSTRAINT_NAME=tc.CONSTRAINT_NAME AND ku.CONSTRAINT_SCHEMA=tc.CONSTRAINT_SCHEMA
            WHERE tc.CONSTRAINT_TYPE='PRIMARY KEY' AND tc.TABLE_SCHEMA='dbo' AND tc.TABLE_NAME=@Table
            ORDER BY ku.ORDINAL_POSITION;
            """;
        await using var command=new SqlCommand(sql,connection,transaction);command.Parameters.Add("@Table",SqlDbType.NVarChar,100).Value=table;
        await using var reader=await command.ExecuteReaderAsync(token);
        var result=new List<string>();
        while(await reader.ReadAsync(token))result.Add(reader.GetString(0));
        return result;
    }

    private static async Task<IReadOnlyList<string>> GetIdentityColumnsAsync(SqlConnection connection,SqlTransaction transaction,string table,CancellationToken token)
    {
        const string sql="SELECT c.name FROM sys.tables t INNER JOIN sys.columns c ON c.object_id=t.object_id WHERE t.name=@Table AND t.schema_id=SCHEMA_ID('dbo') AND c.is_identity=1;";
        await using var command=new SqlCommand(sql,connection,transaction);command.Parameters.Add("@Table",SqlDbType.NVarChar,100).Value=table;
        await using var reader=await command.ExecuteReaderAsync(token);
        var result=new List<string>();
        while(await reader.ReadAsync(token))result.Add(reader.GetString(0));
        return result;
    }

    private static async Task<bool> ColumnExistsAsync(SqlConnection connection,SqlTransaction? transaction,string table,string column,CancellationToken token)
    {
        const string sql="SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA='dbo' AND TABLE_NAME=@Table AND COLUMN_NAME=@Column;";
        await using var command=new SqlCommand(sql,connection,transaction);
        command.Parameters.Add("@Table",SqlDbType.NVarChar,100).Value=table;
        command.Parameters.Add("@Column",SqlDbType.NVarChar,100).Value=column;
        return await command.ExecuteScalarAsync(token) is not null;
    }

    private static async Task<string?> GetDfVerifyAsync(SqlConnection connection,SqlTransaction? transaction,string table,CancellationToken token)
    {
        const string sql="SELECT LTRIM(RTRIM(ISNULL(DF_VERIFY,''))) FROM dbo.TABLES WITH (NOLOCK) WHERE T_ID=@Table;";
        await using var command=new SqlCommand(sql,connection,transaction);command.Parameters.Add("@Table",SqlDbType.NVarChar,100).Value=table;
        var result=await command.ExecuteScalarAsync(token);
        return result is null||string.IsNullOrWhiteSpace(result.ToString())?null:result.ToString();
    }

    private static IReadOnlySet<string> ScopeFields(IReadOnlyList<FormFieldDefinition> fields,IReadOnlyList<string> pkColumns)=>
        fields.Where(field=>!field.DisplayOnly).Select(field=>field.Key).Concat(pkColumns).ToHashSet(StringComparer.OrdinalIgnoreCase);

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

    /// <summary>
    /// 选择器显示值回显：编辑记录时，把复合单元格内"幽灵从字段"（无物理列、只显示不保存，
    /// 如 CLIENT_NAME/SALES_NAME）按主字段值从白名单选择器表解析显示值，避免打开编辑页空白。
    /// 安全约束：表名/列名均来自服务端元数据并做标识符 + 物理存在双重校验；值参数化。
    /// </summary>
    private static async Task ResolveChooserDisplaysAsync(SqlConnection connection,IReadOnlyList<FormFieldDefinition> fields,Dictionary<string,object?> row,CancellationToken token)
    {
        var companionsByGroup = fields
            .Where(field => field.DisplayOnly && field.CellRole == 2 && !string.IsNullOrWhiteSpace(field.CellGroup))
            .GroupBy(field => field.CellGroup!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.OrdinalIgnoreCase);
        if (companionsByGroup.Count == 0) return;

        foreach (var main in fields.Where(field =>
                     field.CellRole == 1 && !string.IsNullOrWhiteSpace(field.CellGroup)
                     && field.Choosers.Any(source => source.Active && !string.IsNullOrWhiteSpace(source.Table))))
        {
            if (!companionsByGroup.TryGetValue(main.CellGroup!, out var companions)) continue;
            if (!row.TryGetValue(main.Key, out var raw) || raw is null || string.IsNullOrWhiteSpace(raw.ToString())) continue;
            var source = main.Choosers.First(item => item.Active && !string.IsNullOrWhiteSpace(item.Table));
            await ResolveChooserGroupAsync(connection,main,source,companions,row,raw.ToString()!,token);
        }
    }

    private static async Task ResolveChooserGroupAsync(
        SqlConnection connection,
        FormFieldDefinition main,
        FieldChooserSource source,
        IReadOnlyList<FormFieldDefinition> companions,
        Dictionary<string,object?> row,
        string value,
        CancellationToken token)
    {
        var table = source.Table!.Trim();
        if (!Identifier.IsMatch(table) || !await TableExistsAsync(connection,table,token)) return;

        var mapping = FormFieldSelector.ParseReturnMapping(source.ReturnMapping);
        var keyColumn = mapping.FirstOrDefault(pair => string.Equals(FormFieldSelector.NormalizeChooserTarget(pair.Target),main.Key,StringComparison.OrdinalIgnoreCase)).Column;
        if (string.IsNullOrWhiteSpace(keyColumn)) keyColumn = main.Key;
        if (!Identifier.IsMatch(keyColumn)) return;

        var selected = new List<(FormFieldDefinition Field,string Column)>();
        foreach (var companion in companions)
        {
            var column = mapping.FirstOrDefault(pair => string.Equals(FormFieldSelector.NormalizeChooserTarget(pair.Target),companion.Key,StringComparison.OrdinalIgnoreCase)).Column;
            if (string.IsNullOrWhiteSpace(column)) column = companion.Key;
            if (!Identifier.IsMatch(column)) return;
            selected.Add((companion,column));
        }

        var columns = selected.Select(item => item.Column).Append(keyColumn).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (!await ColumnsExistAsync(connection,table,columns,token)) return;

        var select = string.Join(",", columns.Select(column => $"[{column}]"));
        await using var command = new SqlCommand($"SELECT TOP 1 {select} FROM dbo.[{table}] WHERE [{keyColumn}]=@Value;",connection);
        command.Parameters.Add("@Value",SqlDbType.NVarChar,256).Value=value;
        await using var reader = await command.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token)) return;
        foreach (var (field,column) in selected)
            row[field.Key] = reader.IsDBNull(reader.GetOrdinal(column)) ? null : reader.GetValue(reader.GetOrdinal(column));
    }

    private static async Task<bool> TableExistsAsync(SqlConnection connection,string table,CancellationToken token)
    {
        await using var command = new SqlCommand("SELECT 1 FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_SCHEMA='dbo' AND TABLE_NAME=@Table;",connection);
        command.Parameters.Add("@Table",SqlDbType.NVarChar,128).Value=table;
        return await command.ExecuteScalarAsync(token) is not null;
    }

    private static async Task<bool> ColumnsExistAsync(SqlConnection connection,string table,IReadOnlyList<string> columns,CancellationToken token)
    {
        var placeholders = string.Join(",", columns.Select((_,i) => $"@C{i}"));
        await using var command = new SqlCommand($"SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA='dbo' AND TABLE_NAME=@Table AND COLUMN_NAME IN ({placeholders});",connection);
        command.Parameters.Add("@Table",SqlDbType.NVarChar,128).Value=table;
        for (var i = 0; i < columns.Count; i++)
            command.Parameters.Add($"@C{i}",SqlDbType.NVarChar,128).Value=columns[i];
        var count = Convert.ToInt32(await command.ExecuteScalarAsync(token));
        return count == columns.Count;
    }

    private static async Task<Dictionary<string,object?>?> ReadRowAsync(
        SqlConnection connection,SqlTransaction? transaction,string table,IReadOnlyList<string> pkColumns,IReadOnlyList<string> keyValues,IReadOnlyList<string> fields,CancellationToken token)
    {
        var rows=await ReadRowsAsync(connection,transaction,table,pkColumns,keyValues,fields,token);
        return rows.Count==0?null:rows[0];
    }

    private static async Task<IReadOnlyList<Dictionary<string,object?>>> ReadRowsAsync(
        SqlConnection connection,SqlTransaction? transaction,string table,IReadOnlyList<string> pkColumns,IReadOnlyList<string> keyValues,IReadOnlyList<string> fields,CancellationToken token)
    {
        var select=string.Join(',',fields.Select(field=>$"[{field}]"));
        var where=string.Join(" AND ",pkColumns.Select((column,index)=>$"[{column}]=@k{index}"));
        await using var command=new SqlCommand($"SELECT {select} FROM dbo.[{table}] WHERE {where};",connection,transaction);
        AddKeyParameters(command,pkColumns,keyValues);
        await using var reader=await command.ExecuteReaderAsync(token);
        var result=new List<Dictionary<string,object?>>();
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
        return result;
    }

    private static async Task<bool> RowExistsAsync(SqlConnection connection,SqlTransaction transaction,string table,IReadOnlyList<string> pkColumns,IReadOnlyList<string> keyValues,CancellationToken token)
    {
        var where=string.Join(" AND ",pkColumns.Select((column,index)=>$"[{column}]=@k{index}"));
        await using var command=new SqlCommand($"SELECT 1 FROM dbo.[{table}] WHERE {where};",connection,transaction);
        AddKeyParameters(command,pkColumns,keyValues);
        return await command.ExecuteScalarAsync(token) is not null;
    }

    private static async Task<bool> RecordInScopeAsync(
        SqlConnection connection,SqlTransaction? transaction,string table,IReadOnlyList<string> pkColumns,IReadOnlyList<string> keyValues,string predicate,IReadOnlyList<object> parameters,CancellationToken token)
    {
        var where=string.Join(" AND ",pkColumns.Select((column,index)=>$"[{column}]=@k{index}"));
        await using var command=new SqlCommand($"SELECT 1 FROM dbo.[{table}] WHERE {where} AND ({predicate});",connection,transaction);
        AddKeyParameters(command,pkColumns,keyValues);
        for(var i=0;i<parameters.Count;i++)command.Parameters.AddWithValue($"@df{i}",parameters[i]??DBNull.Value);
        return await command.ExecuteScalarAsync(token) is not null;
    }

    private static async Task DeleteDetailRowsAsync(SqlConnection connection,SqlTransaction transaction,string detailTable,IReadOnlyList<string> pkColumns,IReadOnlyList<string> keyValues,CancellationToken token)
    {
        var where=string.Join(" AND ",pkColumns.Select((column,index)=>$"[{column}]=@k{index}"));
        await using var command=new SqlCommand($"DELETE FROM dbo.[{detailTable}] WHERE {where};",connection,transaction);
        AddKeyParameters(command,pkColumns,keyValues);
        await command.ExecuteNonQueryAsync(token);
    }

    private static void AddKeyParameters(SqlCommand command,IReadOnlyList<string> pkColumns,IReadOnlyList<string> keyValues)
    {
        for(var i=0;i<pkColumns.Count;i++)command.Parameters.AddWithValue($"@k{i}",keyValues[i]??string.Empty);
    }

    private static object NormalizeDbValue(object? value)=>value is null?DBNull.Value:value;

    private static string ValueToString(object? value)=>value switch
    {
        null=>string.Empty,
        DateTime dateTime=>dateTime.ToString("yyyy-MM-dd HH:mm:ss",System.Globalization.CultureInfo.InvariantCulture),
        IFormattable formattable=>formattable.ToString(null,System.Globalization.CultureInfo.InvariantCulture),
        _=>value.ToString()??string.Empty,
    };

    internal static bool ValuesEqual(object? left,object? right)
    {
        if(left is null&&right is null)return true;
        if(left is null||right is null)return false;
        if(left is string leftText&&right is string rightText)return string.Equals(leftText.Trim(),rightText.Trim(),StringComparison.Ordinal);
        if(left is DateTime leftDate&&right is DateTime rightDate)return leftDate==rightDate;
        if(left is double leftDouble&&right is double rightDouble)return Math.Abs(leftDouble-rightDouble)<0.0001;
        // 数值跨类型比较：TryConvert('float') 产生 double、DB int 读出 Int64 等场景
        if(IsNumeric(left)&&IsNumeric(right))
        {
            var leftNumber=Convert.ToDecimal(left,System.Globalization.CultureInfo.InvariantCulture);
            var rightNumber=Convert.ToDecimal(right,System.Globalization.CultureInfo.InvariantCulture);
            return leftNumber==rightNumber;
        }
        // F_TYPE 与物理类型不一致（如 nvarchar 标 smallint）时一侧为字符串一侧为数值，
        // 统一按数值解析比较（失败回落字符串比较），避免此类字段更新永远 CONCURRENT_MODIFIED。
        if(left is string||right is string)
        {
            var leftTextValue=ValueToString(left).Trim();
            var rightTextValue=ValueToString(right).Trim();
            if(decimal.TryParse(leftTextValue,System.Globalization.NumberStyles.Number,System.Globalization.CultureInfo.InvariantCulture,out var leftNumber)&&
               decimal.TryParse(rightTextValue,System.Globalization.NumberStyles.Number,System.Globalization.CultureInfo.InvariantCulture,out var rightNumber))
                return leftNumber==rightNumber;
            return string.Equals(leftTextValue,rightTextValue,System.StringComparison.OrdinalIgnoreCase);
        }
        return left.Equals(right);
    }

    private static bool IsNumeric(object value)=>value is sbyte or byte or short or ushort or int or uint
        or long or ulong or float or double or decimal;

    /// <summary>
    /// 写操作审计留痕（旧库 SYSDF 系统日志表，不新建表）。
    /// TYPE：INSERT/UPDATE/DELETE/APPROVE/DEAPPROVE；RECORD_IDX 为主键值逗号连接。
    /// </summary>
    private static async Task WriteAuditAsync(
        SqlConnection connection,
        SqlTransaction? transaction,
        int moduleId,
        string recordKey,
        string type,
        string content,
        string executor,
        CancellationToken token)
    {
        const string sql = """
            INSERT INTO dbo.SYSDF (M_IDX, RECORD_IDX, CONTENT, TYPE, EXEC_BY, EXEC_DATE, CI, OPERFLAG)
            VALUES (@ModuleId, @RecordKey, @Content, @Type, @ExecBy, GETDATE(), NULL, 1);
            """;
        await using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.Add("@ModuleId", SqlDbType.Int).Value = moduleId;
        command.Parameters.Add("@RecordKey", SqlDbType.NVarChar, 100).Value =
            recordKey.Length > 100 ? recordKey[..100] : recordKey;
        command.Parameters.Add("@Content", SqlDbType.NVarChar, 1000).Value =
            content.Length > 1000 ? content[..1000] : content;
        command.Parameters.Add("@Type", SqlDbType.NVarChar, 50).Value = type;
        command.Parameters.Add("@ExecBy", SqlDbType.NVarChar, 50).Value = executor;
        await command.ExecuteNonQueryAsync(token);
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
            while(await reader.ReadAsync(token)){var key=reader.GetString(0);if(Identifier.IsMatch(key)&&seen.Add(key))result.Add(new(key,reader.GetString(1),reader.GetBoolean(2),++order,reader.GetBoolean(4)));}
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
            while(await reader.ReadAsync(token)){var key=reader.GetString(0);if(Identifier.IsMatch(key)&&seen.Add(key))result.Add(new(key,reader.GetString(1),reader.GetBoolean(2),++order,reader.GetBoolean(4)));}
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
                if(Identifier.IsMatch(key))
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
    {await using var command=new SqlCommand("SELECT LTRIM(RTRIM(F_ID)) FROM dbo.FIELDS WHERE T_ID=@TargetTable AND COALESCE(IS_VISIBLE,1)=1",connection,transaction);command.Parameters.Add("@TargetTable",SqlDbType.NVarChar,100).Value=targetTable;await using var reader=await command.ExecuteReaderAsync(token);var result=new HashSet<string>(StringComparer.OrdinalIgnoreCase);while(await reader.ReadAsync(token)){var key=reader.GetString(0);if(Identifier.IsMatch(key))result.Add(key);}return result;}

    /// <summary>
    /// MODULES.FILTER / DATA_FILTER 字段白名单：主表全部可见、非虚拟、物理存在的字段
    /// （不受用户列选择影响，因为过滤器是服务端行级数据范围，字段不在用户列配置里不代表
    /// 不能用于过滤）；仍受禁止字段/成本/保密权限约束。
    /// </summary>
    private static async Task<IReadOnlySet<string>> ReadFilterFieldKeys(SqlConnection connection,string targetTable,bool canViewCost,bool canViewSecrecy,IReadOnlySet<string> deniedFields,CancellationToken token)
    {
        const string sql="""
            SELECT LTRIM(RTRIM(f.F_ID)),CAST(COALESCE(f.IS_COST,0) AS bit),CAST(COALESCE(f.IS_SECRECY,0) AS bit)
            FROM dbo.FIELDS f WITH (NOLOCK)
            WHERE f.T_ID=@TargetTable AND COALESCE(f.IS_VISIBLE,1)=1 AND COALESCE(f.IS_VIRTUAL,0)=0
              AND EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS c
                          WHERE c.TABLE_SCHEMA='dbo' AND c.TABLE_NAME=@TargetTable AND c.COLUMN_NAME=f.F_ID)
            ORDER BY f.F_ID;
            """;
        await using var command=new SqlCommand(sql,connection);command.Parameters.Add("@TargetTable",SqlDbType.NVarChar,100).Value=targetTable;
        await using var reader=await command.ExecuteReaderAsync(token);
        var result=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while(await reader.ReadAsync(token))
        {
            var key=reader.GetString(0).Trim();
            if(Identifier.IsMatch(key)&&!deniedFields.Contains(key)&&(canViewCost||!reader.GetBoolean(1))&&(canViewSecrecy||!reader.GetBoolean(2)))
                result.Add(key);
        }
        return result;
    }

    private static async Task<IReadOnlyList<WorkbenchField>> ReadFields(SqlConnection connection,string userId,string masterTable,string targetTable,bool canViewCost,bool canViewSecrecy,IReadOnlySet<string> deniedFields,CancellationToken token)
    {
        const string sql="""
            WITH UserFields AS (
              SELECT LTRIM(RTRIM(F_ID)) F_ID,F_IDX FROM dbo.SYSQL_FIELDS WITH (NOLOCK)
              WHERE USER_ID=@UserId AND T_ID=@MasterTable AND T_ID_R=@TargetTable
            ), HasConfig AS (SELECT CASE WHEN EXISTS(SELECT 1 FROM UserFields) THEN 1 ELSE 0 END Value)
            SELECT f.F_ID,COALESCE(NULLIF(LTRIM(RTRIM(f.F_DESC)),''),f.F_ID),COALESCE(f.F_TYPE,'nvarchar'),
                   COALESCE(f.DISPLAY_LENGTH,100),NULLIF(LTRIM(RTRIM(f.ITEM_ALIGN)),''),CAST(CASE WHEN EXISTS(SELECT 1 FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS tc INNER JOIN INFORMATION_SCHEMA.KEY_COLUMN_USAGE ku ON ku.CONSTRAINT_NAME=tc.CONSTRAINT_NAME AND ku.CONSTRAINT_SCHEMA=tc.CONSTRAINT_SCHEMA WHERE tc.CONSTRAINT_TYPE='PRIMARY KEY' AND ku.TABLE_SCHEMA='dbo' AND ku.TABLE_NAME=@TargetTable AND ku.COLUMN_NAME=f.F_ID) THEN 1 ELSE 0 END AS bit),CAST(COALESCE(f.IS_QUERY,1) AS bit),CAST(COALESCE(f.IS_COST,0) AS bit),CAST(COALESCE(f.IS_SECRECY,0) AS bit),COALESCE(NULLIF(f.HEADER_ALIGN,''),'center'),f.DISPLAY_FORMAT,f.BROWSE_URL,f.BROWSE_M_IDX,CAST(COALESCE(f.IS_VIRTUAL,0) AS bit),f.VIRTUAL_EXP
            FROM dbo.FIELDS f WITH (NOLOCK) CROSS JOIN HasConfig h LEFT JOIN UserFields u ON u.F_ID=f.F_ID
            WHERE f.T_ID=@TargetTable AND COALESCE(f.IS_VISIBLE,1)=1
              AND (COALESCE(f.IS_VIRTUAL,0)=1 OR EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS c
                          WHERE c.TABLE_SCHEMA='dbo' AND c.TABLE_NAME=@TargetTable AND c.COLUMN_NAME=f.F_ID))
              AND (EXISTS(SELECT 1 FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS tc INNER JOIN INFORMATION_SCHEMA.KEY_COLUMN_USAGE ku ON ku.CONSTRAINT_NAME=tc.CONSTRAINT_NAME AND ku.CONSTRAINT_SCHEMA=tc.CONSTRAINT_SCHEMA WHERE tc.CONSTRAINT_TYPE='PRIMARY KEY' AND ku.TABLE_SCHEMA='dbo' AND ku.TABLE_NAME=@TargetTable AND ku.COLUMN_NAME=f.F_ID) OR (h.Value=1 AND u.F_ID IS NOT NULL) OR (h.Value=0 AND COALESCE(f.IS_DEFAULT_FIELDS,0)=1))
            ORDER BY CASE WHEN EXISTS(SELECT 1 FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS tc INNER JOIN INFORMATION_SCHEMA.KEY_COLUMN_USAGE ku ON ku.CONSTRAINT_NAME=tc.CONSTRAINT_NAME AND ku.CONSTRAINT_SCHEMA=tc.CONSTRAINT_SCHEMA WHERE tc.CONSTRAINT_TYPE='PRIMARY KEY' AND ku.TABLE_SCHEMA='dbo' AND ku.TABLE_NAME=@TargetTable AND ku.COLUMN_NAME=f.F_ID) AND u.F_ID IS NULL THEN 0 ELSE 1 END,COALESCE(u.F_IDX,COALESCE(f.VERIFY_INDEX,999)),f.F_ID OPTION (OPTIMIZE FOR UNKNOWN);
            """;
        await using var command=new SqlCommand(sql,connection);command.Parameters.Add("@UserId",SqlDbType.NChar,10).Value=userId.Trim();command.Parameters.Add("@MasterTable",SqlDbType.VarChar,100).Value=masterTable;command.Parameters.Add("@TargetTable",SqlDbType.VarChar,100).Value=targetTable;
        var fields=new List<WorkbenchField>();
        await using(var reader=await command.ExecuteReaderAsync(token))
        {
            while(await reader.ReadAsync(token)){var key=reader.GetString(0).Trim();if(Identifier.IsMatch(key)&&!deniedFields.Contains(key)&&(canViewCost||!reader.GetBoolean(7))&&(canViewSecrecy||!reader.GetBoolean(8))){var isVirtual=reader.GetBoolean(13);var virtualExpression=reader.IsDBNull(14)?null:reader.GetString(14).Trim();fields.Add(new(key,reader.GetString(1).Trim(),reader.GetString(2).Trim(),Math.Clamp(reader.GetInt32(3),40,300),reader.IsDBNull(4)?null:reader.GetString(4).Trim(),reader.GetBoolean(5),true,isVirtual?false:reader.GetBoolean(6),reader.GetString(9),reader.IsDBNull(10)?null:reader.GetString(10),reader.IsDBNull(11)?null:reader.GetString(11),reader.IsDBNull(12)?null:reader.GetInt32(12),isVirtual,virtualExpression));}}
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
    /// <summary>
    /// 构造列表查询的列清单：物理列在派生表内无歧义选择；存在虚拟字段时外层经
    /// `[__base]` 派生表 + 受控 LEFT JOIN 输出（QUERY_RELATION 白名单 + 物理存在校验）。
    /// 派生表把 WHERE/ORDER 限制在基表内，避免 JOIN 引入的同名列歧义；
    /// 无法解析的虚拟字段不进入外层列清单（不渲染）。
    /// </summary>
    private async Task<ListSelection> BuildListSelectionAsync(
        SqlConnection connection,
        string table,
        IReadOnlyList<WorkbenchField> selected,
        CancellationToken token)
    {
        var physical=selected.Where(field=>!field.IsVirtual).ToList();
        var innerColumns=physical.Select(field=>$"[{field.Key}]").ToList();
        var outerColumns=physical.Select(field=>$"[__base].[{field.Key}]").ToList();
        var virtualFields=selected.Where(field=>field.IsVirtual).ToList();
        var joinFragment="";
        if(virtualFields.Count>0)
        {
            var resolution=await new VirtualColumnResolver(connection).ResolveAsync(table,virtualFields,token,baseAlias:"__base");
            outerColumns.AddRange(resolution.SelectFragments);
            joinFragment=resolution.JoinFragment;
            // JOIN 条件引用的基表列必须出现在内层投影中，否则外层 [__base].[列] 引用无效。
            foreach(var column in resolution.BaseColumns)
                if(!physical.Any(field=>field.Key.Equals(column,StringComparison.OrdinalIgnoreCase)))
                    innerColumns.Add($"[{column}]");
            foreach(var key in resolution.UnresolvedKeys)
                logger.LogDebug("虚拟字段运行期无法解析，列表不渲染：table={Table} field={Field}",table,key);
        }
        if(outerColumns.Count==0)
        {
            innerColumns.Add($"[{selected[0].Key}]");
            outerColumns.Add($"[__base].[{selected[0].Key}]");
        }
        return new(string.Join(',',innerColumns),string.Join(',',outerColumns),joinFragment,virtualFields.Count>0);
    }

    private sealed record ListSelection(string InnerColumns,string OuterColumns,string JoinFragment,bool HasVirtual);

    /// <summary>把 ResolveOrder 生成的 `[列] ASC` 改写为派生表限定 `[__base].[列] ASC`（仅作用于自身确定性输出）。</summary>
    private static string QualifyOrder(string order) =>
        Regex.Replace(order, @"\[([A-Za-z_][A-Za-z0-9_]{0,127})\]", "[__base].[$1]");

    private static string ResolveOrder(WorkbenchDefinition definition,IReadOnlyList<WorkbenchField> fields,IReadOnlyList<WorkbenchField> selected,bool detail,string? sortFields,string? sortDirections)
    {
        if(!string.IsNullOrWhiteSpace(sortFields))
        {
            var names=sortFields.Split(',',StringSplitOptions.TrimEntries|StringSplitOptions.RemoveEmptyEntries);
            if(names.Length>5)throw new ArgumentException("排序字段不能超过 5 个。");
            var dirs=(sortDirections??string.Empty).Split(',',StringSplitOptions.TrimEntries|StringSplitOptions.RemoveEmptyEntries);
            var parts=new List<string>();
            for(var i=0;i<names.Length;i++)
            {
                var field=fields.FirstOrDefault(item=>!item.IsVirtual&&item.Key.Equals(names[i],StringComparison.OrdinalIgnoreCase))??throw new ArgumentException("排序字段无效。");
                var desc=i<dirs.Length&&dirs[i].Equals("desc",StringComparison.OrdinalIgnoreCase);
                parts.Add($"[{field.Key}] {(desc?"DESC":"ASC")}");
            }
            return string.Join(',',parts);
        }
        if(!detail&&!string.IsNullOrWhiteSpace(definition.DefaultSort))return definition.DefaultSort;
        var keys=fields.Where(field=>field.IsPrimaryKey&&!field.IsVirtual).ToList();
        if(keys.Count==0)keys=selected.Where(field=>!field.IsVirtual).Take(1).ToList();
        if(keys.Count==0)throw new InvalidOperationException("工作台列表缺少可排序列。");
        return string.Join(',',keys.Select(field=>$"[{field.Key}]"));
    }

    private static string? NormalizeSort(string? value,string table,IReadOnlyList<WorkbenchField> fields)
    {
        if(string.IsNullOrWhiteSpace(value))return null;var allowed=fields.Where(field=>!field.IsVirtual).Select(field=>field.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);var result=new List<string>();
        foreach(var part in value.Split(',',StringSplitOptions.TrimEntries|StringSplitOptions.RemoveEmptyEntries)){var tokens=Regex.Split(part.Trim(),"\\s+");if(tokens.Length is <1 or >2)return null;var identifier=tokens[0].Split('.');if(identifier.Length==2&&!identifier[0].Equals(table,StringComparison.OrdinalIgnoreCase))return null;var field=identifier[^1].Trim('[',']');if(!Identifier.IsMatch(field)||!allowed.Contains(field))return null;var direction=tokens.Length==2&&tokens[1].Equals("DESC",StringComparison.OrdinalIgnoreCase)?" DESC":tokens.Length==1||tokens[1].Equals("ASC",StringComparison.OrdinalIgnoreCase)?" ASC":null;if(direction is null)return null;result.Add($"[{field}]{direction}");}
        return result.Count==0?null:string.Join(',',result);
    }

    private static void AddQueryPredicates(WorkbenchQuery query,IReadOnlyList<WorkbenchField> fields,List<string> predicates,SqlCommand command)
    {
        if(query.Conditions.Count>20)throw new ArgumentException("查询条件不能超过 20 个。");
        var queryPredicates=new List<string>();
        foreach(var condition in query.Conditions)
        {
        var field=fields.FirstOrDefault(item=>item.Key.Equals(condition.Field,StringComparison.OrdinalIgnoreCase))??throw new ArgumentException($"无效查询字段：{condition.Field}");
        if(field.IsVirtual)throw new ArgumentException($"虚拟字段不可查询：{condition.Field}");
        // 比较运算符（eq/ne/gt/gte/lt/lte/between）允许定义白名单内任意字段（如批核/结案状态位），
        // 仍以参数化 + 权限过滤后的定义白名单为边界；LIKE 类运算符继续限定 IsQueryable 字段。
        var comparisonOnly=condition.Operator is "eq" or "ne" or "gt" or "gte" or "lt" or "lte" or "between";
        if(!comparisonOnly&&!field.IsQueryable)throw new ArgumentException($"字段不可查询：{condition.Field}");
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

    private static void AddKeywordPredicates(string keyword,IReadOnlyList<WorkbenchField> fields,List<string> predicates,SqlCommand command)
    {
        var textFields=fields.Where(field=>field.IsQueryable&&IsTextLike(field.DataType)).ToList();
        if(textFields.Count==0)return;
        var name=$"@kw{predicates.Count}";command.Parameters.AddWithValue(name,$"%{keyword.Trim()}%");
        predicates.Add("("+string.Join(" OR ",textFields.Select(field=>IsDateLike(field.DataType)?$"CONVERT(varchar(23),[{field.Key}],120) LIKE {name}":$"[{field.Key}] LIKE {name}"))+")");
    }

    private static bool IsTextLike(string dataType)
    {
        var type=dataType.ToLowerInvariant();
        return type.Contains("char")||type.Contains("text")||type.Contains("date")||type.Contains("time")
            ||type is "idcard" or "url" or "email" or "phoneno" or "zipcode";
    }

    private static bool IsDateLike(string dataType)=>
        dataType.Contains("date",StringComparison.OrdinalIgnoreCase)||dataType.Contains("time",StringComparison.OrdinalIgnoreCase);
    private SqlConnection CreateConnection()=>connections.Create();
    /// <summary>
    private static bool IsWorkbenchUrl(string url)
    {
        var value=url.Trim().Replace('\\','/');
        return value.Equals("/document-workbench",StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("/document-workbench/",StringComparison.OrdinalIgnoreCase);
    }
}
