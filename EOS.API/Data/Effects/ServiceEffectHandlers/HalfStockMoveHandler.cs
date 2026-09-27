using System.Text;
using System.Text.Json;
using EOS.API.Data.Inventory;
using Microsoft.Data.SqlClient;

namespace EOS.API.Data.Effects.ServiceEffectHandlers;

/// <summary>
/// Half-finished stock move effect (service-level): the C# port of the baseline
/// half-stock update procedure semantics (P_UPDATE_HALF_PRO_DEPOT), driven by closed
/// fieldMap parameters. Per document line the three-key stock slot
/// (PRO_NO + PROCEDURE_TYPE_ID + DEPOT_ID) is ensured and then moved:
/// the IN leg adds unconditionally while the OUT leg first passes a sufficiency
/// gate, exactly like the baseline helper (approve-IN / deapprove-OUT add;
/// approve-OUT / deapprove-IN gate then subtract). Unlike the finished-goods
/// inventory move there is no depot log, no batch ledger and no product derived
/// columns to maintain — the stock table is the only footprint.
/// Two intentional deviations from the baseline text: the gate aggregates rows
/// sharing one stock slot (the baseline cursor checks line by line, so two lines
/// for the same slot can jointly overdraw), and the shortage report carries a
/// header line for readability (row text is unchanged).
/// </summary>
public sealed class HalfStockMoveHandler : IEffectServiceHandler
{
    private readonly DepotStockPolicyService _policy;

    public HalfStockMoveHandler(DepotStockPolicyService policy) => _policy = policy;

    public string EffectKey => "half-stock-move";

    public async Task<int> ExecuteAsync(ServiceEffectContext context, CancellationToken token)
    {
        var root = context.Action.Params ?? throw new EffectConfigException("half-stock-move 缺少参数。");
        var plan = HalfStockMovePlan.Parse(root);
        RequireReverseKind(context.Action.Reverse);

        // 关账守卫排在读列 / 组行集之前：期间已关账就整单拒绝，不写一半再回滚。
        await EnsurePeriodOpenAsync(context, plan, token);

        var columns = await new EffectPhysicalColumns().LoadAsync(context.Connection, token, context.Transaction);
        plan.ValidateColumns(context.Plan, columns);
        var rowSet = plan.BuildRowSet(context.Plan, context.MasterKeyValues, columns);
        var executor = new HalfStockMoveSql(context.Connection, context.Transaction, plan, context.ExecutionEvent);
        return await executor.RunAsync(rowSet, token);
    }

    /// <summary>
    /// 半成品账的关账守卫（D3）：**是否启用由部署级参数 `MONTH_CLOSE_SCOPE_HALF_STOCK` 决定**，
    /// 与月结快照的范围同源——关 ⇒ 既不拦也不快照（默认；也就是这个参数落地之前的实际行为），开 ⇒ 拦。
    /// </summary>
    /// <remarks>
    /// 日期与主账移动引擎**同一口径**：批核 / 保存方向取**源单的日期列**（参数 `fieldMap.masterDate`），
    /// 其余（解批等）方向取当前时间——两个方向本来就要判不同的日期，不各写一份。
    /// 半成品账**不写流水**（本处理器只动余额表那一行），所以只判"这次改动落在不在已关账期"，
    /// 没有主账那条"回看这张单已有流水"的反向规矩可适用。
    /// </remarks>
    private async Task EnsurePeriodOpenAsync(ServiceEffectContext context, HalfStockMovePlan plan, CancellationToken token)
    {
        // 参数取值只经 DepotStockPolicyService（策略的唯一求值入口），且**一律取部署级行**：
        // 月结范围是全库口径，按库别配粒度会让同一个月的快照口径分裂。
        var policy = await _policy.ResolveAsync(null, context.Connection, context.Transaction, token);
        if (!policy.MonthCloseScopeHalfStock)
        {
            return;
        }

        var approve = context.ExecutionEvent is EffectEvent.ApproveEffect or EffectEvent.Save;
        var businessDate = approve ? await ReadMasterDateAsync(context, plan.MasterDateField, token) : DateTime.Now;
        await InventoryPeriodService.EnsureDateOpenAsync(
            context.Connection, context.Transaction, businessDate,
            $"半成品账（{context.Plan.MasterTable}）的这笔变动", token);
    }

    /// <summary>取源单的业务日期；日期列没值就按"现在"处理（与主账非批核方向同口径）。</summary>
    private static async Task<DateTime> ReadMasterDateAsync(
        ServiceEffectContext context, string dateField, CancellationToken token)
    {
        var table = context.Plan.MasterTable
            ?? throw new EffectConfigException("half-stock-move 关账守卫：源单主表未定义，取不到业务日期。");
        var keyColumns = context.Plan.MasterPkOrder;
        if (keyColumns.Count == 0 || keyColumns.Count != context.MasterKeyValues.Count)
        {
            throw new EffectConfigException("half-stock-move 关账守卫：源单主键列未定义，取不到业务日期。");
        }

        var where = string.Join(" AND ", keyColumns.Select((column, index) => $"[{column}]=@k{index}"));
        await using var command = new SqlCommand(
            $"SELECT [{dateField}] FROM dbo.[{table}] WHERE {where};", context.Connection, context.Transaction);
        for (var index = 0; index < keyColumns.Count; index++)
        {
            command.Parameters.AddWithValue($"@k{index}", context.MasterKeyValues[index]);
        }

        var value = await command.ExecuteScalarAsync(token);
        return value is null or DBNull ? DateTime.Now : Convert.ToDateTime(value);
    }

    /// <summary>Only the mirror reverse is supported; anything else is rejected fail-closed.</summary>
    internal static string RequireReverseKind(JsonElement? reverse)
    {
        var kind = reverse is { } element
            && element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty("kind", out var value)
            && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
        return kind switch
        {
            "reverse-flow" => kind,
            null => throw new EffectConfigException("half-stock-move 解批缺少 reverse.kind，禁止无守卫执行。"),
            _ => throw new EffectConfigException($"half-stock-move 解批 reverse.kind '{kind}' 不受支持。"),
        };
    }
}

/// <summary>Parsed half-stock-move parameters with closed shapes only.</summary>
public sealed record HalfStockMovePlan(
    int Direction,
    string MasterDateField,
    string QtyField)
{
    public static HalfStockMovePlan Parse(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            throw new EffectConfigException("half-stock-move 参数必须是 JSON 对象。");
        var direction = root.TryGetProperty("direction", out var d) && d.ValueKind == JsonValueKind.String
            ? d.GetString()!.Trim().ToUpperInvariant()
            : throw new EffectConfigException("half-stock-move 缺少 direction。");
        if (direction is not ("IN" or "OUT"))
            throw new EffectConfigException("half-stock-move.direction 仅允许 IN/OUT。");
        if (!root.TryGetProperty("fieldMap", out var fieldMap) || fieldMap.ValueKind != JsonValueKind.Object)
            throw new EffectConfigException("half-stock-move 缺少 fieldMap。");
        var masterDate = fieldMap.TryGetProperty("masterDate", out var md) && md.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(md.GetString())
            ? md.GetString()!.Trim()
            : throw new EffectConfigException("half-stock-move.fieldMap 缺少 masterDate。");
        var qty = fieldMap.TryGetProperty("qty", out var q) && q.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(q.GetString())
            ? q.GetString()!.Trim()
            : throw new EffectConfigException("half-stock-move.fieldMap.qty 必须是非空字段名。");
        return new HalfStockMovePlan(direction == "IN" ? 1 : -1, masterDate, qty);
    }

    /// <summary>Physical validation shared by the executor and the save-time check.</summary>
    public void ValidateColumns(ModuleEffectPlan plan, ISet<string> columns)
    {
        if (plan.MasterTable is null || plan.DetailTable is null)
            throw new EffectConfigException("half-stock-move 需要主表+明细表模块形态。");
        if (plan.MasterPkOrder.Count == 0)
            throw new EffectConfigException("half-stock-move 需要主表主键序。");
        if (!columns.Contains(plan.MasterTable + "." + MasterDateField)
            && !columns.Contains(plan.DetailTable + "." + MasterDateField))
            throw new EffectConfigException($"half-stock-move 单据日期列不存在：{MasterDateField}。");
        foreach (var field in new[] { QtyField, "PRO_NO", "PROCEDURE_TYPE_ID", "DEPOT_ID" })
            if (!columns.Contains(plan.DetailTable + "." + field))
                throw new EffectConfigException($"half-stock-move 明细列不存在：{plan.DetailTable}.{field}。");
        foreach (var pk in plan.MasterPkOrder)
            if (!columns.Contains(plan.MasterTable + "." + pk))
                throw new EffectConfigException($"half-stock-move 主表主键列不存在：{plan.MasterTable}.{pk}。");
    }

    /// <summary>Generated row-set statement: document lines scoped by the master keys.</summary>
    public sealed record RowSetStatement(string Sql, IReadOnlyList<EffectSqlParameter> Parameters);

    /// <summary>
    /// Builds the parameterized row-set SELECT feeding the move: module master joined
    /// to detail on same-named primary key columns. All identifiers come from
    /// configuration and are checked against the physical column whitelist.
    /// </summary>
    public RowSetStatement BuildRowSet(
        ModuleEffectPlan plan,
        IReadOnlyList<string> masterKeyValues,
        ISet<string> columns)
    {
        ValidateColumns(plan, columns);
        var keys = Math.Min(plan.MasterPkOrder.Count, Math.Max(masterKeyValues.Count, 0));
        if (keys == 0)
            throw new EffectConfigException("half-stock-move 缺少主表主键值，禁止无条件读取。");
        if (keys < 2)
            throw new EffectConfigException("half-stock-move 需要单据双键（类型+单号）主表形态，禁止单键执行。");

        var parameters = new List<EffectSqlParameter>();
        var builder = new StringBuilder("SELECT ");
        builder.Append($"M.{Q(plan.MasterPkOrder[0])} AS BILL_TYPE, ");
        builder.Append($"M.{Q(plan.MasterPkOrder[1])} AS BILL_NO, ");
        builder.Append($"M.{Q(MasterDateField)} AS BILL_DATE, ");
        builder.Append($"D.{Q("PRO_NO")} AS PRO_NO, ");
        builder.Append($"D.{Q("PROCEDURE_TYPE_ID")} AS PROCEDURE_TYPE_ID, ");
        builder.Append($"D.{Q("DEPOT_ID")} AS DEPOT_ID, ");
        builder.Append($"ISNULL(D.{Q(QtyField)}, 0) AS QTY ");
        builder.Append($"FROM dbo.{Q(plan.MasterTable!)} M JOIN dbo.{Q(plan.DetailTable!)} D ON ");
        builder.Append(string.Join(" AND ", plan.MasterPkOrder.Take(keys).Select(pk => $"D.{Q(pk)} = M.{Q(pk)}")));
        builder.Append(" WHERE ");
        var where = new List<string>();
        for (var index = 0; index < keys; index++)
        {
            var name = "@mk" + index;
            parameters.Add(new EffectSqlParameter(name, masterKeyValues[index]));
            where.Add($"M.{Q(plan.MasterPkOrder[index])} = {name}");
        }
        builder.Append(string.Join(" AND ", where));
        return new RowSetStatement(builder.ToString(), parameters);
    }

    internal static string Q(string identifier)
    {
        if (!WorkbenchSql.Identifier.IsMatch(identifier))
            throw new EffectConfigException($"标识符非法：'{identifier}'。");
        return "[" + identifier + "]";
    }
}

/// <summary>Executes the ported half-stock move against the module connection and transaction.</summary>
public sealed class HalfStockMoveSql
{
    private const string Tmp = "#HALF_MOVE_TMP";

    private readonly SqlConnection _connection;
    private readonly SqlTransaction _transaction;
    private readonly HalfStockMovePlan _plan;
    private readonly EffectEvent _event;

    public HalfStockMoveSql(
        SqlConnection connection,
        SqlTransaction transaction,
        HalfStockMovePlan plan,
        EffectEvent executionEvent)
    {
        _connection = connection;
        _transaction = transaction;
        _plan = plan;
        _event = executionEvent;
    }

    private bool IsApprove => _event is EffectEvent.ApproveEffect or EffectEvent.Save;

    public async Task<int> RunAsync(HalfStockMovePlan.RowSetStatement rowSet, CancellationToken token)
    {
        await ExecAsync($"IF OBJECT_ID('tempdb..{Tmp}') IS NOT NULL DROP TABLE {Tmp}", token);
        // Explicit temp shape (same pattern as the finished-goods move): widths follow
        // the document detail columns so no value is ever silently truncated.
        await ExecAsync(
            $"CREATE TABLE {Tmp}(PRO_NO nchar(60), PROCEDURE_TYPE_ID nchar(20), DEPOT_ID nchar(20), QTY float)", token);
        var fill = new SqlCommand(
            $"INSERT INTO {Tmp}(PRO_NO, PROCEDURE_TYPE_ID, DEPOT_ID, QTY) "
            + $"SELECT PRO_NO, PROCEDURE_TYPE_ID, DEPOT_ID, QTY FROM ({rowSet.Sql}) R",
            _connection, _transaction);
        foreach (var parameter in rowSet.Parameters)
            fill.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);
        await fill.ExecuteNonQueryAsync(token);

        // Missing stock slots are ensured first (the baseline helper inserts them at
        // zero before either leg runs, so the gate below always finds its rows).
        var affected = await ExecAsync(
            "INSERT INTO dbo.HALF_PRO_DEPOT(PRO_NO, PROCEDURE_TYPE_ID, DEPOT_ID, QTY, INIT_QTY) "
            + $"SELECT DISTINCT t.PRO_NO, t.PROCEDURE_TYPE_ID, t.DEPOT_ID, 0, 0 FROM {Tmp} t "
            + "WHERE NOT EXISTS (SELECT 1 FROM dbo.HALF_PRO_DEPOT d WHERE d.PRO_NO=t.PRO_NO "
            + "AND d.PROCEDURE_TYPE_ID=t.PROCEDURE_TYPE_ID AND d.DEPOT_ID=t.DEPOT_ID)", token);

        // Effective sign: approve-IN / deapprove-OUT add; approve-OUT / deapprove-IN
        // gate then subtract. The OUT leg always gates, whichever event drives it.
        var sign = _plan.Direction * (IsApprove ? 1 : -1);
        affected += sign == 1 ? await ApplyAddAsync(token) : await ApplySubtractAsync(token);

        await ExecAsync($"DROP TABLE {Tmp}", token);
        return affected;
    }

    private async Task<int> ApplyAddAsync(CancellationToken token) => await ExecAsync(
        "UPDATE d SET d.QTY = ISNULL(d.QTY,0) + ISNULL(s.QTY,0) "
        + $"FROM (SELECT PRO_NO, PROCEDURE_TYPE_ID, DEPOT_ID, SUM(QTY) QTY FROM {Tmp} "
        + "GROUP BY PRO_NO, PROCEDURE_TYPE_ID, DEPOT_ID) s "
        + "JOIN dbo.HALF_PRO_DEPOT d ON d.PRO_NO=s.PRO_NO "
        + "AND d.PROCEDURE_TYPE_ID=s.PROCEDURE_TYPE_ID AND d.DEPOT_ID=s.DEPOT_ID", token);

    private async Task<int> ApplySubtractAsync(CancellationToken token)
    {
        await CheckStockAsync(token);
        return await ExecAsync(
            "UPDATE d SET d.QTY = ISNULL(d.QTY,0) - ISNULL(s.QTY,0) "
            + $"FROM (SELECT PRO_NO, PROCEDURE_TYPE_ID, DEPOT_ID, SUM(QTY) QTY FROM {Tmp} "
            + "GROUP BY PRO_NO, PROCEDURE_TYPE_ID, DEPOT_ID) s "
            + "JOIN dbo.HALF_PRO_DEPOT d ON d.PRO_NO=s.PRO_NO "
            + "AND d.PROCEDURE_TYPE_ID=s.PROCEDURE_TYPE_ID AND d.DEPOT_ID=s.DEPOT_ID", token);
    }

    /// <summary>
    /// Sufficiency gate for the subtracting leg: every touched slot must cover the
    /// document quantity (rows sharing one slot are aggregated first, unlike the
    /// baseline cursor which checks line by line and can jointly overdraw).
    /// </summary>
    private async Task CheckStockAsync(CancellationToken token)
    {
        const string sql = """
            SELECT LTRIM(RTRIM(s.PRO_NO)), LTRIM(RTRIM(s.PROCEDURE_TYPE_ID)), LTRIM(RTRIM(s.DEPOT_ID)),
                   CAST(s.QTY - ISNULL(d.QTY,0) AS varchar(30))
            FROM (SELECT PRO_NO, PROCEDURE_TYPE_ID, DEPOT_ID, SUM(QTY) QTY FROM %TMP%
                  GROUP BY PRO_NO, PROCEDURE_TYPE_ID, DEPOT_ID) s
            JOIN dbo.HALF_PRO_DEPOT d ON d.PRO_NO=s.PRO_NO
             AND d.PROCEDURE_TYPE_ID=s.PROCEDURE_TYPE_ID AND d.DEPOT_ID=s.DEPOT_ID
            WHERE s.QTY > ISNULL(d.QTY,0) + 0.001
            """;
        var rows = await QueryListAsync(sql.Replace("%TMP%", Tmp), token);
        if (rows.Count > 0)
            throw new EffectValidationException(
                "库存数量不足\r品号----工序----库别----不足数量\r"
                + string.Join("", rows.Select(row => $"{row.Item1}    {row.Item2}    {row.Item3}    {row.Item4}\r")));
    }

    private async Task<int> ExecAsync(string sql, CancellationToken token)
    {
        await using var command = new SqlCommand(sql, _connection, _transaction);
        return await command.ExecuteNonQueryAsync(token);
    }

    private async Task<List<(string, string, string, string)>> QueryListAsync(string sql, CancellationToken token)
    {
        var result = new List<(string, string, string, string)>();
        await using var command = new SqlCommand(sql, _connection, _transaction);
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            result.Add((
                reader.IsDBNull(0) ? "" : reader.GetString(0),
                reader.IsDBNull(1) ? "" : reader.GetString(1),
                reader.IsDBNull(2) ? "" : reader.GetString(2),
                reader.IsDBNull(3) ? "" : reader.GetValue(3).ToString() ?? ""));
        }
        return result;
    }
}
