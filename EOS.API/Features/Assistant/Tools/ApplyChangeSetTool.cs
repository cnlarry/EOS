using System.Text;
using System.Text.Json;
using EOS.API.Features.Assistant.Admin;
using EOS.API.Security;

namespace EOS.API.Features.Assistant.Tools;

/// <summary>
/// apply_changeset (AdminWrite): trial-only metadata changeset preview.
/// The tool NEVER executes: it validates the changeset and returns a structured
/// diff card; execution happens only after the user presses confirm in the card,
/// which calls the dedicated endpoint. Natural-language approval cannot execute.
/// </summary>
public sealed class ApplyChangeSetTool(
    ChangeSetService changeSets,
    IPermissionService permissions) : AssistantSchemaToolBase(permissions)
{
    public const string ToolName = "apply_changeset";

    public override string Name => ToolName;
    public override AssistantToolRisk Risk => AssistantToolRisk.AdminWrite;
    public override string Description =>
        "试算元数据变更集（只预览不执行：登记表元数据/批量生成字段元数据）。结果以确认卡展示，用户点确认后才执行。";
    public override string ParametersJson => """
        {
          "type": "object",
          "properties": {
            "changeset": {
              "type": "object",
              "description": "变更集：{version:1, goal, tables:[{table, action:register_table|add_fields, description?, kind?, fields?}]}"
            }
          },
          "required": ["changeset"]
        }
        """;

    public override async Task<ToolExecutionResult> ExecuteAsync(string userId, JsonElement arguments, CancellationToken token)
    {
        var denied = await RequireSetupAsync(userId, token);
        if (denied is not null) return denied;
        if (!arguments.TryGetProperty("changeset", out var changeset) || changeset.ValueKind != JsonValueKind.Object)
        {
            return ToolExecutionResult.Deny("缺少变更集对象（changeset）。");
        }

        var trial = await changeSets.TrialAsync(changeset, token);
        var draft = new
        {
            kind = "admin-changeset",
            goal = trial.Goal,
            blocked = trial.Blocked,
            changeset,
            tables = trial.Tables.Select(table => new
            {
                table = table.Table,
                action = table.Action,
                status = table.Status,
                wouldCreate = table.WouldCreate,
                wouldSkip = table.WouldSkip,
                errors = table.Errors,
            }),
            errors = trial.Errors,
        };
        var sb = new StringBuilder(trial.Blocked
            ? $"变更集试算未通过（{trial.Goal}），已拦截，不会执行："
            : $"变更集试算通过（{trial.Goal}），待你在确认卡上确认后执行：");
        foreach (var table in trial.Tables)
        {
            sb.AppendLine().Append($"- {table.Table} [{table.Action}] {table.Status}");
            foreach (var line in table.WouldCreate) sb.AppendLine().Append($"  新建：{line}");
            foreach (var line in table.WouldSkip) sb.AppendLine().Append($"  跳过：{line}");
            foreach (var line in table.Errors) sb.AppendLine().Append($"  错误：{line}");
        }

        return new ToolExecutionResult(!trial.Blocked, sb.ToString().TrimEnd(), draft);
    }
}
