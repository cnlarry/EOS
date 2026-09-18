using System.Data;
using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace EOS.API.Data.Effects.ServiceEffectHandlers;

/// <summary>
/// detail-flag-and-rollup: 明细"可用标记 + 主表价格下发 + 明细金额回算 + 主表汇总"四步链，
/// 用于加工备案手册（成品单耗状态与加工金额/数量汇总）。四步与原
/// `CusDomainRules.CusManualAfterSaveAsync` 逐字一致：
///   ① 本单明细可用标记清零；② 存在对应 BOM 行的明细置 1；
///   ③ 明细单价取主表单价，明细金额＝**原**明细单价 × 数量（与原实现同为单条 UPDATE，
///      SQL Server 的 SET 右值取更新前的行值 ⇒ 语义一致）；
///   ④ 主表金额/数量＝明细求和（按配置位数 ROUND）。
/// 参数闭合：两表 + BOM 表 + 八个列名 + 舍入位数，全部校验为物理列；主键值只作参数传入。
/// </summary>
public sealed class DetailFlagAndRollupHandler : IEffectServiceHandler
{
    public string EffectKey => "detail-flag-and-rollup";

    public async Task<int> ExecuteAsync(ServiceEffectContext context, CancellationToken token)
    {
        var root = context.Action.Params ?? throw new EffectConfigException("detail-flag-and-rollup 缺少参数。");
        if (context.ExecutionEvent is not (EffectEvent.ApproveEffect or EffectEvent.Save)) return 0;
        if (context.MasterKeyValues.Count == 0)
            throw new EffectConfigException("detail-flag-and-rollup 缺少单据主键值。");
        var columns = await new EffectPhysicalColumns().LoadAsync(context.Connection, token, context.Transaction);
        var config = Parse(root, columns);
        var key = new EffectSqlParameter("@k", context.MasterKeyValues[0]);

        var affected = 0;
        var clear = "UPDATE D SET D." + ServiceEffectSql.Q(config.StateField) + "=0 FROM dbo."
            + ServiceEffectSql.Q(config.DetailTable) + " D WHERE D." + ServiceEffectSql.Q(config.KeyField) + "=@k;";
        affected += await ServiceEffectSql.ExecAsync(context.Connection, context.Transaction, clear, [key], token);

        var flag = "UPDATE D SET D." + ServiceEffectSql.Q(config.StateField) + "=1 FROM dbo."
            + ServiceEffectSql.Q(config.DetailTable) + " D WHERE D." + ServiceEffectSql.Q(config.KeyField) + "=@k"
            + " AND EXISTS (SELECT 1 FROM dbo." + ServiceEffectSql.Q(config.BomTable) + " B WHERE B."
            + ServiceEffectSql.Q(config.BomSerialField) + "=D." + ServiceEffectSql.Q(config.DetailSerialField)
            + " AND B." + ServiceEffectSql.Q(config.BomKeyField) + "=@k);";
        affected += await ServiceEffectSql.ExecAsync(context.Connection, context.Transaction, flag, [key], token);

        var price = "UPDATE P SET P." + ServiceEffectSql.Q(config.PriceField) + "=M." + ServiceEffectSql.Q(config.PriceField)
            + ", P." + ServiceEffectSql.Q(config.AmountField) + "=P." + ServiceEffectSql.Q(config.PriceField)
            + "*P." + ServiceEffectSql.Q(config.QtyField)
            + " FROM dbo." + ServiceEffectSql.Q(config.DetailTable) + " P INNER JOIN dbo."
            + ServiceEffectSql.Q(config.MasterTable) + " M ON M." + ServiceEffectSql.Q(config.KeyField)
            + "=P." + ServiceEffectSql.Q(config.KeyField)
            + " WHERE P." + ServiceEffectSql.Q(config.KeyField) + "=@k;";
        affected += await ServiceEffectSql.ExecAsync(context.Connection, context.Transaction, price, [key], token);

        var rollup = "UPDATE M SET M." + ServiceEffectSql.Q(config.AmountField) + "=D.AMOUNT_SUM, M."
            + ServiceEffectSql.Q(config.QtyField) + "=D.QTY_SUM FROM dbo." + ServiceEffectSql.Q(config.MasterTable)
            + " M INNER JOIN (SELECT " + ServiceEffectSql.Q(config.KeyField) + " AS K, ROUND(SUM("
            + ServiceEffectSql.Q(config.AmountField) + "), @digits) AS AMOUNT_SUM, ROUND(SUM("
            + ServiceEffectSql.Q(config.QtyField) + "), @digits) AS QTY_SUM FROM dbo."
            + ServiceEffectSql.Q(config.DetailTable) + " WHERE " + ServiceEffectSql.Q(config.KeyField)
            + "=@k GROUP BY " + ServiceEffectSql.Q(config.KeyField) + ") D ON D.K=M." + ServiceEffectSql.Q(config.KeyField)
            + " WHERE M." + ServiceEffectSql.Q(config.KeyField) + "=@k;";
        affected += await ServiceEffectSql.ExecAsync(context.Connection, context.Transaction, rollup,
            [new EffectSqlParameter("@k", context.MasterKeyValues[0]), new EffectSqlParameter("@digits", config.RoundDigits)], token);
        return affected;
    }

    /// <summary>参数解析（fail-closed：所有列名必须是物理列，舍入位数 0..6）。</summary>
    internal static DetailFlagAndRollupConfig Parse(JsonElement root, ISet<string> columns)
    {
        var config = new DetailFlagAndRollupConfig(
            Required(root, "masterTable"), Required(root, "detailTable"), Required(root, "keyField"),
            Required(root, "bomTable"), Required(root, "bomKeyField"), Required(root, "bomSerialField"),
            Required(root, "detailSerialField"), Required(root, "stateField"), Required(root, "priceField"),
            Required(root, "amountField"), Required(root, "qtyField"),
            root.TryGetProperty("roundDigits", out var digits) && digits.ValueKind == JsonValueKind.Number
                && digits.TryGetInt32(out var value) ? value : 2);
        if (config.RoundDigits is < 0 or > 6)
            throw new EffectConfigException("detail-flag-and-rollup 的 roundDigits 必须在 0..6 之间。");
        foreach (var (table, column) in new[]
                 {
                     (config.MasterTable, config.KeyField), (config.MasterTable, config.PriceField),
                     (config.MasterTable, config.AmountField), (config.MasterTable, config.QtyField),
                     (config.DetailTable, config.KeyField), (config.DetailTable, config.DetailSerialField),
                     (config.DetailTable, config.StateField), (config.DetailTable, config.PriceField),
                     (config.DetailTable, config.AmountField), (config.DetailTable, config.QtyField),
                     (config.BomTable, config.BomKeyField), (config.BomTable, config.BomSerialField),
                 })
        {
            if (!columns.Contains(table + "." + column))
                throw new EffectConfigException($"detail-flag-and-rollup 列不存在：{table}.{column}。");
        }
        return config;
    }

    private static string Required(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(value.GetString())
                ? value.GetString()!.Trim()
                : throw new EffectConfigException($"detail-flag-and-rollup 缺少字符串字段 {name}。");
}

internal sealed record DetailFlagAndRollupConfig(
    string MasterTable,
    string DetailTable,
    string KeyField,
    string BomTable,
    string BomKeyField,
    string BomSerialField,
    string DetailSerialField,
    string StateField,
    string PriceField,
    string AmountField,
    string QtyField,
    int RoundDigits);
