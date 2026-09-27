using System.Collections.Concurrent;
using System.Globalization;
using System.Text.RegularExpressions;

using EOS.API.Models;

namespace EOS.API.Data;

/// <summary>
/// 记录保存载荷的纯校验/转换逻辑（与数据库解耦，便于单元测试）。
/// 规则：
/// - 只接受表单定义内的字段；未知字段、只读/虚拟/serverFilled 字段一律拒绝（审计列视为服务端持有）；
/// - 按 F_TYPE 做类型转换；必填（IS_VERIFY）非空；REGEX 校验；
/// - 默认值（DFT_VALUE）在新增时服务端应用（不信任前端）。
/// </summary>
internal static class RecordPayloadValidator
{
    public static readonly IReadOnlySet<string> AuditColumns = new HashSet<string>(
        WorkflowStates.LifecycleActorColumns.Concat(WorkflowStates.OwnershipColumns),
        StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 长度校验只适用于字符类型。datetime/numeric 等类型的 sys.columns.max_length 是字节数
    /// （如 datetime=8、numeric(18,2)=9），不能当作字符上限，否则 ISO 日期/较长数值会被误拒。
    /// </summary>
    private static bool IsTextType(string dataType)
    {
        var type = dataType.Trim().ToLowerInvariant();
        return type is "char" or "nchar" or "varchar" or "nvarchar" or "text" or "ntext";
    }

    public static bool IsAuditColumn(string field) => AuditColumns.Contains(field);

    /// <summary>
    /// decimal/numeric 超精度校验：precision/scale 源自 sys.types 随 form-definition 下发。
    /// 仅 decimal/numeric 启用——float 无精度语义、money 固定 scale=4，均不适用。
    /// 校验为拒绝式（不做静默舍入），与服务端 decimal away-from-zero 语义一致。
    /// </summary>
    private static FieldError? CheckNumericScale(FormFieldDefinition field, object? value)
    {
        if (value is not decimal number || field.Precision is not int precision || field.Scale is not int scale)
        {
            return null;
        }
        var type = field.DataType.Trim().ToLowerInvariant();
        if (!type.Contains("decimal") && !type.Contains("numeric"))
        {
            return null;
        }
        var rounded = decimal.Round(number, scale, MidpointRounding.AwayFromZero);
        if (rounded != number)
        {
            return new FieldError(field.Key, $"小数位超出精度（最多 {scale} 位）。", "SCALE_EXCEEDED");
        }
        var integerLimit = (decimal)Math.Pow(10, precision - scale);
        if (Math.Abs(number) >= integerLimit)
        {
            return new FieldError(field.Key, $"数值超出精度范围（整数部分最多 {Math.Max(0, precision - scale)} 位）。", "PRECISION_EXCEEDED");
        }
        return null;
    }

    public sealed record ValidationResult(
        IReadOnlyList<FieldError> Errors,
        IReadOnlyDictionary<string, object?> Converted);

    public static ValidationResult ValidateSubmitted(
        IReadOnlyList<FormFieldDefinition> fields,
        IReadOnlyDictionary<string, string?> values)
    {
        var map = fields.ToDictionary(field => field.Key, StringComparer.OrdinalIgnoreCase);
        var errors = new List<FieldError>();
        var converted = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, raw) in values)
        {
            if (!map.TryGetValue(key, out var field))
            {
                errors.Add(new FieldError(key, "字段不在表单定义中。", "UNKNOWN_FIELD"));
                continue;
            }
            // 服务端持有（serverFilled）与虚拟字段拒绝客户端提交。
            if (field.IsVirtual || field.ServerFilled || field.DisplayOnly)
            {
                errors.Add(new FieldError(key, "该字段由服务端维护，不可提交。", "READONLY_FIELD"));
                continue;
            }
            // 只读字段只在「界面确实会带值」时才算可写：必填的联动字段，或带选择器的回填字段
            // ——与前端 writableFields 同一口径。否则界面上根本打不开的格子仍可被构造请求改写；
            // 模块判别字段（模块 FILTER 依赖它，如按保密/离职标志分表的工资表）尤其不能被改，
            // 改后记录会落到另一个模块的可见范围。
            // 无选择器的只读联动列（如由币别带出的汇率）不靠提交：服务端在保存主表前补齐
            // （MasterDerivedColumnFiller），两者不冲突。
            if (field.IsReadonly
                && !field.IsRequired
                && !field.Choosers.Any(source => source.Active && !string.IsNullOrWhiteSpace(source.Table)))
            {
                errors.Add(new FieldError(key, "该字段为只读，不可提交。", "READONLY_FIELD"));
                continue;
            }
            // Trim whitespace before validation and storage
            var trimmed = raw?.Trim();
            if (trimmed is not null && IsTextType(field.DataType) && field.MaxLength is int maxLength && trimmed.Length > maxLength)
            {
                errors.Add(new FieldError(key, $"内容长度超出限制（最多 {maxLength} 字符）。", "VALUE_TOO_LONG"));
                continue;
            }
            if (!TryConvert(field.DataType, trimmed, out var value))
            {
                errors.Add(new FieldError(key, "数值格式不正确。", "INVALID_VALUE"));
                continue;
            }
            var scaleError = CheckNumericScale(field, value);
            if (scaleError is not null)
            {
                errors.Add(scaleError);
                continue;
            }
            converted[key] = value;
        }
        return new ValidationResult(errors, converted);
    }

    public static void ApplyDefaults(IReadOnlyList<FormFieldDefinition> fields, IDictionary<string, object?> values)
    {
        foreach (var field in fields)
        {
            if (field.IsReadonly || field.IsVirtual || field.ServerFilled || field.DisplayOnly || values.ContainsKey(field.Key)) continue;
            if (string.IsNullOrWhiteSpace(field.DefaultValue)) continue;
            // Date macro: DFT_VALUE='D' means "today" for datetime fields (legacy convention).
            // Previously this was silently skipped on conversion failure, breaking defaults like HR in-service dates.
            if (field.DataType.Contains("date", StringComparison.OrdinalIgnoreCase)
                && field.DefaultValue.Trim().Equals("D", StringComparison.OrdinalIgnoreCase))
            {
                values[field.Key] = DateTime.Today;
                continue;
            }
            if (TryConvert(field.DataType, field.DefaultValue, out var value)) values[field.Key] = value;
        }
    }

    /// <summary>
    /// 字段正则来自元数据（管理员可配置），必须带 match timeout：无超时的回溯失控模式会把整个
    /// 保存请求拖住。编译结果按模式文本缓存，避免每次保存重复解析同一条模式。
    /// </summary>
    private static readonly ConcurrentDictionary<string, Regex> FieldRegexes =
        new(StringComparer.Ordinal);

    /// <summary>
    /// 取字段正则的编译结果；无法编译的模式（历史元数据）返回 null。保存侧不因一条坏配置
    /// 让整个模块写不进——语法在字段维护侧拦（<see cref="FieldAdminRepository"/> 的保存校验）。
    /// </summary>
    private static Regex? TryCompileFieldRegex(string pattern)
    {
        if (FieldRegexes.TryGetValue(pattern, out var cached)) return cached;
        // 模式数受字段元数据规模约束（数百条）；超出即视为异常输入，整体重建
        if (FieldRegexes.Count > 1024) FieldRegexes.Clear();
        try
        {
            var compiled = new Regex(pattern, RegexOptions.None, TimeSpan.FromSeconds(1));
            FieldRegexes[pattern] = compiled;
            return compiled;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// 字段值是否符合正则。匹配超时（回溯失控）按"不匹配"处理：宁可拒绝这一次输入，
    /// 也不让请求挂住。超时与不匹配共用 REGEX_MISMATCH，前端提示口径一致。
    /// </summary>
    private static bool MatchesFieldRegex(string textValue, string pattern)
    {
        if (TryCompileFieldRegex(pattern) is not { } regex) return true;
        try
        {
            return regex.IsMatch(textValue);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    public static IReadOnlyList<FieldError> CheckRequiredAndRegex(
        IReadOnlyList<FormFieldDefinition> fields,
        IReadOnlyDictionary<string, object?> values)
    {
        var errors = new List<FieldError>();
        foreach (var field in fields)
        {
            if (field.IsVirtual || field.ServerFilled) continue;
            var present = values.TryGetValue(field.Key, out var value);
            // 勾选/布尔字段永远有值（true/false），不适用"必填"语义
            var isBoolean = field.DataType.Contains("bit", StringComparison.OrdinalIgnoreCase);
            if (field.IsRequired && !isBoolean)
            {
                if (!present || value is null || value is string text && string.IsNullOrWhiteSpace(text))
                {
                    errors.Add(new FieldError(field.Key, "该字段不能为空。", "REQUIRED_FIELD_MISSING"));
                    continue;
                }
            }
            if (string.IsNullOrWhiteSpace(field.Regex) || !present || value is null) continue;
            var textValue = Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
            if (!MatchesFieldRegex(textValue, field.Regex))
                errors.Add(new FieldError(field.Key, "内容不符合格式要求。", "REGEX_MISMATCH"));
        }
        return errors;
    }

    /// <summary>
    /// 明细行序号：**既有行保留原号，新行取下一个未占用号**。
    ///
    /// 序号（SERIAL_NO）是明细行的身份而不是序号：下游单据按"单号 + 项次"引用明细行，
    /// 若每次保存都按提交顺序重赋 1..n，删掉中间一行就会让其后各行整体前移，
    /// 下游引用便静默指向另一行。因此调用方把各行原有的项次随请求回传（未回传=新行），
    /// 这里接纳既有号、再给新行分配下一个未占用的号——行序变化不再改变其它行的身份。
    /// </summary>
    public static void AssignSerialNumbers(
        IReadOnlyList<IDictionary<string, object?>> rows,
        IReadOnlyList<FormFieldDefinition> fields,
        IReadOnlyList<string?>? submittedSerials = null)
    {
        var serialField = fields.FirstOrDefault(field => field.Key.Equals("SERIAL_NO", StringComparison.OrdinalIgnoreCase));
        if (serialField is null) return;
        var used = new HashSet<int>();
        var assigned = new bool[rows.Count];
        for (var index = 0; index < rows.Count; index++)
        {
            if (rows[index].ContainsKey(serialField.Key))
            {
                // 行内已有值（服务端带入或界面显式提交）：视为已占用，避免新行撞号
                if (int.TryParse(Convert.ToString(rows[index][serialField.Key], CultureInfo.InvariantCulture), out var existing)
                    && existing > 0)
                {
                    used.Add(existing);
                }
                assigned[index] = true;
                continue;
            }
            if (submittedSerials is null || index >= submittedSerials.Count) continue;
            if (!int.TryParse(submittedSerials[index]?.Trim(), out var remembered) || remembered <= 0) continue;
            if (!used.Add(remembered)) continue;
            if (TryConvert(serialField.DataType, remembered.ToString(CultureInfo.InvariantCulture), out var value))
            {
                rows[index][serialField.Key] = value;
                assigned[index] = true;
            }
        }
        // 新行取「已用最大号 +1」，**不复用空洞**：被删行留下的项次可能仍被下游单据引用，
        // 复用那个号会让新行顶替它的身份。号只会向上增长，历史引用的去向保持稳定。
        var next = used.Count == 0 ? 1 : used.Max() + 1;
        for (var index = 0; index < rows.Count; index++)
        {
            if (assigned[index]) continue;
            if (TryConvert(serialField.DataType, next.ToString(CultureInfo.InvariantCulture), out var value))
                rows[index][serialField.Key] = value;
            used.Add(next);
            next++;
        }
    }

    /// <summary>
    /// 数值输入规范化：全角数字/句点转半角、去除千分位逗号。
    /// 仅用于数值类型分支；日期与文本不受影响（文本 trim 由 ValidateSubmitted 处理）。
    /// </summary>
    private static string NormalizeNumericText(string text)
    {
        var builder = new System.Text.StringBuilder(text.Length);
        foreach (var character in text)
        {
            if (character >= '０' && character <= '９') builder.Append((char)(character - '０' + '0'));
            else if (character == '．') builder.Append('.');
            else builder.Append(character);
        }
        return builder.ToString().Replace(",", string.Empty);
    }

    public static bool TryConvert(string dataType, string? raw, out object? value)
    {
        value = null;
        if (string.IsNullOrWhiteSpace(raw))
        {
            value = null;
            return true;
        }
        var type = dataType.ToLowerInvariant();
        try
        {
            var text = raw.Trim();
            if (type.Contains("bit", StringComparison.Ordinal))
            {
                value = text.Equals("true", StringComparison.OrdinalIgnoreCase)
                    || text.Equals("1", StringComparison.Ordinal)
                    || text.Equals("是", StringComparison.Ordinal);
            }
            else if (type.Contains("datetime", StringComparison.Ordinal) || type.Contains("smalldatetime", StringComparison.Ordinal)
                     || type.Contains("date", StringComparison.Ordinal) || type.Contains("time", StringComparison.Ordinal))
            {
                value = DateTime.Parse(text, CultureInfo.InvariantCulture);
            }
            else if (type.Contains("int", StringComparison.Ordinal))
            {
                value = int.Parse(NormalizeNumericText(text), CultureInfo.InvariantCulture);
            }
            else if (type.Contains("float", StringComparison.Ordinal) || type.Contains("real", StringComparison.Ordinal))
            {
                value = double.Parse(NormalizeNumericText(text), CultureInfo.InvariantCulture);
            }
            else if (type.Contains("decimal", StringComparison.Ordinal) || type.Contains("numeric", StringComparison.Ordinal)
                     || type.Contains("money", StringComparison.Ordinal))
            {
                value = decimal.Parse(NormalizeNumericText(text), NumberStyles.Number, CultureInfo.InvariantCulture);
            }
            else
            {
                value = raw;
            }
            return true;
        }
        catch
        {
            return false;
        }
    }
}
