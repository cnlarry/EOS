using System.Text;
using System.Text.Json;
using EOS.API.Data;
using EOS.API.Security;

namespace EOS.API.Features.Assistant.Tools;

/// <summary>
/// get_form_schema：返回某模块统一表单的可录入字段清单（key/标签/类型/必填/下拉选项）。
/// 供 draft_record 前确认字段 Key 与取值范围，也可独立回答「XX 单有哪些必填项」。
/// </summary>
public sealed class GetFormSchemaTool(
    IWorkbenchSearchGateway gateway,
    IPermissionService permissions) : AssistantToolBase
{
    public const string ToolName = "get_form_schema";

    public override string Name => ToolName;

    public override AssistantToolRisk Risk => AssistantToolRisk.Read;

    public override string Description =>
        "查询某模块录入表单的字段清单（字段Key、标签、类型、是否必填、可选值）。"
        + "在生成单据草稿（draft_record）前必须先调用本工具获取合法字段Key。";

    public override string ParametersJson => """
        {
          "type": "object",
          "properties": {
            "module_title": { "type": "string", "description": "模块中文名关键字；与 module_id 二选一" },
            "module_id": { "type": "integer", "description": "模块 ID" }
          }
        }
        """;

    public override async Task<ToolExecutionResult> ExecuteAsync(string userId, JsonElement arguments, CancellationToken token)
    {
        var (definition, permission) = await ResolveModuleAsync(userId, arguments, token);
        if (definition is null) return ToolExecutionResult.Deny("未找到匹配的 ERP 模块或用户无浏览权限。");

        var (execTag, canViewCost, canViewSecrecy, deniedMaster, deniedDetail) = permission!.Scope();
        var form = await gateway.GetFormDefinitionAsync(
            definition, userId, "new", canViewCost, canViewSecrecy,
            deniedMaster, deniedDetail, deniedMaster, deniedDetail, deniedMaster, deniedDetail,
            token);
        if (form is null)
        {
            return ToolExecutionResult.Deny($"模块 #{definition.ModuleId} 未配置统一表单，无法生成录入草稿。");
        }

        return ToolExecutionResult.Success(Compress(form));
    }

    /// <summary>模块解析 + 权限门（与 SearchRecordsTool 同口径）。</summary>
    internal async Task<(Data.WorkbenchDefinition? Definition, ModulePermission? Permission)> ResolveModuleAsync(
        string userId, JsonElement arguments, CancellationToken token)
    {
        var title = arguments.GetStringArg("module_title");
        int moduleId = arguments.GetIntArg("module_id");
        if (moduleId <= 0 && !string.IsNullOrWhiteSpace(title))
        {
            moduleId = await gateway.FindGenericModuleIdByTitleAsync(title.Trim(), token) ?? 0;
        }

        if (moduleId <= 0) return (null, null);

        var permission = await permissions.GetAsync(userId, moduleId, token);
        if (permission is null || !permission.CanBrowse) return (null, null);

        var (execTag, canViewCost, canViewSecrecy, deniedMaster, deniedDetail) = permission.Scope();
        var definition = await gateway.GetDefinitionAsync(
            moduleId, userId, execTag, canViewCost, canViewSecrecy, deniedMaster, deniedDetail, token);
        return (definition, permission);
    }

    /// <summary>只列可写字段：可见 + 非只读 + 非主键/自增/服务端维护/虚拟。</summary>
    internal static string Compress(FormDefinition form)
    {
        var sb = new StringBuilder($"module={form.ModuleId}({form.Title}) writable-fields:");
        sb.AppendLine();
        foreach (var field in form.MasterFields)
        {
            if (!IsWritable(field)) continue;
            sb.Append("- ").Append(field.Key).Append(" = ").Append(field.Label)
                .Append("（").Append(field.DataType);
            if (field.IsRequired) sb.Append(",必填");
            if (field.Options is { Count: > 0 })
            {
                sb.Append(",可选值:").Append(string.Join('/', field.Options.Select(o => $"{o.Value}={o.Label}")));
            }

            sb.Append('）');
            sb.AppendLine();
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>可写判定（draft_record 与 schema 输出共用同一口径）。</summary>
    internal static bool IsWritable(FormFieldDefinition field) =>
        field.IsVisible
        && !field.IsReadonly
        && !field.IsPrimaryKey
        && !field.IsAutoIncrement
        && !field.ServerFilled
        && !field.IsVirtual;
}
