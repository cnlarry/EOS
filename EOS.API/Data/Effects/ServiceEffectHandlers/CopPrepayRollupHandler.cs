using System.Data;
using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace EOS.API.Data.Effects.ServiceEffectHandlers;

/// <summary>
/// cop-prepay-rollup: 预收帐款单保存后的主表金额汇总（原 `CopDomainRules.CopPrepayAfterSaveAsync`，
/// 来源旧过程 `P_COP_PREPAY_After_Save` 的
/// `update COP_PREPAY_M set AMOUNT=d.AMOUNT from (select ... round(sum(AMOUNT),3) AMOUNT ...) d`）。
/// 与旧过程逐字一致：明细为空时**不回写**（内连接形态），舍入位数可配（旧实现为 3）。
/// 参数闭合：两张表 + 各列名 + 舍入位数，全部校验为物理列；单据键值只作参数传入。
/// </summary>
public sealed class CopPrepayRollupHandler : IEffectServiceHandler
{
    public string EffectKey => "cop-prepay-rollup";

    public async Task<int> ExecuteAsync(ServiceEffectContext context, CancellationToken token)
    {
        var root = context.Action.Params ?? throw new EffectConfigException("cop-prepay-rollup 缺少参数。");
        if (context.ExecutionEvent is not (EffectEvent.ApproveEffect or EffectEvent.Save)) return 0;
        if (context.MasterKeyValues.Count < 2)
            throw new EffectConfigException("cop-prepay-rollup 缺少单据主键值。");
        if (string.IsNullOrWhiteSpace(context.Plan.MasterTable))
            throw new EffectConfigException("cop-prepay-rollup 需要主表形态。");
        var columns = await new EffectPhysicalColumns().LoadAsync(context.Connection, token, context.Transaction);
        var config = Parse(root, context.Plan.MasterTable, columns);
        var type = context.MasterKeyValues[0] ?? string.Empty;
        var no = context.MasterKeyValues[1] ?? string.Empty;

        var rollup = "UPDATE m SET m." + Q(config.Master.AmountField) + "=s.AMOUNT_SUM FROM dbo."
            + Q(context.Plan.MasterTable) + " m INNER JOIN (SELECT d." + Q(config.Master.TypeField) + ", d."
            + Q(config.Master.NoField) + ", ROUND(SUM(d." + Q(config.Detail.AmountField)
            + "),@digits) AMOUNT_SUM FROM dbo." + Q(config.Detail.Table) + " d WHERE d."
            + Q(config.Master.TypeField) + "=@type AND d." + Q(config.Master.NoField) + "=@no GROUP BY d."
            + Q(config.Master.TypeField) + ", d." + Q(config.Master.NoField) + ") s ON s."
            + Q(config.Master.TypeField) + "=m." + Q(config.Master.TypeField) + " AND s."
            + Q(config.Master.NoField) + "=m." + Q(config.Master.NoField) + " WHERE m."
            + Q(config.Master.TypeField) + "=@type AND m." + Q(config.Master.NoField) + "=@no;";
        return await ServiceEffectSql.ExecAsync(context.Connection, context.Transaction, rollup,
            [
                new EffectSqlParameter("@type", type), new EffectSqlParameter("@no", no),
                new EffectSqlParameter("@digits", config.RoundDigits),
            ], token);
    }

    private static string Q(string identifier) => ServiceEffectSql.Q(identifier);

    /// <summary>参数解析（fail-closed：两张表的每个列名都必须物理存在，舍入位数 0..6）。</summary>
    internal static CopPrepayRollupConfig Parse(JsonElement root, string masterTable, ISet<string> columns)
    {
        var master = Section(root, "master");
        var detail = Section(root, "detail");
        var config = new CopPrepayRollupConfig(
            new CopPrepayRollupMaster(Required(master, "typeField"), Required(master, "noField"),
                Required(master, "amountField")),
            new CopPrepayRollupDetail(Required(detail, "table"), Required(detail, "amountField")),
            root.TryGetProperty("roundDigits", out var digits) && digits.ValueKind == JsonValueKind.Number
                && digits.TryGetInt32(out var value) ? value : 3);
        if (config.RoundDigits is < 0 or > 6)
            throw new EffectConfigException("cop-prepay-rollup 的 roundDigits 必须在 0..6 之间。");
        foreach (var (table, column) in new[]
                 {
                     (masterTable, config.Master.TypeField), (masterTable, config.Master.NoField),
                     (masterTable, config.Master.AmountField),
                     (config.Detail.Table, config.Master.TypeField), (config.Detail.Table, config.Master.NoField),
                     (config.Detail.Table, config.Detail.AmountField),
                 })
        {
            if (!columns.Contains(table + "." + column))
                throw new EffectConfigException($"cop-prepay-rollup 列不存在：{table}.{column}。");
        }
        return config;
    }

    private static JsonElement Section(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Object
            ? value
            : throw new EffectConfigException($"cop-prepay-rollup 缺少对象字段 {name}。");

    private static string Required(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(value.GetString())
                ? value.GetString()!.Trim()
                : throw new EffectConfigException($"cop-prepay-rollup 缺少字符串字段 {name}。");
}

internal sealed record CopPrepayRollupMaster(string TypeField, string NoField, string AmountField);
internal sealed record CopPrepayRollupDetail(string Table, string AmountField);
internal sealed record CopPrepayRollupConfig(CopPrepayRollupMaster Master, CopPrepayRollupDetail Detail,
    int RoundDigits);
