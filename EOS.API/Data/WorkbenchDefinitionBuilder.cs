using EOS.API.Data.Forms;
using EOS.API.Models;
using EOS.API.Telemetry;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using System.Data;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace EOS.API.Data;

/// <summary>
/// Builds workbench and unified-form definitions: resolves each user's field view from a
/// published snapshot baseline or live metadata and turns controlled metadata (FILTER, groups,
/// sorting, form buttons and tabs) into a runtime definition. Dynamic identifiers always come
/// from server-side whitelists and values are parameterized.
/// </summary>
public sealed class WorkbenchDefinitionBuilder(
    DbConnectionFactory connections,
    WorkbenchDefinitionProvider definitionProvider,
    IOptions<UnifiedFormEditorSettings> formSettings,
    ILogger<WorkbenchDefinitionBuilder> logger)
{
    private static readonly Regex BrowseUrlPlaceholder = new(@"\{([^{}]*)\}", RegexOptions.Compiled);
    /// <summary>能打开统一表单的模块（写名单 + 只读名单）：跨模块浏览链接只指向这些目标。</summary>
    private readonly IReadOnlySet<int> formOpenableModules =
        formSettings.Value.EnabledModuleIds.Concat(formSettings.Value.ReadOnlyModuleIds).ToHashSet();

    /// <summary>
    /// 过账引擎可能要求、因此必须出现在明细表单里的维度列（与"必填"同款处理）：
    /// 位置档 3 的库别要求指明库位，管批次的料号（`MANAGE_BATCH=1` 或批次档 2）要求批号。
    /// 判据在引擎侧，而这两列是否进表单取决于用户的「选择列」配置——不补进表单，
    /// 用户就会遇到「引擎要求填、界面没有格子」的死结。
    /// </summary>
    private static readonly HashSet<string> RuntimeRequiredDetailColumns =
        new(StringComparer.OrdinalIgnoreCase) { "LOCATION_NO", "BATCH_NO" };

    private SqlConnection CreateConnection()=>connections.Create();
    /// <summary>
    /// Parses MODULES.FORM_BUTTONS (e.g. '1=copy;2=approve;3=print') into a controlled button list.
    /// Format: semicolon-separated 'index=action' entries; allowed actions are new/edit/delete/copy/
    /// approve/deapprove/print/export/search. Empty or invalid entries are ignored (server-side
    /// whitelist, raw config text is not trusted); an empty config returns null.
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
    /// Whitelist validation for BROWSE_URL browse-link templates.
    /// Only in-site relative paths are allowed (starting with ~ and no external protocol), and every
    /// {placeholder} must be an authorized field of the same table (case-insensitive); otherwise null
    /// is returned and the browse link is not rendered.
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

    /// <summary>
    /// 运行时/发布共用的定义构建入口。默认（forPublish=false）运行时语义：已发布且未脏的
    /// 模块以快照基线为模块级事实源（未发布的元数据改动不参与执行），字段视图按用户实时重建。
    /// forPublish=true 时一律走元数据重建（<see cref="BuildFromMetadataAsync"/>），忽略基线——
    /// 发布/校验产出的是"当前代码 + 当前元数据"的定义，模块级字段（表/主键/业务规则/系统列/
    /// 明细必填口径/校验规则等）每次发布都重新推导，不再继承旧快照。
    /// </summary>
    public async Task<WorkbenchDefinition?> GetDefinitionAsync(int moduleId, string userId, string? execTag, bool canViewCost, bool canViewSecrecy, IReadOnlySet<string> deniedMasterFields, IReadOnlySet<string> deniedDetailFields, CancellationToken token, bool forPublish = false)
    {
        using var timing = DbTimingCollector.Instance.Measure();
        await using var connection = CreateConnection();
        await connection.OpenAsync(token);
        // 发布路径不读基线缓存：即使库里已无当前快照或批次首模块命中旧缓存，发布所得
        // 定义都从 MODULES/FIELDS/代码注册表重建，杜绝「删码后快照仍指向已删族名」。
        if (forPublish)
        {
            return await BuildFromMetadataAsync(connection, moduleId, userId, execTag, canViewCost, canViewSecrecy,
                deniedMasterFields, deniedDetailFields, version: null, token);
        }
        // 运行时的生效定义只有一个来源：最近一次发布的快照。未发布的元数据改动不参与执行
        // （「已保存未发布」只改变管理端状态），因此这里不再按脏标记回退实时元数据——
        // 回退会让快照内的效果引擎配置段丢失，使引擎静默退出而落到旧批核路径。
        if (definitionProvider.TryGetBaseline(moduleId, out var baseline, out var snapshotVersion))
        {
            var fromBaseline = await BuildFromBaselineAsync(connection, baseline, snapshotVersion, moduleId, userId, execTag,
                canViewCost, canViewSecrecy, deniedMasterFields, deniedDetailFields, token);
            logger.LogDebug("工作台定义（快照）module={ModuleId} version={Version}", moduleId, snapshotVersion);
            return fromBaseline;
        }
        return await BuildFromMetadataAsync(connection, moduleId, userId, execTag, canViewCost, canViewSecrecy,
            deniedMasterFields, deniedDetailFields, version: string.Empty, token);
    }

    /// <summary>
    /// Builds the per-user definition from a published snapshot baseline:
    /// module-level immutable metadata (tables/routes/keys/filters/forms/business rules) comes from
    /// the snapshot; per-user field views (permission filtering + column order), the FILTER
    /// whitelist and group expressions are resolved live per user.
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
            master, formOpenableModules, token);
        var detailFields = detail is null
            ? []
            : await WorkbenchBrowseResolver.ResolveAsync(connection,
                await ReadFields(connection, userId, master, detail, canViewCost, canViewSecrecy, deniedDetailFields, token),
                detail, formOpenableModules, token);
        var (_, groupExpressions) = await ReadGroupExpressionsAsync(connection, moduleId, token);
        // 版式段是模块级事实，随快照冻结；历史快照没有该段时按当前配置补读（含默认推导）
        var formLayout = baseline.FormLayout ?? await FormLayoutReader.ReadAsync(
            connection, moduleId, master, detail,
            FormLayoutDerivation.DefaultColumns, token);
        return baseline with
        {
            MasterFields = masterFields,
            DetailFields = detailFields,
            FormLayout = formLayout,
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
        const string sql = "SELECT M_DESC,MASTER_TABLE,DETAIL_TABLE,M_URL,SORT_FIELDS,MODI_URL,DETAIL_NO_SAVE,DETAIL_NO_FIELDS,FILTER,AUTO_APPROVE," +
                           "GROUP1,GROUP_EXP1,GROUP2,GROUP_EXP2,GROUP3,GROUP_EXP3,GROUP4,GROUP_EXP4,GROUP5,GROUP_EXP5," +
                           // FORM_TABS / FORM_COLUMNS 已退役：**在原位返回 NULL 占位**，下游按位置取值不改，
                           // 等字段级配置彻底清理时再一并删掉这两段
                           "NULL AS FORM_TABS,NULL AS FORM_COLUMNS,FORM_BUTTONS,NEW_URL,IF_COPY,SEARCH_1,SEARCH_2,HELP_URL " +
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
        var autoApprove=!reader.IsDBNull(9)&&reader.GetBoolean(9);
        var groupExpressions = new string[5];
        for (var i = 0; i < 5; i++)
        {
            var offset = 10 + i * 2;
            var enabled = !reader.IsDBNull(offset) && reader.GetBoolean(offset);
            var expression = reader.IsDBNull(offset + 1) ? string.Empty : reader.GetString(offset + 1).Trim();
            groupExpressions[i] = enabled ? expression : string.Empty;
        }
        var formTabs = reader.IsDBNull(20) ? null : reader.GetString(20).Trim();
        var formColumns = reader.IsDBNull(21) ? (int?)null : (int)reader.GetByte(21);
        var formButtons = reader.IsDBNull(22) ? null : reader.GetString(22).Trim();
        var newUrlRaw = reader.IsDBNull(23) ? string.Empty : reader.GetString(23).Trim();
        var ifCopy = !reader.IsDBNull(24) && reader.GetBoolean(24);
        var searchMaster = !reader.IsDBNull(25) && reader.GetBoolean(25);
        var searchDetail = !reader.IsDBNull(26) && reader.GetBoolean(26);
        var helpUrl = reader.IsDBNull(27) ? null : reader.GetString(27).Trim();
        if (string.IsNullOrEmpty(helpUrl)) helpUrl = null;
        await reader.CloseAsync();
        if (!ModuleRouteValidator.IsWorkbenchUrl(url) || !WorkbenchSql.Identifier.IsMatch(master) || (detail is not null && !WorkbenchSql.Identifier.IsMatch(detail)))
        {
            logger.LogWarning("模块 {ModuleId} 未通过工作台校验 url={Url} master={Master} detail={Detail}", moduleId, url, master, detail);
            return null;
        }
        // NEW_URL/MODI_URL decide the add/edit routes: resolvable modern action routes are sent to
        // the front end; empty or invalid values return null so the controller falls back or hides
        // the buttons.
        var resolvedNewUrl = ModuleRouteValidator.ResolveActionUrl(newUrlRaw, moduleId);
        var resolvedModiUrl = ModuleRouteValidator.ResolveActionUrl(modiUrl, moduleId);
        var masterFields=await WorkbenchBrowseResolver.ResolveAsync(connection,
            await ReadFields(connection,userId,master,master,canViewCost,canViewSecrecy,deniedMasterFields,token),master,formOpenableModules,token);
        var masterPkOrder=await WorkbenchSql.GetPrimaryKeyColumnsAsync(connection,null,master,token);
        // Business rule: a static mapping wins (it carries bill-number and offset-table config);
        // otherwise it is auto-registered from MODULES metadata.
        var businessRule=ModuleBusinessMap.Get(moduleId);
        if(businessRule is null)
        {
            // 是否自动编号只取决于 BILLKIND 里有没有该模块的单号规则，与模块是否登记
            // 保存后处理无关（此前靠"有没有保存后钩子"推断，于是校验搬到目录后仍要
            // 保留空壳钩子才能编号）。
            var hasAutoBillNo=await BillNoGenerator.HasAutoBillNoAsync(connection,null,moduleId,token);
            // 「目录承接」同样与遗留钩子无关：钩子字段（UPDATE_SP/AFTERSAVE_SP）已物理删除，
            // 但保存期行为必须照常装配，否则「行为已迁目录、又不自动编号」的模块会静默丢规则。
            var catalogPorted=CatalogAfterSaveMap.IsPorted(moduleId);
            if(hasAutoBillNo||catalogPorted)
            {
                string? billNoField=null;
                string? billTypeField=null;
                if(hasAutoBillNo&&masterPkOrder.Count>=2)
                {
                    billNoField=masterPkOrder.FirstOrDefault(column=>column.Contains("NO",StringComparison.OrdinalIgnoreCase));
                    if(billNoField is not null)
                        billTypeField=masterPkOrder.First(column=>!column.Equals(billNoField,StringComparison.OrdinalIgnoreCase));
                }
                businessRule=new(moduleId,
                    billNoField is not null,
                    billNoField,
                    billTypeField);
            }
        }
        WorkbenchDefinition definition=new(moduleId,title,master,detail,masterFields,
            detail is null?[]:await WorkbenchBrowseResolver.ResolveAsync(connection,
                await ReadFields(connection,userId,master,detail,canViewCost,canViewSecrecy,deniedDetailFields,token),detail,formOpenableModules,token),NormalizeSort(defaultSort,master,masterFields),
            resolvedNewUrl is not null || resolvedModiUrl is not null,resolvedModiUrl is not null,detailNoSave,
            masterPkOrder,detailNoFields,
            await WorkflowEngine.HasFlowAsync(connection,moduleId,token),
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
        // 版式：有版式行即定制（该表字段集完全由版式决定），无行则按字段级配置推导默认版式
        var formLayout = await FormLayoutReader.ReadAsync(
            connection, moduleId, master, detail, FormLayoutDerivation.DefaultColumns, token);
        return definition with { DefinitionVersion = version, FormLayout = formLayout };
    }


    /// <summary>
    /// Builds the unified form definition (entry field view filtered by the current user's rights).
    /// mode supports new/edit/view (validated by the controller); no high-risk expressions are
    /// evaluated here. Detail fields stay aligned with the DocumentWorkbench child-table columns:
    /// the column set and order come from the same workbench source (user SYSQL_FIELDS, then
    /// SYSQL_DEFAULT, then FIELDS.IS_DEFAULT_FIELDS) so add pages and child tables share one column
    /// configuration.
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
        // 主表也含虚拟列：它们是选择器回写出来的伴生显示列（客户名称/业务员/送货地址…），
        // 旧系统里就是表单上的独立只读控件；把它们排除掉，回写目标在表单上根本不存在，
        // 前端写进内存的值既不回显、又不提交，等于白选。
        var masterRows=await ReadFormFieldRows(connection,definition.MasterTable,definition.MasterTable,token,includeVirtual:true);
        var masterFields=FormFieldSelector.Select(masterRows,mode,canViewCost,canViewSecrecy,deniedMasterFields,deniedNewMasterFields,deniedModiMasterFields);
        // 权限过滤（上一步）→ 版式裁剪字段集 → 版式排序与属性。未定制的表原样保留，
        // 故零配置模块的字段集与顺序与改造前逐字一致。
        masterFields=FormLayoutDerivation.ApplyMasterLayout(masterFields,definition.FormLayout);
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
            // 明细版式（已定制时）：按版式行重排列序、剔除隐藏列。随后仍会补回必填与
            // 引擎必需的列（见下），故版式不可能把必填项挤出表单。
            var orderedKeys=orderedRows.Select(row=>row.Key).ToList();
            var layoutKeys=FormLayoutDerivation.ApplyDetailOrder(orderedKeys,definition.FormLayout);
            if(!ReferenceEquals(layoutKeys,orderedKeys))
            {
                var byKey=new Dictionary<string,FormFieldRow>(StringComparer.OrdinalIgnoreCase);
                foreach(var row in orderedRows) byKey.TryAdd(row.Key,row);
                orderedRows=layoutKeys.Where(key=>byKey.ContainsKey(key)).Select(key=>byKey[key]).ToList();
            }
            // Required fields (IS_VERIFY=1) must stay in the form: the workbench column config only
            // controls the list view, not which fields can be entered; a missing required column in
            // the user/default config would make document creation fail.
            // 位置与批次同理（见 RuntimeRequiredDetailColumns）：引擎可能要求它们，而选择列未勾时
            // 表单里就没有格子可填，单据必然过账失败。
            var includedKeys=new HashSet<string>(orderedRows.Select(row=>row.Key),StringComparer.OrdinalIgnoreCase);
            foreach(var row in detailRows)
            {
                if(!includedKeys.Add(row.Key))continue;
                if(!row.IsRequired && !RuntimeRequiredDetailColumns.Contains(row.Key))continue;
                orderedRows.Add(row);
            }
            detailFields=FormFieldSelector.Select(orderedRows,mode,canViewCost,canViewSecrecy,deniedDetailFields,deniedNewDetailFields,deniedModiDetailFields);
            // Detail master-key columns (columns shared with the master primary key, e.g.
            // ORDER_TYPE/ORDER_NO) are carried from the master server-side and shown read-only.
            if(detailFields.Count>0)
            {
                detailFields=detailFields
                    .Select(field=>pkColumns.Any(column=>column.Equals(field.Key,StringComparison.OrdinalIgnoreCase))
                        || field.Key.Equals("SERIAL_NO",StringComparison.OrdinalIgnoreCase)
                        ? field with { IsReadonly=true, ServerFilled=true }
                        : field)
                    .ToList();
            }
            // Master-key columns must stay in the detail form definition because they are supplied
            // by the master server-side. With detail fields sourced from the workbench column
            // config, hidden or non-default required key columns would otherwise be lost and
            // creation rejected by MASTER_KEY_NOT_IN_DETAIL. Missing keys are appended from the
            // full physical rows as read-only, server-filled columns, applying the same permission
            // filtering as the field selection.
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
        // View mode: every field is read-only; save endpoints are unavailable in this mode.
        if(mode=="view")
        {
            masterFields=masterFields.Select(field=>field with { IsReadonly=true }).ToList();
            detailFields=detailFields.Select(field=>field with { IsReadonly=true }).ToList();
        }
        // 页签只来自版式表（MODULE_FORM_TAB）；没有页签行时由前端兜底为常驻「默认」页签
        var tabs = definition.FormLayout?.Tabs ?? [];
        var columns = definition.FormColumns is int formColumns and > 0 ? formColumns : 2;
        var defaultValues = await BuildNewDefaultsAsync(connection,definition,masterFields,mode,token);
        // 无副作用批核能力与服务端分支同口径（WorkflowStates.IsStatelessApproveCapable），
        // 工具栏据此显隐批核/解批：有能力即显示，无能力即隐藏，不出现点后必败的死按钮。
        var hasStatelessApprove = WorkflowStates.IsStatelessApproveCapable(
            definition.AutoApprove,
            definition.EffectEngineEnabled,
            await WorkflowEngine.HasFlowAsync(connection, definition.ModuleId, token));
        // 批核/解批入口能力（工具栏显隐）：流程 / 效果引擎接管 / 效果链 / 无副作用自动批核
        // 四者取并集，与发布门的 lifecycle_columns 检查共用同一判定——效果链接管批核的模块（无过程、无流程）
        // 同样要有入口，否则退役遗留过程后按钮会凭空消失。
        var hasApproveCapability = WorkflowStates.NeedsApproveColumn(
            definition.AutoApprove,
            definition.EffectEngineEnabled,
            definition.HasWorkflow,
            EnabledActionEventCodes(definition).ToList());
        // 版式设计权：只影响前端是否渲染设计入口，服务端写端点仍独立鉴权
        var canFormDesign = (await FormDesignPermissionResolver.ResolveAsync(
            connection, userId, definition.ModuleId, token)).CanDesign;
        return new FormDefinition(definition.ModuleId,definition.Title,definition.MasterTable,definition.DetailTable,
            definition.HasAdd,definition.HasEdit,mode,masterFields,detailFields,pkColumns,definition.DetailNoFields,detailDfVerify,
            tabs,columns,definition.FormButtons,defaultValues,definition.HasWorkflow,
            definition.IfCopy,definition.SearchMaster,definition.SearchDetail,
            canDelete,canApprove,canDeapprove,canEndCase,canUnEndCase,canFileView,canFileUpda,canFileEdit,canFileDele,
            canAddNew,canEdit,definition.HelpUrl,canSetup,hasStatelessApprove,hasApproveCapability,
            CanFormDesign: canFormDesign);
    }

    /// <summary>已发布定义里启用的效果动作事件码（供批核能力判定；未启用/占位行不计）。</summary>
    private static IEnumerable<string> EnabledActionEventCodes(WorkbenchDefinition definition)
    {
        if (definition.BusinessActions is not { ValueKind: JsonValueKind.Array } actions)
        {
            yield break;
        }
        foreach (var action in actions.EnumerateArray())
        {
            if (action.ValueKind != JsonValueKind.Object
                || !action.TryGetProperty("enabled", out var enabled)
                || enabled.ValueKind != JsonValueKind.True
                || !action.TryGetProperty("eventCode", out var code)
                || code.ValueKind != JsonValueKind.String)
            {
                continue;
            }
            var text = code.GetString();
            if (!string.IsNullOrWhiteSpace(text))
            {
                yield return text;
            }
        }
    }

    /// <summary>
    /// Default values for new mode:
    /// 1) auto bill-number modules get the default bill type plus the next generated number;
    /// 2) date fields (editable, not server-filled, no DFT_VALUE) default to today.
    /// Returned only for mode=new; values are generated server-side for display and re-validated
    /// by the save pipeline.
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
                var newNo = await BillNoGenerator.PeekAsync(connection,null,definition.ModuleId,token);
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
        // 复合格主字段带默认值时，同格的从字段（名称类）必须一起出现，否则新增态只有一个代号。
        // 主字段的生效默认值有两条来源：本方法上面生成的值（单别/单号/日期），以及字段元数据的
        // DFT_VALUE（新增态由客户端本地套用，服务端不感知）。两条都要参与解析，否则带元数据
        // 默认值的主字段（如库别 CP）不会带出同格伴生名称。
        foreach (var field in masterFields)
        {
            if (field.CellRole != 1 || string.IsNullOrWhiteSpace(field.CellGroup)) continue;
            var defaultValue = defaults.TryGetValue(field.Key, out var generated)
                ? generated
                : MetadataDefaultValue(field);
            if (string.IsNullOrWhiteSpace(defaultValue)) continue;
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
    /// 字段元数据里的新增默认值（FIELDS.DFT_VALUE），按字段类型转换后返回。
    /// 不可转换的值（无参函数表达式、日期哨兵 'D'、页面代码规则 token）一律视为"没有默认值"：
    /// 这类值新增态由客户端原样套用，服务端不解析、也不拿它去查伴生显示值。
    /// </summary>
    private static string? MetadataDefaultValue(FormFieldDefinition field)
    {
        if (string.IsNullOrWhiteSpace(field.DefaultValue)) return null;
        return RecordPayloadValidator.TryConvert(field.DataType, field.DefaultValue, out var value)
            ? Convert.ToString(value, CultureInfo.InvariantCulture)
            : null;
    }

    /// <summary>
    /// Backfills new-mode default values: queries the chooser source table by the code-field
    /// default and fills companion name fields in the same cell group (physical columns only;
    /// virtual columns needing JOINs are handled by the chooser runtime). Any validation failure
    /// skips the backfill (fail-closed, never throws).
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
            // Optional display-only backfill: when the source query fails (schema drift, lock
            // waits), degrade to an empty backfill instead of failing the whole new-mode form
            // definition (fail-closed).
        }
    }

    /// <summary>
    /// 表单字段原始行。F_DESC 取字段中文名，但占位文本一律视为"没有名字"并回落字段代号：
    /// 空串之外还有两类字符串占位（'NULL'、'&nbsp;'——历史元数据补齐脚本把"无说明"写成了它们），
    /// 它们非空，只判空串的兜底拦不住，会原样显示到表单标签上。
    /// </summary>
    internal static async Task<IReadOnlyList<FormFieldRow>> ReadFormFieldRows(SqlConnection connection,string masterTable,string targetTable,CancellationToken token,bool includeVirtual=false)
    {
        const string sql="""
            SELECT LTRIM(RTRIM(f.F_ID)) AS F_ID,COALESCE(NULLIF(NULLIF(NULLIF(LTRIM(RTRIM(f.F_DESC)),''),'NULL'),'&nbsp;'),LTRIM(RTRIM(f.F_ID))) AS F_DESC,
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
                   CAST(1 AS int) AS FORM_TAB_NO,
                   CAST(NULL AS int) AS FORM_ORDER,
                   CAST(1 AS int) AS FORM_SPAN,
                   CAST(0 AS bit) AS FORM_NEW_LINE,
                   CAST(N'' AS nvarchar(50)) AS FORM_CELL_GROUP,
                   CAST(0 AS int) AS FORM_CELL_ROLE,
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
              AND (COALESCE(f.IS_VIRTUAL,0)=@IncludeVirtual OR col.COLUMN_NAME IS NOT NULL)
            ORDER BY COALESCE(d.F_IDX,COALESCE(f.VERIFY_INDEX,999)),f.F_ID;
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

    /// <summary>Reads chooser data sources for all fields of the target table (FIELD_DATASOURCE, ordered by SERIAL_NO).</summary>
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

    /// <summary>
    /// Parses the legacy tab definition string (e.g. '1=Basic;2=Other'); invalid items are skipped and
    /// tabs sort by number. 该列已退役（页签归 MODULE_FORM_TAB），此处保留以解析原位占位值（恒空）。
    /// </summary>
    internal static IReadOnlyList<FormTabDefinition> ParseFormTabs(string? raw)
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
    /// Field whitelist for MODULES.FILTER / DATA_FILTER: all physically existing non-virtual master
    /// fields, including hidden ones (filter expressions may reference hidden fields, so limiting
    /// to visible fields would reject whole modules). Filtering is server-side row scoping
    /// independent of the user's column selection, so columns absent from the user config may still
    /// be used for filtering. Denied, cost and secrecy fields remain restricted.
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

    /// <summary>Reads module group expressions (GROUP1..5/GROUP_EXP1..5); snapshots omit high-risk expressions so these are read live.</summary>
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

    /// <summary>
    /// 读取用户可见列（显示列）：列集合与顺序完全由「选择列」配置决定——有用户配置（SYSQL_FIELDS）
    /// 时取其列与 F_IDX 顺序，否则取系统默认列（FIELDS.IS_DEFAULT_FIELDS）按 VERIFY_INDEX 排序。
    /// 主键列不在此处补入：用户未勾选的主键列只用于行标识/子表关联，由查询层单独并入投影，
    /// 不参与渲染（行键与显示列元数据分离）。
    /// </summary>
    private static async Task<IReadOnlyList<WorkbenchField>> ReadFields(SqlConnection connection,string userId,string masterTable,string targetTable,bool canViewCost,bool canViewSecrecy,IReadOnlySet<string> deniedFields,CancellationToken token)
    {
        const string sql="""
            WITH UserFields AS (
              SELECT LTRIM(RTRIM(F_ID)) F_ID,F_IDX FROM dbo.SYSQL_FIELDS WITH (NOLOCK)
              WHERE USER_ID=@UserId AND T_ID=@MasterTable AND T_ID_R=@TargetTable
            ), HasConfig AS (SELECT CASE WHEN EXISTS(SELECT 1 FROM UserFields) THEN 1 ELSE 0 END Value)
            SELECT f.F_ID,COALESCE(NULLIF(NULLIF(NULLIF(LTRIM(RTRIM(f.F_DESC)),''),'NULL'),'&nbsp;'),LTRIM(RTRIM(f.F_ID))),COALESCE(f.F_TYPE,'nvarchar'),
                   COALESCE(f.DISPLAY_LENGTH,100),NULLIF(LTRIM(RTRIM(f.ITEM_ALIGN)),''),CAST(CASE WHEN EXISTS(SELECT 1 FROM sys.indexes i2 JOIN sys.index_columns ic2 ON i2.object_id=ic2.object_id AND i2.index_id=ic2.index_id JOIN sys.columns c2 ON ic2.object_id=c2.object_id AND ic2.column_id=c2.column_id JOIN sys.tables t3 ON i2.object_id=t3.object_id JOIN sys.schemas s3 ON t3.schema_id=s3.schema_id WHERE s3.name=N'dbo' AND t3.name=@TargetTable AND i2.is_primary_key=1 AND c2.name=f.F_ID) THEN 1 ELSE 0 END AS bit),CAST(COALESCE(f.IS_QUERY,1) AS bit),CAST(COALESCE(f.IS_COST,0) AS bit),CAST(COALESCE(f.IS_SECRECY,0) AS bit),COALESCE(NULLIF(f.HEADER_ALIGN,''),'center'),f.DISPLAY_FORMAT,f.BROWSE_URL,f.BROWSE_M_IDX,CAST(COALESCE(f.IS_VIRTUAL,0) AS bit),f.VIRTUAL_EXP,f.CONVERT_FUNCTION
            FROM dbo.FIELDS f WITH (NOLOCK) CROSS JOIN HasConfig h LEFT JOIN UserFields u ON u.F_ID=f.F_ID
            WHERE f.T_ID=@TargetTable AND COALESCE(f.IS_VISIBLE,1)=1
              AND (COALESCE(f.IS_VIRTUAL,0)=1 OR EXISTS (SELECT 1 FROM sys.columns c
                          JOIN sys.objects o ON c.object_id=o.object_id AND o.type IN ('U','V')
                          JOIN sys.schemas s ON o.schema_id=s.schema_id
                          WHERE s.name=N'dbo' AND o.name=@TargetTable AND c.name=f.F_ID))
              AND ((h.Value=1 AND u.F_ID IS NOT NULL) OR (h.Value=0 AND COALESCE(f.IS_DEFAULT_FIELDS,0)=1))
            ORDER BY CASE WHEN u.F_IDX IS NULL THEN 1 ELSE 0 END,COALESCE(u.F_IDX,COALESCE(f.VERIFY_INDEX,999)),f.F_ID OPTION (OPTIMIZE FOR UNKNOWN);
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
}
