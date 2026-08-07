namespace EOS.API.Data;

/// <summary>
/// FIELDS 表单字段原始行（自 f.T_ID=目标表的物理列，已含 SYSQL_DEFAULT 顺序与主键标记）。
/// 不含高危表达式内容（CHOOSE_FILTER 等）——普通用户响应不返回这些内容。
/// </summary>
internal sealed record FormFieldRow(
    string Key,
    string Label,
    string DataType,
    int DisplayLength,
    string? DisplayFormat,
    bool IsRequired,
    int? VerifyIndex,
    string? Regex,
    string? DefaultValue,
    bool IsReadonly,
    bool IsVisible,
    bool OnlyChoose,
    bool ChooseMultiple,
    string? ChoosePage,
    IReadOnlyList<FormChooserRow> Choosers,
    bool IsVirtual,
    bool IsCost,
    bool IsSecrecy,
    bool IsAutoIncrement,
    bool IsPrimaryKey,
    int? MaxLength);

internal sealed record FormChooserRow(
    bool Active,
    string? Table,
    string? Description,
    int? ModuleId,
    string? ReturnMapping);

/// <summary>
/// 纯字段选择逻辑（与数据库解耦，便于单元测试）。
/// 规则（对齐方案 §4 决策 2 与阶段 1 验收）：
/// - 权限过滤：成本/保密（无权限剔除）、DENY_VIEW 剔除；mode=new 剔 DENY_NEW，mode=edit 剔 DENY_MODI；
/// - 虚拟字段保留标记 isVirtual 且强制只读；
/// - 必填且只读/隐藏的字段标记 serverFilled（服务端填充），隐藏且非必填的字段不进表单；
/// - 输入行顺序即表单顺序（SQL 已按 SYSQL_DEFAULT.F_IDX 排序）。
/// </summary>
internal static class FormFieldSelector
{
    public static IReadOnlyList<FormFieldDefinition> Select(
        IReadOnlyList<FormFieldRow> rows,
        string mode,
        bool canViewCost,
        bool canViewSecrecy,
        IReadOnlySet<string> deniedView,
        IReadOnlySet<string> deniedNew,
        IReadOnlySet<string> deniedModi)
    {
        var deniedForMode = mode == "edit" ? deniedModi : deniedNew;
        var result = new List<FormFieldDefinition>(rows.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            if (!seen.Add(row.Key)) continue; // 防御：同名元数据行只取第一条
            if (row.IsCost && !canViewCost) continue;
            if (row.IsSecrecy && !canViewSecrecy) continue;
            if (deniedView.Contains(row.Key)) continue;
            if (deniedForMode.Contains(row.Key)) continue;

            // 审计列（CREATE_PERSON/CREATE_DATE/LAST_UPDATE_BY/LAST_UPDATE_DATE）一律服务端持有，
            // 即使 FIELDS.IS_READONLY 误标为可编辑（旧页面控件本身也是只读）。
            var serverOwned = RecordPayloadValidator.IsAuditColumn(row.Key);
            var serverFilled = serverOwned || row.IsRequired && (row.IsReadonly || !row.IsVisible);
            if (!row.IsVisible && !serverFilled) continue;

            var choosers = row.Choosers
                .Where(source => source.Active && !string.IsNullOrWhiteSpace(source.Table))
                .Select(source => new FieldChooserSource(
                    source.Active,
                    source.Table,
                    source.Description,
                    source.ModuleId,
                    Filter: null, // CHOOSE_FILTER 属高危表达式内容，受控解析完成前不返回普通用户
                    source.ReturnMapping))
                .ToArray();

            result.Add(new FormFieldDefinition(
                row.Key,
                row.Label,
                row.DataType,
                row.DisplayLength,
                row.DisplayFormat,
                row.IsRequired,
                row.VerifyIndex,
                row.Regex,
                row.DefaultValue,
                IsReadonly: row.IsReadonly || row.IsVirtual || serverOwned,
                row.IsVisible,
                row.OnlyChoose,
                row.ChooseMultiple,
                row.ChoosePage,
                choosers,
                row.IsPrimaryKey,
                row.IsAutoIncrement,
                row.IsVirtual,
                row.IsCost,
                row.IsSecrecy,
                serverFilled,
                row.MaxLength));
        }
        return result;
    }
}
