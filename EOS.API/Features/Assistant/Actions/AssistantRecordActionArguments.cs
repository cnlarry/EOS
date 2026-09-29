using System.Text.Json;

using EOS.API.Features.Assistant.Governance;
using EOS.API.Features.Assistant.Tools;

namespace EOS.API.Features.Assistant.Actions;

/// <summary>
/// 动作工具的参数契约（新增 / 修改 / 删除共用一份）。
///
/// <para>
/// 这里**没有幂等键字段**，也不接受模型传入任何键：键由服务端从工具调用身份推导，
/// 否则模型每次重试都能造出一个新键，幂等保护等于不存在。
/// </para>
/// </summary>
public static class AssistantRecordActionArguments
{
    /// <summary>一次调用最多处理的行数：不设上限等于允许一句"全删了"。取值来自阈值单一事实源。</summary>
    public const int MaxRows = AssistantActionLimits.MaxRowsPerAction;

    /// <summary>两个动作工具共用的 JSON Schema。</summary>
    public const string ParametersJson = """
        {
          "type": "object",
          "properties": {
            "module_id": { "type": "integer", "description": "模块 ID；与 module_title 二选一" },
            "module_title": { "type": "string", "description": "模块中文名关键字；与 module_id 二选一" },
            "action": { "type": "string", "enum": ["insert", "update", "delete"], "description": "动作：新增 / 修改 / 删除" },
            "rows": {
              "type": "array",
              "description": "逐行处理的目标：修改与删除只需 keys，新增只需 values",
              "items": {
                "type": "object",
                "properties": {
                  "keys": { "type": "array", "items": { "type": "string" }, "description": "单据主键值，顺序与模块主键一致；缺省时按当前页面处境推断" },
                  "values": { "type": "object", "description": "主表字段名 → 值（字符串）" },
                  "details": { "type": "array", "items": { "type": "object" }, "description": "明细行，每行是字段名 → 值" },
                  "detail_serials": { "type": "array", "items": { "type": "string" }, "description": "明细各行原有项次，按行对齐；新行给空" }
                }
              }
            }
          },
          "required": ["action", "rows"]
        }
        """;

    /// <summary>解析参数；失败时返回 null 并给出可直接回喂模型的原因。</summary>
    public static AssistantActionRequest? TryParse(JsonElement arguments, out string error)
    {
        error = string.Empty;
        if (!AssistantRecordActionNames.TryParse(arguments.GetStringArg("action"), out var kind))
        {
            error = "参数 action 必须是 insert、update 或 delete。";
            return null;
        }

        if (!arguments.TryGetProperty("rows", out var rowsElement) || rowsElement.ValueKind != JsonValueKind.Array)
        {
            error = "参数 rows 必须是数组，且至少一行。";
            return null;
        }

        var rows = new List<AssistantActionRow>();
        foreach (var item in rowsElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                error = "rows 的每一项都必须是对象。";
                return null;
            }
            rows.Add(new AssistantActionRow(
                ReadKeys(item),
                ReadValues(item, "values"),
                ReadDetailRows(item),
                ReadStringList(item, "detail_serials")));
        }

        if (rows.Count == 0)
        {
            error = "rows 不能为空。";
            return null;
        }
        if (rows.Count > MaxRows)
        {
            error = $"一次最多处理 {MaxRows} 行（本次 {rows.Count} 行），请分批提交。";
            return null;
        }

        return new AssistantActionRequest(arguments.GetIntArg("module_id"), kind, rows);
    }

    private static IReadOnlyList<string> ReadKeys(JsonElement row)
        => ReadStringList(row, "keys") ?? [];

    private static IReadOnlyList<IReadOnlyDictionary<string, string?>>? ReadDetailRows(JsonElement row)
    {
        if (!row.TryGetProperty("details", out var details) || details.ValueKind != JsonValueKind.Array)
        {
            return null;
        }
        var rows = new List<IReadOnlyDictionary<string, string?>>();
        foreach (var item in details.EnumerateArray())
        {
            rows.Add(item.ValueKind == JsonValueKind.Object ? ReadValues(item) : new Dictionary<string, string?>());
        }
        return rows.Count == 0 ? null : rows;
    }

    private static IReadOnlyDictionary<string, string?>? ReadValues(JsonElement element, string name)
        => element.TryGetProperty(name, out var values) && values.ValueKind == JsonValueKind.Object
            ? ReadValues(values)
            : null;

    private static IReadOnlyDictionary<string, string?> ReadValues(JsonElement obj)
    {
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in obj.EnumerateObject())
        {
            values[property.Name] = property.Value.ValueKind switch
            {
                JsonValueKind.String => property.Value.GetString(),
                JsonValueKind.Null or JsonValueKind.Undefined => null,
                JsonValueKind.True => "1",
                JsonValueKind.False => "0",
                // 数字按原文提交：模型不必猜精度，精度由服务端按字段类型转换。
                _ => property.Value.GetRawText(),
            };
        }
        return values;
    }

    private static IReadOnlyList<string>? ReadStringList(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var array) || array.ValueKind != JsonValueKind.Array)
        {
            return null;
        }
        return [.. array.EnumerateArray().Select(item => item.ValueKind == JsonValueKind.String
            ? item.GetString() ?? string.Empty
            : item.ValueKind == JsonValueKind.Null ? string.Empty : item.GetRawText())];
    }
}
