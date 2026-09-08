using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace EOS.API.Data.Effects.ServiceEffectHandlers;

/// <summary>
/// payment-date-calc: on approval the expected receive/payout date of the module master
/// is derived from the settlement month plus the party's payment days, ported from the
/// legacy workflow procedures:
///   - the month base is the settlement month (TABLE.COLUMN monthField, YYYYMM) when
///     present, otherwise the document date (dateField) month;
///   - the due date = first day of the following month + party payment days, where the
///     party is the master's CLIENT/SUPPLIER row joined on the party key (party name +
///     "_ID", the cross-table convention across this ERP);
///   - ISNULL(PAYMENT_DAY, 0) keeps a party without payment days on the month's first day.
/// Deapprove mirrors the approve chain: reverse kind "clear-on-deapprove" empties the
/// target date (the settled semantics; legacy procedures left the date in place),
/// "no-reverse" is a no-op, anything else is a configuration error.
/// All identifiers come from closed configuration checked against physical columns.
/// </summary>
public sealed class PaymentDateCalcHandler : IEffectServiceHandler
{
    public string EffectKey => "payment-date-calc";

    private readonly EffectPhysicalColumns _columns = new();

    public async Task<int> ExecuteAsync(ServiceEffectContext context, CancellationToken token)
    {
        var root = context.Action.Params ?? throw new EffectConfigException("payment-date-calc 缺少参数。");
        var plan = context.Plan;
        if (plan.MasterTable is null)
            throw new EffectConfigException("payment-date-calc 需要主表形态。");
        if (context.MasterKeyValues.Count == 0)
            throw new EffectConfigException("payment-date-calc 缺少单据主键值。");
        var columns = await _columns.LoadAsync(context.Connection, token, context.Transaction);
        var config = PaymentDateConfig.Parse(root, plan, columns);

        if (context.ExecutionEvent is EffectEvent.ApproveEffect or EffectEvent.Save)
            return await CalculateAsync(context, config, token);
        return ReverseKind(context) switch
        {
            "clear-on-deapprove" => await ClearAsync(context, config, token),
            "no-reverse" => 0,
            null => throw new EffectConfigException("payment-date-calc 解批缺少 reverse.kind，禁止无守卫执行。"),
            var kind => throw new EffectConfigException($"payment-date-calc 解批 reverse.kind '{kind}' 不受支持。"),
        };
    }

    /// <summary>
    /// UPDATE M SET M.[target] = DATEADD(DAY, ISNULL(P.[days], 0), DATEADD(MONTH, 1,
    /// CAST(monthBase + '01' AS DATETIME))) FROM master M JOIN party P ON the party key,
    /// restricted to the master key values. monthBase is a CHAR(8) "YYYY-MM-" built from
    /// the settlement month or the document date, mirroring the legacy procedures
    /// (CONVERT(CHAR(8), date, 20) truncates to "YYYY-MM-").
    /// </summary>
    private static async Task<int> CalculateAsync(
        ServiceEffectContext context,
        PaymentDateConfig config,
        CancellationToken token)
    {
        var plan = context.Plan;
        var parameters = new List<EffectSqlParameter>();
        var where = ServiceEffectSql.MasterKeyFilter(plan, context.MasterKeyValues, parameters);
        var month = ServiceEffectSql.Q(config.MonthField);
        var date = ServiceEffectSql.Q(config.DateField);
        var monthBase = $"CASE WHEN ISNULL(M.{month}, N'') = N'' THEN CONVERT(CHAR(8), M.{date}, 20) "
            + $"ELSE LEFT(M.{month}, 4) + '-' + RIGHT(M.{month}, 2) + '-' END";
        var due = $"DATEADD(DAY, ISNULL(P.{ServiceEffectSql.Q(config.DaysColumn)}, 0), "
            + $"DATEADD(MONTH, 1, CAST({monthBase} + '01' AS DATETIME)))";
        var sql = $"UPDATE M SET {ServiceEffectSql.Q(config.TargetField)} = {due} "
            + $"FROM dbo.{ServiceEffectSql.Q(plan.MasterTable!)} M "
            + $"JOIN dbo.{ServiceEffectSql.Q(config.PartyTable)} P "
            + $"ON M.{ServiceEffectSql.Q(config.PartyKeyColumn)} = P.{ServiceEffectSql.Q(config.PartyKeyColumn)} "
            + $"WHERE {where}";
        return await ServiceEffectSql.ExecAsync(context.Connection, context.Transaction, sql, parameters, token);
    }

    private static async Task<int> ClearAsync(
        ServiceEffectContext context,
        PaymentDateConfig config,
        CancellationToken token)
    {
        var plan = context.Plan;
        var parameters = new List<EffectSqlParameter>();
        var where = ServiceEffectSql.MasterKeyFilter(plan, context.MasterKeyValues, parameters);
        var sql = $"UPDATE M SET {ServiceEffectSql.Q(config.TargetField)} = NULL "
            + $"FROM dbo.{ServiceEffectSql.Q(plan.MasterTable!)} M WHERE {where}";
        return await ServiceEffectSql.ExecAsync(context.Connection, context.Transaction, sql, parameters, token);
    }

    private static string? ReverseKind(ServiceEffectContext context)
    {
        if (context.Action.Reverse is not { } reverse
            || reverse.ValueKind != JsonValueKind.Object
            || !reverse.TryGetProperty("kind", out var kind)
            || kind.ValueKind != JsonValueKind.String)
            return null;
        return kind.GetString();
    }
}

/// <summary>Parsed payment-date-calc configuration with closed shapes.</summary>
internal sealed record PaymentDateConfig(
    string TargetField,
    string MonthField,
    string DateField,
    string PartyTable,
    string PartyKeyColumn,
    string DaysColumn)
{
    public static PaymentDateConfig Parse(JsonElement root, ModuleEffectPlan plan, ISet<string> columns)
    {
        if (root.ValueKind != JsonValueKind.Object)
            throw new EffectConfigException("payment-date-calc 参数必须是 JSON 对象。");
        var target = Required(root, "targetField");
        var month = Required(root, "monthField");
        var date = Required(root, "dateField");
        var source = Required(root, "paymentDaysFrom");
        var dot = source.IndexOf('.');
        if (dot <= 0 || dot == source.Length - 1)
            throw new EffectConfigException("payment-date-calc.paymentDaysFrom 必须是 'TABLE.COLUMN' 形式。");
        var party = source[..dot];
        var daysColumn = source[(dot + 1)..];
        var master = plan.MasterTable!;
        var partyKey = party + "_ID";
        if (!columns.Contains(master + "." + target))
            throw new EffectConfigException($"payment-date-calc 目标列不存在：{master}.{target}。");
        if (!columns.Contains(master + "." + month))
            throw new EffectConfigException($"payment-date-calc 账期月列不存在：{master}.{month}。");
        if (!columns.Contains(master + "." + date))
            throw new EffectConfigException($"payment-date-calc 单据日期列不存在：{master}.{date}。");
        if (!columns.Contains(party + "." + daysColumn))
            throw new EffectConfigException($"payment-date-calc 主档天数列不存在：{party}.{daysColumn}。");
        if (!columns.Contains(master + "." + partyKey) || !columns.Contains(party + "." + partyKey))
            throw new EffectConfigException($"payment-date-calc 主表与主档缺少同名列 {partyKey}，无法关联。");
        return new PaymentDateConfig(target, month, date, party, partyKey, daysColumn);
    }

    private static string Required(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
        && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()!.Trim()
            : throw new EffectConfigException($"payment-date-calc 缺少字符串字段 '{name}'。");
}
