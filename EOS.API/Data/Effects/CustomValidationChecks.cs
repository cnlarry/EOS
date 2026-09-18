using System.Data;
using System.Globalization;
using System.Text.Json;
using EOS.API.Data.Effects.ServiceEffectHandlers;
using Microsoft.Data.SqlClient;

namespace EOS.API.Data.Effects;

/// <summary>
/// 定制校验的执行上下文（与效果链共用连接/事务：校验与效果同处一个保存事务）。
/// </summary>
internal sealed record CustomValidationContext(
    SqlConnection Connection,
    SqlTransaction Transaction,
    ModuleEffectPlan Plan,
    IReadOnlyList<string> MasterKeyValues);

/// <summary>
/// `custom-validation` 模板的注册表：**闭集**——校验键由代码注册，配置只能引用已注册的实现
/// （未注册即 fail-closed 报配置错）。用于"现有模板表达不了的表达式级跨表判据"，
/// 见 ADR-012 §14.2 的 `custom-validation` 行。
/// </summary>
internal static class CustomValidationChecks
{
    private static readonly Dictionary<string, Func<CustomValidationContext, JsonElement, CancellationToken, Task<string?>>>
        Checks = new(StringComparer.OrdinalIgnoreCase)
        {
            [CopOrderCheck.HandlerKey] = CopOrderCheck.CheckAsync,
            [CopSendCheck.HandlerKey] = CopSendCheck.CheckAsync,
        };

    public static bool TryGet(string key,
        out Func<CustomValidationContext, JsonElement, CancellationToken, Task<string?>> check)
        => Checks.TryGetValue(key, out check!);

    public static IReadOnlyCollection<string> Keys => Checks.Keys;
}

/// <summary>
/// cop-order-check: 客户订单保存期的八条判据（原 `CopDomainRules.CopOrderAfterSaveAsync`，判据来源旧过程
/// `P_COP_ORDER_After_Save` 调用的 `P_COP_ORDER_CHECK`）。这些判据都是**表达式级跨表比较**
/// （如"客户最低订单额 × 客户币别汇率 &gt; 订单价税合计 × 订单币别汇率"、"日期差超过客户/产品交易天数"、
/// "订单量低于产品最小生产量"、"产品计价已过有效期"、"客户订单号重复"、"预交日期早于订单日期"），
/// 现有六个校验模板都表达不了 ⇒ 按 ADR-012 §14.2 的 **`custom-validation`（注册代码）** 承载：
/// 整族判据按旧顺序逐条执行，命中即以 `EffectValidationException` 阻断保存（文案与旧实现逐字一致）。
/// 参数闭合：八张表与各列名分组声明，全部校验为物理列；单据键值只作参数传入。
/// </summary>
internal static class CopOrderCheck
{
    public const string HandlerKey = "cop-order-check";

    public static async Task<string?> CheckAsync(
        CustomValidationContext context, JsonElement root, CancellationToken token)
    {
        if (context.MasterKeyValues.Count < 2)
            throw new EffectConfigException("cop-order-check 缺少单据主键值。");
        if (string.IsNullOrWhiteSpace(context.Plan.MasterTable))
            throw new EffectConfigException("cop-order-check 需要主表形态。");
        var columns = await new EffectPhysicalColumns().LoadAsync(context.Connection, token, context.Transaction);
        var config = Parse(root, context.Plan.MasterTable, columns);
        var type = context.MasterKeyValues[0] ?? string.Empty;
        var no = context.MasterKeyValues[1] ?? string.Empty;

        // 标识符一律经 Q() 加方括号；下面用插值拼出各条判据的 SQL（列名全部来自闭合参数）。
        var master = Q(context.Plan.MasterTable);
        var detail = Q(config.Detail.Table);
        var client = Q(config.Client.Table);
        var currency = Q(config.Currency.Table);
        var product = Q(config.Product.Table);
        var clientPrice = Q(config.ClientPrice.Table);
        var settings = Q(config.Settings.Table);

        var keyScope = $"m.{Q(config.Master.TypeField)}=@Type AND m.{Q(config.Master.NoField)}=@No";
        var detailScope = $"o.{Q(config.Master.TypeField)}=@Type AND o.{Q(config.Master.NoField)}=@No";
        var detailScopeD = $"d.{Q(config.Master.TypeField)}=@Type AND d.{Q(config.Master.NoField)}=@No";

        // ① 客户交易天数（系统设置的交易天数与客户最后交易日期都非空才判）
        var tradeDaysSql = $"""
            SELECT TOP 1 1 FROM dbo.{master} m
            CROSS JOIN (SELECT TOP 1 {Q(config.Settings.ClientDaysField)} FROM dbo.{settings}) s
            JOIN dbo.{client} c ON c.{Q(config.Client.KeyField)}=m.{Q(config.Master.ClientField)}
            WHERE {keyScope} AND s.{Q(config.Settings.ClientDaysField)} IS NOT NULL
              AND c.{Q(config.Client.LastTradeDateField)} IS NOT NULL
              AND s.{Q(config.Settings.ClientDaysField)}
                  < DATEDIFF(day, c.{Q(config.Client.LastTradeDateField)}, m.{Q(config.Master.DateField)});
            """;
        if (await ExistsAsync(context, tradeDaysSql, type, no, token))
            return config.Messages.TradeDays;

        // ② 最低订单金额（客户维护了最低订单额才判）
        var minOrderSql = $"""
            SELECT c.{Q(config.Client.MinOrderAmountField)}, c.{Q(config.Client.CurrencyField)} FROM dbo.{master} m
            JOIN dbo.{client} c ON c.{Q(config.Client.KeyField)}=m.{Q(config.Master.ClientField)}
            LEFT JOIN dbo.{currency} r ON r.{Q(config.Currency.KeyField)}=c.{Q(config.Client.CurrencyField)}
            WHERE {keyScope} AND ISNULL(c.{Q(config.Client.MinOrderAmountField)},0) > 0
              AND c.{Q(config.Client.MinOrderAmountField)} * ISNULL(r.{Q(config.Currency.RateField)},1)
                  > ISNULL(m.{Q(config.Master.AmountTaxField)},0) * ISNULL(m.{Q(config.Master.CurrencyRateField)},1);
            """;
        var minOrder = await LinesAsync(context, minOrderSql, type, no, token,
            reader => Num(reader, 0) + Str(reader, 1));
        if (minOrder is not null)
            return config.Messages.MinOrder + minOrder;

        // ③ 客户信用余额（信用额度为空时不启用——与旧实现的三值语义一致）
        var creditSql = $"""
            SELECT c.{Q(config.Client.CreditLimitField)} * ISNULL(r.{Q(config.Currency.RateField)},1)
                       - ISNULL(m.{Q(config.Master.AmountTaxField)},0) * ISNULL(m.{Q(config.Master.CurrencyRateField)},1),
                   c.{Q(config.Client.CurrencyField)}
            FROM dbo.{master} m JOIN dbo.{client} c ON c.{Q(config.Client.KeyField)}=m.{Q(config.Master.ClientField)}
            LEFT JOIN dbo.{currency} r ON r.{Q(config.Currency.KeyField)}=c.{Q(config.Client.CurrencyField)}
            WHERE {keyScope}
              AND c.{Q(config.Client.CreditLimitField)} * ISNULL(r.{Q(config.Currency.RateField)},1)
                  < ISNULL(m.{Q(config.Master.AmountTaxField)},0) * ISNULL(m.{Q(config.Master.CurrencyRateField)},1);
            """;
        var credit = await LinesAsync(context, creditSql, type, no, token,
            reader => Round2(Num(reader, 0)) + Str(reader, 1));
        if (credit is not null)
            return config.Messages.Credit + credit;

        // ④ 产品交易天数（最多回报 10 个产品号）
        var productDaysSql = $"""
            SELECT TOP 10 p.{Q(config.Product.KeyField)} FROM dbo.{detail} o
            JOIN dbo.{master} m ON m.{Q(config.Master.TypeField)}=o.{Q(config.Master.TypeField)}
                               AND m.{Q(config.Master.NoField)}=o.{Q(config.Master.NoField)}
            JOIN dbo.{client} c ON c.{Q(config.Client.KeyField)}=m.{Q(config.Master.ClientField)}
            JOIN dbo.{product} p ON p.{Q(config.Product.KeyField)}=o.{Q(config.Detail.ProductField)}
            CROSS JOIN (SELECT TOP 1 {Q(config.Settings.ProductDaysField)} FROM dbo.{settings}) s
            WHERE {detailScope} AND s.{Q(config.Settings.ProductDaysField)} IS NOT NULL
              AND c.{Q(config.Client.LastTradeDateField)} IS NOT NULL
              AND s.{Q(config.Settings.ProductDaysField)}
                  < DATEDIFF(day, p.{Q(config.Product.LastTradeDateField)}, m.{Q(config.Master.DateField)});
            """;
        var productDays = await LinesAsync(context, productDaysSql, type, no, token, reader => Str(reader, 0));
        if (productDays is not null)
            return config.Messages.ProductDays + productDays;

        // ⑤ 产品计价有效期
        var priceExpiredSql = $"""
            SELECT DISTINCT a.{Q(config.Detail.ProductField)} FROM dbo.{detail} a
            JOIN dbo.{master} m ON m.{Q(config.Master.TypeField)}=a.{Q(config.Master.TypeField)}
                               AND m.{Q(config.Master.NoField)}=a.{Q(config.Master.NoField)}
            JOIN dbo.{clientPrice} p ON p.{Q(config.ClientPrice.ClientField)}=m.{Q(config.Master.ClientField)}
                                    AND p.{Q(config.ClientPrice.ProductField)}=a.{Q(config.Detail.ProductField)}
            WHERE a.{Q(config.Master.TypeField)}=@Type AND a.{Q(config.Master.NoField)}=@No
              AND p.{Q(config.ClientPrice.InEffectDateField)} < m.{Q(config.Master.DateField)};
            """;
        var priceExpired = await LinesAsync(context, priceExpiredSql, type, no, token, reader => Str(reader, 0));
        if (priceExpired is not null)
            return config.Messages.PriceExpired + priceExpired;

        // ⑥ 最小生产数量
        var minProduceSql = $"""
            SELECT TOP 10 p.{Q(config.Product.KeyField)} FROM dbo.{detail} o
            JOIN dbo.{product} p ON p.{Q(config.Product.KeyField)}=o.{Q(config.Detail.ProductField)}
            WHERE {detailScope} AND o.{Q(config.Detail.QtyField)} < ISNULL(p.{Q(config.Product.MinProduceQtyField)},0);
            """;
        var minProduce = await LinesAsync(context, minProduceSql, type, no, token, reader => Str(reader, 0));
        if (minProduce is not null)
            return config.Messages.MinProduce + minProduce;

        // ⑦ 客户订单号不重复（排除本单）
        var duplicateSql = $"""
            SELECT TOP 1 1 FROM dbo.{master}
            WHERE {Q(config.Master.ClientOrderNoField)}=(SELECT {Q(config.Master.ClientOrderNoField)} FROM dbo.{master}
                                                          WHERE {Q(config.Master.TypeField)}=@Type AND {Q(config.Master.NoField)}=@No)
              AND ISNULL({Q(config.Master.ClientOrderNoField)},'')<>''
              AND NOT ({Q(config.Master.TypeField)}=@Type AND {Q(config.Master.NoField)}=@No);
            """;
        if (await ExistsAsync(context, duplicateSql, type, no, token))
            return config.Messages.DuplicateOrderNo;

        // ⑧ 预交日期不得早于订单日期
        var preSendSql = $"""
            SELECT d.{Q(config.Detail.SerialField)} FROM dbo.{detail} d
            JOIN dbo.{master} m ON m.{Q(config.Master.TypeField)}=d.{Q(config.Master.TypeField)}
                               AND m.{Q(config.Master.NoField)}=d.{Q(config.Master.NoField)}
            WHERE {detailScopeD} AND d.{Q(config.Detail.PreSendDateField)} < m.{Q(config.Master.DateField)};
            """;
        var preSend = await LinesAsync(context, preSendSql, type, no, token,
            reader => Convert.ToInt32(reader.GetValue(0)).ToString(CultureInfo.InvariantCulture));
        if (preSend is not null)
            return config.Messages.PreSend + preSend;
        return null;
    }

    private static string Q(string identifier) => ServiceEffectSql.Q(identifier);

    private static async Task<bool> ExistsAsync(
        CustomValidationContext context, string sql, string type, string no, CancellationToken token)
    {
        await using var command = new SqlCommand(sql, context.Connection, context.Transaction);
        command.Parameters.AddWithValue("@Type", type);
        command.Parameters.AddWithValue("@No", no);
        return await command.ExecuteScalarAsync(token) is not null;
    }

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

    private static string Num(SqlDataReader reader, int index)
        => reader.IsDBNull(index)
            ? "0"
            : Convert.ToDouble(reader.GetValue(index)).ToString("0.######", CultureInfo.InvariantCulture);

    private static string Round2(string value)
        => double.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed)
            ? Math.Round(parsed, 2).ToString("0.##", CultureInfo.InvariantCulture)
            : value;

    private static string Str(SqlDataReader reader, int index)
        => reader.IsDBNull(index) ? string.Empty : reader.GetValue(index).ToString()?.Trim() ?? string.Empty;

    /// <summary>参数解析（fail-closed：八张表的每个列名都必须物理存在，八条文案均非空）。</summary>
    internal static CopOrderCheckConfig Parse(JsonElement root, string masterTable, ISet<string> columns)
    {
        const string label = "cop-order-check";
        var master = Section(root, "master", label);
        var detail = Section(root, "detail", label);
        var client = Section(root, "client", label);
        var currency = Section(root, "currency", label);
        var product = Section(root, "product", label);
        var clientPrice = Section(root, "clientPrice", label);
        var settings = Section(root, "settings", label);
        var messages = Section(root, "messages", label);
        var config = new CopOrderCheckConfig(
            new CopOrderMasterFields(Required(master, "typeField"), Required(master, "noField"),
                Required(master, "dateField"), Required(master, "amountTaxField"),
                Required(master, "clientField"), Required(master, "currencyRateField"),
                Required(master, "clientOrderNoField")),
            new CopOrderDetailFields(Required(detail, "table"), Required(detail, "productField"),
                Required(detail, "qtyField"), Required(detail, "preSendDateField"), Required(detail, "serialField")),
            new CopOrderClientFields(Required(client, "table"), Required(client, "keyField"),
                Required(client, "lastTradeDateField"), Required(client, "creditLimitField"),
                Required(client, "minOrderAmountField"), Required(client, "currencyField")),
            new CopOrderCurrencyFields(Required(currency, "table"), Required(currency, "keyField"),
                Required(currency, "rateField")),
            new CopOrderProductFields(Required(product, "table"), Required(product, "keyField"),
                Required(product, "lastTradeDateField"), Required(product, "minProduceQtyField")),
            new CopOrderClientPriceFields(Required(clientPrice, "table"), Required(clientPrice, "clientField"),
                Required(clientPrice, "productField"), Required(clientPrice, "inEffectDateField")),
            new CopOrderSettingsFields(Required(settings, "table"), Required(settings, "clientDaysField"),
                Required(settings, "productDaysField")),
            new CopOrderMessages(Required(messages, "tradeDays"), Required(messages, "minOrder"),
                Required(messages, "credit"), Required(messages, "productDays"), Required(messages, "priceExpired"),
                Required(messages, "minProduce"), Required(messages, "duplicateOrderNo"),
                Required(messages, "preSend")));
        foreach (var (table, column) in new[]
                 {
                     (masterTable, config.Master.TypeField), (masterTable, config.Master.NoField),
                     (masterTable, config.Master.DateField), (masterTable, config.Master.AmountTaxField),
                     (masterTable, config.Master.ClientField), (masterTable, config.Master.CurrencyRateField),
                     (masterTable, config.Master.ClientOrderNoField),
                     (config.Detail.Table, config.Detail.ProductField), (config.Detail.Table, config.Detail.QtyField),
                     (config.Detail.Table, config.Detail.PreSendDateField),
                     (config.Detail.Table, config.Detail.SerialField),
                     (config.Detail.Table, config.Master.TypeField), (config.Detail.Table, config.Master.NoField),
                     (config.Client.Table, config.Client.KeyField),
                     (config.Client.Table, config.Client.LastTradeDateField),
                     (config.Client.Table, config.Client.CreditLimitField),
                     (config.Client.Table, config.Client.MinOrderAmountField),
                     (config.Client.Table, config.Client.CurrencyField),
                     (config.Currency.Table, config.Currency.KeyField),
                     (config.Currency.Table, config.Currency.RateField),
                     (config.Product.Table, config.Product.KeyField),
                     (config.Product.Table, config.Product.LastTradeDateField),
                     (config.Product.Table, config.Product.MinProduceQtyField),
                     (config.ClientPrice.Table, config.ClientPrice.ClientField),
                     (config.ClientPrice.Table, config.ClientPrice.ProductField),
                     (config.ClientPrice.Table, config.ClientPrice.InEffectDateField),
                     (config.Settings.Table, config.Settings.ClientDaysField),
                     (config.Settings.Table, config.Settings.ProductDaysField),
                 })
        {
            if (!columns.Contains(table + "." + column))
                throw new EffectConfigException($"cop-order-check 列不存在：{table}.{column}。");
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
                : throw new EffectConfigException($"cop-order-check 缺少字符串字段 {name}。");
}

internal sealed record CopOrderMasterFields(string TypeField, string NoField, string DateField, string AmountTaxField,
    string ClientField, string CurrencyRateField, string ClientOrderNoField);
internal sealed record CopOrderDetailFields(string Table, string ProductField, string QtyField,
    string PreSendDateField, string SerialField);
internal sealed record CopOrderClientFields(string Table, string KeyField, string LastTradeDateField,
    string CreditLimitField, string MinOrderAmountField, string CurrencyField);
internal sealed record CopOrderCurrencyFields(string Table, string KeyField, string RateField);
internal sealed record CopOrderProductFields(string Table, string KeyField, string LastTradeDateField,
    string MinProduceQtyField);
internal sealed record CopOrderClientPriceFields(string Table, string ClientField, string ProductField,
    string InEffectDateField);
internal sealed record CopOrderSettingsFields(string Table, string ClientDaysField, string ProductDaysField);
internal sealed record CopOrderMessages(string TradeDays, string MinOrder, string Credit, string ProductDays,
    string PriceExpired, string MinProduce, string DuplicateOrderNo, string PreSend);
internal sealed record CopOrderCheckConfig(CopOrderMasterFields Master, CopOrderDetailFields Detail,
    CopOrderClientFields Client, CopOrderCurrencyFields Currency, CopOrderProductFields Product,
    CopOrderClientPriceFields ClientPrice, CopOrderSettingsFields Settings, CopOrderMessages Messages);
