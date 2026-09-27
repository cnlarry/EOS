using System.Data;
using System.Globalization;
using System.Text.Json;
using EOS.API.Data.Effects.ServiceEffectHandlers;
using EOS.API.Data.Inventory;
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
/// <summary>余额表里"未指定位置"的哨兵值：明细未填位置时应与它比对，而不是与任意位置行比对。</summary>
internal static class CopSendCheckConstants
{
    public const string LocationSentinel = "-";
}

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
        var columns = await new EffectPhysicalColumns().LoadWithParametersAsync(context.Connection, token, context.Transaction);
        var config = Parse(root, context.Plan.MasterTable, columns);
        var type = context.MasterKeyValues[0] ?? string.Empty;
        var no = context.MasterKeyValues[1] ?? string.Empty;

        var master = Q(context.Plan.MasterTable);
        var detail = Q(config.Detail.Table);
        var product = Q(config.Product.Table);
        var depot = Q(config.Depot.Table);
        var batchStock = Q(config.BatchStock.Table);
        var detailScope = $"d.{Q(config.Master.TypeField)}=@Type AND d.{Q(config.Master.NoField)}=@No";

        // 单位折算：出库单位与产品单位一致取 1，否则按产品单位对应的换算率取值（与既有实现同形的 CASE）
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

            // 库存侧经 InventoryQueryService 取：同一组合多行要 SUM、哨兵位置要归一，
            // 这些口径只有那里知道。描述符配的库存表必须是余额表本身——
            // 允许配置指向"另一张表"会让这里的收口变成静默读错，故 fail-closed 校验。
            EnsureStockSourceIsBalanceTable(config);

            // 维度键：库别始终在键内，**位置与批次是可选扩展**——描述符配了才叠加。
            var keyNames = new List<string> { "PRO_NO", "DEPOT_ID" };
            var detailKeys = new List<string>
            {
                $"d.{Q(config.Detail.ProductField)}", $"d.{Q(config.Detail.DepotField)}",
            };
            var groupByLocation = config.Stock.LocationField is not null;
            var groupByBatch = config.Stock.BatchField is not null;
            if (groupByLocation)
            {
                detailKeys.Add($"ISNULL(d.{Q(config.Detail.LocationField!)}, N'{InventoryQueryService.LocationSentinel}')");
                keyNames.Add("LOCATION_NO");
            }
            if (groupByBatch)
            {
                detailKeys.Add($"ISNULL(d.{Q(config.Detail.BatchField)}, '')");
                keyNames.Add("BATCH_NO");
            }

            var stock = await InventoryQueryService.GetQuantitiesAsync(
                context.Connection, context.Transaction,
                new InventoryQueryService.QuantityQuery
                {
                    GroupByDepot = true,
                    GroupByLocation = groupByLocation,
                    GroupByBatch = groupByBatch,
                },
                InventoryQueryService.ReadLock.None, token);
            var available = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (var row in stock)
            {
                available[StockKey(row.ProductNo, row.DepotId, row.LocationNo, row.BatchNo)] = row.Quantity ?? 0d;
            }

            // 明细侧仍是一条聚合语句（它自己的换算与分组与库存无关），
            // 只是"够不够"的判断从 SQL 的 WHERE 挪到内存里与库存合计比较。
            var demandSql = $"""
                SELECT {string.Join(", ", detailKeys.Select((expr, index) => $"{expr} AS {keyNames[index]}"))}, SUM({sendQty}) AS QTY
                FROM dbo.{detail} d INNER JOIN dbo.{product} p ON p.{Q(config.Product.KeyField)}=d.{Q(config.Detail.ProductField)}
                WHERE {detailScope}
                GROUP BY {string.Join(", ", detailKeys)};
                """;
            var stockLines = await LinesWhereAsync(context, demandSql, type, no, token,
                reader => StockLine(reader, keyNames, available));
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

    /// <summary>
    /// 库存不足行的文案：第一列料号、第二列**维度标识**（库别，配了位置 / 批次则追加），随后出库 / 库存 / 不足数量。
    /// 未配置新维度键时键只有「品号 + 库别」，输出与改造前逐字相同。
    /// 返回 null 表示这一行够用（不进文案）。
    /// </summary>
    private static string? StockLine(
        SqlDataReader reader, List<string> keyNames, IReadOnlyDictionary<string, double> available)
    {
        var depotIndex = keyNames.IndexOf("DEPOT_ID");
        var locationIndex = keyNames.IndexOf("LOCATION_NO");
        var batchIndex = keyNames.IndexOf("BATCH_NO");
        var qtyIndex = keyNames.Count;

        var product = Str(reader, 0);
        var depot = Str(reader, depotIndex);
        var location = locationIndex >= 0 ? Str(reader, locationIndex) : string.Empty;
        var batch = batchIndex >= 0 ? Str(reader, batchIndex) : string.Empty;
        var demand = reader.IsDBNull(qtyIndex) ? null : (double?)Convert.ToDouble(reader.GetValue(qtyIndex));
        var have = available.GetValueOrDefault(
            StockKey(product, depot,
                location.Length == 0 ? InventoryQueryService.LocationSentinel : location,
                batch), 0d);
        // 与改造前的 `WHERE a.QTY > ISNULL(b.QTY,0)` 同口径：出库量为空不参与判定。
        if (demand is null || demand <= have) return null;

        var scope = depot;
        // 哨兵位置不额外展示：它表示"该库别尚未启用位置管理"，写成 库别/- 只会让人困惑。
        if (location.Length > 0 && !string.Equals(location, CopSendCheckConstants.LocationSentinel, StringComparison.Ordinal))
            scope += "/" + location;
        if (batch.Length > 0) scope += "/" + batch;
        return product + "    " + scope + "    " + Num(demand.Value) + "    " + Num(have) + "    "
            + (demand.Value - have).ToString("0.######", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// 四键的对齐键：明细侧与库存侧走同一套归一化——空值/哨兵不产生差异。
    /// 维度没启用时（库存侧该列为 null、明细侧为空串）两侧都归一到哨兵，才能对上同一条合计。
    /// </summary>
    private static string StockKey(string product, string? depot, string? location, string? batch)
    {
        var locationKey = (location ?? string.Empty).Trim();
        if (locationKey.Length == 0) locationKey = InventoryQueryService.LocationSentinel;
        return string.Join('|', product.Trim(), (depot ?? string.Empty).Trim(),
            locationKey, (batch ?? string.Empty).Trim());
    }

    /// <summary>配置的"库存来源"必须就是余额表本身，否则收口会变成指鹿为马（读的是别的表）。</summary>
    private static void EnsureStockSourceIsBalanceTable(CopSendCheckConfig config)
    {
        var expected = new[]
        {
            (config.Stock.Table, InventoryQueryService.BalanceTable),
            (config.Stock.ProductField, InventoryQueryService.ProductColumn),
            (config.Stock.DepotField, InventoryQueryService.DepotColumn),
            (config.Stock.QtyField, InventoryQueryService.QuantityColumn),
        }.ToList();
        if (config.Stock.LocationField is { } location)
            expected.Add((location, InventoryQueryService.LocationColumn));
        if (config.Stock.BatchField is { } batch)
            expected.Add((batch, InventoryQueryService.BatchColumn));
        foreach (var (actual, want) in expected)
        {
            if (!string.Equals(actual?.Trim(), want, StringComparison.OrdinalIgnoreCase))
                throw new EffectConfigException(
                    $"cop-send-check 的库存来源必须是 {want}（{InventoryQueryService.BalanceTable} 的库存读取出口），实际配置为 {actual}。");
        }
    }

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
        return await SystemParameterService.GetBoolAsync(
            context.Connection, context.Transaction, SystemParameterService.SystemOwner, flag,
            fallback: false, token);
    }

    private static async Task<string?> LinesAsync(CustomValidationContext context, string sql, string type, string no,
        CancellationToken token, Func<SqlDataReader, string> format)
        => await LinesWhereAsync(context, sql, type, no, token, reader => format(reader));

    /// <summary>
    /// 与 <see cref="LinesAsync"/> 同形，但格式化函数可以返回 null 表示"这一行不进文案"——
    /// 库存不足的判据从 SQL 的 WHERE 挪到内存比较之后，过滤发生在读的这一侧。
    /// </summary>
    private static async Task<string?> LinesWhereAsync(CustomValidationContext context, string sql, string type, string no,
        CancellationToken token, Func<SqlDataReader, string?> format)
    {
        var lines = new List<string>();
        await using var command = new SqlCommand(sql, context.Connection, context.Transaction);
        command.Parameters.AddWithValue("@Type", type);
        command.Parameters.AddWithValue("@No", no);
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            if (format(reader) is { } line) lines.Add(line);
        }
        return lines.Count == 0 ? null : string.Join("\r\n", lines);
    }

    private static string Num(SqlDataReader reader, int index)
        => reader.IsDBNull(index)
            ? "0"
            : Num(Convert.ToDouble(reader.GetValue(index)));

    private static string Num(double value) => value.ToString("0.######", CultureInfo.InvariantCulture);

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
                Required(detail, "depotField"), Required(detail, "unitField"), Required(detail, "serialField"),
                Optional(detail, "locationField")),
            new CopSendProductFields(Required(product, "table"), Required(product, "keyField"),
                Required(product, "manageBatchField"), Required(product, "unitField"),
                Required(product, "unit1Field"), Required(product, "unitRate1Field"),
                Required(product, "unit2Field"), Required(product, "unitRate2Field"),
                Required(product, "unit3Field"), Required(product, "unitRate3Field"),
                Required(product, "unit4Field"), Required(product, "unitRate4Field")),
            new CopSendDepotFields(Required(depot, "table"), Required(depot, "keyField")),
            new CopSendStockFields(Required(stock, "table"), Required(stock, "productField"),
                Required(stock, "depotField"), Required(stock, "qtyField"),
                Optional(stock, "locationField"), Optional(stock, "batchField")),
            new CopSendBatchStockFields(Required(batchStock, "table"), Required(batchStock, "batchField"),
                Required(batchStock, "productField"), Required(batchStock, "inField"), Required(batchStock, "outField")),
            Required(root, "gateFlag"), OptionalInt(root, "maxDays", 30),
            new CopSendMessages(Verbatim(messages, "batchRequired"), Required(messages, "dateTooOld"),
                Verbatim(messages, "depotMissing"), Verbatim(messages, "stockNotEnough"),
                Verbatim(messages, "batchStockNotEnough")));
        // 位置维度必须成对配置：只配一边建不起关联，属配置错误（fail-closed，不留到运行期才发现）。
        if ((config.Detail.LocationField is null) != (config.Stock.LocationField is null))
            throw new EffectConfigException(
                "cop-send-check 的位置维度需同时配置 detail.locationField 与 stock.locationField（只配一边无法关联）。");

        var required = new List<(string Table, string Column)>
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
                 };
        if (config.Detail.LocationField is { } detailLocation)
        {
            required.Add((config.Detail.Table, detailLocation));
            required.Add((config.Stock.Table, config.Stock.LocationField!));
        }
        if (config.Stock.BatchField is { } stockBatch)
            required.Add((config.Stock.Table, stockBatch));

        foreach (var (table, column) in required)
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

    /// <summary>可选字符串键：缺省或空白一律视为"未配置"（维度键就是靠这个保持向后兼容）。</summary>
    private static string? Optional(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(value.GetString())
                ? value.GetString()!.Trim()
                : null;

    /// <summary>文案按**逐字**取值（不裁剪）：旧文案自带换行与尾随空格，裁剪会改变用户看到的排版。</summary>
    private static string Verbatim(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(value.GetString())
                ? value.GetString()!
                : throw new EffectConfigException($"cop-send-check 缺少字符串字段 {name}。");

    private static int OptionalInt(JsonElement element, string name, int fallback)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out var parsed) ? parsed : fallback;
}

internal sealed record CopSendMasterFields(string TypeField, string NoField, string DateField, string CreateDateField);
internal sealed record CopSendDetailFields(string Table, string ProductField, string QtyField, string SpareQtyField,
    string BatchField, string DepotField, string UnitField, string SerialField, string? LocationField);
internal sealed record CopSendProductFields(string Table, string KeyField, string ManageBatchField, string UnitField,
    string Unit1Field, string UnitRate1Field, string Unit2Field, string UnitRate2Field, string Unit3Field,
    string UnitRate3Field, string Unit4Field, string UnitRate4Field);
internal sealed record CopSendDepotFields(string Table, string KeyField);
internal sealed record CopSendStockFields(string Table, string ProductField, string DepotField, string QtyField,
    string? LocationField, string? BatchField);
internal sealed record CopSendBatchStockFields(string Table, string BatchField, string ProductField, string InField,
    string OutField);
internal sealed record CopSendMessages(string BatchRequired, string DateTooOld, string DepotMissing,
    string StockNotEnough, string BatchStockNotEnough);
internal sealed record CopSendCheckConfig(CopSendMasterFields Master, CopSendDetailFields Detail,
    CopSendProductFields Product, CopSendDepotFields Depot, CopSendStockFields Stock,
    CopSendBatchStockFields BatchStock, string GateFlag, int MaxDays, CopSendMessages Messages);
