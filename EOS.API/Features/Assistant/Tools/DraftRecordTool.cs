using System.Text;
using System.Text.Json;
using EOS.API.Data;
using EOS.API.Models;
using EOS.API.Security;

using EOS.API.Features.Assistant.ModelAccess;
using EOS.API.Features.Assistant.Parameters;

namespace EOS.API.Features.Assistant.Tools;

/// <summary>Document draft sent to the frontend (structured change set). The frontend confirms it and executes through the existing save pipeline; the assistant adds no write path.</summary>
public sealed record AssistantFormDraft(
    int ModuleId,
    string ModuleTitle,
    IReadOnlyDictionary<string, string> Values,
    IReadOnlyDictionary<string, string> Labels,
    IReadOnlyList<string> MissingRequired,
    IReadOnlyList<string> UnknownKeys,
    IReadOnlyList<string> Warnings);

/// <summary>
/// draft_record（DRAFT 级）：按模块生成「录入草稿」——只产出结构化变更集供用户在表单中确认，
/// 本工具不写库。values 的 Key 必须来自 get_form_schema；未知 Key 剔除、必填缺失警告、
/// 数值/日期做类型粗验。真正的写入由用户在前端确认后走现有统一表单保存管线
/// （幂等键 + 服务端必填/长度校验 + 审计全部复用）。
/// </summary>
public sealed class DraftRecordTool(
    IWorkbenchSearchGateway gateway,
    IPermissionService permissions,
    IAssistantRuntimeConfig? runtime = null) : AssistantToolBase
{
    public const string ToolName = "draft_record";

    private AssistantToolLimitsOptions Limits => runtime?.Current.Policy.ToolLimits ?? new();

    public override string Name => ToolName;

    public override AssistantToolRisk Risk => AssistantToolRisk.Draft;

    public override string Description =>
        "为用户新建单据生成录入草稿（不直接保存）。必须先调 get_form_schema 拿到合法字段Key再调用本工具；"
        + "引用类字段（客户/厂商等）的值应为编码 ID，可先用 search_records 在对应基础资料模块查询取得。"
        + "草稿将展示给用户确认，由用户带入表单完成录入。";

    public override string ParametersJson => """
        {
          "type": "object",
          "properties": {
            "module_id": { "type": "integer", "description": "目标模块 ID" },
            "values": {
              "type": "object",
              "description": "字段Key → 字符串值 的映射；Key 必须来自 get_form_schema 返回",
              "additionalProperties": { "type": "string" }
            }
          },
          "required": ["module_id", "values"]
        }
        """;

    public override async Task<ToolExecutionResult> ExecuteAsync(string userId, JsonElement arguments, CancellationToken token)
    {
        int moduleId = arguments.GetIntArg("module_id");
        if (moduleId <= 0)
        {
            return ToolExecutionResult.Deny("缺少有效的 module_id。请先用 get_form_schema 确认模块。");
        }

        if (!arguments.TryGetProperty("values", out var valuesEl) || valuesEl.ValueKind != JsonValueKind.Object)
        {
            return ToolExecutionResult.Deny("缺少 values 对象（字段Key→值的映射）。");
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
            return ToolExecutionResult.Deny($"模块 #{moduleId} 不是支持统一表单的工作台模块。");
        }

        var form = await gateway.GetFormDefinitionAsync(
            definition, userId, "new", canViewCost, canViewSecrecy,
            deniedMaster, deniedDetail, deniedMaster, deniedDetail, deniedMaster, deniedDetail,
            token, canAddNew: permission.CanAddNew);
        if (form is null)
        {
            return ToolExecutionResult.Deny($"模块 #{definition.ModuleId} 未配置统一表单，无法生成录入草稿。");
        }

        // —— 校验与规范化：白名单剔除 / 类型粗验 / 必填缺失 / 长度截断 ——
        var writable = form.MasterFields.Where(GetFormSchemaTool.IsWritable).ToDictionary(f => f.Key);
        var values = new Dictionary<string, string>();
        var unknownKeys = new List<string>();
        var warnings = new List<string>();

        foreach (var property in valuesEl.EnumerateObject())
        {
            if (!writable.TryGetValue(property.Name, out var field))
            {
                unknownKeys.Add(property.Name);
                continue;
            }

            if (property.Value.ValueKind != JsonValueKind.String)
            {
                warnings.Add($"字段 {field.Label}({field.Key}) 的值必须是字符串，已忽略非字符串输入。");
                continue;
            }

            var raw = property.Value.GetString() ?? string.Empty;
            if (raw.Length > Limits.DraftMaxValueLength)
            {
                raw = raw[..Limits.DraftMaxValueLength];
                warnings.Add(
                    $"字段 {field.Label}({field.Key}) 的值过长，已截断到 {Limits.DraftMaxValueLength} 字符。");
            }

            if (!ValidateType(field, raw, out var typeWarning))
            {
                warnings.Add(typeWarning);
                continue;
            }

            values[field.Key] = raw;
        }

        var missingRequired = writable.Values
            .Where(f => f.IsRequired
                && !values.ContainsKey(f.Key)
                && string.IsNullOrEmpty(f.DefaultValue))
            .Select(f => $"{f.Label}({f.Key})")
            .ToList();

        if (unknownKeys.Count > 0)
        {
            warnings.Add($"以下字段Key不在该模块表单中，已剔除：{string.Join("、", unknownKeys)}。"
                + "请以 get_form_schema 返回为准。");
        }

        if (!form.HasAdd || !permission.CanAddNew)
        {
            return ToolExecutionResult.Deny("该模块不支持新增或你没有新增权限，不能生成录入草稿。");
        }

        var missingNote = missingRequired.Count > 0
            ? $"缺必填：{string.Join("、", missingRequired)}。"
            : string.Empty;
        var content = $"已生成「{form.Title}」录入草稿（{values.Count} 个字段）。{missingNote}"
            + "草稿已展示给用户确认，不会自动保存。";

        var draft = new AssistantFormDraft(
            form.ModuleId, form.Title,
            new Dictionary<string, string>(values),
            values.Keys.ToDictionary(k => k, k => writable[k].Label),
            missingRequired, unknownKeys, warnings);

        return new ToolExecutionResult(true, content, draft);
    }

    /// <summary>类型粗验：数值/日期解析失败即剔除并提示（最终仍由保存管线权威校验）。</summary>
    internal static bool ValidateType(FormFieldDefinition field, string value, out string warning)
    {
        warning = string.Empty;
        if (string.IsNullOrEmpty(value)) return true;
        var dataType = field.DataType.ToLowerInvariant();
        if (dataType is "decimal" or "numeric" or "float" or "money" || dataType.Contains("int"))
        {
            if (!decimal.TryParse(value, out _))
            {
                warning = $"字段 {field.Label}({field.Key}) 需要数值，已忽略无法解析的值「{value}」。";
                return false;
            }
        }
        else if (dataType.Contains("date"))
        {
            if (!DateTime.TryParse(value, out _))
            {
                warning = $"字段 {field.Label}({field.Key}) 需要日期，已忽略无法解析的值「{value}」。";
                return false;
            }
        }

        return true;
    }
}
