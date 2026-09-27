using System.Data;
using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace EOS.API.Data.Effects.ServiceEffectHandlers;

/// <summary>
/// card-sibling-close: 发卡保存后作废冲突旧卡——同一卡号下的其他持卡人、以及同一员工名下的其他卡，
/// 到期日统一置为本次生效日的前一天（仅当旧卡未填到期日或不早于本次生效日）。
/// 语义与原 `HrDomainRules.CloseConflictingCardsAsync` 逐字一致，且**该实现是唯一实现**：
/// 单卡维护（180208）走本处理器，批量发卡（/jobs/card-batch）直接调用同一静态核心。
/// 参数闭合：目标表 + 四个列名（生效日/到期日/卡号列/员工列），后两者必须是模块主键列；
/// 偏移天数与比较口径固定在服务端。生效日为空时不动作（与既有实现一致）。
/// </summary>
public sealed class CardSiblingCloseHandler : IEffectServiceHandler
{
    public string EffectKey => "card-sibling-close";

    /// <summary>批量发卡（`/jobs/card-batch`）使用的固定配置：与 180208 的单卡维护同一张表、同一口径。</summary>
    internal static readonly CardSiblingCloseConfig CardBatchConfig =
        new("HR_EMPLOYEE_CARD", "BEGIN_DATE", "END_DATE", "CARD_ID", "EMP_ID", -1);

    public async Task<int> ExecuteAsync(ServiceEffectContext context, CancellationToken token)
    {
        var root = context.Action.Params ?? throw new EffectConfigException("card-sibling-close 缺少参数。");
        if (context.ExecutionEvent is not (EffectEvent.ApproveEffect or EffectEvent.Save)) return 0;
        var plan = context.Plan;
        if (plan.MasterTable is null) throw new EffectConfigException("card-sibling-close 需要主表形态。");
        var columns = await new EffectPhysicalColumns().LoadAsync(context.Connection, token, context.Transaction);
        var config = Parse(root, plan, columns);
        var keyIndex = IndexOf(plan, config.KeyField);
        var ownerIndex = IndexOf(plan, config.OwnerField);
        if (context.MasterKeyValues.Count <= Math.Max(keyIndex, ownerIndex))
            throw new EffectConfigException("card-sibling-close 缺少单据主键值。");

        var begin = await ReadBeginAsync(context, config, token);
        if (begin is null) return 0; // 未填生效日：与既有实现一致，不动作
        return await CloseAsync(context.Connection, context.Transaction, config,
            context.MasterKeyValues[keyIndex], context.MasterKeyValues[ownerIndex], begin.Value, token);
    }

    /// <summary>
    /// 作废冲突旧卡的核心（单卡维护与批量发卡共用）：两段 UPDATE，条件与既有实现逐字一致。
    /// </summary>
    internal static async Task<int> CloseAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        CardSiblingCloseConfig config,
        string? keyValue,
        string? ownerValue,
        DateTime begin,
        CancellationToken token)
    {
        var expires = begin.AddDays(config.OffsetDays);
        var affected = 0;
        foreach (var (column, value, otherColumn, otherValue) in new[]
                 {
                     (config.KeyField, keyValue, config.OwnerField, ownerValue),
                     (config.OwnerField, ownerValue, config.KeyField, keyValue),
                 })
        {
            var sql = "UPDATE dbo." + ServiceEffectSql.Q(config.Table)
                + " SET " + ServiceEffectSql.Q(config.EndField) + "=@expires"
                + " WHERE " + ServiceEffectSql.Q(column) + "=@value"
                + " AND " + ServiceEffectSql.Q(otherColumn) + "<>@other"
                + " AND (" + ServiceEffectSql.Q(config.EndField) + " IS NULL OR "
                + ServiceEffectSql.Q(config.EndField) + ">=@begin);";
            affected += await ServiceEffectSql.ExecAsync(connection, transaction, sql,
                [
                    new EffectSqlParameter("@expires", expires),
                    new EffectSqlParameter("@begin", begin),
                    new EffectSqlParameter("@value", value),
                    new EffectSqlParameter("@other", otherValue),
                ], token);
        }
        return affected;
    }

    private static async Task<DateTime?> ReadBeginAsync(
        ServiceEffectContext context, CardSiblingCloseConfig config, CancellationToken token)
    {
        var parameters = new List<EffectSqlParameter>();
        var where = ServiceEffectSql.MasterKeyFilter(context.Plan, context.MasterKeyValues, parameters);
        var sql = "SELECT M." + ServiceEffectSql.Q(config.BeginField) + " FROM dbo."
            + ServiceEffectSql.Q(config.Table) + " M WHERE " + where + ";";
        await using var command = new SqlCommand(sql, context.Connection, context.Transaction);
        foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);
        var value = await command.ExecuteScalarAsync(token);
        return value is null or DBNull ? null : Convert.ToDateTime(value);
    }

    /// <summary>参数解析（fail-closed：列必须物理存在，卡号列与员工列必须是本模块主键列）。</summary>
    internal static CardSiblingCloseConfig Parse(JsonElement root, ModuleEffectPlan plan, ISet<string> columns)
    {
        var table = Required(root, "table");
        var beginField = Required(root, "beginField");
        var endField = Required(root, "endField");
        var keyField = Required(root, "keyField");
        var ownerField = Required(root, "ownerField");
        var offsetDays = root.TryGetProperty("offsetDays", out var offset) && offset.ValueKind == JsonValueKind.Number
            && offset.TryGetInt32(out var days) ? days : -1;
        foreach (var column in new[] { beginField, endField, keyField, ownerField })
            if (!columns.Contains(table + "." + column))
                throw new EffectConfigException($"card-sibling-close 列不存在：{table}.{column}。");
        foreach (var column in new[] { keyField, ownerField })
            if (!plan.MasterPkOrder.Contains(column, StringComparer.OrdinalIgnoreCase))
                throw new EffectConfigException($"card-sibling-close 的 {column} 必须是本模块主键列（禁止按任意列匹配他行）。");
        return new CardSiblingCloseConfig(table, beginField, endField, keyField, ownerField, offsetDays);
    }

    private static int IndexOf(ModuleEffectPlan plan, string column)
    {
        for (var index = 0; index < plan.MasterPkOrder.Count; index++)
            if (plan.MasterPkOrder[index].Equals(column, StringComparison.OrdinalIgnoreCase)) return index;
        throw new EffectConfigException($"card-sibling-close 主键列 {column} 不在模块主键顺序内。");
    }

    private static string Required(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(value.GetString())
                ? value.GetString()!.Trim()
                : throw new EffectConfigException($"card-sibling-close 缺少字符串字段 {name}。");
}

internal sealed record CardSiblingCloseConfig(
    string Table, string BeginField, string EndField, string KeyField, string OwnerField, int OffsetDays);
