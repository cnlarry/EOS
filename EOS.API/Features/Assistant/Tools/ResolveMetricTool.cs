using System.Text;
using System.Text.Json;
using EOS.API.Data;
using EOS.API.Features.Assistant.Metrics;
using EOS.API.Models;
using EOS.API.Security;

namespace EOS.API.Features.Assistant.Tools;

/// <summary>
/// resolve_metric：按系统口径计算度量值（只读）。执行链全程 fail-closed：
/// 口径必须已业务确认（CONFIRMED）、定义经受控 parser/validator 编译为参数化计划、
/// 来源表必须挂靠模块并注入该模块的 CanBrowse/EXEC_TAG/DATA_FILTER/字段过滤，
/// 任何环节失败都拒绝计算，绝不执行定义原文。返回结果并附依据链
/// （口径标识/版本/来源/维度/过滤摘要）供溯源。
/// </summary>
public sealed class ResolveMetricTool(
    IMetricRepository repository,
    IMetricExecutor executor,
    IMetricSchemaProbe schemaProbe,
    MetricDefinitionValidator validator,
    IPermissionService permissions,
    IWorkbenchSearchGateway gateway,
    WorkbenchScopeFilter scopeFilter) : AssistantToolBase
{
    public const string ToolName = "resolve_metric";
    private const string ConfirmedStatus = "CONFIRMED";

    public override string Name => ToolName;
    public override AssistantToolRisk Risk => AssistantToolRisk.Read;
    public override string Description =>
        "按系统确认的业务口径计算指标（如销售额、库存数量），可带维度过滤。"
        + "回答金额/数量/比率问题时必须使用本工具取数，禁止自行估算。"
        + "指标标识来自 enum_metrics；未确认的口径会明确拒绝。";

    public override string ParametersJson => """
        {
          "type": "object",
          "properties": {
            "metric_id": { "type": "string", "description": "口径标识（来自 enum_metrics），如 sales_amount" },
            "dimensions": {
              "type": "object",
              "description": "可选：维度过滤。键=维度名（来自口径的可用维度，如 CLIENT_ID）；值=数字/字符串（等值）或含 from/to 的对象（闭区间，如 {\"from\":\"2026-01-01\",\"to\":\"2026-03-31\"}）"
            }
          },
          "required": ["metric_id"]
        }
        """;

    public override async Task<ToolExecutionResult> ExecuteAsync(string userId, JsonElement arguments, CancellationToken token)
    {
        var metricId = arguments.GetStringArg("metric_id").Trim();
        if (metricId.Length == 0)
        {
            return ToolExecutionResult.Deny("缺少口径标识（metric_id）。");
        }

        var metric = await repository.GetAsync(metricId, token);
        if (metric is null)
        {
            return ToolExecutionResult.Deny($"该指标在系统内尚无定义：{metricId}。可先用 enum_metrics 查看现有口径。");
        }

        var parse = MetricExpressionParser.Parse(metric.Definition);
        if (!parse.Ok || parse.Expression is null)
        {
            return ToolExecutionResult.Deny($"口径 {metric.MetricId} 的定义无法按受控语法解析，拒绝计算。");
        }
        if (!string.Equals(metric.ConfirmStatus, ConfirmedStatus, StringComparison.OrdinalIgnoreCase))
        {
            return ToolExecutionResult.Deny($"口径 {metric.MetricId} 尚未完成业务确认，暂不能用于计算。");
        }

        var moduleIds = await repository.FindModuleIdsByTableAsync(metric.SourceTable, token);
        if (moduleIds.Count == 0)
        {
            return ToolExecutionResult.Deny($"口径 {metric.MetricId} 的来源表未关联任何模块，无法施加数据范围，拒绝计算。");
        }
        var moduleId = moduleIds[0];
        var permission = await permissions.GetAsync(userId, moduleId, token);
        if (!permission.CanBrowse)
        {
            return this.DenyBrowse($"#{moduleId}");
        }

        var definition = await gateway.GetDefinitionAsync(moduleId, userId,
            permission.Rights.ExecuteTag, permission.Rights.CanViewCost, permission.Rights.CanViewSecrecy,
            permission.Rights.DeniedMasterFields, permission.Rights.DeniedDetailFields, token);
        if (definition is null)
        {
            return ToolExecutionResult.Deny("无法在当前用户权限范围内构建该来源的数据定义，拒绝计算。");
        }

        var sourceIsMaster = string.Equals(definition.MasterTable, metric.SourceTable, StringComparison.OrdinalIgnoreCase);
        var sourceAllowed = (sourceIsMaster ? definition.MasterFields : definition.DetailFields)
            .Select(field => field.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (sourceAllowed.Count == 0)
        {
            return ToolExecutionResult.Deny("当前用户在该来源表上没有可见字段，拒绝计算。");
        }
        var masterAllowed = definition.MasterFields.Select(field => field.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var validation = await validator.ValidateAsync(new MetricValidationInput(
            parse.Expression, metric.SourceTable, sourceAllowed, metric.DimensionKeys,
            metric.RowFilter, sourceIsMaster ? null : definition.MasterTable,
            sourceIsMaster ? null : masterAllowed), token);
        if (!validation.Ok)
        {
            return ToolExecutionResult.Deny($"口径校验未通过，拒绝计算：{validation.Error}");
        }

        if (!scopeFilter.TryBuildRecordScopePredicate(definition, permission.Rights.DataFilter,
                out var scopePredicate, out var scopeValues))
        {
            return ToolExecutionResult.Deny("当前用户的权限数据范围尚不支持，已拒绝计算。");
        }

        var dimensions = await ParseDimensionsAsync(arguments, metric, definition,
            sourceIsMaster, sourceAllowed, masterAllowed, token);
        if (dimensions.Error is not null)
        {
            return ToolExecutionResult.Deny(dimensions.Error);
        }

        var masterJoinColumns = sourceIsMaster
            ? []
            : await ResolveMasterJoinColumnsAsync(definition, metric.SourceTable, token);
        if (!sourceIsMaster && masterJoinColumns.Count == 0)
        {
            return ToolExecutionResult.Deny("主表关联键在来源表中不存在，无法推导数据范围，拒绝计算。");
        }

        MetricPlan plan;
        try
        {
            plan = MetricPlanCompiler.Compile(parse.Expression, metric.SourceTable, validation.RowFilter,
                dimensions.Filters, sourceIsMaster ? null : definition.MasterTable,
                masterJoinColumns, scopePredicate, scopeValues);
        }
        catch (InvalidOperationException ex)
        {
            return ToolExecutionResult.Deny($"执行计划编译失败，拒绝计算：{ex.Message}");
        }

        var value = await executor.ExecuteAsync(plan, token);
        var summary = BuildEvidence(metric, definition, dimensions.Filters, validation.RowFilter,
            sourceIsMaster, scopePredicate, permission.Rights.DataFilter);
        if (value is null)
        {
            return ToolExecutionResult.Success($"口径 {metric.MetricId}（{metric.MetricName}）在指定范围内没有数据。\n{summary}");
        }
        return ToolExecutionResult.Success($"口径 {metric.MetricId}（{metric.MetricName}）计算结果：{value:0.####}\n{summary}");
    }

    private async Task<IReadOnlyList<string>> ResolveMasterJoinColumnsAsync(
        WorkbenchDefinition definition, string sourceTable, CancellationToken token)
    {
        var sourceColumns = await schemaProbe.GetColumnsAsync(sourceTable, token);
        return definition.MasterPkOrder
            .Where(pk => sourceColumns.Contains(pk))
            .ToList();
    }

    private async Task<(IReadOnlyList<MetricDimensionFilter> Filters, string? Error)> ParseDimensionsAsync(
        JsonElement arguments, MetricDefinitionRow metric, WorkbenchDefinition definition,
        bool sourceIsMaster, IReadOnlySet<string> sourceAllowed, IReadOnlySet<string> masterAllowed,
        CancellationToken token)
    {
        var filters = new List<MetricDimensionFilter>();
        if (!arguments.TryGetProperty("dimensions", out var dimensionsElement)
            || dimensionsElement.ValueKind != JsonValueKind.Object)
        {
            return (filters, null);
        }

        var declared = metric.DimensionKeys
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var sourceColumns = await schemaProbe.GetColumnsAsync(metric.SourceTable, token);
        IReadOnlySet<string>? masterColumns = null;
        if (!sourceIsMaster && definition.MasterTable is not null)
        {
            masterColumns = await schemaProbe.GetColumnsAsync(definition.MasterTable, token);
        }

        foreach (var property in dimensionsElement.EnumerateObject())
        {
            if (!System.Text.RegularExpressions.Regex.IsMatch(property.Name,
                    "^[A-Za-z_][A-Za-z0-9_]{0,127}$"))
            {
                return ([], $"维度名不符合标识符规则：{property.Name}");
            }
            if (!declared.Contains(property.Name))
            {
                return ([], $"维度不在该口径的可用维度内：{property.Name}");
            }

            var target = sourceColumns.Contains(property.Name)
                ? MetricDimensionTarget.Source
                : masterColumns?.Contains(property.Name) == true
                    ? MetricDimensionTarget.Master
                    : (MetricDimensionTarget?)null;
            if (target is null)
            {
                return ([], $"维度列物理上不存在：{property.Name}");
            }
            var allowed = target == MetricDimensionTarget.Source ? sourceAllowed : masterAllowed!;
            if (!allowed.Contains(property.Name))
            {
                return ([], $"维度列对当前用户不可见：{property.Name}");
            }

            switch (property.Value.ValueKind)
            {
                case JsonValueKind.Number:
                    filters.Add(new MetricDimensionFilter(property.Name.ToUpperInvariant(), target.Value,
                        MetricDimensionOperator.Equals, [property.Value.GetDecimal()]));
                    break;
                case JsonValueKind.String:
                    filters.Add(new MetricDimensionFilter(property.Name.ToUpperInvariant(), target.Value,
                        MetricDimensionOperator.Equals, [property.Value.GetString() ?? string.Empty]));
                    break;
                case JsonValueKind.Object:
                {
                    if (!property.Value.TryGetProperty("from", out var from)
                        || !property.Value.TryGetProperty("to", out var to)
                        || from.ValueKind is not (JsonValueKind.Number or JsonValueKind.String)
                        || to.ValueKind is not (JsonValueKind.Number or JsonValueKind.String))
                    {
                        return ([], $"区间维度必须同时提供 from 与 to（数字或字符串）：{property.Name}");
                    }
                    filters.Add(new MetricDimensionFilter(property.Name.ToUpperInvariant(), target.Value,
                        MetricDimensionOperator.Range,
                        [FromElement(from), FromElement(to)]));
                    break;
                }
                default:
                    return ([], $"维度值必须是数字、字符串或 from/to 区间：{property.Name}");
            }
        }
        return (filters, null);
    }

    private static object FromElement(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Number => element.GetDecimal(),
        _ => element.GetString() ?? string.Empty,
    };

    private static string BuildEvidence(
        MetricDefinitionRow metric, WorkbenchDefinition definition,
        IReadOnlyList<MetricDimensionFilter> dimensions, MetricRowFilterPlan? rowFilter,
        bool sourceIsMaster, string scopePredicate, string dataFilter)
    {
        var evidence = new StringBuilder();
        evidence.Append("依据链：口径版本 v").Append(metric.Version)
            .Append("，定义 ").Append(metric.Definition)
            .Append("，来源表 ").Append(metric.SourceTable);
        if (!sourceIsMaster && definition.MasterTable is not null)
        {
            evidence.Append("（数据范围经主表 ").Append(definition.MasterTable).Append(" 推导）");
        }
        if (dimensions.Count > 0)
        {
            evidence.Append("，维度 ").Append(string.Join("、",
                dimensions.Select(d => $"{d.Column}{(d.Operator == MetricDimensionOperator.Range ? "区间" : "=")}")));
        }
        if (rowFilter is not null)
        {
            evidence.Append("，行过滤 [").Append(string.Join(" AND ",
                rowFilter.Conditions.Select(c => $"{c.Column} {c.Operator} {FormatValue(c.Value)}"))).Append(']');
        }
        evidence.Append("，执行范围：模块 #").Append(definition.ModuleId).Append(' ').Append(definition.Title)
            .Append("，EXEC_TAG ").Append(definition.ExecTag ?? "Z")
            .Append(string.IsNullOrWhiteSpace(dataFilter) ? string.Empty : "，DATA_FILTER 已注入")
            .Append("。结果仅统计当前用户权限范围内的数据。");
        return evidence.ToString();
    }

    private static string FormatValue(object value) => value switch
    {
        bool b => b ? "1" : "0",
        decimal d => d.ToString("0.####"),
        _ => value.ToString() ?? string.Empty,
    };
}
