using System.Text;
using System.Text.Json;

namespace EOS.API.Data.Effects.ServiceEffectHandlers;

/// <summary>
/// detail-rollup: 保存/批核后把**明细按单据键聚合**回写主表——即"主表 ← 明细汇总"这一形的**通用键**。
/// 由配置声明：明细表、舍入位数，以及若干条「主表列 ← ROUND(SUM(明细列), 位数) [＋主表列]」赋值。
/// 合并了原先四个同形键（`cop-account-rollup` / `purchase-due-rollup` / `cop-prepay-rollup` / `pur-prepay-rollup`）。
/// 与旧实现逐字一致的两处口径：⒜ 明细为空时**不回写**（走内连接而非标量子查询）；
/// ⒝ 求和一律 `ROUND(SUM(...), @digits)`，"＋主表列"的那一项不再额外舍入。
/// 参数闭合：明细表名与全部列名都校验为物理列；单据键列取自单据计划（不额外配置），键值只作参数传入。
/// </summary>
public sealed class DetailRollupHandler : IEffectServiceHandler
{
    public string EffectKey => "detail-rollup";

    public async Task<int> ExecuteAsync(ServiceEffectContext context, CancellationToken token)
    {
        var root = context.Action.Params ?? throw new EffectConfigException("detail-rollup 缺少参数。");
        if (context.ExecutionEvent is not (EffectEvent.ApproveEffect or EffectEvent.Save)) return 0;
        var plan = context.Plan;
        if (string.IsNullOrWhiteSpace(plan.MasterTable))
            throw new EffectConfigException("detail-rollup 需要主表形态。");
        if (plan.MasterPkOrder.Count < 2)
            throw new EffectConfigException("detail-rollup 需要两列单据主键。");
        if (context.MasterKeyValues.Count < 2)
            throw new EffectConfigException("detail-rollup 缺少单据主键值。");
        var columns = await new EffectPhysicalColumns().LoadAsync(context.Connection, token, context.Transaction);
        var config = Parse(root, plan.MasterTable, columns);

        var sql = BuildUpdateStatement(plan.MasterTable, plan.MasterPkOrder[0], plan.MasterPkOrder[1], config);
        return await ServiceEffectSql.ExecAsync(context.Connection, context.Transaction, sql,
            [
                new EffectSqlParameter("@type", context.MasterKeyValues[0] ?? string.Empty),
                new EffectSqlParameter("@no", context.MasterKeyValues[1] ?? string.Empty),
                new EffectSqlParameter("@digits", config.RoundDigits),
            ], token);
    }

    /// <summary>
    /// 同一个明细列只聚合一次，多个目标列共用该别名（与旧实现复用 `AMOUNT_TAX_SUM` 的做法一致）。
    /// 外层条件整体带别名限定：汇总子查询与主表存在同名单据键列，未限定的列名会报"列名不明确"。
    /// </summary>
    internal static string BuildUpdateStatement(string masterTable, string typeColumn, string noColumn,
        DetailRollupConfig config)
    {
        var q = ServiceEffectSql.Q;
        var type = q(typeColumn);
        var no = q(noColumn);
        var aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var selects = new List<string>();
        foreach (var assignment in config.Assignments)
        {
            if (aliases.ContainsKey(assignment.Sum))
                continue;
            var alias = "S" + (aliases.Count + 1);
            aliases[assignment.Sum] = alias;
            selects.Add($"ROUND(SUM(d.{q(assignment.Sum)}),@digits) {alias}");
        }
        var sets = new StringBuilder();
        foreach (var assignment in config.Assignments)
        {
            if (sets.Length > 0)
                sets.Append(", ");
            sets.Append("m.").Append(q(assignment.Target)).Append("=s.").Append(aliases[assignment.Sum]);
            if (assignment.PlusMaster is not null)
                sets.Append("+m.").Append(q(assignment.PlusMaster));
        }
        return $"UPDATE m SET {sets} FROM dbo.{q(masterTable)} m INNER JOIN (SELECT d.{type}, d.{no}, "
            + $"{string.Join(", ", selects)} FROM dbo.{q(config.DetailTable)} d "
            + $"WHERE d.{type}=@type AND d.{no}=@no GROUP BY d.{type}, d.{no}) s "
            + $"ON s.{type}=m.{type} AND s.{no}=m.{no} WHERE m.{type}=@type AND m.{no}=@no;";
    }

    private static string Q(string identifier) => ServiceEffectSql.Q(identifier);

    /// <summary>
    /// 参数解析（fail-closed）：明细表 + 每个目标列/来源列/附加主表列都必须物理存在；
    /// 目标列不得重复；赋值条数 1..8；舍入位数 0..6。
    /// </summary>
    internal static DetailRollupConfig Parse(JsonElement root, string masterTable, ISet<string> columns)
    {
        const string label = "detail-rollup";
        if (root.ValueKind != JsonValueKind.Object)
            throw new EffectConfigException($"{label} 参数必须是 JSON 对象。");
        var detailTable = RequiredField(root, "detailTable", label);
        var roundDigits = root.TryGetProperty("roundDigits", out var digits) && digits.ValueKind == JsonValueKind.Number
            && digits.TryGetInt32(out var value) ? value : 2;
        if (roundDigits is < 0 or > 6)
            throw new EffectConfigException($"{label} 的 roundDigits 必须在 0..6 之间。");
        if (!root.TryGetProperty("assignments", out var assignments) || assignments.ValueKind != JsonValueKind.Array)
            throw new EffectConfigException($"{label} 缺少数组字段 assignments。");

        var list = new List<DetailRollupAssignment>();
        var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in assignments.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                throw new EffectConfigException($"{label} 的 assignments 项必须是对象。");
            var target = RequiredField(item, "target", label);
            var sum = RequiredField(item, "sum", label);
            var plusMaster = item.TryGetProperty("plusMaster", out var plus) && plus.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(plus.GetString()) ? plus.GetString()!.Trim() : null;
            if (!targets.Add(target))
                throw new EffectConfigException($"{label} 的目标列重复：{target}。");
            list.Add(new DetailRollupAssignment(target, sum, plusMaster));
        }
        if (list.Count is 0 or > 8)
            throw new EffectConfigException($"{label} 的 assignments 条数必须在 1..8 之间。");

        foreach (var assignment in list)
        {
            RequireColumn(columns, masterTable, assignment.Target, label);
            RequireColumn(columns, detailTable, assignment.Sum, label);
            if (assignment.PlusMaster is not null)
                RequireColumn(columns, masterTable, assignment.PlusMaster, label);
        }
        return new DetailRollupConfig(detailTable, roundDigits, list);
    }

    private static void RequireColumn(ISet<string> columns, string table, string column, string label)
    {
        if (!columns.Contains(table + "." + column))
            throw new EffectConfigException($"{label} 列不存在：{table}.{column}。");
    }

    private static string RequiredField(JsonElement element, string name, string label)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(value.GetString())
                ? value.GetString()!.Trim()
                : throw new EffectConfigException($"{label} 缺少字符串字段 {name}。");
}

/// <summary>一条回写：主表 <paramref name="Target"/> ＝ ROUND(SUM(明细 <paramref name="Sum"/>), 位数)［＋主表 PlusMaster］。</summary>
internal sealed record DetailRollupAssignment(string Target, string Sum, string? PlusMaster);
internal sealed record DetailRollupConfig(string DetailTable, int RoundDigits, IReadOnlyList<DetailRollupAssignment> Assignments);
