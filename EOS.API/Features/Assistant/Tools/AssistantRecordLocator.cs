using System.Text.Json;
using EOS.API.Models;
using EOS.API.Security;

namespace EOS.API.Features.Assistant.Tools;

/// <summary>一次工具调用定位到的记录：模块、权限结果、已按权限过滤的定义、主键值。</summary>
public sealed record AssistantRecordTarget(
    int ModuleId,
    ModulePermission Permission,
    WorkbenchDefinition Definition,
    IReadOnlyList<string> Keys);

/// <summary>
/// 「把这次工具调用定位到一条记录」的**共用口径**：模块（ID 或中文名）→ 浏览权限 →
/// 定义（已按权限过滤字段）→ 主键（显式优先，其次页面处境）→ 数据范围可见性。
///
/// <para>
/// 单独立出来的理由：这套步骤此前散在各个记录级工具自己的实现里，每加一个记录级工具就再抄一遍。
/// 抄出来的第二份不会立刻出错，但它会在某一次口径调整（主键优先级、防探测文案、可见性校验的时机）
/// 时与第一份分道扬镳——而这类分道扬镳表现为"某个工具能查到、另一个查不到"。
/// </para>
///
/// <para>
/// **可见性校验不能省**：历史与附件本身没有字段级权限，但若不先用「按主键取行」证明这条记录
/// 在用户的数据范围内，就会泄露 <c>DATA_FILTER</c>/<c>EXEC_TAG</c> 之外记录的存在性。
/// 不可见一律按防探测口径回答"不存在或不在数据范围内"，不改口为"无权限"。
/// </para>
/// </summary>
public sealed class AssistantRecordLocator(
    IWorkbenchSearchGateway gateway,
    IPermissionService permissions)
{
    public async Task<(AssistantRecordTarget? Target, ToolExecutionResult? Deny)> LocateAsync(
        string userId, JsonElement arguments, PageContext? page, CancellationToken token)
    {
        var moduleId = arguments.GetIntArg("module_id");
        if (moduleId <= 0)
        {
            var title = arguments.GetStringArg("module_title").Trim();
            if (title.Length > 0)
            {
                moduleId = await gateway.FindGenericModuleIdByTitleAsync(title, token) ?? 0;
            }
        }

        if (moduleId <= 0)
        {
            return (null, ToolExecutionResult.Deny("未找到匹配的 ERP 模块。请向用户确认准确的模块名称。"));
        }

        var permission = await permissions.GetAsync(userId, moduleId, token);
        if (permission is null || !permission.CanBrowse)
        {
            return (null, AssistantToolExtensions.DenyBrowseFor($"#{moduleId}"));
        }

        var scope = permission.Scope();
        var definition = await gateway.GetDefinitionAsync(
            moduleId, userId, scope.ExecTag, scope.CanViewCost, scope.CanViewSecrecy,
            scope.DeniedMaster, scope.DeniedDetail, token);
        if (definition is null)
        {
            return (null, ToolExecutionResult.Deny($"模块 #{moduleId} 不是可查询的通用工作台模块。"));
        }

        var keys = ReadStringArray(arguments, "_keys");
        if (keys.Count == 0)
        {
            keys = InferKeysFromPage(page, definition);
        }

        if (keys.Count == 0)
        {
            return (null, ToolExecutionResult.Deny(
                "缺少单据主键：请提供 _keys，或先打开该单据（或在工作台列表里选中它）。"));
        }

        if (keys.Count != definition.MasterPkOrder.Count)
        {
            return (null, ToolExecutionResult.Deny(
                $"主键长度不符：该模块主键为 {definition.MasterPkOrder.Count} 段（{string.Join("/", definition.MasterPkOrder)}）。"));
        }

        var visible = await gateway.GetExportRowsByKeysAsync(
            definition, [keys], token, dataFilter: permission.Rights.DataFilter);
        if (visible.Count == 0)
        {
            return (null, ToolExecutionResult.Deny(AssistantToolExtensions.NotFoundMessage));
        }

        return (new AssistantRecordTarget(moduleId, permission, definition, keys), null);
    }

    /// <summary>
    /// 页面处境推断主键：列表里唯一的选中行优先（多段主键只有它能表达），其次是当前单号（单段主键）。
    /// 与既有记录级工具同一优先级——用户问"这张单"时不必报主键。
    /// </summary>
    private static IReadOnlyList<string> InferKeysFromPage(PageContext? page, WorkbenchDefinition definition)
    {
        if (page is null) return [];
        if (page.Selection is { Count: > 0 } selection && selection.Count == definition.MasterPkOrder.Count)
        {
            return [.. selection];
        }

        if (definition.MasterPkOrder.Count == 1 && !string.IsNullOrWhiteSpace(page.DocNo))
        {
            return [page.DocNo.Trim()];
        }

        return [];
    }

    private static IReadOnlyList<string> ReadStringArray(JsonElement arguments, string name)
    {
        if (arguments.ValueKind != JsonValueKind.Object
            || !arguments.TryGetProperty(name, out var element)
            || element.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var values = new List<string>();
        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String) continue;
            var value = item.GetString();
            if (!string.IsNullOrWhiteSpace(value)) values.Add(value.Trim());
        }

        return values;
    }
}
