using System.Text;
using System.Text.Json;
using EOS.API.Data;
using EOS.API.Security;

namespace EOS.API.Features.Assistant.Tools;

/// <summary>
/// search_records：按模块（标题或 ID）+ 关键字搜索单据列表。
/// 数据路径与工作台列表完全同源：IPermissionService 权限门 → GetDefinitionAsync
/// （EXEC_TAG/成本/保密/禁止字段过滤）→ GetRowsAsync（FILTER/DATA_FILTER 范围内），
/// 只读、限 5 行，行附主键值数组（_keys）供 get_record_detail 精确定位。
/// </summary>
public sealed class SearchRecordsTool(
    IWorkbenchSearchGateway gateway,
    IPermissionService permissions) : IAssistantTool
{
    public const string ToolName = "search_records";

    private const int MaxRows = 5;

    private const int MaxColumnsPerRow = 8;

    private const int MaxValueLength = 40;

    public string Name => ToolName;

    public string Description =>
        "在 ERP 模块中搜索单据/资料列表。当用户想找单据、查资料时使用。"
        + "返回前 5 行及每行主键值数组 _keys，可用 get_record_detail 取单行完整详情。";

    public string ParametersJson => """
        {
          "type": "object",
          "properties": {
            "module_title": { "type": "string", "description": "模块中文名关键字，如「客户订单」「送货单」；与 module_id 二选一" },
            "module_id": { "type": "integer", "description": "模块 ID；与 module_title 二选一" },
            "keyword": { "type": "string", "description": "可选过滤关键字（单号/名称等）" }
          }
        }
        """;

    public async Task<ToolExecutionResult> ExecuteAsync(string userId, JsonElement arguments, CancellationToken token)
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

        var (execTag, canViewCost, canViewSecrecy, deniedMaster, deniedDetail) = permission.Scope();
        var definition = await gateway.GetDefinitionAsync(
            moduleId, userId, execTag, canViewCost, canViewSecrecy, deniedMaster, deniedDetail, token);
        if (definition is null)
        {
            return ToolExecutionResult.Deny($"模块 #{moduleId} 不是可查询的通用工作台模块。");
        }

        var keyword = arguments.GetStringArg("keyword");
        var keywordArg = string.IsNullOrWhiteSpace(keyword) ? null : keyword.Trim();
        var data = await gateway.GetRowsAsync(
            definition, detail: false, new Dictionary<string, string>(), page: 1,
            pageSize: MaxRows, token, keyword: keywordArg,
            dataFilter: permission.Rights.DataFilter);

        return ToolExecutionResult.Success(Compress(definition, data, keywordArg));
    }

    /// <summary>
    /// 压缩为紧凑文本：每行 = 主键值数组 + 前 N 个非空列「标签:值」（截断防 token 爆炸）。
    /// 列集合来自权限过滤后的 definition.MasterFields——成本/保密/禁止字段根本不在其中。
    /// </summary>
    internal static string Compress(WorkbenchDefinition definition, WorkbenchData data, string? keyword)
    {
        var sb = new StringBuilder();
        sb.Append($"module={definition.ModuleId}({definition.Title}) total={data.Total} shown={data.Rows.Count}");
        if (!string.IsNullOrEmpty(keyword))
        {
            sb.Append($" keyword=\"{keyword}\"");
        }

        sb.AppendLine();
        foreach (var row in data.Rows)
        {
            var keys = definition.MasterPkOrder
                .Select(key => row.TryGetValue(key, out var kv) ? kv?.ToString() ?? string.Empty : string.Empty)
                .ToArray();
            sb.Append("- _keys=").Append(JsonSerializer.Serialize(keys));
            int taken = 0;
            foreach (var field in definition.MasterFields)
            {
                if (taken >= MaxColumnsPerRow) break;
                if (!row.TryGetValue(field.Key, out var value)) continue;
                var text = value?.ToString();
                if (string.IsNullOrEmpty(text)) continue;
                sb.Append(' ').Append(field.Label).Append('=')
                    .Append(text.Length > MaxValueLength ? text[..MaxValueLength] + "…" : text);
                taken++;
            }

            sb.AppendLine();
        }

        if (data.Total > data.Rows.Count)
        {
            sb.Append($"（仅显示前 {data.Rows.Count} 条，共 {data.Total} 条命中；可加更精确的 keyword 缩小范围）");
        }

        return sb.ToString().TrimEnd();
    }
}
