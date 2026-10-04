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

        // 同一张来源表可能挂靠多个模块，而"挂得上"不等于"算得出"：只按表名匹配时，命中的常是
        // **以该表为主表**的查询页（如 2504 订单查询中心——页面不是工作台承载页，建不出定义）；
        // 而口径里的行过滤往往要求来源表是**明细表**（主表参与关联与过滤，如 sales_amount 依赖
        // COP_ORDER_M.CONFIRM_TAG）。所以这里逐个候选试到第一个真能算出来的模块：
        // 能浏览 → 定义能建出 → 口径校验（列与行过滤在该模块上都可用）。
        WorkbenchDefinition? definition = null;
        ModulePermission? permission = null;
        MetricValidationResult? validation = null;
        string? validationFailure = null;
        var browsable = 0;
        foreach (var moduleId in moduleIds)
        {
            var candidatePermission = await permissions.GetAsync(userId, moduleId, token);
            if (!candidatePermission.CanBrowse) continue;
            browsable++;

            var candidateDefinition = await gateway.GetDefinitionAsync(moduleId, userId,
                candidatePermission.Rights.ExecuteTag, candidatePermission.Rights.CanViewCost,
                candidatePermission.Rights.CanViewSecrecy, candidatePermission.Rights.DeniedMasterFields,
                candidatePermission.Rights.DeniedDetailFields, token);
            if (candidateDefinition is null) continue;

            var candidate = ResolveSourceColumns(candidateDefinition, metric.SourceTable);
            if (candidate.Error is not null)
            {
                validationFailure ??= candidate.Error;
                continue;
            }

            var candidateValidation = await validator.ValidateAsync(new MetricValidationInput(
                parse.Expression, metric.SourceTable, candidate.SourceAllowed, metric.DimensionKeys,
                metric.RowFilter, candidate.SourceIsMaster ? null : candidateDefinition.MasterTable,
                candidate.SourceIsMaster ? null : candidate.MasterAllowed), token);
            if (!candidateValidation.Ok)
            {
                validationFailure ??= candidateValidation.Error;
                continue;
            }

            definition = candidateDefinition;
            permission = candidatePermission;
            validation = candidateValidation;
            break;
        }

        if (definition is null || permission is null || validation is null)
        {
            // 一个候选都浏览不了：沿用防探测口径（"不存在或没有权限"），不点名模块
            if (browsable == 0) return this.DenyBrowse($"#{moduleIds[0]}");
            // 看得到、也能建出定义，只是口径在该模块上算不出来（列不可见 / 行过滤引用不到）——照实说
            if (validationFailure is not null)
            {
                return ToolExecutionResult.Deny($"口径校验未通过，拒绝计算：{validationFailure}");
            }
            return ToolExecutionResult.Deny(
                $"口径 {metric.MetricId} 的来源表 {metric.SourceTable} 在候选模块上都算不出来"
                + "（挂靠的模块不是通用工作台承载页，或你没有浏览权限），无法施加数据范围，拒绝计算。");
        }

        var sourceIsMaster = string.Equals(definition.MasterTable, metric.SourceTable, StringComparison.OrdinalIgnoreCase);
        var sourceAllowed = (sourceIsMaster ? definition.MasterFields : definition.DetailFields)
            .Select(field => field.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var masterAllowed = definition.MasterFields.Select(field => field.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);

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

    /// <summary>
    /// 来源表在这个候选模块里的可见列：它是该模块的主表还是明细表，以及可读列与主表列两条白名单。
    /// 一列都看不见时直接给出拒绝原因——没有可读的列就不该继续往下算。
    /// </summary>
    private static (bool SourceIsMaster, IReadOnlySet<string> SourceAllowed, IReadOnlySet<string> MasterAllowed, string? Error)
        ResolveSourceColumns(WorkbenchDefinition definition, string sourceTable)
    {
        var sourceIsMaster = string.Equals(definition.MasterTable, sourceTable, StringComparison.OrdinalIgnoreCase);
        var columns = sourceIsMaster ? definition.MasterFields : definition.DetailFields;
        IReadOnlySet<string> sourceAllowed = columns.Select(field => field.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        IReadOnlySet<string> masterAllowed = definition.MasterFields.Select(field => field.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var error = sourceAllowed.Count == 0 ? "当前用户在该来源表上没有可见字段，拒绝计算。" : null;
        return (sourceIsMaster, sourceAllowed, masterAllowed, error);
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
