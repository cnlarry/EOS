using System.Text;
using System.Text.Json;
using EOS.API.Data;
using EOS.API.Models;
using EOS.API.Security;

namespace EOS.API.Features.Assistant.Tools;

/// <summary>
/// get_module_flow: module approval definition + instance + record action state.
/// Answers "where does this document go next" and "can it be approved now".
/// Cross-module upstream/downstream relations are not registered anywhere in the
/// system, so the tool reports them as unknown instead of guessing.
/// </summary>
public sealed class GetModuleFlowTool(
    IWorkbenchSearchGateway gateway,
    IModuleFlowGateway flows,
    IPermissionService permissions) : AssistantToolBase
{
    public const string ToolName = "get_module_flow";

    public override string Name => ToolName;
    public override AssistantToolRisk Risk => AssistantToolRisk.Read;
    public override string Description =>
        "查询某模块的审批流程定义、指定单据的流转状态与当前可执行动作。不带单据主键时只返回流程定义。";
    public override string ParametersJson => """
        {
          "type": "object",
          "properties": {
            "module_title": { "type": "string", "description": "模块中文名关键字；与 module_id 二选一" },
            "module_id": { "type": "integer", "description": "模块 ID" },
            "_keys": { "type": "array", "items": { "type": "string" }, "description": "可选：单据主键值数组（来自 search_records）" }
          }
        }
        """;

    public override async Task<ToolExecutionResult> ExecuteAsync(string userId, JsonElement arguments, CancellationToken token)
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
        if (!permission.CanBrowse)
        {
            return this.DenyBrowse($"#{moduleId}");
        }

        var scope = permission.Rights;
        var definition = await gateway.GetDefinitionAsync(moduleId, userId, scope.ExecuteTag,
            scope.CanViewCost, scope.CanViewSecrecy, scope.DeniedMasterFields, scope.DeniedDetailFields, token);
        if (definition is null)
        {
            return ToolExecutionResult.Deny($"模块 #{moduleId} 不是可查询的通用工作台模块。");
        }

        var flow = await flows.GetFlowDefinitionAsync(moduleId, token);
        var keys = ReadKeys(arguments);
        if (keys is null)
        {
            return ToolExecutionResult.Deny("参数不完整：_keys 须为非空主键值数组。");
        }

        if (keys.Count == 0)
        {
            return ToolExecutionResult.Success(CompressDefinition(definition, flow));
        }

        if (keys.Count != definition.MasterPkOrder.Count)
        {
            return ToolExecutionResult.Deny(
                $"主键长度不符：该模块主键为 {definition.MasterPkOrder.Count} 段（{string.Join("/", definition.MasterPkOrder)}）。");
        }

        var keyCondition = ControlledSprocInvoker.BuildKeyCondition(definition.MasterPkOrder, keys);
        var instance = await flows.GetInstanceAsync(moduleId, keyCondition, token);
        var record = await flows.GetRecordStateAsync(
            definition.MasterTable, definition.MasterPkOrder, keys, token);
        if (!record.Found)
        {
            return ToolExecutionResult.Deny("记录不存在或不在你的数据范围内。");
        }

        var confirm = await flows.GetApprovalConfirmAsync(moduleId, keyCondition, token);
        // 上下游边按目标模块逐个复核 CanBrowse：无权限的目标边整条隐藏。
        var visibleEdges = new List<(FlowEdge Edge, bool Outgoing)>();
        foreach (var edge in ModuleFlowEdges.Outgoing(moduleId))
        {
            if ((await permissions.GetAsync(userId, edge.ToModule, token)).CanBrowse)
            {
                visibleEdges.Add((edge, true));
            }
        }

        foreach (var edge in ModuleFlowEdges.Incoming(moduleId))
        {
            if ((await permissions.GetAsync(userId, edge.FromModule, token)).CanBrowse)
            {
                visibleEdges.Add((edge, false));
            }
        }

        return ToolExecutionResult.Success(
            CompressRecord(definition, flow, keys, instance, record, userId, permission, confirm, visibleEdges));
    }

    private static IReadOnlyList<string>? ReadKeys(JsonElement arguments)
    {
        if (!arguments.TryGetProperty("_keys", out var keysEl)) return [];
        if (keysEl.ValueKind != JsonValueKind.Array) return null;
        return keysEl.EnumerateArray()
            .Select(element => element.ValueKind == JsonValueKind.String ? element.GetString() ?? string.Empty : string.Empty)
            .ToArray();
    }

    internal static string CompressDefinition(WorkbenchDefinition definition, FlowDefinitionInfo? flow)
    {
        var sb = new StringBuilder($"module={definition.ModuleId}({definition.Title}) 流程定义：");
        if (flow is null)
        {
            sb.Append("未配置审批流程（直接批核模型）。");
            return sb.ToString();
        }

        sb.Append($"{flow.FlowName}，共 {flow.Steps.Count} 步：");
        foreach (var step in flow.Steps)
        {
            sb.AppendLine().Append($"- {step.SortNo} {step.Desc} [{string.Join("/", step.People)}]");
            if (step.IsSign) sb.Append($"（会签 {step.PassPercent}%）");
            if (step.MustSigners.Length > 0) sb.Append($"（必签 {string.Join("/", step.MustSigners)}）");
            if (step.HasExecCondition) sb.Append("（有执行条件）");
        }

        return sb.ToString().TrimEnd();
    }

    internal sealed record FlowActionInput(
        bool CanApprove, bool CanEdit, bool CanDelete, bool CanEndCase, bool CanUnEndCase,
        string UserId, bool HasFlow, string? InstanceState, bool IsStarter, bool IsCurrentApprover,
        bool Confirmed, bool Finished);

    internal static IReadOnlyList<(string Action, bool Allowed, string Reason)> EvaluateActions(FlowActionInput input)
    {
        var actions = new List<(string, bool, string)>();
        var inProgress = input.InstanceState == "0";
        if (input.Finished)
        {
            actions.Add(("结案", false, "单据已结案"));
            actions.Add(("取消结案", input.CanUnEndCase, input.CanUnEndCase ? "可取消结案后继续处理" : "无取消结案权限"));
            actions.Add(("批核", false, "单据已结案，先取消结案"));
            actions.Add(("编辑", false, "单据已结案，先取消结案"));
            actions.Add(("删除", false, "单据已结案，先取消结案"));
            return actions;
        }

        if (input.HasFlow)
        {
            if (inProgress)
            {
                actions.Add(("审批", input.CanApprove && input.IsCurrentApprover,
                    input.IsCurrentApprover
                        ? (input.CanApprove ? "你是当前待办人" : "无批核权限")
                        : "当前待办人不是你"));
                actions.Add(("撤回", input.IsStarter && !input.Confirmed,
                    input.IsStarter ? "你是发起人且单据未确认" : "只有发起人可撤回在途流程"));
                actions.Add(("编辑", false, "流程在途，须先撤回"));
                actions.Add(("删除", false, "流程在途，须先撤回"));
            }
            else
            {
                actions.Add(("送审", input.CanApprove && !input.Confirmed,
                    input.Confirmed ? "单据已批核" : (input.CanApprove ? "可发起审批" : "无批核权限")));
                actions.Add(("编辑", input.CanEdit && !input.Confirmed,
                    input.Confirmed ? "单据已批核" : (input.CanEdit ? "可编辑" : "无编辑权限")));
                actions.Add(("删除", input.CanDelete && !input.Confirmed,
                    input.Confirmed ? "单据已批核，先解批" : (input.CanDelete ? "可删除" : "无删除权限")));
            }

            actions.Add(("解批", false, "流程模块无直接解批动作，按审批链流转"));
        }
        else
        {
            actions.Add(("批核", input.CanApprove && !input.Confirmed,
                input.Confirmed ? "单据已批核" : (input.CanApprove ? "可直接批核" : "无批核权限")));
            actions.Add(("解批", input.Confirmed && input.CanApprove,
                input.Confirmed ? (input.CanApprove ? "可解批" : "无批核权限") : "单据未批核"));
            actions.Add(("编辑", input.CanEdit && !input.Confirmed,
                input.Confirmed ? "单据已批核，先解批" : (input.CanEdit ? "可编辑" : "无编辑权限")));
            actions.Add(("删除", input.CanDelete && !input.Confirmed,
                input.Confirmed ? "单据已批核，先解批" : (input.CanDelete ? "可删除" : "无删除权限")));
        }

        actions.Add(("结案", input.CanEndCase, input.CanEndCase ? "可结案" : "无结案权限"));
        return actions;
    }

    internal static string CompressRecord(
        WorkbenchDefinition definition, FlowDefinitionInfo? flow, IReadOnlyList<string> keys,
        FlowInstanceInfo? instance, RecordStateInfo record, string userId, ModulePermission permission,
        ApprovalConfirmInfo? confirm = null,
        IReadOnlyList<(FlowEdge Edge, bool Outgoing)>? edges = null)
    {
        var sb = new StringBuilder($"module={definition.ModuleId}({definition.Title})");
        sb.AppendLine().Append("单据主键：").Append(string.Join("/", keys));
        sb.AppendLine().Append("流程：").Append(flow is null ? "未配置（直接批核模型）" : $"{flow.FlowName}（{flow.Steps.Count} 步）");
        if (instance is null)
        {
            sb.AppendLine().Append("流转：无流程实例");
        }
        else
        {
            var stateText = instance.State switch
            {
                "0" => "在途",
                "1" => "已完成",
                "2" => "已撤回",
                _ => $"未知状态（{instance.State}）",
            };
            sb.AppendLine().Append($"流转：{stateText}");
            if (instance.State == "0" && instance.CurrentStepNo is not null)
            {
                sb.Append($"（步骤 {instance.CurrentStepNo} {instance.CurrentStepDesc}，待办 {string.Join("/", instance.CurrentApprovers)}）");
            }
        }

        sb.AppendLine().Append("单据标记：").Append(record.Confirmed ? "已批核" : "未批核")
            .Append(record.Finished ? "、已结案" : "、未结案");
        if (confirm is not null && confirm.Description.Length > 0)
        {
            sb.AppendLine().Append("终审确认：").Append(confirm.Description)
                .Append("（").Append(string.IsNullOrWhiteSpace(confirm.FinishedBy) ? "完成人未留名" : confirm.FinishedBy).Append('）');
        }
        var input = new FlowActionInput(
            permission.CanApprove, permission.CanEdit, permission.CanDelete,
            permission.CanEndCase, permission.CanUnEndCase, userId, flow is not null,
            instance?.State,
            instance is not null && string.Equals(instance.StartUser, userId, StringComparison.OrdinalIgnoreCase),
            instance is not null && instance.CurrentApprovers.Contains(userId, StringComparer.OrdinalIgnoreCase),
            record.Confirmed, record.Finished);
        sb.AppendLine().Append("可执行动作：");
        foreach (var (action, allowed, reason) in EvaluateActions(input))
        {
            sb.AppendLine().Append($"- {action}：{(allowed ? "可" : "否")}（{reason}）");
        }

        var visible = edges ?? [];
        if (visible.Count == 0)
        {
            sb.AppendLine().Append("上下游：已核对的关系边中暂无本模块，不推测。");
        }
        else
        {
            sb.AppendLine().Append("上下游（仅已核对登记的边）：");
            foreach (var (edge, outgoing) in visible)
            {
                sb.AppendLine().Append(outgoing
                    ? $"- 本模块 --{edge.Action}--> #{edge.ToModule}（{edge.Note}）"
                    : $"- #{edge.FromModule} --{edge.Action}--> 本模块（{edge.Note}）");
            }
        }

        return sb.ToString().TrimEnd();
    }
}
