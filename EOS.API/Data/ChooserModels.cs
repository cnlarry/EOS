namespace EOS.API.Data;

public sealed record FormChooserColumn(string Key, string Label, string DataType);
public sealed record FormChooserResult(IReadOnlyList<FormChooserColumn> Columns, IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows);

internal sealed record FormChooserColumnRow(string Key, string Label, string DataType, bool IsCost, bool IsSecrecy);

/// <summary>
/// 选择器显示列选择逻辑（与数据库解耦，便于单元测试）。
/// 规则：剔除成本/保密（无权限时）与禁止查看字段；最多返回 max 列；保持输入顺序。
/// </summary>
internal static class ChooserColumnSelector
{
    public static IReadOnlyList<FormChooserColumn> Select(
        IReadOnlyList<FormChooserColumnRow> rows,
        bool canViewCost,
        bool canViewSecrecy,
        IReadOnlySet<string> deniedFields,
        int max = 6) =>
        rows
            .Where(row => !row.IsCost || canViewCost)
            .Where(row => !row.IsSecrecy || canViewSecrecy)
            .Where(row => !deniedFields.Contains(row.Key))
            .Take(max)
            .Select(row => new FormChooserColumn(row.Key, row.Label, row.DataType))
            .ToArray();
}
