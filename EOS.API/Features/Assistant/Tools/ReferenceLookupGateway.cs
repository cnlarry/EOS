using EOS.API.Data;
using EOS.API.Data.Workbench;
using EOS.API.Models;

namespace EOS.API.Features.Assistant.Tools;

/// <summary>一次引用字段候选查询的结果；<see cref="Code"/> 非空表示服务端拒绝及其原因。</summary>
public sealed record ReferenceLookup(
    string ModuleTitle,
    string FieldLabel,
    IReadOnlyList<FormChooserColumn> Columns,
    IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows,
    int Total,
    string? ValueColumn = null,
    IReadOnlyList<ChooserReturnItem>? ReturnItems = null,
    string? Code = null,
    string? Message = null)
{
    public bool IsDenied => Code is not null;
}

/// <summary>
/// 引用字段候选查询的窄接口。实现走统一选择器同一条服务端解析链
/// （<see cref="WorkbenchAccessPolicy"/> → 字段来源 → <see cref="WorkbenchChooserService"/>），
/// 权限、数据范围与过滤结构都不在助手侧复制。
/// </summary>
public interface IReferenceLookupGateway
{
    Task<ReferenceLookup?> LookupAsync(
        string userId, int moduleId, string fieldKey, int? serialNo, string? keyword,
        int maxRows, CancellationToken token);
}

/// <summary>
/// 默认实现：与 <c>GET document-workbench/{moduleId}/form-chooser/{fieldKey}</c> 同一套编排。
///
/// <para>
/// 这里**不重新判定权限**：先由 <see cref="WorkbenchAccessPolicy.AuthorizeFormAsync"/> 拿到
/// 表单访问权，来源模块再走 <see cref="WorkbenchAccessPolicy.GetRightsAsync"/>；过滤结构与回填映射
/// 只在服务端解析（不从表单 DTO 读）。任一环节 fail-closed 时返回拒绝原因，不返回空列表冒充"没有候选"。
/// </para>
/// </summary>
public sealed class ReferenceLookupGateway(
    WorkbenchAccessPolicy policy,
    WorkbenchChooserService chooser,
    ILogger<ReferenceLookupGateway> logger) : IReferenceLookupGateway
{
    public async Task<ReferenceLookup?> LookupAsync(
        string userId, int moduleId, string fieldKey, int? serialNo, string? keyword,
        int maxRows, CancellationToken token)
    {
        var access = await policy.AuthorizeFormAsync(userId, moduleId, "new", token);
        if (!access.Allowed)
        {
            access = await policy.AuthorizeFormAsync(userId, moduleId, "edit", token);
        }

        if (!access.Allowed || access.Value is null)
        {
            return null;
        }

        var (definition, form, rights) = (access.Value.Definition, access.Value.Form, access.Value.Rights);
        var field = form.MasterFields.Concat(form.DetailFields)
            .FirstOrDefault(item => item.Key.Equals(fieldKey, StringComparison.OrdinalIgnoreCase));
        if (field is null)
        {
            return Deny(definition.Title, fieldKey, "FIELD_NOT_FOUND", $"模块 #{moduleId} 的表单里没有字段 {fieldKey}。");
        }

        var source = await chooser.GetChooserSourceAsync(
            definition.MasterTable, definition.DetailTable, fieldKey, serialNo, token);
        if (source is null || !source.Active || string.IsNullOrWhiteSpace(source.Table))
        {
            return Deny(definition.Title, field.Label, "CHOOSER_SOURCE_NOT_FOUND",
                $"字段 {field.Label}({fieldKey}) 没有启用的选择器来源。");
        }

        if (source.Filter is null)
        {
            // 与 form-chooser 同一口径：NULL 表示存量条件待重建，fail-closed 而不是退化成"无过滤"。
            logger.LogWarning("助手引用解析：选择器过滤条件待重建 module={ModuleId} field={Field}", moduleId, fieldKey);
            return Deny(definition.Title, field.Label, "CHOOSER_FILTER_PENDING",
                $"字段 {field.Label} 的选择器过滤条件尚未重建，暂不能解析候选。");
        }

        if (!ChooserFilterStruct.TryParse(source.Filter, out var filterStruct) || filterStruct is null)
        {
            return Deny(definition.Title, field.Label, "CHOOSER_FILTER_INVALID",
                $"字段 {field.Label} 的选择器过滤条件不是合法的结构化 JSON。");
        }

        var returnItems = ChooserReturnItems.Parse(source.ReturnMapping);
        if (returnItems is null)
        {
            return Deny(definition.Title, field.Label, "CHOOSER_RETURN_INVALID",
                $"字段 {field.Label} 的选择器回填映射不是合法的 JSON 数组。");
        }

        var chooserRights = rights;
        if (source.ModuleId is int sourceModuleId && sourceModuleId > 0)
        {
            var moduleRights = await policy.GetRightsAsync(userId, sourceModuleId, token);
            if (moduleRights is not null)
            {
                chooserRights = moduleRights;
            }
        }

        var result = await chooser.GetChooserOptionsAsync(
            source.Table, keyword?.Trim(), filterField: null, returnItems,
            masterValues: null, detailValues: null, conditions: null,
            chooserRights.CanViewCost, chooserRights.CanViewSecrecy, chooserRights.DeniedMasterFields,
            chooserRights.DataFilter, filterStruct, sortField: null, sortDirection: null,
            page: 1, pageSize: maxRows, chooserRights.ExecuteTag,
            source.ModuleId ?? moduleId, moduleId, userId, token);
        if (result is null)
        {
            return Deny(definition.Title, field.Label, "CHOOSER_UNAVAILABLE",
                $"字段 {field.Label} 的选择器来源当前不可用。");
        }

        // 回填值由 RETURN_ITEMS 决定，不是"第一列"——目标的字段键优先，否则取第一个映射。
        var valueColumn = returnItems
            .FirstOrDefault(item => item.Target.Equals(fieldKey, StringComparison.OrdinalIgnoreCase))?.Column
            ?? returnItems.FirstOrDefault()?.Column;

        return new ReferenceLookup(
            definition.Title, field.Label, result.Columns, result.Rows, result.Total, valueColumn, returnItems);
    }

    private static ReferenceLookup Deny(string moduleTitle, string fieldLabel, string code, string message) =>
        new(moduleTitle, fieldLabel, [], [], 0, null, null, code, message);
}
