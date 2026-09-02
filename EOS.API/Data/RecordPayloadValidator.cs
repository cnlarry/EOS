using System.Globalization;
using System.Text.RegularExpressions;

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
        ["CREATE_PERSON", "CREATE_DATE", "LAST_UPDATE_BY", "LAST_UPDATE_DATE",
         "CONFIRM_PERSON", "CONFIRM_DATE", "FINISHED_PERSON", "FINISHED_DATE"],
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
    /// decimal/numeric 超精度校验（ADR-006 决策 2.4）：precision/scale 源自 sys.types 随 form-definition 下发。
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
            // 服务端持有（serverFilled）与虚拟字段拒绝客户端提交；
            // 只读但可见的联动字段（如 CURR_RATE/CURR_ID/TAX_ID）旧系统由前端联动带值随保存提交，
            // 因此允许提交并继续做类型/长度校验（用户无法直接修改，值仍受服务端校验约束）。
            if (field.IsVirtual || field.ServerFilled || field.DisplayOnly)
            {
                errors.Add(new FieldError(key, "该字段由服务端维护，不可提交。", "READONLY_FIELD"));
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
            if (!Regex.IsMatch(textValue, field.Regex))
                errors.Add(new FieldError(field.Key, "内容不符合格式要求。", "REGEX_MISMATCH"));
        }
        return errors;
    }

    /// <summary>
    /// 明细行序号自动编号（对齐旧系统 SetSerialNo）：
    /// 明细存在 SERIAL_NO 字段且行内未提供时，按 1..n 顺序赋值。
    /// </summary>
    public static void AssignSerialNumbers(IReadOnlyList<IDictionary<string, object?>> rows, IReadOnlyList<FormFieldDefinition> fields)
    {
        var serialField = fields.FirstOrDefault(field => field.Key.Equals("SERIAL_NO", StringComparison.OrdinalIgnoreCase));
        if (serialField is null) return;
        for (var index = 0; index < rows.Count; index++)
        {
            if (rows[index].ContainsKey(serialField.Key)) continue;
            if (TryConvert(serialField.DataType, (index + 1).ToString(CultureInfo.InvariantCulture), out var value))
                rows[index][serialField.Key] = value;
        }
    }

    /// <summary>
    /// 数值输入规范化（ADR-006 决策 2.5）：全角数字/句点转半角、去除千分位逗号。
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
