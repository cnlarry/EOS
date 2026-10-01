using System.Text;
using System.Text.Json;
using EOS.API.Data;
using EOS.API.Models;
using EOS.API.Security;

using EOS.API.Features.Assistant.ModelAccess;
using EOS.API.Features.Assistant.Parameters;

namespace EOS.API.Features.Assistant.Tools;

/// <summary>
/// get_record_detail：按主键值数组精确取一行完整详情。
/// 走「导出所选」同一路径（GetExportRowsByKeysAsync），列集合遵循用户选择列 +
/// 成本/保密/禁止字段过滤，DATA_FILTER/EXEC_TAG 范围外的行不可见（查不到即返回未找到）。
/// </summary>
public sealed class GetRecordDetailTool(
    IWorkbenchSearchGateway gateway,
    IPermissionService permissions,
    IAssistantRuntimeConfig? runtime = null) : IAssistantTool
{
    public const string ToolName = "get_record_detail";

    private AssistantToolLimitsOptions Limits => runtime?.Current.Policy.ToolLimits ?? new();

    public string Name => ToolName;

    public AssistantToolRisk Risk => AssistantToolRisk.Read;

    public string Description =>
        "按主键取单条记录的完整字段。module_id 与 _keys 必须来自 search_records 的返回结果，不要臆造。";

    public string ParametersJson => """
        {
          "type": "object",
          "properties": {
            "module_id": { "type": "integer", "description": "search_records 返回的 module 编号" },
            "_keys": { "type": "array", "items": { "type": "string" }, "description": "search_records 每行返回的主键值数组" }
          },
          "required": ["module_id", "_keys"]
        }
        """;

    public async Task<ToolExecutionResult> ExecuteAsync(string userId, JsonElement arguments, CancellationToken token)
    {
        int moduleId = arguments.GetIntArg("module_id");
        IReadOnlyList<string> keys = [];
        if (arguments.TryGetProperty("_keys", out var keysEl) && keysEl.ValueKind == JsonValueKind.Array)
        {
            keys = keysEl.EnumerateArray()
                .Where(k => k.ValueKind == JsonValueKind.String)
                .Select(k => k.GetString() ?? string.Empty)
                .ToArray();
        }

        if (moduleId <= 0 || keys.Count == 0 || keys.Any(string.IsNullOrEmpty))
        {
            return ToolExecutionResult.Deny("参数不完整：需要 module_id 与非空的 _keys 主键值数组。");
        }

        var permission = await permissions.GetAsync(userId, moduleId, token);
        if (permission is null || !permission.CanBrowse)
        {
            return this.DenyBrowse($"#{moduleId}");
        }

        var (execTag, canViewCost, canViewSecrecy, deniedMaster, deniedDetail) = permission.Scope();
        var definition = await gateway.GetDefinitionAsync(
            moduleId, userId, execTag, canViewCost, canViewSecrecy, deniedMaster, deniedDetail, token);
        if (definition is null)
        {
            return ToolExecutionResult.Deny($"模块 #{moduleId} 不是可查询的通用工作台模块。");
        }

        if (keys.Count != definition.MasterPkOrder.Count)
        {
            return ToolExecutionResult.Deny(
                $"主键长度不符：该模块主键为 {definition.MasterPkOrder.Count} 段（{string.Join("/", definition.MasterPkOrder)}）。");
        }

        var rows = await gateway.GetExportRowsByKeysAsync(
            definition, [keys], token, dataFilter: permission.Rights.DataFilter);
        var row = rows.FirstOrDefault();
        if (row is null)
        {
            // DATA_FILTER/EXEC_TAG 范围外的记录在此表现为“不存在”——不泄露其存在性。
            return this.DenyNotFound();
        }

        return ToolExecutionResult.Success(Compress(definition, row, Limits));
    }

    /// <summary>输出全部可见列（限列数与值长），含字段标签；空值跳过。</summary>
    /// <param name="limits">输出上限；为 null 时用参数默认值（单测直接调用时不必造参数对象）。</param>
    internal static string Compress(
        WorkbenchDefinition definition, Dictionary<string, object?> row,
        AssistantToolLimitsOptions? limits = null)
    {
        limits ??= new AssistantToolLimitsOptions();
        var sb = new StringBuilder($"module={definition.ModuleId}({definition.Title}) detail:");
        sb.AppendLine();
        int taken = 0;
        foreach (var field in definition.MasterFields)
        {
            if (taken >= limits.DetailMaxColumns) break;
            if (!row.TryGetValue(field.Key, out var value)) continue;
            var text = value?.ToString();
            if (string.IsNullOrEmpty(text)) continue;
            sb.Append("- ").Append(field.Label).Append('=')
                .Append(text.Length > limits.DetailMaxValueLength
                    ? text[..limits.DetailMaxValueLength] + "…"
                    : text);
            sb.AppendLine();
            taken++;
        }

        if (taken == 0)
        {
            sb.AppendLine("(所有字段均为空)");
        }

        return sb.ToString().TrimEnd();
    }
}
