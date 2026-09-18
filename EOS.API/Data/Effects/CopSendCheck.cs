using System.Data;
using System.Globalization;
using System.Text.Json;
using EOS.API.Data.Effects.ServiceEffectHandlers;
using Microsoft.Data.SqlClient;

namespace EOS.API.Data.Effects;

/// <summary>
/// cop-send-check: 送货单保存期的五条判据（原 `CopDomainRules.CopSendAfterSaveAsync` 的校验段，
/// 判据来源旧过程 `P_COP_SEND_After_Save`）。按旧顺序执行：
///   ① 批管品必填批号（无门控）；
///   ② 送货日期不得早于建立日期 30 天；
///   ③ 受 `SYSSS.SEND_TAG=1` 门控的三条：库别存在、出库数量不超库存（**按产品单位折算**）、
///      批号出库数量不超批号库存（同样按单位折算）。
/// 这些判据含多单位换算 CASE 与跨表聚合比较，现有模板表达不了 ⇒ 走 `custom-validation`（注册代码），
/// 命中返回文案、全部通过返回 null。参数闭合：各表与列名分组声明，全部校验为物理列。
/// </summary>
internal static class CopSendCheck
{
    public const string HandlerKey = "cop-send-check";

    public static async Task<string?> CheckAsync(
        CustomValidationContext context, JsonElement root, CancellationToken token)
    {
        if (context.MasterKeyValues.Count < 2)
            throw new EffectConfigException("cop-send-check 缺少单据主键值。");
        if (string.IsNullOrWhiteSpace(context.Plan.MasterTable))
            throw new EffectConfigException("cop-send-check 需要主表形态。");
        var columns = await new EffectPhysicalColumns().LoadAsync(context.Connection, token, context.Transaction);
        var config = Parse(root, context.Plan.MasterTable, columns);
        var type = context.MasterKeyValues[0] ?? string.Empty;
        var no = context.MasterKeyValues[1] ?? string.Empty;

        var master = Q(context.Plan.MasterTable);
        var detail = Q(config.Detail.Table);
        var product = Q(config.Product.Table);
        var depot = Q(config.Depot.Table);
        var stock = Q(config.Stock.Table);
        var batchStock = Q(config.BatchStock.Table);
        var detailScope = $"d.{Q(config.Master.TypeField)}=@Type AND d.{Q(config.Master.NoField)}=@No";

        // 单位折算：出库单位与产品单位一致取 1，否则按产品单位对应的换算率取值（与旧实现同形的 CASE）
        var unitFactor =
            $"(CASE p.{Q(config.Product.UnitField)} WHEN d.{Q(config.Detail.UnitField)} THEN 1 "
            + $"WHEN p.{Q(config.Product.Unit1Field)} THEN ISNULL(p.{Q(config.Product.UnitRate1Field)},0) "
            + $"WHEN p.{Q(config.Product.Unit2Field)} THEN ISNULL(p.{Q(config.Product.UnitRate2Field)},0) "
            + $"WHEN p.{Q(config.Product.Unit3Field)} THEN ISNULL(p.{Q(config.Product.UnitRate3Field)},0) "
            + $"WHEN p.{Q(config.Product.Unit4Field)} THEN ISNULL(p.{Q(config.Product.UnitRate4Field)},0) ELSE 0 END)";
        var sendQty = $"(d.{Q(config.Detail.QtyField)}+ISNULL(d.{Q(config.Detail.SpareQtyField)},0)) * {unitFactor}";

        // ① 批管品必填批号
        var batchMissingSql = $"""
            SELECT {Q(config.Detail.SerialField)} FROM dbo.{detail} d
            WHERE {detailScope} AND ISNULL(d.{Q(config.Detail.BatchField)},'')=''
              AND EXISTS (SELECT 1 FROM dbo.{product} p WHERE p.{Q(config.Product.KeyField)}=d.{Q(config.Detail.ProductField)}
                            AND p.{Q(config.Product.ManageBatchField)}=1);
            """;
        var batchMissing = await LinesAsync(context, batchMissingSql, type, no, token,
            reader => Convert.ToInt32(reader.GetValue(0)).ToString(CultureInfo.InvariantCulture));
        if (batchMissing is not null)
            return config.Messages.BatchRequired + batchMissing;

        // ② 送货日期不得早于建立日期 30 天
        var dateTooOldSql = $"""
            SELECT TOP 1 1 FROM dbo.{master} m
            WHERE m.{Q(config.Master.TypeField)}=@Type AND m.{Q(config.Master.NoField)}=@No
              AND DATEDIFF(day, m.{Q(config.Master.DateField)}, m.{Q(config.Master.CreateDateField)})>@MaxDays;
            """;
        if (await ExistsAsync(context, dateTooOldSql, type, no, config.MaxDays, token))
            return config.Messages.DateTooOld;

        // ③ 库存与批号库存（受 SYSSS 开关门控）
        if (await SysssFlagAsync(context, config.GateFlag, token))
        {
            var depotMissingSql = $"""
                SELECT d.{Q(config.Detail.SerialField)}, d.{Q(config.Detail.DepotField)} FROM dbo.{detail} d
                WHERE {detailScope} AND ISNULL(d.{Q(config.Detail.DepotField)},'')<>''
                  AND NOT EXISTS (SELECT 1 FROM dbo.{depot} dp WHERE dp.{Q(config.Depot.KeyField)}=d.{Q(config.Detail.DepotField)});
                """;
            var depotMissing = await LinesAsync(context, depotMissingSql, type, no, token,
                reader => Convert.ToInt32(reader.GetValue(0)).ToString(CultureInfo.InvariantCulture)
                    + "    " + Str(reader, 1));
            if (depotMissing is not null)
                return config.Messages.DepotMissing + depotMissing;

            var stockSql = $"""
                SELECT a.PRO_NO, a.DEPOT_ID, a.QTY, ISNULL(b.QTY,0)
                FROM (SELECT d.{Q(config.Detail.ProductField)} AS PRO_NO, d.{Q(config.Detail.DepotField)} AS DEPOT_ID,
                             SUM({sendQty}) AS QTY
                        FROM dbo.{detail} d INNER JOIN dbo.{product} p ON p.{Q(config.Product.KeyField)}=d.{Q(config.Detail.ProductField)}
                       WHERE {detailScope}
                       GROUP BY d.{Q(config.Detail.ProductField)}, d.{Q(config.Detail.DepotField)}) a
                LEFT JOIN dbo.{stock} b ON b.{Q(config.Stock.ProductField)}=a.PRO_NO AND b.{Q(config.Stock.DepotField)}=a.DEPOT_ID
                WHERE a.QTY > ISNULL(b.{Q(config.Stock.QtyField)},0);
                """;
            var stockLines = await LinesAsync(context, stockSql, type, no, token,
                reader => Str(reader, 0) + "    " + Str(reader, 1) + "    " + Num(reader, 2) + "    "
                    + Num(reader, 3) + "    " + NumDiff(reader, 2, 3));
            if (stockLines is not null)
                return config.Messages.StockNotEnough + stockLines;

            var batchStockSql = $"""
                SELECT a.PRO_NO, a.BATCH_NO, a.QTY-(ISNULL(b.{Q(config.BatchStock.InField)},0)-ISNULL(b.{Q(config.BatchStock.OutField)},0))
                FROM (SELECT d.{Q(config.Detail.ProductField)} AS PRO_NO, d.{Q(config.Detail.BatchField)} AS BATCH_NO,
                             SUM({sendQty}) AS QTY
                        FROM dbo.{detail} d INNER JOIN dbo.{product} p ON p.{Q(config.Product.KeyField)}=d.{Q(config.Detail.ProductField)}
                       WHERE {detailScope} AND ISNULL(d.{Q(config.Detail.BatchField)},'')<>''
                       GROUP BY d.{Q(config.Detail.ProductField)}, d.{Q(config.Detail.BatchField)}) a
                LEFT JOIN dbo.{batchStock} b ON b.{Q(config.BatchStock.BatchField)}=a.BATCH_NO
                                            AND b.{Q(config.BatchStock.ProductField)}=a.PRO_NO
                WHERE a.QTY > (ISNULL(b.{Q(config.BatchStock.InField)},0)-ISNULL(b.{Q(config.BatchStock.OutField)},0));
                """;
            var batchStockLines = await LinesAsync(context, batchStockSql, type, no, token,
                reader => Str(reader, 0) + "    " + Str(reader, 1) + "    " + Num(reader, 2));
            if (batchStockLines is not null)
                return config.Messages.BatchStockNotEnough + batchStockLines;
        }
        return null;
    }

    private static string Q(string identifier) => ServiceEffectSql.Q(identifier);

    private static async Task<bool> ExistsAsync(
        CustomValidationContext context, string sql, string type, string no, int maxDays, CancellationToken token)
    {
        await using var command = new SqlCommand(sql, context.Connection, context.Transaction);
        command.Parameters.AddWithValue("@Type", type);
        command.Parameters.AddWithValue("@No", no);
        command.Parameters.AddWithValue("@MaxDays", maxDays);
        return await command.ExecuteScalarAsync(token) is not null;
    }

    private static async Task<bool> SysssFlagAsync(CustomValidationContext context, string flag, CancellationToken token)
    {
        await using var command = new SqlCommand(
            "SELECT TOP 1 ISNULL(CAST(" + Q(flag) + " AS int),0) FROM dbo.SYSSS;", context.Connection, context.Transaction);
        var value = await command.ExecuteScalarAsync(token);
        return value is not null && Convert.ToInt32(value) == 1;
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

    private static string NumDiff(SqlDataReader reader, int left, int right)
        => reader.IsDBNull(left) || reader.IsDBNull(right)
            ? "0"
            : (Convert.ToDouble(reader.GetValue(left)) - Convert.ToDouble(reader.GetValue(right)))
                .ToString("0.######", CultureInfo.InvariantCulture);

    private static string Str(SqlDataReader reader, int index)
        => reader.IsDBNull(index) ? string.Empty : reader.GetValue(index).ToString()?.Trim() ?? string.Empty;

    /// <summary>参数解析（fail-closed：各表列名必须物理存在，文案非空，门控列只能是 SYSSS 上的列）。</summary>
    internal static CopSendCheckConfig Parse(JsonElement root, string masterTable, ISet<string> columns)
    {
        const string label = "cop-send-check";
        var master = Section(root, "master", label);
        var detail = Section(root, "detail", label);
        var product = Section(root, "product", label);
        var depot = Section(root, "depot", label);
        var stock = Section(root, "stock", label);
        var batchStock = Section(root, "batchStock", label);
        var messages = Section(root, "messages", label);
        var config = new CopSendCheckConfig(
            new CopSendMasterFields(Required(master, "typeField"), Required(master, "noField"),
                Required(master, "dateField"), Required(master, "createDateField")),
            new CopSendDetailFields(Required(detail, "table"), Required(detail, "productField"),
                Required(detail, "qtyField"), Required(detail, "spareQtyField"), Required(detail, "batchField"),
                Required(detail, "depotField"), Required(detail, "unitField"), Required(detail, "serialField")),
            new CopSendProductFields(Required(product, "table"), Required(product, "keyField"),
                Required(product, "manageBatchField"), Required(product, "unitField"),
                Required(product, "unit1Field"), Required(product, "unitRate1Field"),
                Required(product, "unit2Field"), Required(product, "unitRate2Field"),
                Required(product, "unit3Field"), Required(product, "unitRate3Field"),
                Required(product, "unit4Field"), Required(product, "unitRate4Field")),
            new CopSendDepotFields(Required(depot, "table"), Required(depot, "keyField")),
            new CopSendStockFields(Required(stock, "table"), Required(stock, "productField"),
                Required(stock, "depotField"), Required(stock, "qtyField")),
            new CopSendBatchStockFields(Required(batchStock, "table"), Required(batchStock, "batchField"),
                Required(batchStock, "productField"), Required(batchStock, "inField"), Required(batchStock, "outField")),
            Required(root, "gateFlag"), OptionalInt(root, "maxDays", 30),
            new CopSendMessages(Required(messages, "batchRequired"), Required(messages, "dateTooOld"),
                Required(messages, "depotMissing"), Required(messages, "stockNotEnough"),
                Required(messages, "batchStockNotEnough")));
        foreach (var (table, column) in new[]
                 {
                     (masterTable, config.Master.TypeField), (masterTable, config.Master.NoField),
                     (masterTable, config.Master.DateField), (masterTable, config.Master.CreateDateField),
                     (config.Detail.Table, config.Master.TypeField), (config.Detail.Table, config.Master.NoField),
                     (config.Detail.Table, config.Detail.ProductField), (config.Detail.Table, config.Detail.QtyField),
                     (config.Detail.Table, config.Detail.SpareQtyField), (config.Detail.Table, config.Detail.BatchField),
                     (config.Detail.Table, config.Detail.DepotField), (config.Detail.Table, config.Detail.UnitField),
                     (config.Detail.Table, config.Detail.SerialField),
                     (config.Product.Table, config.Product.KeyField),
                     (config.Product.Table, config.Product.ManageBatchField),
                     (config.Product.Table, config.Product.UnitField),
                     (config.Product.Table, config.Product.Unit1Field),
                     (config.Product.Table, config.Product.UnitRate1Field),
                     (config.Product.Table, config.Product.Unit2Field),
                     (config.Product.Table, config.Product.UnitRate2Field),
                     (config.Product.Table, config.Product.Unit3Field),
                     (config.Product.Table, config.Product.UnitRate3Field),
                     (config.Product.Table, config.Product.Unit4Field),
                     (config.Product.Table, config.Product.UnitRate4Field),
                     (config.Depot.Table, config.Depot.KeyField),
                     (config.Stock.Table, config.Stock.ProductField), (config.Stock.Table, config.Stock.DepotField),
                     (config.Stock.Table, config.Stock.QtyField),
                     (config.BatchStock.Table, config.BatchStock.BatchField),
                     (config.BatchStock.Table, config.BatchStock.ProductField),
                     (config.BatchStock.Table, config.BatchStock.InField),
                     (config.BatchStock.Table, config.BatchStock.OutField),
                     ("SYSSS", config.GateFlag),
                 })
        {
            if (!columns.Contains(table + "." + column))
                throw new EffectConfigException($"cop-send-check 列不存在：{table}.{column}。");
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
                : throw new EffectConfigException($"cop-send-check 缺少字符串字段 {name}。");

    private static int OptionalInt(JsonElement element, string name, int fallback)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out var parsed) ? parsed : fallback;
}

internal sealed record CopSendMasterFields(string TypeField, string NoField, string DateField, string CreateDateField);
internal sealed record CopSendDetailFields(string Table, string ProductField, string QtyField, string SpareQtyField,
    string BatchField, string DepotField, string UnitField, string SerialField);
internal sealed record CopSendProductFields(string Table, string KeyField, string ManageBatchField, string UnitField,
    string Unit1Field, string UnitRate1Field, string Unit2Field, string UnitRate2Field, string Unit3Field,
    string UnitRate3Field, string Unit4Field, string UnitRate4Field);
internal sealed record CopSendDepotFields(string Table, string KeyField);
internal sealed record CopSendStockFields(string Table, string ProductField, string DepotField, string QtyField);
internal sealed record CopSendBatchStockFields(string Table, string BatchField, string ProductField, string InField,
    string OutField);
internal sealed record CopSendMessages(string BatchRequired, string DateTooOld, string DepotMissing,
    string StockNotEnough, string BatchStockNotEnough);
internal sealed record CopSendCheckConfig(CopSendMasterFields Master, CopSendDetailFields Detail,
    CopSendProductFields Product, CopSendDepotFields Depot, CopSendStockFields Stock,
    CopSendBatchStockFields BatchStock, string GateFlag, int MaxDays, CopSendMessages Messages);
