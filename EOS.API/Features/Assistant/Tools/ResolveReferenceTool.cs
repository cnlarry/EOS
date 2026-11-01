using System.Text;
using System.Text.Json;
using EOS.API.Features.Assistant.ModelAccess;
using EOS.API.Features.Assistant.Parameters;

namespace EOS.API.Features.Assistant.Tools;

/// <summary>
/// resolve_reference：把一个引用字段（客户/供应商/物料/员工…）的关键字解析成可填写的值。
///
/// <para>
/// 为什么不复用 search_records：引用字段的目标模块与过滤条件由字段的
/// <c>FIELD_DATASOURCE</c> 决定，不是"用户猜一个模块名再搜"。本工具走统一选择器同一条服务端解析链，
/// 因此权限、数据范围、过滤结构、显示列都与界面里的选择器一致；返回的"值"就是回填到该字段的编码。
/// </para>
/// </summary>
public sealed class ResolveReferenceTool(
    IReferenceLookupGateway lookup,
    IWorkbenchSearchGateway gateway,
    IAssistantRuntimeConfig? runtime = null) : AssistantToolBase
{
    public const string ToolName = "resolve_reference";

    private AssistantToolLimitsOptions Limits => runtime?.Current.Policy.ToolLimits ?? new();

    public override string Name => ToolName;

    public override AssistantToolRisk Risk => AssistantToolRisk.Read;

    public override string Description =>
        "解析引用字段的候选值：给模块、字段 Key 和关键字（如客户名称），返回可回填的编码/名称候选。"
        + "在 draft_record 或修改单据前，用本工具把用户说的名称变成编码，不要猜编码。";

    public override string ParametersJson => """
        {
          "type": "object",
          "properties": {
            "module_id": { "type": "integer", "description": "模块 ID；与 module_title 二选一" },
            "module_title": { "type": "string", "description": "模块中文名关键字；与 module_id 二选一" },
            "field_key": { "type": "string", "description": "引用字段的 Key（来自 get_form_schema，如 CLIENT_ID）" },
            "keyword": { "type": "string", "description": "候选关键字（名称/编码的一部分）" },
            "serial_no": { "type": "integer", "description": "可选：同一字段有多个选择器来源时指定来源序号" }
          },
          "required": ["field_key"]
        }
        """;

    public override async Task<ToolExecutionResult> ExecuteAsync(
        string userId, JsonElement arguments, CancellationToken token)
    {
        var moduleId = arguments.GetIntArg("module_id");
        if (moduleId <= 0)
        {
            var title = arguments.GetStringArg("module_title");
            if (!string.IsNullOrWhiteSpace(title))
            {
                moduleId = await gateway.FindGenericModuleIdByTitleAsync(title.Trim(), token) ?? 0;
            }
        }

        if (moduleId <= 0)
        {
            return ToolExecutionResult.Deny("未找到匹配的 ERP 模块。请向用户确认准确的模块名称或给出 module_id。");
        }

        var fieldKey = arguments.GetStringArg("field_key")?.Trim();
        if (string.IsNullOrWhiteSpace(fieldKey))
        {
            return ToolExecutionResult.Deny("缺少 field_key（引用字段的 Key）。");
        }

        var serialNo = arguments.GetIntArg("serial_no");
        var result = await lookup.LookupAsync(
            userId, moduleId, fieldKey, serialNo > 0 ? serialNo : null,
            arguments.GetStringArg("keyword"), Limits.ReferenceMaxRows, token);
        if (result is null)
        {
            return this.DenyBrowse($"#{moduleId}");
        }

        if (result.IsDenied)
        {
            return ToolExecutionResult.Deny($"{result.Code}：{result.Message}");
        }

        return ToolExecutionResult.Success(Compress(result, Limits));
    }

    /// <summary>
    /// 输出"值 + 其余列"的紧凑文本。第一列按选择器的列顺序取——它就是回填该字段的值
    /// （与前端选择器双击回填同一份列定义），不让模型自己挑一列猜。
    /// </summary>
    internal static string Compress(ReferenceLookup result, AssistantToolLimitsOptions? limits = null)
    {
        limits ??= new AssistantToolLimitsOptions();
        var sb = new StringBuilder(
            $"module={result.ModuleTitle} field={result.FieldLabel} total={result.Total}");
        if (result.Columns.Count > 0)
        {
            sb.Append(" columns=").Append(string.Join('/', result.Columns.Select(column => $"{column.Key}:{column.Label}")));
        }

        sb.AppendLine();
        if (result.ReturnItems is { Count: > 0 })
        {
            sb.Append("回填映射：")
                .Append(string.Join('、', result.ReturnItems.Select(item => $"{item.Target}←{item.Column}")))
                .AppendLine();
        }
        if (result.Rows.Count == 0)
        {
            sb.Append("（没有匹配的候选；可换一个关键字或让用户补充名称）");
            return sb.ToString().TrimEnd();
        }

        foreach (var row in result.Rows)
        {
            sb.Append("- ");
            var valueKey = result.ValueColumn ?? result.Columns.FirstOrDefault()?.Key;
            if (valueKey is not null && row.TryGetValue(valueKey, out var value))
            {
                sb.Append("值=").Append(Format(value, limits.SearchMaxValueLength)).Append(' ');
            }

            int taken = 0;
            foreach (var column in result.Columns.Where(column =>
                         !column.Key.Equals(valueKey, StringComparison.OrdinalIgnoreCase)))
            {
                if (taken >= limits.SearchMaxColumns) break;
                if (!row.TryGetValue(column.Key, out var cell) || cell is null) continue;
                sb.Append(column.Label).Append('=').Append(Format(cell, limits.SearchMaxValueLength)).Append(' ');
                taken++;
            }

            sb.AppendLine();
        }

        if (result.Total > result.Rows.Count)
        {
            sb.Append($"（仅显示前 {result.Rows.Count} 条，共 {result.Total} 条；可加关键字缩小范围）");
        }

        return sb.ToString().TrimEnd();
    }

    private static string Format(object? value, int maxLength)
    {
        var text = value?.ToString() ?? string.Empty;
        return text.Length > maxLength ? text[..maxLength] + "…" : text;
    }
}
