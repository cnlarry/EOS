using EOS.API.Models;

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
    bool CanCopy,
    bool IsPrimaryKey,
    int? MaxLength,
    int TabNo = 1,
    int? FormOrder = null,
    int Span = 1,
    bool NewLine = false,
    string? CellGroup = null,
    int CellRole = 0,
    string? Options = null,
    bool IsPhysical = true,
    int? TypePrecision = null,
    int? TypeScale = null);

internal sealed record FormChooserRow(
    bool Active,
    string? Table,
    string? Description,
    int? ModuleId,
    string? ReturnMapping,
    string? Filter,
    int? SerialNo = null);

/// <summary>
/// 纯字段选择逻辑（与数据库解耦，便于单元测试）。
/// 规则：
/// - 权限过滤：成本/保密（无权限剔除）、DENY_VIEW 剔除；mode=new 剔 DENY_NEW，mode=edit 剔 DENY_MODI；
/// - 虚拟字段保留标记 isVirtual 且强制只读；
/// - 必填且只读/隐藏的字段标记 serverFilled（服务端填充），隐藏且非必填的字段不进表单；
/// - 输入行顺序即表单顺序：主表行由 SQL 按 SYSQL_DEFAULT.F_IDX 排序；
/// 明细行由调用方按 DocumentWorkbench 子表列配置（用户 SYSQL_FIELDS → 系统默认）对齐后传入。
/// </summary>
internal static class FormFieldSelector
{
    /// <summary>批核/结案状态位：即隐藏控件（chk_CONFIRM_TAG CssClass=hidden），统一不进表单。</summary>
    private static readonly IReadOnlySet<string> HiddenStatusTags = new HashSet<string>(
        WorkflowStates.LifecycleTagColumns, StringComparer.OrdinalIgnoreCase);

    /// <summary>生命周期系统列（状态位 + 经办人/日期）：浏览态一律只读显示。</summary>
    private static bool IsLifecycleSystemColumn(string key) =>
        HiddenStatusTags.Contains(key)
        || WorkflowStates.LifecycleActorColumns.Contains(key, StringComparer.OrdinalIgnoreCase);

    /// <summary>解析 FIELDS.FORM_OPTIONS（KEY=VALUE;KEY=VALUE），非法项跳过（容错，不抛错）。</summary>
    internal static IReadOnlyList<FormOptionItem> ParseOptions(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return [];
        var options = new List<FormOptionItem>();
        foreach (var part in raw.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var eq = part.IndexOf('=');
            if (eq <= 0) continue;
            var value = part[..eq].Trim();
            var label = part[(eq + 1)..].Trim();
            if (value.Length == 0 || label.Length == 0) continue;
            options.Add(new FormOptionItem(value, label));
        }
        return options;
    }

    /// <summary>规范化选择器回填目标（去掉 txt_/cho_/dro_/chk_/lab_/hidd_ 前缀，与前端一致）。</summary>
    internal static string NormalizeChooserTarget(string target)
    {
        var trimmed = target.Trim();
        foreach (var prefix in new[] { "txt_", "cho_", "dro_", "chk_", "lab_", "hidd_" })
        {
            if (trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return trimmed[prefix.Length..];
        }
        return trimmed;
    }

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
            if (HiddenStatusTags.Contains(row.Key) && mode != "view") continue;
            // ADR-013 §3.6: 系统列组（生命周期经办人/日期）在新增/编辑态隐藏，仅浏览态只读显示。
            if (mode != "view" && WorkflowStates.LifecycleActorColumns.Contains(row.Key, StringComparer.OrdinalIgnoreCase)) continue;
            // 复合单元格从字段（FORM_CELL_ROLE=2 且配置了组）即使隐藏/幽灵也保留，用于同格联动显示
            var isCellCompanion = row.CellRole == 2 && !string.IsNullOrWhiteSpace(row.CellGroup);
            if (row.IsCost && !canViewCost) continue;
            if (row.IsSecrecy && !canViewSecrecy) continue;
            if (deniedView.Contains(row.Key)) continue;
            if (deniedForMode.Contains(row.Key)) continue;

            // 审计列（CREATE_PERSON/CREATE_DATE/LAST_UPDATE_BY/LAST_UPDATE_DATE 与批核/结案
            // CONFIRM_PERSON/CONFIRM_DATE/FINISHED_PERSON/FINISHED_DATE）一律服务端持有，
            // 即使 FIELDS.IS_READONLY 误标为可编辑。
            // 自增主键与"必填且隐藏"字段也标记服务端填充。
            // 注意：必填但只读可见的字段（如 CURR_RATE 汇率，由前端选择币别后联动带出）
            // 不属于服务端填充，保留为客户端可提交字段，避免 SERVER_FILL_MISSING 误拦。
            var serverOwned = RecordPayloadValidator.IsAuditColumn(row.Key);
            // 幽灵从字段（无物理列）只显示不保存：DisplayOnly=true、强制只读、绝不服务端填充
            var displayOnly = isCellCompanion && !row.IsPhysical;
            var serverFilled = !displayOnly && (serverOwned || row.IsAutoIncrement || row.IsRequired && !row.IsVisible);
            if (!row.IsVisible && !serverFilled && !isCellCompanion) continue;

            var choosers = row.Choosers
                .Where(source => source.Active && !string.IsNullOrWhiteSpace(source.Table))
                .Select(source => new FieldChooserSource(
                    source.Active,
                    source.Table,
                    source.Description,
                    source.ModuleId,
                    // FILTER_STRUCT 仅字段设置 CanSetup 可见，普通用户表单定义不下发
                    Filter: null,
                    source.ReturnMapping,
                    source.SerialNo))
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
                IsReadonly: row.IsReadonly || row.IsVirtual || serverOwned || displayOnly || IsLifecycleSystemColumn(row.Key),
                row.IsVisible || isCellCompanion,
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
                row.MaxLength,
                row.TabNo,
                row.FormOrder,
                row.Span,
                row.NewLine,
                string.IsNullOrWhiteSpace(row.CellGroup) ? null : row.CellGroup,
                row.CellRole,
                ParseOptions(row.Options),
                displayOnly,
                row.CanCopy,
                row.TypePrecision,
                row.TypeScale));
        }
        return result;
    }
}
