using System.Text.Json;
using EOS.API.Features.Assistant.Tools;

namespace EOS.API.Features.Assistant.Config;

/// <summary>
/// 配置写工具的参数契约（克隆 / 预演 / 应用共用一份：界面与模型提交同一种形状）。
///
/// <para>
/// 这里**没有幂等键字段**，也没有"要写入的值"字段。前者由服务端从工具调用身份推导；
/// 后者由服务端按源与目标现场算出来，请求只能决定"对哪两个对象做比对"与"要应用哪些项"。
/// 键与值都不进请求体，模型就既无从伪造，也无从绕过。
/// </para>
/// </summary>
public static class AssistantConfigArguments
{
    public const string ParametersJson = """
        {
          "type": "object",
          "properties": {
            "surface": { "type": "string", "enum": ["fields", "datasource", "buttons", "effect"],
                         "description": "配置面：字段 / 数据来源 / 自定义按钮 / 效果键" },
            "source_table": { "type": "string", "description": "字段与数据来源面：源表 T_ID" },
            "target_table": { "type": "string", "description": "字段与数据来源面：目标表 T_ID" },
            "source_module_id": { "type": "integer", "description": "按钮与效果面：源模块 ID" },
            "target_module_id": { "type": "integer", "description": "按钮与效果面：目标模块 ID" },
            "objects": { "type": "array", "items": { "type": "string" },
                         "description": "点名要照抄的对象：字段面是字段名，动作面是效果键；缺省为该面上的全部对象" },
            "record_key": { "type": "array", "items": { "type": "string" },
                            "description": "预演用的真实单据主键（需要预演时给出；缺省则该类改动无法预演）" },
            "event": { "type": "string", "description": "预演的事件（仅批核生效 APPROVE_EFFECT 与解批 DEAPPROVE 可预演）" },
            "items": { "type": "array", "items": { "type": "string" },
                       "description": "要应用的改动项 id，来自对照卡；应用时必填" }
          },
          "required": ["surface"]
        }
        """;

    /// <summary>解析参数；失败时返回 null 并给出可直接回喂模型的原因。</summary>
    public static ConfigCloneRequest? TryParse(JsonElement arguments, out string error)
    {
        error = string.Empty;
        if (!ConfigSurfaceNames.TryParse(arguments.GetStringArg("surface"), out var surface))
        {
            error = "参数 surface 必须是 fields、datasource、buttons 或 effect。";
            return null;
        }
        var objects = ReadStringList(arguments, "objects");
        if (objects is { Count: > ConfigClonePlanner.MaxObjects })
        {
            error = $"一次最多照抄 {ConfigClonePlanner.MaxObjects} 个对象（本次 {objects.Count} 个），请分批提交。";
            return null;
        }
        return new ConfigCloneRequest(
            surface,
            NullIfBlank(arguments.GetStringArg("source_table")),
            NullIfBlank(arguments.GetStringArg("target_table")),
            arguments.GetIntArg("source_module_id") is var sourceModule && sourceModule > 0 ? sourceModule : null,
            arguments.GetIntArg("target_module_id") is var targetModule && targetModule > 0 ? targetModule : null,
            objects,
            ReadStringList(arguments, "record_key"),
            NullIfBlank(arguments.GetStringArg("event")));
    }

    /// <summary>要应用的改动项 id（来自对照卡）。</summary>
    public static IReadOnlyList<string>? ReadItems(JsonElement arguments) => ReadStringList(arguments, "items");

    private static IReadOnlyList<string>? ReadStringList(JsonElement arguments, string name)
    {
        if (arguments.ValueKind != JsonValueKind.Object
            || !arguments.TryGetProperty(name, out var array) || array.ValueKind != JsonValueKind.Array)
        {
            return null;
        }
        var values = array.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString()?.Trim() ?? string.Empty)
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return values.Count == 0 ? null : values;
    }

    private static string? NullIfBlank(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
