using EOS.API.Models;

namespace EOS.API.Data;

/// <summary>
/// 默认值扩展点：按模块登记"页面代码默认值/自动单号"。
/// 原则：未登记不臆造；仅在新增（new）时应用；只作用于可写且未提供值的字段；
/// 规则值支持动态 token：@today = 当天日期（仅对日期/时间类型字段生效）；
/// @today:yyyyMM = 按格式生成字符串（如工时表月份 COUNT_MONTH=当前年月）。
/// 登记来源： code-behind 核对。
/// </summary>
internal static class FormDefaultRules
{
    private static readonly IReadOnlyDictionary<int, IReadOnlyDictionary<string, string>> RegisteredRules =
        new Dictionary<int, IReadOnlyDictionary<string, string>>
        {
            // 单据默认折扣 100
            [1404] = new Dictionary<string, string> { ["REBATE"] = "100" }, // 报价单 
            [1604] = new Dictionary<string, string> { ["REBATE"] = "100" }, // 厂商报价单 
            [1606] = new Dictionary<string, string> { ["REBATE"] = "100" }, // 采购单 
            [1405] = new Dictionary<string, string> { ["REBATE"] = "100" },
            // 退货/退料 SEND_TAG 默认 0：与模块 FILTER 契约一致（1407/1608 FILTER=SEND_TAG=0，
            // 全库数据亦为 0；旧默认 1 会让新建记录落在过滤范围外， 收紧时修正）
            [1407] = new Dictionary<string, string> { ["SEND_TAG"] = "0" }, // 退货单 
            [1409] = new Dictionary<string, string> { ["SEND_TAG"] = "1" }, // 扣款退货单
            [1608] = new Dictionary<string, string> { ["SEND_TAG"] = "0" }, // 退料单 
            [1612] = new Dictionary<string, string> { ["SEND_TAG"] = "1" }, // 扣款退料单
            // 成品请购单：默认请购类型 QG
            [1615] = new Dictionary<string, string> { ["APPLY_TYPE"] = "QG" },
            // 产品资料：新增默认成品 + 自制（dro 默认选中项）
            [1201] = new Dictionary<string, string> { ["PRO_TYPE"] = "1", ["MAIN_SOURCE"] = "2" },
            // 退料单(生产不良)：默认退料类别 2（dro_BACK_CODE 默认选中）
            [1423] = new Dictionary<string, string> { ["BACK_CODE"] = "2" },
            // 生产领料（按 m 参数勾选：m=2 重工、m=3 托外、m=4 托外+重工）
            [1514] = new Dictionary<string, string> { ["REWORK_TAG"] = "1", ["OUTSIDE_TAG"] = "0" },
            [2805] = new Dictionary<string, string> { ["OUTSIDE_TAG"] = "1" },
            [2806] = new Dictionary<string, string> { ["OUTSIDE_TAG"] = "1", ["REWORK_TAG"] = "1" },
            // 制令单（同规则）
            [1512] = new Dictionary<string, string> { ["REWORK_TAG"] = "1", ["OUTSIDE_TAG"] = "0" },
            [2803] = new Dictionary<string, string> { ["OUTSIDE_TAG"] = "1" },
            [2804] = new Dictionary<string, string> { ["OUTSIDE_TAG"] = "1", ["REWORK_TAG"] = "1" },
            // 工时录入表：新增默认当前年月
            [180207] = new Dictionary<string, string> { ["COUNT_MONTH"] = "@today:yyyyMM" },
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
            if (raw.StartsWith("@today:", StringComparison.Ordinal))
            {
                var format = raw["@today:".Length..];
                values[field.Key] = DateTime.Today.ToString(format, System.Globalization.CultureInfo.InvariantCulture);
                continue;
            }
            if (RecordPayloadValidator.TryConvert(field.DataType, raw, out var value)) values[field.Key] = value;
        }
    }
}
