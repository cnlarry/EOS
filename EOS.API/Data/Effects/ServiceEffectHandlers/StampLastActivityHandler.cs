using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace EOS.API.Data.Effects.ServiceEffectHandlers;

/// <summary>
/// stamp-last-activity (quote-price form): on approval the latest purchase price of the
/// quoted products is stamped from the quote detail lines (price / unit / currency),
/// matching PRODUCT.PRO_NO or PRODUCT.STUFF_ID; when the configuration carries a
/// condition the quote date must not be earlier than the product's last trade date.
/// Ported from the PRODUCT update in P_WF_COP_QUOTE / P_WF_PUR_QUOTE. Deapprove is a
/// no-op (reverse kind "no-reverse"). Formula instances (1406) keep executing their
/// expanded rows; this handler backs the placeholder instances (1404/1604).
/// All identifiers come from closed configuration checked against physical columns.
/// </summary>
public sealed class StampLastActivityHandler : IEffectServiceHandler
{
    public string EffectKey => "stamp-last-activity";

    public async Task<int> ExecuteAsync(ServiceEffectContext context, CancellationToken token)
    {
        var root = context.Action.Params ?? throw new EffectConfigException("stamp-last-activity 缺少参数。");
        var plan = context.Plan;
        if (plan.MasterTable is null || plan.DetailTable is null)
            throw new EffectConfigException("stamp-last-activity 需要主子表形态。");
        if (context.MasterKeyValues.Count < 2)
            throw new EffectConfigException("stamp-last-activity 缺少单据主键值。");
        var columns = await new EffectPhysicalColumns().LoadAsync(context.Connection, token, context.Transaction);
        var cfg = StampLastActivityConfig.Parse(root, plan, columns);

        if (context.ExecutionEvent is EffectEvent.ApproveEffect or EffectEvent.Save)
        {
            return await StampAsync(context, cfg, token);
        }
        return 0; // no-reverse: the latest-price stamp is not rolled back on deapprove.
    }

    private static async Task<int> StampAsync(ServiceEffectContext context, StampLastActivityConfig cfg, CancellationToken token)
    {
        var plan = context.Plan;
        var parameters = new List<EffectSqlParameter>
        {
            new("@ct", context.MasterKeyValues[0]),
            new("@cn", context.MasterKeyValues[1]),
        };
        var match = cfg.MatchStuffId
            ? $"(T.PRO_NO=D.PRO_NO OR T.STUFF_ID=D.PRO_NO)"
            : "T.PRO_NO=D.PRO_NO";
        var sets = string.Join(", ", cfg.Fields.Select(field =>
            $"T.{ServiceEffectSql.Q(field.Target)} = D.{ServiceEffectSql.Q(field.Source)}"));
        var sql = $"UPDATE T SET {sets} FROM dbo.{ServiceEffectSql.Q(cfg.TargetTable)} T "
            + $"JOIN dbo.{ServiceEffectSql.Q(plan.DetailTable!)} D ON {match} "
            + $"WHERE D.{ServiceEffectSql.Q(plan.MasterPkOrder[0])}=@ct AND D.{ServiceEffectSql.Q(plan.MasterPkOrder[1])}=@cn";
        if (cfg.RequiresQuoteDateGuard)
        {
            var quoteDate = "@qd";
            var read = $"SELECT M.QUOTE_DATE FROM dbo.{ServiceEffectSql.Q(plan.MasterTable!)} M "
                + $"WHERE {ServiceEffectSql.MasterKeyFilter(plan, context.MasterKeyValues, parameters)}";
            object? quoteDateValue;
            await using (var command = new SqlCommand(read, context.Connection, context.Transaction))
            {
                foreach (var parameter in parameters)
                    command.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);
                var scalar = await command.ExecuteScalarAsync(token);
                quoteDateValue = scalar is null or DBNull ? null : Convert.ToDateTime(scalar);
            }
            parameters.Add(new EffectSqlParameter(quoteDate, quoteDateValue));
            sql += $" AND @qd>=COALESCE(T.LAST_TRADE_DATE, '1900-01-01')";
        }
        await using var update = new SqlCommand(sql, context.Connection, context.Transaction);
        foreach (var parameter in parameters)
            update.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);
        return await update.ExecuteNonQueryAsync(token);
    }
}

internal sealed record StampLastActivityField(string Target, string Source);

internal sealed record StampLastActivityConfig(
    string TargetTable,
    IReadOnlyList<StampLastActivityField> Fields,
    bool MatchStuffId,
    bool RequiresQuoteDateGuard)
{
    public static StampLastActivityConfig Parse(JsonElement root, ModuleEffectPlan plan, ISet<string> columns)
    {
        if (root.ValueKind != JsonValueKind.Object)
            throw new EffectConfigException("stamp-last-activity 参数必须是 JSON 对象。");
        var target = Req(root, "targetTable");
        var fields = StrArr(root, "fields");
        if (fields.Length == 0)
            throw new EffectConfigException("stamp-last-activity.fields 不能为空。");
        var matchBy = StrArr(root, "matchBy");
        var hasCondition = root.TryGetProperty("condition", out var condition) && condition.ValueKind == JsonValueKind.Object;

        if (!columns.Contains(target))
            throw new EffectConfigException($"stamp-last-activity 目标表不存在：{target}。");
        var fieldPairs = new List<StampLastActivityField>();
        foreach (var field in fields)
        {
            var source = field.StartsWith("LAST_PURCHASE_", StringComparison.OrdinalIgnoreCase)
                ? field["LAST_PURCHASE_".Length..]
                : throw new EffectConfigException($"stamp-last-activity 目标字段 '{field}' 无法推导来源列。");
            if (!columns.Contains(target + "." + field) || !columns.Contains(plan.DetailTable + "." + source))
                throw new EffectConfigException($"stamp-last-activity 列不存在：{target}.{field}/{source}。");
            fieldPairs.Add(new StampLastActivityField(field, source));
        }
        var matchStuff = matchBy.Any(item => item.Equals("STUFF_ID", StringComparison.OrdinalIgnoreCase));
        foreach (var reference in new[]
        {
            plan.MasterTable + ".QUOTE_DATE", plan.MasterTable + ".QUOTE_TYPE", plan.MasterTable + ".QUOTE_NO",
            plan.DetailTable + ".PRO_NO", plan.DetailTable + ".QUOTE_TYPE", plan.DetailTable + ".QUOTE_NO",
            target + ".PRO_NO", target + ".LAST_TRADE_DATE",
        })
        {
            if (!columns.Contains(reference))
                throw new EffectConfigException($"stamp-last-activity 列不存在：{reference}。");
        }
        if (matchStuff && !columns.Contains(target + ".STUFF_ID"))
            throw new EffectConfigException("stamp-last-activity 目标表缺少 STUFF_ID 列。");
        return new StampLastActivityConfig(target, fieldPairs, matchStuff, hasCondition);
    }

    private static string Req(JsonElement e, string n) =>
        e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString())
            ? v.GetString()!.Trim()
            : throw new EffectConfigException($"stamp-last-activity 缺少字符串字段 '{n}'。");

    private static string[] StrArr(JsonElement e, string n)
    {
        if (!e.TryGetProperty(n, out var v) || v.ValueKind != JsonValueKind.Array)
            return Array.Empty<string>();
        var list = new List<string>();
        foreach (var item in v.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(item.GetString()))
                throw new EffectConfigException($"stamp-last-activity.{n} 必须是非空字段名数组。");
            list.Add(item.GetString()!.Trim());
        }
        return list.ToArray();
    }
}