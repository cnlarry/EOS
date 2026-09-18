using System.Data;
using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace EOS.API.Data.Effects.ServiceEffectHandlers;

/// <summary>
/// pur-apply-sync: 请购单保存后的"明细补行 + 汇总回填 + 订单字段回填 + 申购数量分配 + 主表单号串联"整链
/// （原 `PurDomainRules.PurApplyAfterSaveAsync`，判据来源为旧过程 `P_PUR_APPLY_After_Save`）：
///   ① 本单明细应购数量清零；
///   ② 待购表（`PUR_APPLY_MORE`）中尚未成行的产品按 `SUM(应购数量)` 追加明细行（序号递增，
///      仓库/单位取产品档案，**产品档案缺行时仍补行**）；
///   ③ 明细 应购数量/损耗数量 按待购表同产品求和回填；
///   ④ 明细 订单类型/单号/序号、客户品号、客户单号、预交日期、客户 从待购表关联的订单行回填；
///   ⑤ 申购数量分配：待购表 QTY 先清零，再按 `(产品, 序号)` 顺序把明细 QTY 逐行分配给待购行；
///   ⑥ 主表 采购订单号/生产单号 = 待购表去重非空值按序串联（全空则不回写）。
/// ⑤ 需逐行更新，故在事务内读取后循环执行（与原实现一致）。
/// 参数闭合：六张表与各列名分组声明，全部校验为物理列；单据键值只作参数传入。
/// </summary>
public sealed class PurApplySyncHandler : IEffectServiceHandler
{
    public string EffectKey => "pur-apply-sync";

    public async Task<int> ExecuteAsync(ServiceEffectContext context, CancellationToken token)
    {
        var root = context.Action.Params ?? throw new EffectConfigException("pur-apply-sync 缺少参数。");
        if (context.ExecutionEvent is not (EffectEvent.ApproveEffect or EffectEvent.Save)) return 0;
        if (context.MasterKeyValues.Count < 2)
            throw new EffectConfigException("pur-apply-sync 缺少单据主键值。");
        var columns = await new EffectPhysicalColumns().LoadAsync(context.Connection, token, context.Transaction);
        var config = Parse(root, columns);
        var type = context.MasterKeyValues[0] ?? string.Empty;
        var no = context.MasterKeyValues[1] ?? string.Empty;
        var scope = Q(config.Master.TypeField) + "=@type AND " + Q(config.Master.NoField) + "=@no";
        var parameters = new[]
        {
            new EffectSqlParameter("@type", type),
            new EffectSqlParameter("@no", no),
        };
        var affected = 0;

        // ① 明细应购数量清零（无待购行时这就是全部动作）
        affected += await ServiceEffectSql.ExecAsync(context.Connection, context.Transaction,
            "UPDATE dbo." + Q(config.Detail.Table) + " SET " + Q(config.Detail.RequireQtyField)
            + "=0 WHERE " + scope + ";", parameters, token);

        var moreCount = await ScalarAsync(context,
            "SELECT TOP 1 1 FROM dbo." + Q(config.More.Table) + " m WHERE m." + Q(config.Master.TypeField)
            + "=@type AND m." + Q(config.Master.NoField) + "=@no;", type, no, token);
        if (moreCount is null) return affected;

        // ② 待购表逐行（按 产品、序号 排序，供 ⑤ 分配使用）
        var moreRows = new List<(int Serial, string Product, decimal RequireQty)>();
        await using (var read = new SqlCommand("SELECT " + Q(config.More.SerialField) + ", "
            + Q(config.More.ProductField) + ", " + Q(config.More.RequireQtyField) + " FROM dbo."
            + Q(config.More.Table) + " WHERE " + scope + " ORDER BY " + Q(config.More.ProductField) + ", "
            + Q(config.More.SerialField) + ";", context.Connection, context.Transaction))
        {
            read.Parameters.AddWithValue("@type", type);
            read.Parameters.AddWithValue("@no", no);
            await using var reader = await read.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
                moreRows.Add((Convert.ToInt32(reader.GetValue(0)),
                    reader.GetValue(1)?.ToString()?.Trim() ?? string.Empty,
                    Convert.ToDecimal(reader.GetValue(2))));
        }

        var maxSerial = await ScalarIntAsync(context, "SELECT ISNULL(MAX(" + Q(config.Detail.SerialField) + "),0) FROM dbo."
            + Q(config.Detail.Table) + " WHERE " + scope + ";", type, no, token);

        // ③ 尚未成行的产品：按产品汇总应购数量后补行
        var missing = new List<(string Product, double RequireQty)>();
        var missingSql = "SELECT m." + Q(config.More.ProductField) + ", SUM(m." + Q(config.More.RequireQtyField)
            + ") FROM dbo." + Q(config.More.Table) + " m WHERE m." + Q(config.Master.TypeField) + "=@type AND m."
            + Q(config.Master.NoField) + "=@no AND NOT EXISTS (SELECT 1 FROM dbo." + Q(config.Detail.Table) + " d WHERE d."
            + Q(config.Master.TypeField) + "=m." + Q(config.Master.TypeField) + " AND d." + Q(config.Master.NoField)
            + "=m." + Q(config.Master.NoField) + " AND d." + Q(config.Detail.ProductField) + "=m."
            + Q(config.More.ProductField) + ") GROUP BY m." + Q(config.More.ProductField) + " ORDER BY m."
            + Q(config.More.ProductField) + ";";
        await using (var command = new SqlCommand(missingSql, context.Connection, context.Transaction))
        {
            command.Parameters.AddWithValue("@type", type);
            command.Parameters.AddWithValue("@no", no);
            await using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
                missing.Add((reader.GetValue(0)?.ToString()?.Trim() ?? string.Empty, Convert.ToDouble(reader.GetValue(1))));
        }
        foreach (var row in missing)
        {
            maxSerial++;
            var insert = "INSERT INTO dbo." + Q(config.Detail.Table) + " (" + Q(config.Master.TypeField) + ","
                + Q(config.Master.NoField) + "," + Q(config.Detail.SerialField) + "," + Q(config.Detail.ProductField)
                + "," + Q(config.Detail.DepotField) + "," + Q(config.Detail.QtyField) + "," + Q(config.Detail.UnitField)
                + ") SELECT @type, @no, @serial, m." + Q(config.More.ProductField) + ","
                + " (SELECT p." + Q(config.Product.DepotField) + " FROM dbo." + Q(config.Product.Table) + " p WHERE p."
                + Q(config.Product.ProductField) + "=m." + Q(config.More.ProductField) + "),"
                + " SUM(m." + Q(config.More.RequireQtyField) + "),"
                + " (SELECT p." + Q(config.Product.UnitField) + " FROM dbo." + Q(config.Product.Table) + " p WHERE p."
                + Q(config.Product.ProductField) + "=m." + Q(config.More.ProductField) + ")"
                + " FROM dbo." + Q(config.More.Table) + " m WHERE m." + Q(config.Master.TypeField) + "=@type AND m."
                + Q(config.Master.NoField) + "=@no AND m." + Q(config.More.ProductField) + "=@product"
                + " GROUP BY m." + Q(config.More.ProductField) + ";";
            affected += await ServiceEffectSql.ExecAsync(context.Connection, context.Transaction, insert,
                [
                    new EffectSqlParameter("@type", type), new EffectSqlParameter("@no", no),
                    new EffectSqlParameter("@serial", maxSerial), new EffectSqlParameter("@product", row.Product),
                ], token);
        }

        // ④ 应购数量/损耗数量回填（子查询列全部带别名限定，避免与外层明细同名同列产生歧义）
        affected += await ServiceEffectSql.ExecAsync(context.Connection, context.Transaction,
            "UPDATE d SET d." + Q(config.Detail.RequireQtyField) + "=s.REQUIRE_SUM, d." + Q(config.Detail.LostQtyField)
            + "=s.LOST_SUM FROM dbo." + Q(config.Detail.Table) + " d INNER JOIN (SELECT m."
            + Q(config.Master.TypeField) + ", m." + Q(config.Master.NoField) + ", m." + Q(config.More.ProductField)
            + ", SUM(m." + Q(config.More.RequireQtyField) + ") REQUIRE_SUM, SUM(m." + Q(config.More.LostQtyField)
            + ") LOST_SUM FROM dbo." + Q(config.More.Table) + " m WHERE m." + Q(config.Master.TypeField) + "=@type AND m."
            + Q(config.Master.NoField) + "=@no GROUP BY m." + Q(config.Master.TypeField) + ", m."
            + Q(config.Master.NoField) + ", m." + Q(config.More.ProductField) + ") s ON d."
            + Q(config.Master.TypeField) + "=s." + Q(config.Master.TypeField) + " AND d." + Q(config.Master.NoField)
            + "=s." + Q(config.Master.NoField) + " AND d." + Q(config.Detail.ProductField) + "=s."
            + Q(config.More.ProductField) + " WHERE d." + Q(config.Master.TypeField) + "=@type AND d."
            + Q(config.Master.NoField) + "=@no;", parameters, token);

        // ⑤ 订单字段回填
        affected += await ServiceEffectSql.ExecAsync(context.Connection, context.Transaction,
            "UPDATE d SET d." + Q(config.Detail.OrderTypeField) + "=o." + Q(config.OrderDetail.TypeField) + ", d."
            + Q(config.Detail.OrderNoField) + "=o." + Q(config.OrderDetail.NoField) + ", d."
            + Q(config.Detail.OrderSerialField) + "=o." + Q(config.OrderDetail.SerialField) + ", d."
            + Q(config.Detail.ClientProNoField) + "=o." + Q(config.OrderDetail.ClientProNoField) + ", d."
            + Q(config.Detail.ClientOrderNoField) + "=o." + Q(config.OrderDetail.ClientOrderNoField) + ", d."
            + Q(config.Detail.UsedDateField) + "=o." + Q(config.OrderDetail.PreSendDateField) + ", d."
            + Q(config.Detail.ClientField) + "=c." + Q(config.OrderMaster.ClientField)
            + " FROM dbo." + Q(config.Detail.Table) + " d INNER JOIN dbo." + Q(config.More.Table) + " m ON m."
            + Q(config.Master.TypeField) + "=d." + Q(config.Master.TypeField) + " AND m." + Q(config.Master.NoField)
            + "=d." + Q(config.Master.NoField) + " AND m." + Q(config.More.ProductField) + "=d."
            + Q(config.Detail.ProductField) + " INNER JOIN dbo." + Q(config.OrderDetail.Table) + " o ON o."
            + Q(config.OrderDetail.TypeField) + "=m." + Q(config.More.OrderTypeField) + " AND o."
            + Q(config.OrderDetail.NoField) + "=m." + Q(config.More.OrderNoField) + " AND o."
            + Q(config.OrderDetail.SerialField) + "=m." + Q(config.More.OrderSerialField) + " INNER JOIN dbo."
            + Q(config.OrderMaster.Table) + " c ON c." + Q(config.OrderMaster.TypeField) + "=o."
            + Q(config.OrderDetail.TypeField) + " AND c." + Q(config.OrderMaster.NoField) + "=o."
            + Q(config.OrderDetail.NoField) + " WHERE d." + Q(config.Master.TypeField) + "=@type AND d."
            + Q(config.Master.NoField) + "=@no;", parameters, token);

        // ⑥ 申购数量分配：明细数量按 (产品, 序号) 顺序逐行分给待购行
        affected += await ServiceEffectSql.ExecAsync(context.Connection, context.Transaction,
            "UPDATE dbo." + Q(config.More.Table) + " SET " + Q(config.More.QtyField) + "=0 WHERE " + scope + ";",
            parameters, token);
        var detailQty = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        await using (var readQty = new SqlCommand("SELECT " + Q(config.Detail.ProductField) + ", "
            + Q(config.Detail.QtyField) + " FROM dbo." + Q(config.Detail.Table) + " WHERE " + scope + ";",
            context.Connection, context.Transaction))
        {
            readQty.Parameters.AddWithValue("@type", type);
            readQty.Parameters.AddWithValue("@no", no);
            await using var reader = await readQty.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
                detailQty[reader.GetValue(0)?.ToString()?.Trim() ?? string.Empty] = Convert.ToDecimal(reader.GetValue(1));
        }
        string? currentProduct = null;
        var remaining = 0m;
        foreach (var row in moreRows)
        {
            if (!string.Equals(currentProduct, row.Product, StringComparison.OrdinalIgnoreCase))
            {
                currentProduct = row.Product;
                remaining = detailQty.TryGetValue(row.Product, out var qty) ? qty : 0m;
            }
            var assign = remaining > row.RequireQty ? row.RequireQty : remaining > 0 ? remaining : (decimal?)null;
            if (assign is null) continue;
            affected += await ServiceEffectSql.ExecAsync(context.Connection, context.Transaction,
                "UPDATE dbo." + Q(config.More.Table) + " SET " + Q(config.More.QtyField) + "=@qty WHERE "
                + scope + " AND " + Q(config.More.SerialField) + "=@serial;",
                [
                    new EffectSqlParameter("@type", type), new EffectSqlParameter("@no", no),
                    new EffectSqlParameter("@serial", row.Serial), new EffectSqlParameter("@qty", assign.Value),
                ], token);
            remaining -= row.RequireQty;
        }

        // ⑦ 主表 采购订单号/生产单号 = 待购表去重非空值按序串联（全空不回写）
        affected += await UpdateDistinctAsync(context, config.Master.Table, config.Master.TypeField,
            config.Master.NoField, config.Master.OrderNoField, config.More.Table, config.More.OrderNoField, type, no, token);
        affected += await UpdateDistinctAsync(context, config.Master.Table, config.Master.TypeField,
            config.Master.NoField, config.Master.ProduceNoField, config.More.Table, config.More.ProduceNoField, type, no, token);
        return affected;
    }

    private static string Q(string identifier) => ServiceEffectSql.Q(identifier);

    private static async Task<int> UpdateDistinctAsync(ServiceEffectContext context, string masterTable,
        string typeField, string noField, string masterColumn, string moreTable, string moreColumn,
        string type, string no, CancellationToken token)
    {
        var values = new List<string>();
        await using (var command = new SqlCommand("SELECT DISTINCT LTRIM(RTRIM(" + Q(moreColumn) + ")) FROM dbo."
            + Q(moreTable) + " WHERE " + Q(typeField) + "=@type AND " + Q(noField) + "=@no AND ISNULL("
            + Q(moreColumn) + ",'')<>'' ORDER BY 1;", context.Connection, context.Transaction))
        {
            command.Parameters.AddWithValue("@type", type);
            command.Parameters.AddWithValue("@no", no);
            await using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token)) values.Add(reader.GetString(0));
        }
        if (values.Count == 0) return 0;
        return await ServiceEffectSql.ExecAsync(context.Connection, context.Transaction,
            "UPDATE dbo." + Q(masterTable) + " SET " + Q(masterColumn) + "=@value WHERE " + Q(typeField)
            + "=@type AND " + Q(noField) + "=@no;",
            [
                new EffectSqlParameter("@value", string.Join(',', values)),
                new EffectSqlParameter("@type", type), new EffectSqlParameter("@no", no),
            ], token);
    }

    private static async Task<int> ScalarIntAsync(
        ServiceEffectContext context, string sql, string type, string no, CancellationToken token)
        => Convert.ToInt32(await ScalarAsync(context, sql, type, no, token) ?? 0);

    private static async Task<object?> ScalarAsync(
        ServiceEffectContext context, string sql, string type, string no, CancellationToken token)
    {
        await using var command = new SqlCommand(sql, context.Connection, context.Transaction);
        command.Parameters.AddWithValue("@type", type);
        command.Parameters.AddWithValue("@no", no);
        return await command.ExecuteScalarAsync(token);
    }

    /// <summary>参数解析（fail-closed：六张表的每个列名都必须物理存在）。</summary>
    internal static PurApplySyncConfig Parse(JsonElement root, ISet<string> columns)
    {
        var master = Section(root, "master");
        var detail = Section(root, "detail");
        var more = Section(root, "more");
        var product = Section(root, "product");
        var orderMaster = Section(root, "orderMaster");
        var orderDetail = Section(root, "orderDetail");
        var config = new PurApplySyncConfig(
            new PurApplyMaster(Required(master, "table"), Required(master, "typeField"), Required(master, "noField"),
                Required(master, "orderNoField"), Required(master, "produceNoField")),
            new PurApplyDetail(Required(detail, "table"), Required(detail, "productField"), Required(detail, "serialField"),
                Required(detail, "requireQtyField"), Required(detail, "lostQtyField"), Required(detail, "qtyField"),
                Required(detail, "depotField"), Required(detail, "unitField"), Required(detail, "orderTypeField"),
                Required(detail, "orderNoField"), Required(detail, "orderSerialField"),
                Required(detail, "clientProNoField"), Required(detail, "clientOrderNoField"),
                Required(detail, "usedDateField"), Required(detail, "clientField")),
            new PurApplyMore(Required(more, "table"), Required(more, "serialField"), Required(more, "productField"),
                Required(more, "requireQtyField"), Required(more, "lostQtyField"), Required(more, "qtyField"),
                Required(more, "orderTypeField"), Required(more, "orderNoField"), Required(more, "orderSerialField"),
                Required(more, "produceNoField")),
            new PurApplyProduct(Required(product, "table"), Required(product, "productField"),
                Required(product, "depotField"), Required(product, "unitField")),
            new PurApplyOrderMaster(Required(orderMaster, "table"), Required(orderMaster, "typeField"),
                Required(orderMaster, "noField"), Required(orderMaster, "clientField")),
            new PurApplyOrderDetail(Required(orderDetail, "table"), Required(orderDetail, "typeField"),
                Required(orderDetail, "noField"), Required(orderDetail, "serialField"),
                Required(orderDetail, "clientProNoField"), Required(orderDetail, "clientOrderNoField"),
                Required(orderDetail, "preSendDateField")));
        foreach (var (table, column) in new[]
                 {
                     (config.Master.Table, config.Master.TypeField), (config.Master.Table, config.Master.NoField),
                     (config.Master.Table, config.Master.OrderNoField), (config.Master.Table, config.Master.ProduceNoField),
                     (config.Detail.Table, config.Master.TypeField), (config.Detail.Table, config.Master.NoField),
                     (config.Detail.Table, config.Detail.ProductField), (config.Detail.Table, config.Detail.SerialField),
                     (config.Detail.Table, config.Detail.RequireQtyField), (config.Detail.Table, config.Detail.LostQtyField),
                     (config.Detail.Table, config.Detail.QtyField), (config.Detail.Table, config.Detail.DepotField),
                     (config.Detail.Table, config.Detail.UnitField), (config.Detail.Table, config.Detail.OrderTypeField),
                     (config.Detail.Table, config.Detail.OrderNoField), (config.Detail.Table, config.Detail.OrderSerialField),
                     (config.Detail.Table, config.Detail.ClientProNoField),
                     (config.Detail.Table, config.Detail.ClientOrderNoField),
                     (config.Detail.Table, config.Detail.UsedDateField), (config.Detail.Table, config.Detail.ClientField),
                     (config.More.Table, config.Master.TypeField), (config.More.Table, config.Master.NoField),
                     (config.More.Table, config.More.SerialField), (config.More.Table, config.More.ProductField),
                     (config.More.Table, config.More.RequireQtyField), (config.More.Table, config.More.LostQtyField),
                     (config.More.Table, config.More.QtyField), (config.More.Table, config.More.OrderTypeField),
                     (config.More.Table, config.More.OrderNoField), (config.More.Table, config.More.OrderSerialField),
                     (config.More.Table, config.More.ProduceNoField),
                     (config.Product.Table, config.Product.ProductField), (config.Product.Table, config.Product.DepotField),
                     (config.Product.Table, config.Product.UnitField),
                     (config.OrderMaster.Table, config.OrderMaster.TypeField),
                     (config.OrderMaster.Table, config.OrderMaster.NoField),
                     (config.OrderMaster.Table, config.OrderMaster.ClientField),
                     (config.OrderDetail.Table, config.OrderDetail.TypeField),
                     (config.OrderDetail.Table, config.OrderDetail.NoField),
                     (config.OrderDetail.Table, config.OrderDetail.SerialField),
                     (config.OrderDetail.Table, config.OrderDetail.ClientProNoField),
                     (config.OrderDetail.Table, config.OrderDetail.ClientOrderNoField),
                     (config.OrderDetail.Table, config.OrderDetail.PreSendDateField),
                 })
        {
            if (!columns.Contains(table + "." + column))
                throw new EffectConfigException($"pur-apply-sync 列不存在：{table}.{column}。");
        }
        return config;
    }

    private static JsonElement Section(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Object
            ? value
            : throw new EffectConfigException($"pur-apply-sync 缺少对象字段 {name}。");

    private static string Required(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(value.GetString())
                ? value.GetString()!.Trim()
                : throw new EffectConfigException($"pur-apply-sync 缺少字符串字段 {name}。");
}

internal sealed record PurApplyMaster(string Table, string TypeField, string NoField, string OrderNoField,
    string ProduceNoField);
internal sealed record PurApplyDetail(string Table, string ProductField, string SerialField, string RequireQtyField,
    string LostQtyField, string QtyField, string DepotField, string UnitField, string OrderTypeField,
    string OrderNoField, string OrderSerialField, string ClientProNoField, string ClientOrderNoField,
    string UsedDateField, string ClientField);
internal sealed record PurApplyMore(string Table, string SerialField, string ProductField, string RequireQtyField,
    string LostQtyField, string QtyField, string OrderTypeField, string OrderNoField, string OrderSerialField,
    string ProduceNoField);
internal sealed record PurApplyProduct(string Table, string ProductField, string DepotField, string UnitField);
internal sealed record PurApplyOrderMaster(string Table, string TypeField, string NoField, string ClientField);
internal sealed record PurApplyOrderDetail(string Table, string TypeField, string NoField, string SerialField,
    string ClientProNoField, string ClientOrderNoField, string PreSendDateField);
internal sealed record PurApplySyncConfig(PurApplyMaster Master, PurApplyDetail Detail, PurApplyMore More,
    PurApplyProduct Product, PurApplyOrderMaster OrderMaster, PurApplyOrderDetail OrderDetail);
