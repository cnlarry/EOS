using System.Data;
using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace EOS.API.Data.Effects.ServiceEffectHandlers;

/// <summary>
/// bom-size-backfill: 产品 BOM 保存后把产品档案的历史长宽列复制到本单旧长宽列
/// （原 `SysDomainRules.BomStruAfterSaveAsync` 的末段，来源旧过程 `P_BOM_STRU_After_Save` 的
/// `update bom_stru_m set P_LENGTH_OLD = d.P_LENGTH, P_WIDTH_OLD = d.P_WIDTH from product d where ...`）。
/// 参数闭合：来源表 + 定位列 + 目标/来源字段对照，全部校验为物理列；单据键值只作参数传入。
/// </summary>
public sealed class BomSizeBackfillHandler : IEffectServiceHandler
{
    public string EffectKey => "bom-size-backfill";

    public async Task<int> ExecuteAsync(ServiceEffectContext context, CancellationToken token)
    {
        var root = context.Action.Params ?? throw new EffectConfigException("bom-size-backfill 缺少参数。");
        if (context.ExecutionEvent is not (EffectEvent.ApproveEffect or EffectEvent.Save)) return 0;
        if (context.MasterKeyValues.Count == 0)
            throw new EffectConfigException("bom-size-backfill 缺少单据主键值。");
        if (string.IsNullOrWhiteSpace(context.Plan.MasterTable))
            throw new EffectConfigException("bom-size-backfill 需要主表形态。");
        var columns = await new EffectPhysicalColumns().LoadAsync(context.Connection, token, context.Transaction);
        var config = Parse(root, context.Plan.MasterTable, columns);
        var parameters = new List<EffectSqlParameter>();
        var masterScope = ServiceEffectSql.MasterKeyFilter(context.Plan, context.MasterKeyValues, parameters);
        var sets = string.Join(", ", config.Pairs.Select(pair =>
            "M." + ServiceEffectSql.Q(pair.Target) + "=S." + ServiceEffectSql.Q(pair.Source)));
        var sql = "UPDATE M SET " + sets + " FROM dbo." + ServiceEffectSql.Q(context.Plan.MasterTable) + " M "
            + "INNER JOIN dbo." + ServiceEffectSql.Q(config.SourceTable) + " S ON S."
            + ServiceEffectSql.Q(config.SourceKey) + "=M." + ServiceEffectSql.Q(config.MasterKey)
            + " WHERE " + masterScope + ";";
        return await ServiceEffectSql.ExecAsync(context.Connection, context.Transaction, sql, parameters, token);
    }

    /// <summary>参数解析（fail-closed：来源表与每个列名都必须物理存在）。</summary>
    internal static BomSizeBackfillConfig Parse(JsonElement root, string masterTable, ISet<string> columns)
    {
        if (root.ValueKind != JsonValueKind.Object)
            throw new EffectConfigException("bom-size-backfill 参数必须是 JSON 对象。");
        var sourceTable = Required(root, "sourceTable");
        var sourceKey = Required(root, "sourceKey");
        var masterKey = Required(root, "masterKey");
        if (!root.TryGetProperty("fields", out var fields) || fields.ValueKind != JsonValueKind.Array
            || fields.GetArrayLength() == 0)
            throw new EffectConfigException("bom-size-backfill 缺少非空 fields 数组。");
        var pairs = new List<(string Target, string Source)>();
        foreach (var field in fields.EnumerateArray())
        {
            if (field.ValueKind != JsonValueKind.Object)
                throw new EffectConfigException("bom-size-backfill.fields 项必须是 {target, source} 对象。");
            pairs.Add((Required(field, "target"), Required(field, "source")));
        }
        var config = new BomSizeBackfillConfig(sourceTable, sourceKey, masterKey, pairs);
        if (!columns.Contains(config.SourceTable))
            throw new EffectConfigException($"bom-size-backfill 来源表不存在：{config.SourceTable}。");
        foreach (var (table, column) in new[]
                 {
                     (config.SourceTable, config.SourceKey), (masterTable, config.MasterKey),
                 }.Concat(config.Pairs.SelectMany(pair => new[]
                 {
                     (masterTable, pair.Target), (config.SourceTable, pair.Source),
                 })))
        {
            if (!columns.Contains(table + "." + column))
                throw new EffectConfigException($"bom-size-backfill 列不存在：{table}.{column}。");
        }
        return config;
    }

    private static string Required(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(value.GetString())
                ? value.GetString()!.Trim()
                : throw new EffectConfigException($"bom-size-backfill 缺少字符串字段 {name}。");
}

internal sealed record BomSizeBackfillConfig(string SourceTable, string SourceKey, string MasterKey,
    IReadOnlyList<(string Target, string Source)> Pairs);
