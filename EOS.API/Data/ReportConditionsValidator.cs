using System.Text.RegularExpressions;

namespace EOS.API.Data;

/// <summary>
/// 报表过滤条件设置（2205）写侧校验（纯逻辑，便于单元测试）。
/// 规则与报表运行时解析严格镜像（ReportRepository.ReadConditionsAsync /
/// SelectSourcePattern / TryResolveField），坏配置在保存时拦截，
/// 不再依赖运行时静默降级（选项丢失、数据源被置空）。
/// </summary>
internal static class ReportConditionsValidator
{
    /// <summary>F_TYPE 3/5 数据源语句：与运行时 SelectSourcePattern 完全一致（列别名列 C_ID/C_VALUE）。</summary>
    private static readonly Regex SelectSourcePattern = new(
        @"^\s*select\s+(\w+)\s+C_ID\s*,\s*(\w+)\s+C_VALUE\s+from\s+(\w+)\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>F_ID 必须「主表.列」点号格式（运行时 TryResolveField 仅接受主表前缀的条件字段）。</summary>
    private static readonly Regex FieldRef = new(
        "^([A-Za-z_][A-Za-z0-9_]*)\\.([A-Za-z_][A-Za-z0-9_]*)$", RegexOptions.Compiled);

    /// <summary>解析数据源语句（ValidateExpression 通过后的 3/5 类型调用），返回 C_ID/C_VALUE/表 三个分组。</summary>
    public static Match? MatchSelectSource(string expression) =>
        SelectSourcePattern.IsMatch(expression) ? SelectSourcePattern.Match(expression) : null;

    public static void ValidateModuleId(int moduleId)
    {
        if (moduleId <= 0) throw new ArgumentException("模块号无效。", nameof(moduleId));
    }

    /// <summary>序号 1-32767（SYSQR_DEFAULT.SERIAL_NO 为 SMALLINT）。</summary>
    public static void ValidateSerialNo(int serialNo)
    {
        if (serialNo is < 1 or > short.MaxValue) throw new ArgumentException("条件序号无效（1-32767）。", nameof(serialNo));
    }

    /// <summary>条件类型白名单：1 范围 / 2 固定单选 / 3 从数据表单选 / 4 固定多选 / 5 从数据表多选。</summary>
    public static void ValidateType(int type)
    {
        if (type is < 1 or > 5) throw new ArgumentException("条件类型无效（1-5）。", nameof(type));
    }

    /// <summary>
    /// 查询字段（F_ID）：「主表.列」点号格式且命中模块主表字段白名单
    /// （运行时条件仅作用于主表 WHERE，明细表前缀不解析，故白名单收窄到主表）。
    /// </summary>
    public static string ValidateField(string? field, string masterTable, IReadOnlySet<(string Table, string Column)> options)
    {
        var trimmed = (field ?? string.Empty).Trim();
        if (trimmed.Length == 0)
            throw new ArgumentException("查询字段不能为空。", nameof(field));
        if (trimmed.Length > 50)
            throw new ArgumentException("查询字段超长（SYSQR_DEFAULT.F_ID 上限 50 字符）。", nameof(field));
        var match = FieldRef.Match(trimmed);
        if (!match.Success)
            throw new ArgumentException($"查询字段必须为「表.列」格式：{trimmed}。", nameof(field));
        if (!match.Groups[1].Value.Equals(masterTable, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"查询字段必须属于模块主表 {masterTable}：{trimmed}。", nameof(field));
        if (!options.Contains((match.Groups[1].Value, match.Groups[2].Value)))
            throw new ArgumentException($"查询字段不在模块主表字段白名单内：{trimmed}。", nameof(field));
        return trimmed;
    }

    /// <summary>
    /// 条件语句（F_EXPR）按类型校验并规范化：
    /// 1 范围 → 必须为空；2/4 固定单选/多选 → 「标签:值;标签:值」（每段恰一个冒号、标签非空）；
    /// 3/5 从数据表单选/多选 → 运行时同款数据源语句（表/列物理存在由仓储另行校验）。
    /// </summary>
    public static string? ValidateExpression(int type, string? expression)
    {
        var raw = (expression ?? string.Empty).Trim();
        switch (type)
        {
            case 1:
                if (raw.Length > 0)
                    throw new ArgumentException("范围条件不使用条件语句，请清空条件语句。", nameof(expression));
                return null;
            case 2 or 4:
            {
                if (raw.Length == 0)
                    throw new ArgumentException("固定单选/多选必须提供选项（标签:值，分号分隔）。", nameof(expression));
                var segments = raw.Split(';', StringSplitOptions.RemoveEmptyEntries);
                if (segments.Length == 0)
                    throw new ArgumentException("固定单选/多选必须提供选项（标签:值，分号分隔）。", nameof(expression));
                var normalized = new List<string>(segments.Length);
                foreach (var segment in segments)
                {
                    var item = segment.Trim();
                    var separator = item.IndexOf(':');
                    if (separator <= 0 || item.IndexOf(':', separator + 1) >= 0)
                        throw new ArgumentException($"选项格式无效（应为 标签:值，每段恰一个冒号）：{item}。", nameof(expression));
                    var label = item[..separator].Trim();
                    if (label.Length == 0)
                        throw new ArgumentException($"选项标签不能为空：{item}。", nameof(expression));
                    normalized.Add($"{label}:{item[(separator + 1)..].Trim()}");
                }
                var result = string.Join(';', normalized);
                if (result.Length > 2000)
                    throw new ArgumentException("条件语句超长（SYSQR_DEFAULT.F_EXPR 上限 2000 字符）。", nameof(expression));
                return result;
            }
            case 3 or 5:
                if (raw.Length == 0)
                    throw new ArgumentException("从数据表单选/多选必须提供数据源语句（SELECT 列 C_ID, 列 C_VALUE FROM 表）。", nameof(expression));
                if (raw.Length > 2000)
                    throw new ArgumentException("条件语句超长（SYSQR_DEFAULT.F_EXPR 上限 2000 字符）。", nameof(expression));
                if (!SelectSourcePattern.IsMatch(raw))
                    throw new ArgumentException(
                        "数据源语句格式无效（仅支持：SELECT 列 C_ID, 列 C_VALUE FROM 表，列别名列固定 C_ID/C_VALUE）。",
                        nameof(expression));
                return raw;
            default:
                throw new ArgumentException("条件类型无效（1-5）。", nameof(type));
        }
    }

    /// <summary>文本列长度校验（与 SYSQR_DEFAULT 列宽一致），返回去空格后的值。</summary>
    public static string? ValidateText(string? value, int maxLength, string fieldName)
    {
        var trimmed = (value ?? string.Empty).Trim();
        if (trimmed.Length > maxLength)
            throw new ArgumentException($"{fieldName}超长（上限 {maxLength} 字符）。", nameof(value));
        return trimmed.Length == 0 ? null : trimmed;
    }
}
