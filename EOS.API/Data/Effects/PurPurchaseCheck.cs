using System.Data;
using System.Globalization;
using System.Text.Json;
using EOS.API.Data.Effects.ServiceEffectHandlers;
using Microsoft.Data.SqlClient;

namespace EOS.API.Data.Effects;

/// <summary>
/// pur-purchase-check: 采购单保存期的两条日期/计价判据（原 `PurDomainRules.PurPurchaseAfterSaveAsync` 的校验段）：
///   ① 产品计价有效期：所引用的厂商计价明细 `IN_EFFECT_DATE` 早于采购日期 ⇒ 拒（回报产品号）；
///   ② 预交日期不得早于采购单日期 ⇒ 拒（回报明细序号）。
/// 两条都是"明细/主表日期与另一张表日期比较"，模板表达不了 ⇒ 走 `custom-validation`（注册代码）。
/// </summary>
internal static class PurPurchaseCheck
{
    public const string HandlerKey = "pur-purchase-check";

    public static async Task<string?> CheckAsync(
        CustomValidationContext context, JsonElement root, CancellationToken token)
    {
        if (context.MasterKeyValues.Count < 2)
            throw new EffectConfigException("pur-purchase-check 缺少单据主键值。");
        if (string.IsNullOrWhiteSpace(context.Plan.MasterTable))
            throw new EffectConfigException("pur-purchase-check 需要主表形态。");
        var columns = await new EffectPhysicalColumns().LoadAsync(context.Connection, token, context.Transaction);
        var config = Parse(root, context.Plan.MasterTable, columns);
        var type = context.MasterKeyValues[0] ?? string.Empty;
        var no = context.MasterKeyValues[1] ?? string.Empty;

        var master = Q(context.Plan.MasterTable);
        var detail = Q(config.Detail.Table);
        var supplierPrice = Q(config.SupplierPrice.Table);

        // ① 产品计价有效期（同厂商 + 产品 + 币别 + 税别 + 税种匹配的计价行）
        var priceExpiredSql = $"""
            SELECT DISTINCT a.{Q(config.Detail.ProductField)} FROM dbo.{detail} a
            INNER JOIN dbo.{master} m ON m.{Q(config.Master.TypeField)}=a.{Q(config.Master.TypeField)}
                                     AND m.{Q(config.Master.NoField)}=a.{Q(config.Master.NoField)}
            INNER JOIN dbo.{supplierPrice} p
              ON p.{Q(config.SupplierPrice.SupplierField)}=m.{Q(config.Master.SupplierField)}
             AND p.{Q(config.SupplierPrice.ProductField)}=a.{Q(config.Detail.ProductField)}
             AND p.{Q(config.SupplierPrice.CurrencyField)}=a.{Q(config.Detail.CurrencyField)}
             AND p.{Q(config.SupplierPrice.TaxIdField)}=a.{Q(config.Detail.TaxIdField)}
             AND p.{Q(config.SupplierPrice.TaxTypeField)}=a.{Q(config.Detail.TaxTypeField)}
            WHERE a.{Q(config.Master.TypeField)}=@Type AND a.{Q(config.Master.NoField)}=@No
              AND p.{Q(config.SupplierPrice.InEffectDateField)} < m.{Q(config.Master.DateField)};
            """;
        var priceExpired = await LinesAsync(context, priceExpiredSql, type, no, token, reader => Str(reader, 0));
        if (priceExpired is not null)
            return config.Messages.PriceExpired + priceExpired;

        // ② 预交日期不得早于采购单日期
        var deliverySql = $"""
            SELECT d.{Q(config.Detail.SerialField)} FROM dbo.{detail} d
            INNER JOIN dbo.{master} m ON m.{Q(config.Master.TypeField)}=d.{Q(config.Master.TypeField)}
                                     AND m.{Q(config.Master.NoField)}=d.{Q(config.Master.NoField)}
            WHERE d.{Q(config.Master.TypeField)}=@Type AND d.{Q(config.Master.NoField)}=@No
              AND d.{Q(config.Detail.PlanDeliveryDateField)} < m.{Q(config.Master.DateField)};
            """;
        var deliveryLines = await LinesAsync(context, deliverySql, type, no, token,
            reader => Convert.ToInt32(reader.GetValue(0)).ToString(CultureInfo.InvariantCulture));
        if (deliveryLines is not null)
            return config.Messages.DeliveryDate + deliveryLines;
        return null;
    }

    private static string Q(string identifier) => ServiceEffectSql.Q(identifier);

    private static async Task<string?> LinesAsync(CustomValidationContext context, string sql, string type, string no,
        CancellationToken token, Func<SqlDataReader, string> format)
    {
        var lines = new List<string>();
        await using var command = new SqlCommand(sql, context.Connection, context.Transaction);
        command.Parameters.AddWithValue("@Type", type);
        command.Parameters.AddWithValue("@No", no);
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token)) lines.Add(format(reader));
        return lines.Count == 0 ? null : string.Join("\r\n", lines);
    }

    private static string Str(SqlDataReader reader, int index)
        => reader.IsDBNull(index) ? string.Empty : reader.GetValue(index).ToString()?.Trim() ?? string.Empty;

    /// <summary>参数解析（fail-closed：三张表的每个列名都必须物理存在，文案非空）。</summary>
    internal static PurPurchaseCheckConfig Parse(JsonElement root, string masterTable, ISet<string> columns)
    {
        const string label = "pur-purchase-check";
        var master = Section(root, "master", label);
        var detail = Section(root, "detail", label);
        var supplierPrice = Section(root, "supplierPrice", label);
        var messages = Section(root, "messages", label);
        var config = new PurPurchaseCheckConfig(
            new PurPurchaseMasterFields(Required(master, "typeField"), Required(master, "noField"),
                Required(master, "dateField"), Required(master, "supplierField")),
            new PurPurchaseDetailFields(Required(detail, "table"), Required(detail, "productField"),
                Required(detail, "serialField"), Required(detail, "planDeliveryDateField"),
                Required(detail, "currencyField"), Required(detail, "taxIdField"), Required(detail, "taxTypeField")),
            new PurPurchaseSupplierPriceFields(Required(supplierPrice, "table"),
                Required(supplierPrice, "supplierField"), Required(supplierPrice, "productField"),
                Required(supplierPrice, "currencyField"), Required(supplierPrice, "taxIdField"),
                Required(supplierPrice, "taxTypeField"), Required(supplierPrice, "inEffectDateField")),
            new PurPurchaseMessages(Required(messages, "priceExpired"), Required(messages, "deliveryDate")));
        foreach (var (table, column) in new[]
                 {
                     (masterTable, config.Master.TypeField), (masterTable, config.Master.NoField),
                     (masterTable, config.Master.DateField), (masterTable, config.Master.SupplierField),
                     (config.Detail.Table, config.Master.TypeField), (config.Detail.Table, config.Master.NoField),
                     (config.Detail.Table, config.Detail.ProductField), (config.Detail.Table, config.Detail.SerialField),
                     (config.Detail.Table, config.Detail.PlanDeliveryDateField),
                     (config.Detail.Table, config.Detail.CurrencyField),
                     (config.Detail.Table, config.Detail.TaxIdField),
                     (config.Detail.Table, config.Detail.TaxTypeField),
                     (config.SupplierPrice.Table, config.SupplierPrice.SupplierField),
                     (config.SupplierPrice.Table, config.SupplierPrice.ProductField),
                     (config.SupplierPrice.Table, config.SupplierPrice.CurrencyField),
                     (config.SupplierPrice.Table, config.SupplierPrice.TaxIdField),
                     (config.SupplierPrice.Table, config.SupplierPrice.TaxTypeField),
                     (config.SupplierPrice.Table, config.SupplierPrice.InEffectDateField),
                 })
        {
            if (!columns.Contains(table + "." + column))
                throw new EffectConfigException($"{label} 列不存在：{table}.{column}。");
        }
        return config;
    }

    private static JsonElement Section(JsonElement root, string name, string label)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Object
            ? value
            : throw new EffectConfigException($"{label} 缺少对象字段 {name}。");

    private static string Required(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(value.GetString())
                ? value.GetString()!.Trim()
                : throw new EffectConfigException($"pur-purchase-check 缺少字符串字段 {name}。");
}

internal sealed record PurPurchaseMasterFields(string TypeField, string NoField, string DateField,
    string SupplierField);
internal sealed record PurPurchaseDetailFields(string Table, string ProductField, string SerialField,
    string PlanDeliveryDateField, string CurrencyField, string TaxIdField, string TaxTypeField);
internal sealed record PurPurchaseSupplierPriceFields(string Table, string SupplierField, string ProductField,
    string CurrencyField, string TaxIdField, string TaxTypeField, string InEffectDateField);
internal sealed record PurPurchaseMessages(string PriceExpired, string DeliveryDate);
internal sealed record PurPurchaseCheckConfig(PurPurchaseMasterFields Master, PurPurchaseDetailFields Detail,
    PurPurchaseSupplierPriceFields SupplierPrice, PurPurchaseMessages Messages);
