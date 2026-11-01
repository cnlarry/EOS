using EOS.API.Data;
using EOS.API.Data.Metadata;

namespace EOS.API.Features.Assistant.Metrics;

/// <summary>
/// 口径计算可用的列白名单。
///
/// 与工作台定义的字段视图**故意不同源**：定义里的字段视图会应用该用户的列选择
/// （`SYS_USER_COLUMN`），而列选择只是显示偏好——某个人没勾选「总金额」不等于他不能
/// 汇总总金额。若拿定义字段视图当白名单，同一口径会因"谁问的"而时算得出时算不出，
/// 且报的是"字段不可见"，与权限无关。
///
/// 真正该管的是权限：字段级禁看、成本与保密切换、元数据声明可见（`FIELDS.IS_VISIBLE`），
/// 以及"登记的列必须真有物理列"。判定逻辑统一走 <see cref="FieldMetadataReader"/>。
/// </summary>
public interface IMetricFieldWhitelist
{
    Task<IReadOnlySet<string>> ReadAsync(string table, bool canViewCost, bool canViewSecrecy,
        IReadOnlySet<string> deniedFields, CancellationToken token);

    /// <summary>
    /// 字段中文名（`FIELDS.F_DESC`），用来把用户/模型说的"日期""客户"映射回维度标识。
    /// 只读名字不带数据值，且这里不套权限过滤：口径维度本来就经 `enum_metrics` 公开。
    /// </summary>
    Task<IReadOnlyDictionary<string, string>> ReadLabelsAsync(string table, IReadOnlyCollection<string> columns,
        CancellationToken token);
}

public sealed class MetricFieldWhitelist(DbConnectionFactory connections) : IMetricFieldWhitelist
{
    public async Task<IReadOnlySet<string>> ReadAsync(string table, bool canViewCost, bool canViewSecrecy,
        IReadOnlySet<string> deniedFields, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(table))
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        var fields = await FieldMetadataReader.ReadAsync(connection, table, new FieldMetadataReader.FieldReadOptions
        {
            VisibleOnly = true,
            DropVirtual = true,
            PhysicalOnly = true,
            CanViewCost = canViewCost,
            CanViewSecrecy = canViewSecrecy,
            DeniedFields = deniedFields,
        }, token);
        return fields.Select(field => field.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    public async Task<IReadOnlyDictionary<string, string>> ReadLabelsAsync(string table,
        IReadOnlyCollection<string> columns, CancellationToken token)
    {
        var labels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(table) || columns.Count == 0)
        {
            return labels;
        }

        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        // 字段清单只经 FieldMetadataReader 一处读（与打印、查询中心、权限矩阵同源）；
        // 这里只要名字，故不做可见性/物理列过滤，成本与保密也放行——维度名本来已经由 enum_metrics 公开。
        var fields = await FieldMetadataReader.ReadAsync(connection, table, new FieldMetadataReader.FieldReadOptions
        {
            CanViewCost = true,
            CanViewSecrecy = true,
            OrderByVerifyIndex = false,
        }, token);
        foreach (var field in fields)
        {
            if (!columns.Contains(field.Key, StringComparer.OrdinalIgnoreCase) || labels.ContainsKey(field.Key))
            {
                continue;
            }
            labels[field.Key] = string.IsNullOrWhiteSpace(field.Label) ? field.Key : field.Label.Trim();
        }
        return labels;
    }
}
