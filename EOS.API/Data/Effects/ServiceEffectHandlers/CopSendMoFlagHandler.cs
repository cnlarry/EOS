using System.Text.Json;

namespace EOS.API.Data.Effects.ServiceEffectHandlers;

/// <summary>
/// cop-send-mo-flag: 送货单保存后给"每个品号客户订单号最大的一行"打 `mo_no='showbaozhuang'` 标记
/// （原 `CopDomainRules.CopSendAfterSaveAsync` 的末段，来源旧过程 `P_COP_SEND_After_Save`）。
/// 参数闭合：主表/明细表、单据键列、明细品号与客户订单号列、标记列与标记值，全部校验为物理列。
/// </summary>
public sealed class CopSendMoFlagHandler : IEffectServiceHandler
{
    public string EffectKey => "cop-send-mo-flag";

    public async Task<int> ExecuteAsync(ServiceEffectContext context, CancellationToken token)
    {
        var root = context.Action.Params ?? throw new EffectConfigException("cop-send-mo-flag 缺少参数。");
        if (context.ExecutionEvent is not (EffectEvent.ApproveEffect or EffectEvent.Save)) return 0;
        if (context.MasterKeyValues.Count < 2)
            throw new EffectConfigException("cop-send-mo-flag 缺少单据主键值。");
        if (string.IsNullOrWhiteSpace(context.Plan.MasterTable))
            throw new EffectConfigException("cop-send-mo-flag 需要主表形态。");
        var columns = await new EffectPhysicalColumns().LoadAsync(context.Connection, token, context.Transaction);
        var config = Parse(root, context.Plan.MasterTable, columns);
        var type = context.MasterKeyValues[0] ?? string.Empty;
        var no = context.MasterKeyValues[1] ?? string.Empty;
        var q = ServiceEffectSql.Q;

        var sql = "UPDATE d SET d." + q(config.FlagField) + "=@flag FROM dbo." + q(config.DetailTable) + " d INNER JOIN ("
            + "SELECT " + q(config.ProductField) + ", MAX(" + q(config.ClientOrderNoField) + ") MAX_CLIENT_ORDER_NO FROM dbo."
            + q(config.DetailTable) + " WHERE " + q(config.TypeField) + "=@type AND " + q(config.NoField)
            + "=@no GROUP BY " + q(config.ProductField) + ") max_d ON d." + q(config.ProductField)
            + "=max_d." + q(config.ProductField) + " AND d." + q(config.ClientOrderNoField)
            + "=max_d.MAX_CLIENT_ORDER_NO WHERE d." + q(config.TypeField) + "=@type AND d." + q(config.NoField) + "=@no;";
        return await ServiceEffectSql.ExecAsync(context.Connection, context.Transaction, sql,
            [
                new EffectSqlParameter("@type", type), new EffectSqlParameter("@no", no),
                new EffectSqlParameter("@flag", config.FlagValue),
            ], token);
    }

    /// <summary>参数解析（fail-closed：两张表与各列名都必须物理存在）。</summary>
    internal static CopSendMoFlagConfig Parse(JsonElement root, string masterTable, ISet<string> columns)
    {
        const string label = "cop-send-mo-flag";
        if (root.ValueKind != JsonValueKind.Object)
            throw new EffectConfigException($"{label} 参数必须是 JSON 对象。");
        var detail = root.TryGetProperty("detail", out var detailElement) && detailElement.ValueKind == JsonValueKind.Object
            ? detailElement
            : throw new EffectConfigException($"{label} 缺少对象字段 detail。");
        string Required(JsonElement element, string name)
            => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(value.GetString())
                    ? value.GetString()!.Trim()
                    : throw new EffectConfigException($"{label} 缺少字符串字段 {name}。");
        var config = new CopSendMoFlagConfig(Required(detail, "table"), Required(root, "typeField"),
            Required(root, "noField"), Required(detail, "productField"), Required(detail, "clientOrderNoField"),
            Required(detail, "flagField"), Required(detail, "flagValue"));
        foreach (var (table, column) in new[]
                 {
                     (masterTable, config.TypeField), (masterTable, config.NoField),
                     (config.DetailTable, config.TypeField), (config.DetailTable, config.NoField),
                     (config.DetailTable, config.ProductField), (config.DetailTable, config.ClientOrderNoField),
                     (config.DetailTable, config.FlagField),
                 })
        {
            if (!columns.Contains(table + "." + column))
                throw new EffectConfigException($"{label} 列不存在：{table}.{column}。");
        }
        return config;
    }
}

internal sealed record CopSendMoFlagConfig(string DetailTable, string TypeField, string NoField, string ProductField,
    string ClientOrderNoField, string FlagField, string FlagValue);
