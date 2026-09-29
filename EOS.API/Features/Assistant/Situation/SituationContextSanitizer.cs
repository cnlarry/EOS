using EOS.API.Data;
using EOS.API.Features.Assistant.Tools;
using EOS.API.Models;
using EOS.API.Security;

namespace EOS.API.Features.Assistant.Situation;

/// <summary>
/// 上报处境的入口闸：截断、字段白名单、错误码白名单、配置标识符校验。
///
/// <para>
/// 前端上报一律**不信任**：上限由服务端强制；页面类型、操作符、字段名、错误码、配置标识符
/// 必须命中服务端白名单，否则剔除并记入 <c>Dropped</c>（如实告知，不静默）。
/// </para>
/// <para>
/// 字段级剔除与统一表单同一套口径：模块定义的字段视图已按 EXEC_TAG、成本位、保密位与
/// 禁止字段过滤，故"不在定义里的字段"即为无权字段（含 <c>DeniedMasterFields</c>）。
/// </para>
/// <para>
/// 上报只用于解释与定位，**不作为权限依据**：模块读取在采纳上报之前已独立鉴权，无权模块的上报整段丢弃。
/// </para>
/// </summary>
public sealed class SituationContextSanitizer(
    IWorkbenchSearchGateway gateway,
    IPermissionService permissions,
    ISituationFactsReader facts,
    AssistantSituationBudget budget,
    ILogger<SituationContextSanitizer> logger)
{
    public async Task<SituationContext> SanitizeAsync(string userId, SituationReport? report, CancellationToken token)
    {
        if (report is null) return SituationContext.Empty;

        var limits = budget.Limits;
        var dropped = new List<string>();

        var pageType = SanitizePageType(report.PageType, dropped);
        var moduleId = report.ModuleId;
        WorkbenchDefinition? definition = null;
        if (moduleId is int requested)
        {
            definition = await LoadDefinitionAsync(userId, requested, token);
            if (definition is null)
            {
                // 无权浏览或不是可查询模块：模块级上报整段丢弃（fail-closed）。
                dropped.Add($"module:{requested}");
                moduleId = null;
            }
        }

        var moduleTitle = budget.Clip(report.ModuleTitle, limits.MaxValueLength);
        if (string.IsNullOrWhiteSpace(moduleTitle) && definition is not null)
        {
            moduleTitle = definition.Title; // 前端未发模块名时由服务端元数据补齐
        }

        var docNo = definition is null ? null : budget.Clip(report.DocNo, limits.MaxValueLength);
        var filters = SanitizeFilters(report.Filters, definition, dropped);
        var selection = SanitizeSelection(report.Selection, definition, dropped);
        var formDirty = SanitizeFormDirty(report.FormDirty, definition, dropped);
        var lastNotice = SanitizeNotice(report.LastNotice, dropped);
        var configTarget = await SanitizeConfigTargetAsync(report.ConfigTarget, dropped, token);

        if (dropped.Count > 0)
        {
            // 剔除是"悄悄降级"的危险动作，必须留痕（只记数量与类别，不复述被丢弃的值）。
            logger.LogDebug("助手处境上报已剔除 {Count} 项 user={UserId}", dropped.Count, userId);
        }

        return new SituationContext(
            moduleId, moduleTitle, pageType, docNo,
            filters, selection, formDirty, lastNotice, configTarget, dropped);
    }

    private async Task<WorkbenchDefinition?> LoadDefinitionAsync(string userId, int moduleId, CancellationToken token)
    {
        var permission = await permissions.GetAsync(userId, moduleId, token);
        if (!permission.CanBrowse) return null;
        var (execTag, canViewCost, canViewSecrecy, deniedMaster, deniedDetail) = permission.Scope();
        return await gateway.GetDefinitionAsync(
            moduleId, userId, execTag, canViewCost, canViewSecrecy, deniedMaster, deniedDetail, token);
    }

    private string? SanitizePageType(string? pageType, List<string> dropped)
    {
        if (string.IsNullOrWhiteSpace(pageType)) return null;
        if (SituationPageTypes.IsKnown(pageType)) return pageType.Trim().ToLowerInvariant();
        dropped.Add($"pageType:{budget.Clip(pageType, 40)}");
        return null;
    }

    private IReadOnlyList<SituationFilter> SanitizeFilters(
        IReadOnlyList<SituationFilter>? reported, WorkbenchDefinition? definition, List<string> dropped)
    {
        if (reported is null || reported.Count == 0) return [];
        if (definition is null)
        {
            dropped.Add("filters:缺少模块上下文");
            return [];
        }

        var queryable = definition.MasterFields
            .Where(field => field.IsQueryable)
            .ToDictionary(field => field.Key, StringComparer.OrdinalIgnoreCase);
        var kept = new List<SituationFilter>();
        foreach (var filter in budget.Take("filters", reported, budget.Limits.MaxFilters))
        {
            var key = filter?.Field?.Trim() ?? string.Empty;
            if (key.Length == 0 || !queryable.TryGetValue(key, out var field))
            {
                dropped.Add($"filter:{budget.Clip(key, 40)}");
                continue;
            }

            if (!SituationOperators.IsKnown(filter!.Operator))
            {
                dropped.Add($"filter-operator:{budget.Clip(filter.Operator, 40)}");
                continue;
            }

            kept.Add(new SituationFilter(
                field.Key,
                filter.Operator!.Trim().ToLowerInvariant(),
                budget.Clip(filter.Value, budget.Limits.MaxValueLength)));
        }

        return kept;
    }

    private IReadOnlyList<string> SanitizeSelection(
        IReadOnlyList<string>? reported, WorkbenchDefinition? definition, List<string> dropped)
    {
        if (reported is null || reported.Count == 0) return [];
        if (definition is null)
        {
            dropped.Add("selection:缺少模块上下文");
            return [];
        }

        var kept = new List<string>();
        foreach (var key in budget.Take("selection", reported, budget.Limits.MaxSelection))
        {
            var value = budget.Clip(key, budget.Limits.MaxValueLength);
            if (value.Length == 0)
            {
                dropped.Add("selection:空主键");
                continue;
            }

            kept.Add(value);
        }

        return kept;
    }

    private IReadOnlyList<SituationDirtyField> SanitizeFormDirty(
        IReadOnlyList<SituationDirtyField>? reported, WorkbenchDefinition? definition, List<string> dropped)
    {
        if (reported is null || reported.Count == 0) return [];
        if (definition is null)
        {
            dropped.Add("formDirty:缺少模块上下文");
            return [];
        }

        // 成本位/保密位/禁止字段已在定义构建时剔除，故这里的键集合即"当前用户可见可写的字段"。
        var allowed = definition.MasterFields.Select(field => field.Key)
            .Concat(definition.DetailFields.Select(field => field.Key))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var kept = new List<SituationDirtyField>();
        var denied = 0;
        foreach (var dirty in budget.Take("formDirty", reported, budget.Limits.MaxDirtyFields))
        {
            var key = dirty?.Field?.Trim() ?? string.Empty;
            if (key.Length == 0 || !allowed.Contains(key))
            {
                denied++;
                continue;
            }

            kept.Add(new SituationDirtyField(
                key,
                budget.Clip(dirty!.Old, budget.Limits.MaxValueLength),
                budget.Clip(dirty.New, budget.Limits.MaxValueLength)));
        }

        if (denied > 0)
        {
            // 只记数量不记字段名：被剔除的字段本身就是无权知情的字段。
            dropped.Add($"formDirty:{denied} 个字段越权或不存在，已剔除");
        }

        return kept;
    }

    private SituationNotice? SanitizeNotice(SituationNotice? reported, List<string> dropped)
    {
        if (reported is null) return null;
        // 错误码一律归一为大写再比对：白名单是闭集，大小写差异不构成新的码。
        var code = reported.Code?.Trim().ToUpperInvariant() ?? string.Empty;
        if (!SituationNoticeCodes.IsKnown(code))
        {
            dropped.Add($"lastNotice.code:{budget.Clip(reported.Code, 60)}");
            return null;
        }

        return new SituationNotice(code, budget.Clip(reported.Summary, budget.Limits.MaxNoticeSummaryLength));
    }

    private async Task<SituationConfigTarget?> SanitizeConfigTargetAsync(
        SituationConfigTarget? reported, List<string> dropped, CancellationToken token)
    {
        if (reported is null) return null;
        var surface = reported.Surface?.Trim().ToLowerInvariant();
        if (!SituationConfigSurfaces.IsKnown(surface))
        {
            dropped.Add($"configTarget.surface:{budget.Clip(reported.Surface, 40)}");
            return null;
        }

        var tableId = budget.Clip(reported.TableId, 100);
        var fieldId = budget.Clip(reported.FieldId, 100);
        var effectKey = budget.Clip(reported.EffectKey, 100);
        var actionId = reported.ActionId is > 0 ? reported.ActionId : null;

        // 效果键的闭集在代码注册表里（库内配置行只可能取其中的值）。
        if (effectKey.Length > 0 && !BusinessActionCatalog.IsKnownEffectKey(effectKey))
        {
            dropped.Add($"configTarget.effectKey:{effectKey}");
            effectKey = string.Empty;
        }

        if (tableId.Length > 0 || fieldId.Length > 0 || actionId is not null)
        {
            var probe = await facts.ProbeConfigTargetAsync(tableId, fieldId, actionId, token);
            if (tableId.Length > 0 && !probe.TableExists)
            {
                dropped.Add($"configTarget.tableId:{tableId}");
                tableId = string.Empty;
                fieldId = string.Empty;
            }

            if (fieldId.Length > 0 && !probe.FieldExists)
            {
                dropped.Add($"configTarget.fieldId:{fieldId}");
                fieldId = string.Empty;
            }

            if (actionId is not null && !probe.ActionExists)
            {
                dropped.Add($"configTarget.actionId:{actionId}");
                actionId = null;
            }
        }

        if (tableId.Length == 0 && fieldId.Length == 0 && actionId is null && effectKey.Length == 0)
        {
            return null;
        }

        return new SituationConfigTarget(
            surface,
            tableId.Length == 0 ? null : tableId,
            fieldId.Length == 0 ? null : fieldId,
            actionId,
            effectKey.Length == 0 ? null : effectKey);
    }
}
