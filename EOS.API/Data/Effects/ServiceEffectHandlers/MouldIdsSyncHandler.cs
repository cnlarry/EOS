using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace EOS.API.Data.Effects.ServiceEffectHandlers;

/// <summary>
/// mould-ids-sync: 产品模具对照保存后按产品回写"所用模具汇总"。
/// 汇总口径由服务端固定的标量函数 `dbo.f_get_pro_moulds` 决定——配置只能给目标表/主键列/回写列，
/// 不能传函数名或 SQL 片段（避免把任意表达式带进执行期）。SAVE 期执行，解批无反向语义。
/// </summary>
public sealed class MouldIdsSyncHandler : IEffectServiceHandler
{
    private const string SummaryFunction = "f_get_pro_moulds";

    public string EffectKey => "mould-ids-sync";

    public async Task<int> ExecuteAsync(ServiceEffectContext context, CancellationToken token)
    {
        var root = context.Action.Params ?? throw new EffectConfigException("mould-ids-sync 缺少参数。");
        if (context.ExecutionEvent is not (EffectEvent.ApproveEffect or EffectEvent.Save)) return 0;
        if (context.MasterKeyValues.Count == 0)
            throw new EffectConfigException("mould-ids-sync 缺少单据主键值。");
        var columns = await new EffectPhysicalColumns().LoadAsync(context.Connection, token, context.Transaction);
        var config = Parse(root, context.Plan, columns);
        await EnsureFunctionAsync(context, token);

        var sql = "UPDATE T SET T." + ServiceEffectSql.Q(config.ValueField)
            + " = dbo." + ServiceEffectSql.Q(SummaryFunction) + "(T." + ServiceEffectSql.Q(config.KeyField) + ") "
            + "FROM dbo." + ServiceEffectSql.Q(config.Table) + " T "
            + "WHERE T." + ServiceEffectSql.Q(config.KeyField) + " = @k0;";
        return await ServiceEffectSql.ExecAsync(context.Connection, context.Transaction, sql,
            [new EffectSqlParameter("@k0", context.MasterKeyValues[0])], token);
    }

    /// <summary>目标表/列与主键列校验（fail-closed；表名与列名都必须是物理对象）。</summary>
    internal static MouldIdsConfig Parse(JsonElement root, ModuleEffectPlan plan, ISet<string> columns)
    {
        var table = Required(root, "table");
        var keyField = Required(root, "keyField");
        var valueField = Required(root, "valueField");
        if (!columns.Contains(table + "." + keyField))
            throw new EffectConfigException($"mould-ids-sync 主键列不存在：{table}.{keyField}。");
        if (!columns.Contains(table + "." + valueField))
            throw new EffectConfigException($"mould-ids-sync 回写列不存在：{table}.{valueField}。");
        if (plan.MasterPkOrder.Count == 0 || !plan.MasterPkOrder[0].Equals(keyField, StringComparison.OrdinalIgnoreCase))
            throw new EffectConfigException($"mould-ids-sync 的 keyField（{keyField}）必须是本模块主键首列，禁止回写他表。");
        return new MouldIdsConfig(table, keyField, valueField);
    }

    private static async Task EnsureFunctionAsync(ServiceEffectContext context, CancellationToken token)
    {
        await using var command = new SqlCommand("SELECT OBJECT_ID(N'dbo." + SummaryFunction + "', N'FN');",
            context.Connection, context.Transaction);
        var id = await command.ExecuteScalarAsync(token);
        if (id is null || id is DBNull)
            throw new EffectConfigException($"mould-ids-sync 依赖的汇总函数 dbo.{SummaryFunction} 不存在。");
    }

    private static string Required(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(value.GetString())
                ? value.GetString()!.Trim()
                : throw new EffectConfigException($"mould-ids-sync 缺少字符串字段 {name}。");
}

internal sealed record MouldIdsConfig(string Table, string KeyField, string ValueField);
