using System.Text.Json;
using EOS.API.Features.Assistant.Diagnosis;
using EOS.API.Models;
using EOS.API.Security;

namespace EOS.API.Features.Assistant.Tools;

/// <summary>
/// diagnose_record：围绕**一条记录**回答"这张单为什么存不下去 / 改不了"，给出可核对的原因与下一步。
///
/// <para>
/// Read 级、只读。门禁是 <c>CanBrowse</c> + 数据范围（<c>EXEC_TAG</c> / <c>DATA_FILTER</c>）双门、fail-closed：
/// 探测"这东西存不存在"时权限不足一律按"不存在或不在你的数据范围内"回答（沿用既有防探测口径，不改口）；
/// 而"**我自己**为什么做不了这件事"必须明说（"你没有批核权限"不是泄露别人数据的存在性）。
/// </para>
/// <para>
/// 主键优先从页面处境推断（用户不必报主键）：显式 <c>_keys</c> 优先，其次当前页面开着的单据，再次列表里唯一的选中行。
/// </para>
/// </summary>
public sealed class DiagnoseRecordTool(
    IWorkbenchSearchGateway gateway,
    IPermissionService permissions,
    RecordDiagnosisService diagnosis) : AssistantToolBase, IPageContextTool
{
    public const string ToolName = "diagnose_record";

    private PageContext? _page;

    public override string Name => ToolName;

    public override AssistantToolRisk Risk => AssistantToolRisk.Read;

    public override string Description =>
        "诊断一张单据当前为什么存不下去 / 改不了：按优先级给出校验规则、字段保护、状态与权限的中断原因与下一步。只读，不改任何数据。";

    public override string ParametersJson => """
        {
          "type": "object",
          "properties": {
            "module_title": { "type": "string", "description": "模块中文名关键字；与 module_id 二选一" },
            "module_id": { "type": "integer", "description": "模块 ID" },
            "_keys": { "type": "array", "items": { "type": "string" }, "description": "可选：单据主键值数组；缺省时按当前页面处境推断" },
            "fields": { "type": "array", "items": { "type": "string" }, "description": "可选：用户正想改的字段名；缺省时按页面里的未保存改动推断" }
          }
        }
        """;

    /// <summary>服务端注入页面处境（单据号 / 未保存字段）：只作定位，不作权限依据。</summary>
    public void UsePageContext(PageContext page) => _page = page;

    public override async Task<ToolExecutionResult> ExecuteAsync(
        string userId, JsonElement arguments, CancellationToken token)
    {
        var title = arguments.GetStringArg("module_title");
        int moduleId = arguments.GetIntArg("module_id");
        if (moduleId <= 0 && !string.IsNullOrWhiteSpace(title))
        {
            moduleId = await gateway.FindGenericModuleIdByTitleAsync(title.Trim(), token) ?? 0;
        }

        if (moduleId <= 0)
        {
            return ToolExecutionResult.Deny("未找到匹配的 ERP 模块。请向用户确认准确的模块名称。");
        }

        var permission = await permissions.GetAsync(userId, moduleId, token);
        if (permission is null || !permission.CanBrowse)
        {
            return this.DenyBrowse($"#{moduleId}");
        }

        var scope = permission.Scope();
        var definition = await gateway.GetDefinitionAsync(
            moduleId, userId, scope.ExecTag, scope.CanViewCost, scope.CanViewSecrecy,
            scope.DeniedMaster, scope.DeniedDetail, token);
        if (definition is null)
        {
            return ToolExecutionResult.Deny($"模块 #{moduleId} 不是可查询的通用工作台模块。");
        }

        var keys = ReadKeys(arguments);
        if (keys is null)
        {
            return ToolExecutionResult.Deny("参数不完整：_keys 须为字符串数组。");
        }

        if (keys.Count == 0)
        {
            keys = InferKeysFromPage(definition);
        }

        if (keys.Count == 0)
        {
            return ToolExecutionResult.Deny(
                "缺少单据主键：请提供 _keys，或先打开该单据（或在工作台列表里选中它）。");
        }

        if (keys.Count != definition.MasterPkOrder.Count)
        {
            return ToolExecutionResult.Deny(
                $"主键长度不符：该模块主键为 {definition.MasterPkOrder.Count} 段（{string.Join("/", definition.MasterPkOrder)}）。");
        }

        var outcome = await diagnosis.DiagnoseAsync(
            userId, new DiagnosisContext(definition, permission, keys, ReadAttemptedFields(arguments)), token);
        if (outcome.Document is null)
        {
            // 防探测口径：不区分"不存在"与"不在数据范围内"（沿用既有 FormAccess → NotFound 的既有设计）。
            return this.DenyNotFound();
        }

        return ToolExecutionResult.Success(DiagnosisJson.Serialize(outcome.Document));
    }

    /// <summary>页面处境推断主键：当前单号（单段主键）优先，其次是列表里唯一的选中行。</summary>
    private IReadOnlyList<string> InferKeysFromPage(WorkbenchDefinition definition)
    {
        if (_page is null) return [];
        if (_page.Selection is { Count: > 0 } selection && selection.Count == definition.MasterPkOrder.Count)
        {
            return [.. selection];
        }

        if (definition.MasterPkOrder.Count == 1 && !string.IsNullOrWhiteSpace(_page.DocNo))
        {
            return [_page.DocNo.Trim()];
        }

        return [];
    }

    private IReadOnlyList<string> ReadAttemptedFields(JsonElement arguments)
    {
        var fields = ReadStringArray(arguments, "fields");
        if (fields.Count > 0) return fields;
        return _page?.FormDirty is { Count: > 0 } dirty
            ? [.. dirty.Select(field => field.Field ?? string.Empty).Where(name => name.Length > 0)]
            : [];
    }

    private static IReadOnlyList<string>? ReadKeys(JsonElement arguments) => ReadStringArrayOrNull(arguments, "_keys");

    private static IReadOnlyList<string> ReadStringArray(JsonElement arguments, string name) =>
        ReadStringArrayOrNull(arguments, name) ?? [];

    private static IReadOnlyList<string>? ReadStringArrayOrNull(JsonElement arguments, string name)
    {
        if (!arguments.TryGetProperty(name, out var element)) return [];
        if (element.ValueKind != JsonValueKind.Array) return null;
        return [.. element.EnumerateArray()
            .Select(item => item.ValueKind == JsonValueKind.String ? item.GetString() ?? string.Empty : string.Empty)];
    }
}
