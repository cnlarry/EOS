namespace EOS.API.Data;

public sealed record FormChooserColumn(string Key, string Label, string DataType, string? DisplayFormat);
public sealed record FormChooserResult(IReadOnlyList<FormChooserColumn> Columns, IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows, int Total);

internal sealed record FormChooserColumnRow(string Key, string Label, string DataType, bool IsCost, bool IsSecrecy, int OrderIndex, bool IsVisible, string? DisplayFormat);

/// <summary>
/// 选择器显示列选择逻辑（与数据库解耦，便于单元测试）。
/// 规则：剔除不可见、成本/保密（无权限时）与禁止查看字段；最多返回 max 列；保持输入顺序。
/// 不可见列仍留在解析白名单（CHOOSE_FILTER 可引用），只是不作为显示列。
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
            .Where(row => row.IsVisible)
            .Where(row => !row.IsCost || canViewCost)
            .Where(row => !row.IsSecrecy || canViewSecrecy)
            .Where(row => !deniedFields.Contains(row.Key))
            .Take(max)
            .Select(row => new FormChooserColumn(row.Key, row.Label, row.DataType, row.DisplayFormat))
            .ToArray();
}
