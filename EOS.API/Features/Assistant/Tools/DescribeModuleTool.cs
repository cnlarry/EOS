using System.Text;
using System.Text.Json;
using EOS.API.Models;
using EOS.API.Security;

using EOS.API.Features.Assistant.ModelAccess;
using EOS.API.Features.Assistant.Parameters;

namespace EOS.API.Features.Assistant.Tools;

/// <summary>Describes a browsable module from its permission-filtered workbench definition.</summary>
public sealed class DescribeModuleTool(
    IWorkbenchSearchGateway gateway,
    IPermissionService permissions,
    IAssistantRuntimeConfig? runtime = null) : AssistantToolBase
{
    public const string ToolName = "describe_module";

    private AssistantToolLimitsOptions Limits => runtime?.Current.Policy.ToolLimits ?? new();

    public override string Name => ToolName;
    public override AssistantToolRisk Risk => AssistantToolRisk.Read;
    public override string Description =>
        "查询一个可浏览 ERP 模块的主表、明细表及当前用户可见字段结构。模块编号和中文名二选一。";
    public override string ParametersJson => """
        {
          "type": "object",
          "properties": {
            "module_id": { "type": "integer", "description": "模块 ID" },
            "module_title": { "type": "string", "description": "模块中文名关键字" }
          }
        }
        """;

    public override async Task<ToolExecutionResult> ExecuteAsync(string userId, JsonElement arguments, CancellationToken token)
    {
        var moduleId = arguments.GetIntArg("module_id");
        if (moduleId <= 0)
        {
            var title = arguments.GetStringArg("module_title");
            if (!string.IsNullOrWhiteSpace(title))
                moduleId = await gateway.FindGenericModuleIdByTitleAsync(title.Trim(), token) ?? 0;
        }

        if (moduleId <= 0)
            return ToolExecutionResult.Deny("未找到匹配的 ERP 模块，请提供准确的模块编号或名称。");

        var permission = await permissions.GetAsync(userId, moduleId, token);
        if (!permission.CanBrowse)
            return this.DenyBrowse($"#{moduleId}");

        var scope = permission.Rights;
        var definition = await gateway.GetDefinitionAsync(moduleId, userId, scope.ExecuteTag,
            scope.CanViewCost, scope.CanViewSecrecy, scope.DeniedMasterFields, scope.DeniedDetailFields, token);
        if (definition is null)
            return ToolExecutionResult.Deny($"模块 #{moduleId} 不是可查询的通用工作台模块。");

        return ToolExecutionResult.Success(Compress(definition, Limits));
    }

    internal static string Compress(WorkbenchDefinition definition, AssistantToolLimitsOptions? limits = null)
    {
        limits ??= new AssistantToolLimitsOptions();
        var sb = new StringBuilder($"模块 #{definition.ModuleId} {definition.Title}");
        sb.AppendLine().Append("- 主表：").Append(definition.MasterTable);
        if (!string.IsNullOrWhiteSpace(definition.DetailTable))
            sb.AppendLine().Append("- 明细表：").Append(definition.DetailTable);

        AppendFields(sb, "主表字段", definition.MasterFields, limits);
        if (definition.DetailFields.Count > 0)
            AppendFields(sb, "明细字段", definition.DetailFields, limits);
        return sb.ToString().TrimEnd();
    }

    private static void AppendFields(
        StringBuilder sb, string label, IReadOnlyList<WorkbenchField> fields,
        AssistantToolLimitsOptions limits)
    {
        var maxFields = limits.DescribeMaxFields;
        sb.AppendLine().Append(label).Append("（").Append(fields.Count).Append("）：");
        foreach (var field in fields.Take(maxFields))
        {
            sb.AppendLine().Append("- ").Append(field.Key).Append(' ').Append(field.Label)
                .Append(" [").Append(field.DataType).Append(']');
            if (field.IsPrimaryKey) sb.Append(" PK");
            if (field.BrowseModuleId is not null) sb.Append(" chooser→").Append(field.BrowseModuleId.Value);
        }

        if (fields.Count > maxFields)
            sb.AppendLine().Append($"（其余 {fields.Count - maxFields} 个字段未展示）");
    }
}
