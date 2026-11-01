using System.Text;
using System.Text.Json;
using EOS.API.Data;
using EOS.API.Features.Assistant.ModelAccess;
using EOS.API.Features.Assistant.Parameters;
using EOS.API.Models;
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
    IPermissionService permissions,
    // 行数 / 列数 / 单值长度取自参数目录的 TOOL_LIMIT 域；离线构造不传时退回默认值。
    IAssistantRuntimeConfig? runtime = null) : IAssistantTool
{
    public const string ToolName = "search_records";

    private AssistantToolLimitsOptions Limits => runtime?.Current.Policy.ToolLimits ?? new();

    public string Name => ToolName;

    public AssistantToolRisk Risk => AssistantToolRisk.Read;

    public string Description =>
        "在 ERP 模块中搜索单据/资料列表。可按关键字、结构化条件（字段/算子/值）、日期/批核状态/金额区间筛选，"
        + "并可指定排序与翻页。返回行及每行主键值数组 _keys，可用 get_record_detail 取单行完整详情（含明细行）。";

    public string ParametersJson => """
        {
          "type": "object",
          "properties": {
            "module_title": { "type": "string", "description": "模块中文名关键字，如「客户订单」「送货单」；与 module_id 二选一" },
            "module_id": { "type": "integer", "description": "模块 ID；与 module_title 二选一" },
            "keyword": { "type": "string", "description": "可选过滤关键字（单号/名称等），在文本字段中模糊匹配" },
            "conditions": {
              "type": "array",
              "description": "结构化条件（可选）。字段必须来自模块可见字段；多条默认 AND，可用 logic=or 连接。",
              "items": {
                "type": "object",
                "properties": {
                  "field": { "type": "string", "description": "字段 Key，如 APPROVED、ORDER_DATE" },
                  "operator": { "type": "string", "enum": ["eq", "ne", "gt", "gte", "lt", "lte", "contains", "notcontains", "startswith", "endswith", "empty", "notempty", "between"] },
                  "value": { "type": "string", "description": "比较值；空值类算子可省略" },
                  "value_to": { "type": "string", "description": "between 的结束值" },
                  "logic": { "type": "string", "enum": ["and", "or"], "description": "与前一条的连接词，默认 and" }
                },
                "required": ["field", "operator"]
              }
            },
            "status": { "type": "string", "enum": ["approved", "unapproved", "finished", "unfinished"], "description": "批核/结案状态快捷筛选（可选）" },
            "date_from": { "type": "string", "description": "业务日期起（yyyy-MM-dd，可选）" },
            "date_to": { "type": "string", "description": "业务日期止（yyyy-MM-dd，可选）" },
            "amount_min": { "type": "string", "description": "金额下限（可选）" },
            "amount_max": { "type": "string", "description": "金额上限（可选）" },
            "sort_field": { "type": "string", "description": "排序字段 Key（可选，必须是可见字段）" },
            "sort_direction": { "type": "string", "enum": ["asc", "desc"], "description": "排序方向，默认 asc" },
            "page": { "type": "integer", "description": "页码（从 1 开始，默认 1）" }
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

        AssistantFilterResult filter;
        try
        {
            filter = AssistantQueryBuilder.Build(
                definition,
                arguments.GetStringArg("date_from"),
                arguments.GetStringArg("date_to"),
                arguments.GetStringArg("status"),
                arguments.GetStringArg("amount_min"),
                arguments.GetStringArg("amount_max"),
                ReadFilters(arguments));
        }
        catch (ArgumentException ex)
        {
            return ToolExecutionResult.Deny(ex.Message);
        }

        var sortRequested = arguments.GetStringArg("sort_field");
        var (sortField, sortDirection) = AssistantQueryBuilder.ResolveSort(
            definition, sortRequested, arguments.GetStringArg("sort_direction"));
        var sortNote = !string.IsNullOrWhiteSpace(sortRequested) && sortField is null
            ? $"排序字段「{sortRequested}」不在可见字段内，未应用排序。"
            : null;

        var page = arguments.GetIntArg("page");
        if (page <= 0) page = 1;

        var data = await gateway.GetRowsAsync(
            definition, detail: false, new Dictionary<string, string>(), page,
            pageSize: Limits.SearchMaxRows, token, keyword: keywordArg,
            query: filter.Query,
            sortField: sortField, sortDirection: sortDirection,
            dataFilter: permission.Rights.DataFilter);

        return ToolExecutionResult.Success(
            Compress(definition, data, keywordArg, Limits, filter.NotApplied, sortNote));
    }

    /// <summary>解析 conditions 数组；字段/算子白名单由 <see cref="AssistantQueryBuilder"/> 统一执行。</summary>
    internal static IReadOnlyList<AssistantQueryBuilder.Filter> ReadFilters(JsonElement arguments)
    {
        if (!arguments.TryGetProperty("conditions", out var element) || element.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var filters = new List<AssistantQueryBuilder.Filter>();
        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            var field = item.TryGetProperty("field", out var f) && f.ValueKind == JsonValueKind.String
                ? f.GetString() : null;
            var op = item.TryGetProperty("operator", out var o) && o.ValueKind == JsonValueKind.String
                ? o.GetString() : null;
            if (string.IsNullOrWhiteSpace(field) || string.IsNullOrWhiteSpace(op)) continue;
            filters.Add(new AssistantQueryBuilder.Filter(
                field!,
                op!,
                item.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null,
                item.TryGetProperty("value_to", out var vt) && vt.ValueKind == JsonValueKind.String ? vt.GetString() : null,
                item.TryGetProperty("logic", out var l) && l.ValueKind == JsonValueKind.String ? l.GetString() : null));
        }

        return filters;
    }

    /// <summary>
    /// 压缩为紧凑文本：每行 = 主键值数组 + 前 N 个非空列「标签:值」（截断防 token 爆炸）。
    /// 列集合来自权限过滤后的 definition.MasterFields——成本/保密/禁止字段根本不在其中。
    /// </summary>
    /// <param name="limits">
    /// 输出上限（行 / 列 / 单值长度）。为 null 时用参数默认值——单测直接调本方法时不必造参数对象；
    /// 生产路径由 <c>ExecuteAsync</c> 传当轮的运行期取值。
    /// </param>
    internal static string Compress(
        WorkbenchDefinition definition, WorkbenchData data, string? keyword,
        AssistantToolLimitsOptions? limits = null,
        IReadOnlyList<string>? notApplied = null, string? sortNote = null)
    {
        limits ??= new AssistantToolLimitsOptions();
        var sb = new StringBuilder();
        sb.Append($"module={definition.ModuleId}({definition.Title}) total={data.Total}")
            .Append($" page={data.Page} shown={data.Items.Count}");
        if (!string.IsNullOrEmpty(keyword))
        {
            sb.Append($" keyword=\"{keyword}\"");
        }

        sb.AppendLine();
        if (notApplied is { Count: > 0 })
        {
            sb.AppendLine($"未应用的条件：{string.Join("、", notApplied)}（字段或算子不在白名单内，已忽略）。");
        }

        if (!string.IsNullOrEmpty(sortNote))
        {
            sb.AppendLine(sortNote);
        }
        foreach (var row in data.Items)
        {
            var keys = definition.MasterPkOrder
                .Select(key => row.TryGetValue(key, out var kv) ? kv?.ToString() ?? string.Empty : string.Empty)
                .ToArray();
            sb.Append("- _keys=").Append(JsonSerializer.Serialize(keys));
            sb.Append(" _view=").Append(AssistantRecordLinks.View(definition.ModuleId, keys));
            if (definition.HasEdit)
            {
                sb.Append(" _edit=").Append(AssistantRecordLinks.Edit(definition.ModuleId, keys));
            }
            int taken = 0;
            foreach (var field in definition.MasterFields)
            {
                if (taken >= limits.SearchMaxColumns) break;
                if (!row.TryGetValue(field.Key, out var value)) continue;
                var text = value?.ToString();
                if (string.IsNullOrEmpty(text)) continue;
                sb.Append(' ').Append(field.Label).Append('=')
                    .Append(text.Length > limits.SearchMaxValueLength
                        ? text[..limits.SearchMaxValueLength] + "…"
                        : text);
                taken++;
            }

            sb.AppendLine();
        }

        var shownUpTo = (data.Page - 1) * data.PageSize + data.Items.Count;
        if (data.Total > shownUpTo)
        {
            sb.Append($"（本页显示 {data.Items.Count} 条，累计到第 {shownUpTo} 条，共 {data.Total} 条命中；")
                .Append($"可传 page={data.Page + 1} 继续，或加更精确的条件缩小范围）");
        }

        return sb.ToString().TrimEnd();
    }
}
