using System.Text.Json;
using System.Text.Json.Serialization;

namespace EOS.API.Features.Assistant.Metrics;

/// <summary>
/// Physical existence probe for metric validation. Backed by sys.objects/sys.columns
/// (INFORMATION_SCHEMA is banned); abstracted so unit tests can supply schema fakes.
/// </summary>
public interface IMetricSchemaProbe
{
    Task<bool> TableExistsAsync(string table, CancellationToken token);

    Task<IReadOnlySet<string>> GetColumnsAsync(string table, CancellationToken token);
}

/// <summary>Structured row filter attached to a metric (REPORT_METRIC.ROW_FILTER JSON).</summary>
public sealed record MetricRowFilterPlan(
    string Table,
    IReadOnlyList<string> JoinColumns,
    IReadOnlyList<MetricFilterCondition> Conditions);

public sealed record MetricFilterCondition(string Column, string Operator, object Value);

internal sealed record MetricRowFilterSpec(
    [property: JsonPropertyName("table")] string? Table,
    [property: JsonPropertyName("on")] string[]? On,
    [property: JsonPropertyName("conditions")] MetricFilterConditionSpec[]? Conditions);

internal sealed record MetricFilterConditionSpec(
    [property: JsonPropertyName("column")] string Column,
    [property: JsonPropertyName("op")] string Op,
    [property: JsonPropertyName("value")] JsonElement Value);

/// <summary>
/// Metric validation input. <paramref name="AllowedColumns"/> is the permission-visible
/// column whitelist for <paramref name="SourceTable"/> and <paramref name="MasterColumns"/>
/// the whitelist for the owning module's master table (workbench definition fields, so
/// denied/cost/secrecy columns cannot enter a plan). Dimension keys may live on either
/// table; dimension filters route accordingly at plan-compile time.
/// </summary>
public sealed record MetricValidationInput(
    MetricExpression Expression,
    string SourceTable,
    IReadOnlySet<string> AllowedColumns,
    string? DimensionKeysCsv,
    string? RowFilterJson,
    string? MasterTable = null,
    IReadOnlySet<string>? MasterColumns = null);

public sealed record MetricValidationResult(bool Ok, string? Error, MetricRowFilterPlan? RowFilter = null)
{
    public static MetricValidationResult Fail(string error) => new(false, error, null);
}

/// <summary>
/// Validates a parsed metric expression plus its structured row filter against
/// physical schema (sys.*) and the permission-visible column whitelists. Every
/// failure is deterministic and reported with a reason; nothing here executes SQL.
/// </summary>
public sealed class MetricDefinitionValidator(IMetricSchemaProbe probe)
{
    private const string IdentifierPattern = "^[A-Za-z_][A-Za-z0-9_]{0,127}$";

    public async Task<MetricValidationResult> ValidateAsync(MetricValidationInput input, CancellationToken token)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(input.SourceTable, IdentifierPattern))
        {
            return MetricValidationResult.Fail($"来源表名不符合标识符规则：{input.SourceTable}");
        }
        if (!await probe.TableExistsAsync(input.SourceTable, token))
        {
            return MetricValidationResult.Fail($"口径来源表在库中不存在：{input.SourceTable}");
        }
        var sourceColumns = await probe.GetColumnsAsync(input.SourceTable, token);
        var expressionError = ValidateExpression(input.Expression, input.AllowedColumns, sourceColumns);
        if (expressionError is not null)
        {
            return MetricValidationResult.Fail(expressionError);
        }

        IReadOnlySet<string>? masterColumns = null;
        if (input.MasterTable is not null && !string.Equals(input.MasterTable, input.SourceTable, StringComparison.OrdinalIgnoreCase))
        {
            if (!await probe.TableExistsAsync(input.MasterTable, token))
            {
                return MetricValidationResult.Fail($"口径关联主表在库中不存在：{input.MasterTable}");
            }
            masterColumns = await probe.GetColumnsAsync(input.MasterTable, token);
        }

        foreach (var dimension in ParseDimensionKeys(input.DimensionKeysCsv))
        {
            if (!sourceColumns.Contains(dimension)
                && masterColumns?.Contains(dimension) is not true)
            {
                return MetricValidationResult.Fail($"维度列在来源表或主表中均不存在：{dimension}");
            }
        }

        var rowFilter = await ValidateRowFilterAsync(input, sourceColumns, masterColumns, token);
        if (rowFilter.Error is not null)
        {
            return MetricValidationResult.Fail(rowFilter.Error);
        }
        return new MetricValidationResult(true, null, rowFilter.Plan);
    }

    private static string? ValidateExpression(
        MetricExpression expression, IReadOnlySet<string> allowedColumns, IReadOnlySet<string> sourceColumns)
    {
        var hasAggregate = false;
        var error = Walk(expression);
        if (error is not null)
        {
            return error;
        }
        if (!hasAggregate)
        {
            return "口径定义必须包含聚合函数。";
        }
        return null;

        string? Walk(MetricExpression node)
        {
            switch (node)
            {
                case MetricColumn column:
                    if (!allowedColumns.Contains(column.Name))
                    {
                        return $"口径引用了当前用户不可见或不存在的字段：{column.Name}";
                    }
                    if (!sourceColumns.Contains(column.Name))
                    {
                        return $"口径引用的列在来源表中不存在：{column.Name}";
                    }
                    return null;
                case MetricLiteral:
                    return null;
                case MetricAggregate aggregate:
                    hasAggregate = true;
                    return Walk(aggregate.Argument);
                case MetricNullIf nullIf:
                    return Walk(nullIf.Left) ?? Walk(nullIf.Right);
                case MetricBinary binary:
                    return Walk(binary.Left) ?? Walk(binary.Right);
                default:
                    return "口径定义包含不受支持的节点。";
            }
        }
    }

    private static IReadOnlySet<string> ParseDimensionKeys(string? csv)
    {
        if (string.IsNullOrWhiteSpace(csv))
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
        return csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private async Task<(MetricRowFilterPlan? Plan, string? Error)> ValidateRowFilterAsync(
        MetricValidationInput input, IReadOnlySet<string> sourceColumns,
        IReadOnlySet<string>? masterColumns, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(input.RowFilterJson))
        {
            return (null, null);
        }

        MetricRowFilterSpec? spec;
        try
        {
            spec = JsonSerializer.Deserialize<MetricRowFilterSpec>(input.RowFilterJson,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (JsonException ex)
        {
            return (null, $"行过滤 JSON 无法解析：{ex.Message}");
        }
        if (spec is null)
        {
            return (null, "行过滤 JSON 为空。");
        }

        var declaredTable = spec.Table;
        var joinColumns = (spec.On ?? []).ToList();
        var conditions = (spec.Conditions ?? []).ToList();

        if (conditions.Count == 0)
        {
            return (null, "行过滤缺少条件。");
        }
        if (conditions.Count > 10)
        {
            return (null, "行过滤条件数量超过上限（10）。");
        }
        if (joinColumns.Count > 8)
        {
            return (null, "行过滤关联列数量超过上限（8）。");
        }

        // The filter table must be the source table itself or the owning module's
        // master table — anything else has no permission anchor and is refused.
        var sameTable = declaredTable is null
            || string.Equals(declaredTable, input.SourceTable, StringComparison.OrdinalIgnoreCase);
        var isMasterTable = !sameTable
            && input.MasterTable is not null
            && string.Equals(declaredTable, input.MasterTable, StringComparison.OrdinalIgnoreCase);
        if (!sameTable && !isMasterTable)
        {
            return (null, "行过滤表既不是来源表也不是来源模块的主表，拒绝校验。");
        }
        if (sameTable && joinColumns.Count > 0)
        {
            return (null, "行过滤与来源表为同一张表时不允许声明关联列。");
        }
        if (isMasterTable && joinColumns.Count == 0)
        {
            return (null, "行过滤表与来源表不同时必须声明关联列。");
        }

        var filterTable = sameTable ? input.SourceTable : declaredTable!;
        var filterColumns = await probe.GetColumnsAsync(filterTable, token);
        var filterAllowed = sameTable ? input.AllowedColumns : input.MasterColumns;
        if (filterAllowed is null)
        {
            return (null, "行过滤表缺少权限可见列白名单，拒绝校验。");
        }

        foreach (var join in joinColumns)
        {
            if (!System.Text.RegularExpressions.Regex.IsMatch(join, IdentifierPattern))
            {
                return (null, $"行过滤关联列不符合标识符规则：{join}");
            }
            if (!filterColumns.Contains(join) || !sourceColumns.Contains(join))
            {
                return (null, $"行过滤关联列必须同时存在于行过滤表与来源表：{join}");
            }
        }

        var compiled = new List<MetricFilterCondition>(conditions.Count);
        foreach (var condition in conditions)
        {
            if (!System.Text.RegularExpressions.Regex.IsMatch(condition.Column, IdentifierPattern))
            {
                return (null, $"行过滤列不符合标识符规则：{condition.Column}");
            }
            if (!filterColumns.Contains(condition.Column))
            {
                return (null, $"行过滤列在行过滤表中不存在：{condition.Column}");
            }
            if (!filterAllowed.Contains(condition.Column))
            {
                return (null, $"行过滤列对当前用户不可见：{condition.Column}");
            }
            if (condition.Op is not ("=" or "<>" or ">" or "<" or ">=" or "<="))
            {
                return (null, $"行过滤运算符不在白名单内：{condition.Op}");
            }
            if (condition.Value.ValueKind is not (JsonValueKind.Number or JsonValueKind.String
                    or JsonValueKind.True or JsonValueKind.False))
            {
                return (null, "行过滤值必须是标量（数字/字符串/布尔）。");
            }
            compiled.Add(new MetricFilterCondition(
                condition.Column.ToUpperInvariant(),
                condition.Op,
                condition.Value.ValueKind switch
                {
                    JsonValueKind.Number => condition.Value.GetDecimal(),
                    JsonValueKind.String => condition.Value.GetString() ?? string.Empty,
                    _ => condition.Value.GetBoolean(),
                }));
        }

        return (new MetricRowFilterPlan(
            filterTable.ToUpperInvariant(),
            joinColumns.Select(j => j.ToUpperInvariant()).ToList(),
            compiled), null);
    }
}
