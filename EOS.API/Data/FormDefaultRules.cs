namespace EOS.API.Data;

/// <summary>
/// 默认值扩展点（B6）：按模块登记"页面代码默认值/自动单号"的受控等价规则。
/// 原则：未登记不臆造；仅在新增（new）时应用；只作用于可写且未提供值的字段；
/// 规则值支持动态 token：@today = 当天日期（仅对日期/时间类型字段生效）。
/// 登记来源：旧页面代码核对（例：ERP/CLIENT/Huajing/Order.aspx.cs OnInit 中 REBATE=100）。
/// </summary>
internal static class FormDefaultRules
{
    private static readonly IReadOnlyDictionary<int, IReadOnlyDictionary<string, string>> RegisteredRules =
        new Dictionary<int, IReadOnlyDictionary<string, string>>
        {
            // 1405 客户订单：新增时默认折扣 100（对齐旧 Order.aspx.cs OnInit）
            [1405] = new Dictionary<string, string> { ["REBATE"] = "100" },
        };

    public static void Apply(
        int moduleId,
        IReadOnlyList<FormFieldDefinition> fields,
        IDictionary<string, object?> values,
        IReadOnlyDictionary<string, string>? rulesOverride = null)
    {
        var rules = rulesOverride ?? (RegisteredRules.TryGetValue(moduleId, out var registered) ? registered : null);
        if (rules is null) return;
        foreach (var field in fields)
        {
            if (field.IsReadonly || field.IsVirtual || field.ServerFilled || values.ContainsKey(field.Key)) continue;
            if (!rules.TryGetValue(field.Key, out var raw) || string.IsNullOrWhiteSpace(raw)) continue;
            if (raw == "@today")
            {
                var type = field.DataType.ToLowerInvariant();
                if (type.Contains("date") || type.Contains("time")) values[field.Key] = DateTime.Today;
                continue;
            }
            if (RecordPayloadValidator.TryConvert(field.DataType, raw, out var value)) values[field.Key] = value;
        }
    }
}
