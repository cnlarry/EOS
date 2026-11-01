using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.RegularExpressions;

using EOS.API.Data;
using EOS.API.Data.Reports;

namespace EOS.API.Features.Assistant.Metrics;

/// <summary>
/// 口径维护的校验（比界面更严）：标识规范、表达式受控解析、来源表与维度的物理存在性、
/// 行过滤的表归属与关联列——后半段直接复用 <see cref="MetricDefinitionValidator"/>，
/// 不另写一套判定（否则"人改的口径"与"机器算的口径"会有两种标准）。
/// </summary>
public sealed partial class ReportMetricAdminValidator(IMetricSchemaProbe probe)
{
    /// <summary>口径标识：小写字母开头，只含小写字母/数字/下划线，3–50 字符。</summary>
    [GeneratedRegex("^[a-z][a-z0-9_]{2,49}$")]
    private static partial Regex MetricIdPattern();

    private static readonly string[] AllowedStatuses = ["CANDIDATE", "CONFIRMED"];

    /// <summary>校验草稿；不通过时抛 <see cref="ArgumentException"/>（由统一异常映射转 400）。</summary>
    public async Task ValidateAsync(ReportMetricAdminDraft draft, CancellationToken token)
    {
        var metricId = draft.MetricId?.Trim() ?? string.Empty;
        if (!MetricIdPattern().IsMatch(metricId))
        {
            throw new ArgumentException(
                "口径标识必须小写字母开头、只含小写字母/数字/下划线，长度 3–50（例如 sales_amount）。");
        }
        if (string.IsNullOrWhiteSpace(draft.MetricName) || draft.MetricName.Trim().Length > 100)
        {
            throw new ArgumentException("口径名称不能为空，且不超过 100 字符。");
        }
        if (!string.IsNullOrWhiteSpace(draft.Description) && draft.Description.Trim().Length > 500)
        {
            throw new ArgumentException("说明不超过 500 字符。");
        }
        if (!string.IsNullOrWhiteSpace(draft.Domain) && draft.Domain.Trim().Length > 20)
        {
            throw new ArgumentException("业务域不超过 20 字符。");
        }
        var status = (draft.ConfirmStatus ?? string.Empty).Trim().ToUpperInvariant();
        if (!AllowedStatuses.Contains(status, StringComparer.Ordinal))
        {
            throw new ArgumentException("确认状态只能是 CANDIDATE（候选）或 CONFIRMED（已确认）。");
        }

        var parse = MetricExpressionParser.Parse(draft.Definition);
        if (!parse.Ok || parse.Expression is null)
        {
            throw new ArgumentException($"口径定义无法按受控语法解析：{parse.Error}");
        }

        var sourceTable = draft.SourceTable?.Trim() ?? string.Empty;
        if (!WorkbenchSql.Identifier.IsMatch(sourceTable) || !await probe.TableExistsAsync(sourceTable, token))
        {
            throw new ArgumentException($"来源表不存在或名称不合法：{sourceTable}。");
        }
        var sourceColumns = await probe.GetColumnsAsync(sourceTable, token);

        // 行过滤可能落在主表：表名与列都要真实存在，且归属由 MetricDefinitionValidator 判定
        string? filterTable = null;
        IReadOnlySet<string>? filterColumns = null;
        if (!string.IsNullOrWhiteSpace(draft.RowFilter))
        {
            try
            {
                using var document = JsonDocument.Parse(draft.RowFilter);
                if (document.RootElement.ValueKind == JsonValueKind.Object
                    && document.RootElement.TryGetProperty("table", out var table)
                    && table.ValueKind == JsonValueKind.String)
                {
                    filterTable = table.GetString()?.Trim();
                }
            }
            catch (JsonException ex)
            {
                throw new ArgumentException($"行过滤 JSON 无法解析：{ex.Message}");
            }

            if (!string.IsNullOrWhiteSpace(filterTable))
            {
                if (!WorkbenchSql.Identifier.IsMatch(filterTable) || !await probe.TableExistsAsync(filterTable, token))
                {
                    throw new ArgumentException($"行过滤表不存在或名称不合法：{filterTable}。");
                }
                filterColumns = await probe.GetColumnsAsync(filterTable, token);
            }
        }

        // 表达式列、维度、行过滤三件事统一交给既有校验器（与 resolve_metric 同一实现）
        var validation = await new MetricDefinitionValidator(probe).ValidateAsync(new MetricValidationInput(
            parse.Expression, sourceTable, sourceColumns, draft.DimensionKeys, draft.RowFilter,
            filterTable, filterColumns), token);
        if (!validation.Ok)
        {
            throw new ArgumentException($"口径校验未通过：{validation.Error}");
        }
    }
}
