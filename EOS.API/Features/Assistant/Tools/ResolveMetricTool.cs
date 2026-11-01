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
    IMetricFieldWhitelist whitelist,
    IPermissionService permissions,
    IWorkbenchSearchGateway gateway,
    WorkbenchScopeFilter scopeFilter) : AssistantToolBase
{
    public const string ToolName = "resolve_metric";
    private const string ConfirmedStatus = "CONFIRMED";

    /// <summary>候选模块逐个试；拒绝时最多回报这么多条不同原因，避免把界面撑长。</summary>
    private const int MaxReportedFailures = 3;

    public override string Name => ToolName;
    public override AssistantToolRisk Risk => AssistantToolRisk.Read;
    public override string Description =>
        "按系统确认的业务口径计算指标（如销售额、库存数量），可带维度过滤或按单据过滤。"
        + "回答金额/数量/比率问题时必须使用本工具取数，禁止自行估算。"
        + "metric_id 可直接写业务词（如「营业额」「销售额」）：命中唯一已确认口径即计算，命中多条会要求指定，"
        + "所以多数情况下不必先调 enum_metrics；问「这张单多少钱」时用 record 传完整主键；未确认的口径会明确拒绝。";

    public override string ParametersJson => """
        {
          "type": "object",
          "properties": {
            "metric_id": { "type": "string", "description": "口径标识（如 sales_amount）或业务词（如 营业额、销售额）；业务词命中多条口径时会被要求改用标识" },
            "dimensions": {
              "type": "object",
              "description": "可选：维度过滤。键=维度（来自结果里的「可用维度」，标识或中文名都行，如 ORDER_DATE / 订单日期）；值=数字/字符串（等值）或含 from/to 的对象（闭区间，如 {\"from\":\"2026-01-01\",\"to\":\"2026-03-31\"}）。带不了某个过滤条件时不要省略它重算——那会把范围换成另一个问题"
            },
            "record": {
              "type": "object",
              "description": "可选：按单据过滤。键=该模块的主键列名（search_records 返回的 _keys 各段的列名），值=对应主键值；主键列必须全部给齐（如 {\"ORDER_TYPE\":\"CGD\",\"ORDER_NO\":\"CGD26100010\"}）。用于\"这张单多少钱\"这类精确取数"
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

        // 用户/模型说的常是业务词（营业额、销售额），不是标识：按业务词解析能省掉一次
        // enum_metrics 往返，也避免"枚举—猜标识—再算"的来回。命中唯一才用，多条一律要求指定——
        // 猜错口径算出来的数比拒绝更糟。
        var metric = await repository.GetAsync(metricId, token);
        var resolvedFrom = metric is null ? metricId : null;
        if (metric is null)
        {
            var candidates = await repository.SearchConfirmedAsync(metricId, token);
            if (candidates.Count == 1)
            {
                metric = candidates[0];
            }
            else if (candidates.Count > 1)
            {
                // 仓储侧最多取 10 条，够列出候选；超过时如实说"10 条以上"，不谎报精确条数。
                var countText = candidates.Count >= 10 ? "10 条以上" : $"{candidates.Count} 条";
                return ToolExecutionResult.Deny(
                    $"业务词「{metricId}」命中 {countText}已确认口径，请改用口径标识重新调用："
                    + string.Join("、", candidates.Select(row => $"{row.MetricId}（{row.MetricName}）"))
                    + "。");
            }
        }
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
        // **以该表为主表**、却不是工作台承载的模块（页面建不出定义）；而口径里的行过滤往往要求
        // 来源表是**明细表**（主表参与关联与过滤，如 sales_amount 依赖 COP_ORDER_M.APPROVED）。
        // 所以这里逐个候选试到第一个真能算出来的模块：
        // 能浏览 → 定义能建出 → 口径校验（列与行过滤在该模块上都可用）。
        WorkbenchDefinition? definition = null;
        ModulePermission? permission = null;
        MetricValidationResult? validation = null;
        IReadOnlySet<string>? acceptedSourceAllowed = null;
        IReadOnlySet<string>? acceptedMasterAllowed = null;
        IReadOnlyList<MetricDimensionFilter>? acceptedRecordFilters = null;
        var failures = new List<string>();
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

            var candidate = await ResolveColumnsAsync(candidateDefinition, metric, candidatePermission.Rights, token);
            if (candidate.Error is not null)
            {
                AddFailure(failures, candidate.Error);
                continue;
            }

            var candidateValidation = await validator.ValidateAsync(new MetricValidationInput(
                parse.Expression, metric.SourceTable, candidate.SourceAllowed, metric.DimensionKeys,
                metric.RowFilter, candidate.SourceIsMaster ? null : candidateDefinition.MasterTable,
                candidate.SourceIsMaster ? null : candidate.MasterAllowed), token);
            if (!candidateValidation.Ok)
            {
                AddFailure(failures, candidateValidation.Error);
                continue;
            }

            // 按单据过滤依赖候选模块的主键列名：主键对不上的候选直接换下一个，
            // 不能因为"第一个候选叫 ORDER_NO、第二个叫 PURCHASE_NO"就把整个调用否掉。
            var candidateRecord = ParseRecordFilters(arguments, candidateDefinition, candidate.SourceIsMaster,
                candidate.SourceAllowed, candidate.MasterAllowed);
            if (candidateRecord.Error is not null)
            {
                AddFailure(failures, candidateRecord.Error);
                continue;
            }

            definition = candidateDefinition;
            permission = candidatePermission;
            validation = candidateValidation;
            acceptedSourceAllowed = candidate.SourceAllowed;
            acceptedMasterAllowed = candidate.MasterAllowed;
            acceptedRecordFilters = candidateRecord.Filters;
            break;
        }

        if (definition is null || permission is null || validation is null
            || acceptedSourceAllowed is null || acceptedMasterAllowed is null || acceptedRecordFilters is null)
        {
            // 一个候选都浏览不了：沿用防探测口径（"不存在或没有权限"），不点名模块
            if (browsable == 0) return this.DenyBrowse($"#{moduleIds[0]}");
            // 看得到、也能建出定义，只是口径在该模块上算不出来（列不可见 / 行过滤引用不到）——照实说。
            // 每个候选都可能给出不同原因，逐条列出而不是只报第一个：第一个命中的模块常常只是
            // "表名对得上"（例如把来源表当主表的定制页），把它的失败当成结论会指错方向。
            if (failures.Count > 0)
            {
                return ToolExecutionResult.Deny($"口径校验未通过，拒绝计算：{string.Join("；", failures)}");
            }
            return ToolExecutionResult.Deny(
                $"口径 {metric.MetricId} 的来源表 {metric.SourceTable} 在候选模块上都算不出来"
                + "（挂靠的模块不是通用工作台承载页，或你没有浏览权限），无法施加数据范围，拒绝计算。");
        }

        var sourceIsMaster = string.Equals(definition.MasterTable, metric.SourceTable, StringComparison.OrdinalIgnoreCase);
        var sourceAllowed = acceptedSourceAllowed;
        var masterAllowed = acceptedMasterAllowed;

        if (!scopeFilter.TryBuildRecordScopePredicate(definition, permission.Rights.DataFilter,
                out var scopePredicate, out var scopeValues))
        {
            return ToolExecutionResult.Deny("当前用户的权限数据范围尚不支持，已拒绝计算。");
        }

        var dimensionLabels = await ReadDimensionLabelsAsync(definition, metric, token);
        var dimensions = await ParseDimensionsAsync(arguments, metric, definition,
            sourceIsMaster, sourceAllowed, masterAllowed, dimensionLabels, token);
        if (dimensions.Error is not null)
        {
            return ToolExecutionResult.Deny(dimensions.Error);
        }

        var filters = dimensions.Filters.Concat(acceptedRecordFilters).ToList();

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
                filters, sourceIsMaster ? null : definition.MasterTable,
                masterJoinColumns, scopePredicate, scopeValues);
        }
        catch (InvalidOperationException ex)
        {
            return ToolExecutionResult.Deny($"执行计划编译失败，拒绝计算：{ex.Message}");
        }

        var value = await executor.ExecuteAsync(plan, token);
        var summary = BuildEvidence(metric, definition, filters, validation.RowFilter,
            sourceIsMaster, scopePredicate, permission.Rights.DataFilter, dimensionLabels, acceptedRecordFilters.Count > 0);
        var label = resolvedFrom is null
            ? $"口径 {metric.MetricId}（{metric.MetricName}）"
            : $"口径 {metric.MetricId}（{metric.MetricName}，按业务词「{resolvedFrom}」解析）";
        if (value is null)
        {
            return ToolExecutionResult.Success($"{label}在指定范围内没有数据。\n{summary}");
        }
        return ToolExecutionResult.Success($"{label}计算结果：{value:0.####}\n{summary}");
    }

    /// <summary>
    /// 来源表在这个候选模块里的可用列：它是该模块的主表还是明细表，以及来源列与主表列两条白名单。
    /// 白名单走口径专用的可见列读取（权限口径，不含该用户的列选择），见 <see cref="IMetricFieldWhitelist"/>。
    /// 一列都看不见时直接给出拒绝原因——没有可读的列就不该继续往下算。
    /// </summary>
    private async Task<(bool SourceIsMaster, IReadOnlySet<string> SourceAllowed, IReadOnlySet<string> MasterAllowed, string? Error)>
        ResolveColumnsAsync(WorkbenchDefinition definition, MetricDefinitionRow metric, ModuleRights rights,
            CancellationToken token)
    {
        var sourceIsMaster = string.Equals(definition.MasterTable, metric.SourceTable, StringComparison.OrdinalIgnoreCase);
        var sourceAllowed = await whitelist.ReadAsync(metric.SourceTable, rights.CanViewCost, rights.CanViewSecrecy,
            sourceIsMaster ? rights.DeniedMasterFields : rights.DeniedDetailFields, token);
        if (sourceAllowed.Count == 0)
        {
            return (sourceIsMaster, sourceAllowed, sourceAllowed, "当前用户在该来源表上没有可见字段，拒绝计算。");
        }
        if (sourceIsMaster)
        {
            return (true, sourceAllowed, sourceAllowed, null);
        }

        var masterAllowed = definition.MasterTable is null
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : await whitelist.ReadAsync(definition.MasterTable, rights.CanViewCost, rights.CanViewSecrecy,
                rights.DeniedMasterFields, token);
        return (false, sourceAllowed, masterAllowed, null);
    }

    private static void AddFailure(List<string> failures, string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason) || failures.Count >= MaxReportedFailures) return;
        var text = reason.Trim();
        if (!failures.Contains(text, StringComparer.Ordinal)) failures.Add(text);
    }

    private async Task<IReadOnlyList<string>> ResolveMasterJoinColumnsAsync(
        WorkbenchDefinition definition, string sourceTable, CancellationToken token)
    {
        var sourceColumns = await schemaProbe.GetColumnsAsync(sourceTable, token);
        return definition.MasterPkOrder
            .Where(pk => sourceColumns.Contains(pk))
            .ToList();
    }

    /// <summary>
    /// 按单据（主键）过滤：主键列必须**全部给齐**、且对当前用户可见。
    ///
    /// <para>
    /// 只接受完整主键：口径声明的维度里通常没有单号，允许"只给单号"会在单号跨单别重复时
    /// 静默放大范围（多算了别的单别）——那正好是最难被发现的一类错。
    /// </para>
    /// </summary>
    private static (IReadOnlyList<MetricDimensionFilter> Filters, string? Error) ParseRecordFilters(
        JsonElement arguments, WorkbenchDefinition definition, bool sourceIsMaster,
        IReadOnlySet<string> sourceAllowed, IReadOnlySet<string> masterAllowed)
    {
        if (!arguments.TryGetProperty("record", out var element) || element.ValueKind != JsonValueKind.Object)
        {
            return ([], null);
        }

        var pkOrder = definition.MasterPkOrder;
        if (pkOrder.Count == 0)
        {
            return ([], "该模块没有声明主键，无法按单据过滤。");
        }

        var provided = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in element.EnumerateObject())
        {
            if (!pkOrder.Contains(property.Name, StringComparer.OrdinalIgnoreCase))
            {
                return ([], $"record 的键必须是该模块的主键列（{string.Join("/", pkOrder)}），未识别：{property.Name}。");
            }
            if (property.Value.ValueKind is not (JsonValueKind.String or JsonValueKind.Number))
            {
                return ([], $"record 的主键值必须是字符串或数字：{property.Name}。");
            }
            provided[property.Name] = property.Value;
        }

        var missing = pkOrder.Where(key => !provided.ContainsKey(key)).ToList();
        if (missing.Count > 0)
        {
            return ([], $"record 必须给齐主键（{string.Join("/", pkOrder)}），缺：{string.Join("、", missing)}。");
        }

        var allowed = sourceIsMaster ? sourceAllowed : masterAllowed;
        var filters = new List<MetricDimensionFilter>(pkOrder.Count);
        foreach (var key in pkOrder)
        {
            if (!allowed.Contains(key))
            {
                return ([], $"主键列对当前用户不可见，无法按单据过滤：{key}。");
            }
            var value = provided[key];
            object scalar = value.ValueKind == JsonValueKind.Number
                ? value.GetDecimal()
                : value.GetString() ?? string.Empty;
            filters.Add(new MetricDimensionFilter(key.ToUpperInvariant(),
                sourceIsMaster ? MetricDimensionTarget.Source : MetricDimensionTarget.Master,
                MetricDimensionOperator.Equals, [scalar]));
        }
        return (filters, null);
    }

    private async Task<(IReadOnlyList<MetricDimensionFilter> Filters, string? Error)> ParseDimensionsAsync(
        JsonElement arguments, MetricDefinitionRow metric, WorkbenchDefinition definition,
        bool sourceIsMaster, IReadOnlySet<string> sourceAllowed, IReadOnlySet<string> masterAllowed,
        IReadOnlyDictionary<string, string> labels, CancellationToken token)
    {
        var filters = new List<MetricDimensionFilter>();
        if (!arguments.TryGetProperty("dimensions", out var dimensionsElement)
            || dimensionsElement.ValueKind != JsonValueKind.Object)
        {
            return (filters, null);
        }

        var declaredKeys = metric.DimensionKeys
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToArray();
        var declared = declaredKeys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        // 用户/模型说的是业务说法（"日期""客户"），不是列名：先按中文名映射回标识，
        // 再走原有的白名单校验。映射不到就报出全部可用维度（带中文名），
        // 让下一次调用能一次改对——否则模型会"省掉过滤重算"，把问题换成另一个范围。
        var labelToKey = BuildLabelMap(declaredKeys, labels);
        var available = FormatDimensions(declaredKeys, labels);
        var sourceColumns = await schemaProbe.GetColumnsAsync(metric.SourceTable, token);
        IReadOnlySet<string>? masterColumns = null;
        if (!sourceIsMaster && definition.MasterTable is not null)
        {
            masterColumns = await schemaProbe.GetColumnsAsync(definition.MasterTable, token);
        }

        foreach (var property in dimensionsElement.EnumerateObject())
        {
            var column = ResolveDimensionName(property.Name, declared, labelToKey);
            if (column is null)
            {
                return ([], $"维度不在该口径的可用维度内：{property.Name}。可用维度：{available}。");
            }
            if (!declared.Contains(column))
            {
                return ([], $"维度不在该口径的可用维度内：{property.Name}。可用维度：{available}。");
            }

            var target = sourceColumns.Contains(column)
                ? MetricDimensionTarget.Source
                : masterColumns?.Contains(column) == true
                    ? MetricDimensionTarget.Master
                    : (MetricDimensionTarget?)null;
            if (target is null)
            {
                return ([], $"维度列物理上不存在：{column}。可用维度：{available}。");
            }
            var allowed = target == MetricDimensionTarget.Source ? sourceAllowed : masterAllowed!;
            if (!allowed.Contains(column))
            {
                return ([], $"维度列对当前用户不可见：{column}。可用维度：{available}。");
            }

            switch (property.Value.ValueKind)
            {
                case JsonValueKind.Number:
                    filters.Add(new MetricDimensionFilter(column.ToUpperInvariant(), target.Value,
                        MetricDimensionOperator.Equals, [property.Value.GetDecimal()]));
                    break;
                case JsonValueKind.String:
                    filters.Add(new MetricDimensionFilter(column.ToUpperInvariant(), target.Value,
                        MetricDimensionOperator.Equals, [property.Value.GetString() ?? string.Empty]));
                    break;
                case JsonValueKind.Object:
                {
                    if (!property.Value.TryGetProperty("from", out var from)
                        || !property.Value.TryGetProperty("to", out var to)
                        || from.ValueKind is not (JsonValueKind.Number or JsonValueKind.String)
                        || to.ValueKind is not (JsonValueKind.Number or JsonValueKind.String))
                    {
                        return ([], $"区间维度必须同时提供 from 与 to（数字或字符串）：{column}。");
                    }
                    filters.Add(new MetricDimensionFilter(column.ToUpperInvariant(), target.Value,
                        MetricDimensionOperator.Range,
                        [FromElement(from), FromElement(to)]));
                    break;
                }
                default:
                    return ([], $"维度值必须是数字、字符串或 from/to 区间：{column}。");
            }
        }
        return (filters, null);
    }

    /// <summary>业务说法 → 维度标识：中文名与标识本身都接受；同名冲突时不映射（宁可报错也不猜）。</summary>
    private static IReadOnlyDictionary<string, string> BuildLabelMap(
        IReadOnlyList<string> declaredKeys, IReadOnlyDictionary<string, string> labels)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var ambiguous = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in declaredKeys)
        {
            if (!labels.TryGetValue(key, out var label)) continue;
            var text = label.Trim();
            if (text.Length == 0 || string.Equals(text, key, StringComparison.OrdinalIgnoreCase)) continue;
            if (map.TryGetValue(text, out var existing) && !string.Equals(existing, key, StringComparison.OrdinalIgnoreCase))
            {
                ambiguous.Add(text);
                continue;
            }
            map[text] = key;
        }
        foreach (var text in ambiguous) map.Remove(text);
        return map;
    }

    private static string? ResolveDimensionName(string name, IReadOnlySet<string> declared,
        IReadOnlyDictionary<string, string> labelToKey)
    {
        var trimmed = name.Trim();
        if (declared.Contains(trimmed)) return trimmed;
        return labelToKey.TryGetValue(trimmed, out var key) ? key : null;
    }

    private static string FormatDimensions(IReadOnlyList<string> keys, IReadOnlyDictionary<string, string> labels) =>
        keys.Count == 0
            ? "（该口径未声明维度）"
            : string.Join("、", keys.Select(key =>
                labels.TryGetValue(key, out var label) && !string.Equals(label, key, StringComparison.OrdinalIgnoreCase)
                    ? $"{key}（{label}）"
                    : key));

    private async Task<IReadOnlyDictionary<string, string>> ReadDimensionLabelsAsync(
        WorkbenchDefinition definition, MetricDefinitionRow metric, CancellationToken token)
    {
        var keys = metric.DimensionKeys
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToArray();
        var labels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (keys.Length == 0)
        {
            return labels;
        }

        // 维度可能在来源表（明细）也可能在主表：两边的 `FIELDS` 都读一遍，先到先得。
        foreach (var table in new[] { metric.SourceTable, definition.MasterTable })
        {
            if (string.IsNullOrWhiteSpace(table)) continue;
            var found = await whitelist.ReadLabelsAsync(table, keys, token);
            foreach (var (key, label) in found)
            {
                labels.TryAdd(key, label);
            }
        }
        return labels;
    }


    private static object FromElement(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Number => element.GetDecimal(),
        _ => element.GetString() ?? string.Empty,
    };

    private static string BuildEvidence(
        MetricDefinitionRow metric, WorkbenchDefinition definition,
        IReadOnlyList<MetricDimensionFilter> dimensions, MetricRowFilterPlan? rowFilter,
        bool sourceIsMaster, string scopePredicate, string dataFilter,
        IReadOnlyDictionary<string, string> dimensionLabels, bool byRecord = false)
    {
        var evidence = new StringBuilder();
        evidence.Append("依据链：口径版本 v").Append(metric.Version)
            .Append("，定义 ").Append(metric.Definition)
            .Append("，来源表 ").Append(metric.SourceTable);
        if (!sourceIsMaster && definition.MasterTable is not null)
        {
            evidence.Append("（数据范围经主表 ").Append(definition.MasterTable).Append(" 推导）");
        }
        if (byRecord)
        {
            evidence.Append("，按单据主键精确过滤（").Append(string.Join("/", definition.MasterPkOrder)).Append('）');
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
        // 可用维度随结果一起给出：模型下一次要带过滤条件时不必再枚举一遍口径，
        // 也不会因为不知道列名而"省掉过滤重算"（那等于把问题换成另一个范围）。
        var declared = metric.DimensionKeys
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (declared.Length > 0)
        {
            evidence.Append("，可用维度 ").Append(FormatDimensions(declared, dimensionLabels));
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
