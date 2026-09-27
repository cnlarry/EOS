using System.Data;
using System.Globalization;
using System.Text;
using System.Text.Json;
using EOS.API.Data.Inventory;
using EOS.API.Models;
using Microsoft.Data.SqlClient;

namespace EOS.API.Data.Effects.ServiceEffectHandlers;

/// <summary>
/// Inventory move effect (service-level): the C# port of the legacy inventory update
/// procedure semantics, driven by closed fieldMap parameters. See
/// docs/plans/库存移动效果移植清单.md for the semantic checklist and the two intentional
/// deviations (row set built from whitelisted identifiers instead of dynamic SQL;
/// deapprove writes reverse log records instead of deleting them).
/// </summary>
public sealed class InventoryMoveHandler : IEffectServiceHandler
{
    public string EffectKey => "inventory-move";

    private readonly EffectPhysicalColumns _columns;
    private readonly DepotStockPolicyService _policies;
    private readonly WorkbenchAuditWriter _auditWriter;

    public InventoryMoveHandler(
        EffectPhysicalColumns columns, DepotStockPolicyService policies, WorkbenchAuditWriter auditWriter)
    {
        _columns = columns;
        _policies = policies;
        _auditWriter = auditWriter;
    }

    public async Task<int> ExecuteAsync(ServiceEffectContext context, CancellationToken token)
    {
        var plan = InventoryMovePlan.Parse(context.Action.Params ?? JsonSerializer.SerializeToElement(new { }));
        var columns = await _columns.LoadAsync(context.Connection, token, context.Transaction);
        var sql = plan.BuildRowSet(context.Plan, context.MasterKeyValues, columns);
        var executor = new InventoryMoveSql(
            context.Connection, context.Transaction, plan, context.ExecutionEvent, _policies,
            _auditWriter, context.Plan.ModuleId, context.RecordKey, context.Executor, context.Warnings);
        return await executor.RunAsync(sql, token);
    }
}

/// <summary>
/// One column of the generated row set: the document detail column of that name, or — when
/// <see cref="Constant"/> is set — a configured literal written into the slot instead (the
/// legacy procedures filled slots a document does not carry, such as the amount of a
/// document without amount columns, with inline literals).
/// </summary>
public sealed record InventoryRowColumn(string Column, string? Constant = null);

/// <summary>Parsed inventory-move parameters with closed shapes only.</summary>
public sealed record InventoryMovePlan(
    int Direction,
    string MasterDateField,
    string DepotField,
    IReadOnlyList<EffectTerm> QuantityTerms,
    IReadOnlyList<InventoryRowColumn> DetailColumns,
    IReadOnlyList<string> RowPositiveFields)
{
    /// <summary>
    /// 库位列由库别列派生：DEPOT_ID→LOCATION_NO、IN_DEPOT_ID→IN_LOCATION_NO、
    /// OUT_DEPOT_ID→OUT_LOCATION_NO、BAD_DEPOT_ID→BAD_LOCATION_NO。明细表的位置列与库别列
    /// 一一对应，因此不需要再增加一个配置项，也就不存在"配了库别却漏配库位"这种漏配。
    /// </summary>
    public string LocationField => DeriveLocationField(DepotField);

    public static string DeriveLocationField(string depotField) =>
        depotField.EndsWith("DEPOT_ID", StringComparison.OrdinalIgnoreCase)
            ? depotField[..^"DEPOT_ID".Length] + "LOCATION_NO"
            : "LOCATION_NO";

    public static InventoryMovePlan Parse(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            throw new EffectConfigException("inventory-move 参数必须是 JSON 对象。");
        var direction = root.TryGetProperty("direction", out var d) && d.ValueKind == JsonValueKind.String
            ? d.GetString()!.Trim().ToUpperInvariant()
            : throw new EffectConfigException("inventory-move 缺少 direction。");
        if (direction is not ("IN" or "OUT"))
            throw new EffectConfigException("inventory-move.direction 仅允许 IN/OUT。");

        if (!root.TryGetProperty("fieldMap", out var fieldMap) || fieldMap.ValueKind != JsonValueKind.Object)
            throw new EffectConfigException("inventory-move 缺少 fieldMap。");
        var masterDate = fieldMap.TryGetProperty("masterDate", out var md) && md.ValueKind == JsonValueKind.String
            ? md.GetString()!.Trim()
            : throw new EffectConfigException("inventory-move.fieldMap 缺少 masterDate。");

        var depotField = root.TryGetProperty("depotField", out var df) && df.ValueKind == JsonValueKind.String
            ? df.GetString()!.Trim()
            : "DEPOT_ID";

        var terms = new List<EffectTerm>();
        if (fieldMap.TryGetProperty("qty", out var qty))
        {
            if (qty.ValueKind == JsonValueKind.String)
                terms.Add(new EffectTerm(qty.GetString()!.Trim(), 1));
            else if (qty.ValueKind == JsonValueKind.Object && qty.TryGetProperty("terms", out var qterms)
                     && qterms.ValueKind == JsonValueKind.Array)
                foreach (var term in qterms.EnumerateArray())
                {
                    var field = term.TryGetProperty("field", out var f) && f.ValueKind == JsonValueKind.String
                        ? f.GetString()!.Trim()
                        : throw new EffectConfigException("inventory-move qty terms 存在非法 field。");
                    var coef = term.TryGetProperty("coef", out var c) && c.TryGetInt32(out var n) ? n : 1;
                    if (coef is not (1 or -1))
                        throw new EffectConfigException("inventory-move qty terms 的 coef 仅允许 1/-1。");
                    terms.Add(new EffectTerm(field, coef));
                }
            else
                throw new EffectConfigException("inventory-move.fieldMap.qty 必须是字段名或 terms 闭式结构。");
        }
        if (terms.Count == 0)
            throw new EffectConfigException("inventory-move.fieldMap 缺少可用的 qty。");

        var detailColumns = new List<InventoryRowColumn>();
        if (fieldMap.TryGetProperty("detail", out var detail) && detail.ValueKind == JsonValueKind.Array)
            foreach (var item in detail.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                {
                    var column = item.GetString()!.Trim();
                    if (column.Length > 0)
                        detailColumns.Add(new InventoryRowColumn(column));
                    continue;
                }
                if (item.ValueKind != JsonValueKind.Object)
                    throw new EffectConfigException(
                        "inventory-move.fieldMap.detail 项必须是列名或 {column, constant} 对象。");
                detailColumns.Add(ParseRowColumnConstant(item));
            }

        // Optional row gate: only rows with any of these detail fields positive take
        // part in the move (mirrors legacy per-call row filters such as "bad quantity
        // present"); absent means all document rows participate (existing behavior).
        var rowPositiveFields = new List<string>();
        if (root.TryGetProperty("rowFilter", out var rowFilter) && rowFilter.ValueKind == JsonValueKind.Object)
        {
            if (rowFilter.TryGetProperty("anyPositive", out var anyPositive) && anyPositive.ValueKind == JsonValueKind.Array)
                foreach (var item in anyPositive.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(item.GetString()))
                        throw new EffectConfigException("inventory-move.rowFilter.anyPositive 必须是非空字段名数组。");
                    rowPositiveFields.Add(item.GetString()!.Trim());
                }
            foreach (var property in rowFilter.EnumerateObject())
                if (!property.NameEquals("anyPositive"))
                    throw new EffectConfigException($"inventory-move.rowFilter 未知键 '{property.Name}'。");
            if (rowPositiveFields.Count == 0)
                throw new EffectConfigException("inventory-move.rowFilter.anyPositive 不可为空。");
        }

        return new InventoryMovePlan(
            direction == "IN" ? 1 : -1,
            masterDate,
            depotField,
            terms,
            detailColumns,
            rowPositiveFields);
    }

    /// <summary>
    /// Reads a constant row-set entry: {column, constant} writes the configured value into
    /// that row-set column instead of reading a document column. The legacy procedures used
    /// inline literals for slots a document does not carry (for example the amount slot of a
    /// document without amount columns); the closed form keeps the same semantics while the
    /// value travels as a bound parameter rather than as statement text.
    /// </summary>
    private static InventoryRowColumn ParseRowColumnConstant(JsonElement item)
    {
        string? column = null;
        var hasConstant = false;
        var constant = default(JsonElement);
        foreach (var property in item.EnumerateObject())
        {
            switch (property.Name)
            {
                case "column":
                    if (property.Value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(property.Value.GetString()))
                        throw new EffectConfigException("inventory-move.fieldMap.detail.column 必须是非空列名。");
                    column = property.Value.GetString()!.Trim();
                    break;
                case "constant":
                    constant = property.Value;
                    hasConstant = true;
                    break;
                default:
                    throw new EffectConfigException(
                        $"inventory-move.fieldMap.detail 含未登记键 '{property.Name}'（仅允许 column/constant）。");
            }
        }
        if (column is null)
            throw new EffectConfigException("inventory-move.fieldMap.detail 缺少 column。");
        if (!hasConstant)
            throw new EffectConfigException("inventory-move.fieldMap.detail 缺少 constant。");
        var text = constant.ValueKind switch
        {
            JsonValueKind.String => constant.GetString() ?? string.Empty,
            JsonValueKind.Number => constant.GetRawText(),
            _ => throw new EffectConfigException("inventory-move.fieldMap.detail.constant 必须是字符串或数字。"),
        };
        return new InventoryRowColumn(column, text);
    }

    /// <summary>Binds a configured constant: numbers stay numeric, any other text binds as text.</summary>
    internal static object ParseConstant(string text) =>
        decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var number)
            ? number
            : text;

    /// <summary>Generated row-set statement: target column list + aligned SELECT + parameters.</summary>
    public sealed record RowSetStatement(
        IReadOnlyList<string> Columns,
        string Sql,
        IReadOnlyList<EffectSqlParameter> Parameters);

    /// <summary>
    /// Builds the parameterized row-set SELECT feeding the move: module master joined to
    /// detail on same-named primary key columns (the convention across this ERP; a
    /// missing detail column is a hard configuration error). All identifiers come from
    /// configuration and are checked against the physical column whitelist.
    /// </summary>
    public RowSetStatement BuildRowSet(
        ModuleEffectPlan plan,
        IReadOnlyList<string> masterKeyValues,
        ISet<string> columns)
    {
        if (plan.MasterTable is null || plan.DetailTable is null)
            throw new EffectConfigException("inventory-move 需要主表+明细表模块形态。");
        if (plan.MasterPkOrder.Count == 0)
            throw new EffectConfigException("inventory-move 需要主表主键序。");
        var keys = Math.Min(plan.MasterPkOrder.Count, Math.Max(masterKeyValues.Count, 0));
        if (keys == 0)
            throw new EffectConfigException("inventory-move 缺少主表主键值，禁止无条件读取。");
        if (keys < 2)
            throw new EffectConfigException("inventory-move 需要单据双键（类型+单号）主表形态，禁止单键执行。");

        var check = new[] { MasterDateField, DepotField, LocationField }
            .Concat(DetailColumns.Where(column => column.Constant is null).Select(column => column.Column))
            .Concat(QuantityTerms.Select(t => t.Field))
            .Concat(RowPositiveFields)
            .Concat(plan.MasterPkOrder.Take(keys));
        foreach (var field in check)
        {
            if (!columns.Contains(plan.DetailTable + "." + field) && !columns.Contains(plan.MasterTable + "." + field))
                throw new EffectConfigException($"inventory-move 列不存在：{field}。");
        }

        var parameters = new List<EffectSqlParameter>();
        // Row columns mirror the legacy procedure temp table; the SELECT column order and
        // the INSERT column list are generated from the same sequence so they always align.
        var rowColumns = new List<(string Column, string Source)>
        {
            ("BILL_TYPE", $"M.{Q(plan.MasterPkOrder[0])}"),
            ("BILL_NO", $"M.{Q(plan.MasterPkOrder[1])}"),
            ("BILL_DATE", $"M.{Q(MasterDateField)}"),
            ("SERIAL_NO", "D.[SERIAL_NO]"),
            ("PRO_NO", "D.[PRO_NO]"),
            ("DEPOT_ID", $"D.{Q(DepotField)}"),
            ("LOCATION_NO", $"D.{Q(LocationField)}"),
        };
        var constantIndex = 0;
        foreach (var entry in DetailColumns)
        {
            if (entry.Column is "SERIAL_NO" or "PRO_NO")
                continue;
            if (rowColumns.Any(item => item.Column == entry.Column))
                continue;
            if (entry.Constant is { } constant)
            {
                var name = "@dc" + constantIndex++;
                parameters.Add(new EffectSqlParameter(name, ParseConstant(constant)));
                rowColumns.Add((entry.Column, name));
                continue;
            }
            rowColumns.Add((entry.Column, $"D.{Q(entry.Column)}"));
        }
        rowColumns.Add(("QTY", BuildQuantityExpression("D")));

        var select = new StringBuilder("SELECT ");
        for (var index = 0; index < rowColumns.Count; index++)
        {
            var (column, source) = rowColumns[index];
            select.Append(source).Append(" AS ").Append(Q(column));
            if (index + 1 < rowColumns.Count)
                select.Append(", ");
        }
        select.Append(" FROM dbo.").Append(Q(plan.MasterTable)).Append(" M JOIN dbo.")
            .Append(Q(plan.DetailTable)).Append(" D ON ");
        var join = plan.MasterPkOrder.Take(keys)
            .Select(pk => $"D.{Q(pk)} = M.{Q(pk)}");
        select.Append(string.Join(" AND ", join)).Append(" WHERE ");
        var where = new List<string>();
        for (var index = 0; index < keys; index++)
        {
            var name = "@mk" + index;
            parameters.Add(new EffectSqlParameter(name, masterKeyValues[index]));
            where.Add($"M.{Q(plan.MasterPkOrder[index])} = {name}");
        }
        if (RowPositiveFields.Count > 0)
            where.Add("(" + string.Join(" OR ", RowPositiveFields.Select(field => $"ISNULL(D.{Q(field)}, 0) > 0")) + ")");
        // 库位为空的行没有可移动的库位：原实现以 isnull(<库位列>,'')<>'' 跳过这类行
        // （借出单/返还单的"归还库位"列常年为空），照搬会往 INV_PRO_DEPOT 写 NULL 库位而整单失败。
        where.Add($"ISNULL(D.{Q(DepotField)}, '') <> ''");
        select.Append(string.Join(" AND ", where));
        return new RowSetStatement(
            rowColumns.Select(item => item.Column).ToList(),
            select.ToString(),
            parameters);
    }

    internal string BuildQuantityExpression(string alias) => string.Join(" ",
        QuantityTerms.Select((term, index) =>
        {
            var column = $"ISNULL({alias}.{Q(term.Field)}, 0)";
            if (index == 0)
                return term.Coef == -1 ? "-" + column : column;
            return (term.Coef == -1 ? "- " : "+ ") + column;
        }));

    internal static string Q(string identifier)
    {
        if (!WorkbenchSql.Identifier.IsMatch(identifier))
            throw new EffectConfigException($"标识符非法：'{identifier}'。");
        return "[" + identifier + "]";
    }
}

/// <summary>Executes the ported inventory move against the module connection and transaction.</summary>
public sealed class InventoryMoveSql
{
    private const string Tmp = "#INV_MOVE_TMP";
    private const string PolicyTmp = "#INV_MOVE_POLICY";

    /// <summary>
    /// 「该批次从未有过任何进出量」的判据：两个累计字段**各自为零**，不是"净额为 0"——
    /// 进过又出光的批次是实打实用过的，它的效期是既成事实，不该被后来的单据覆盖。
    /// 冲突拦截（排除这类行）与效期补写（只认这类行）必须同源：各写一份，只改一处就会静默分叉。
    /// </summary>
    private const string NoBatchActivityPredicate = "ISNULL(b.IN_SUM,0)=0 AND ISNULL(b.OUT_SUM,0)=0";

    private readonly SqlConnection _connection;
    private readonly SqlTransaction _transaction;
    private readonly InventoryMovePlan _plan;
    private readonly EffectEvent _event;
    private readonly DepotStockPolicyService _policies;
    private readonly WorkbenchAuditWriter _auditWriter;
    private readonly int _moduleId;
    private readonly string _recordKey;
    private readonly string _executor;
    private readonly IList<string>? _warnings;

    public InventoryMoveSql(
        SqlConnection connection,
        SqlTransaction transaction,
        InventoryMovePlan plan,
        EffectEvent executionEvent,
        DepotStockPolicyService policies,
        WorkbenchAuditWriter auditWriter,
        int moduleId,
        string? recordKey,
        string executor,
        // 非阻断告警（目前只有"过期批次档位=告警"用）：由管线回传前端，不留在此处。
        IList<string>? warnings = null)
    {
        _connection = connection;
        _transaction = transaction;
        _plan = plan;
        _event = executionEvent;
        _policies = policies;
        _auditWriter = auditWriter;
        _moduleId = moduleId;
        _recordKey = recordKey ?? string.Empty;
        _executor = executor;
        _warnings = warnings;
    }

    private bool IsApprove => _event is EffectEvent.ApproveEffect or EffectEvent.Save;

    /// <summary>
    /// Inventory-log direction for the movement being recorded: the document's stock
    /// impact sign (IN=+1, OUT=-1) times the event sign (approve=+1, deapprove=-1).
    /// Deapprove therefore writes the mirrored direction, never colliding with the
    /// original row (whose key it otherwise shares).
    /// </summary>
    internal static string FlowDirectionChar(int direction, bool approve) =>
        direction * (approve ? 1 : -1) == 1 ? "I" : "O";

    public async Task<int> RunAsync(InventoryMovePlan.RowSetStatement rowSet, CancellationToken token)
    {
        var approveTag = IsApprove ? 1 : -1;
        var direct = _plan.Direction;
        await ExecAsync($"IF OBJECT_ID('tempdb..{Tmp}') IS NOT NULL DROP TABLE {Tmp}", token);
        await CreateTempAsync(token);

        var insertSql = $"INSERT INTO {Tmp} (" + string.Join(",", rowSet.Columns.Select(InventoryMovePlan.Q)) + ") " + rowSet.Sql;
        var fill = new SqlCommand(insertSql, _connection, _transaction);
        foreach (var parameter in rowSet.Parameters)
            fill.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);
        await fill.ExecuteNonQueryAsync(token);

        // 关账拦截排在所有写入之前：期间已关账就整单拒绝，不写一半再回滚。
        await EnsurePeriodOpenAsync(token);

        // 必须在行集落进 Tmp 之后：库别清单是从 Tmp 里读的，先读只会读到空表，
        // 于是每个库别都拿不到策略行、一律按档 0 处理（表面正常，档位静默失效）。
        await LoadPolicyAsync(token);

        var affected = 0;
        if (IsApprove)
            affected += await PrecheckAsync(token);
        // 存放方式解析（D1c）：入库且单据没给位置时，按库别策略解析「放哪」并写回临时表。
        // 必须在**归一化之前**——归一化把「没填位置」抹成哨兵 N'-'，之后就没有这个信息了。
        if (IsApprove && direct == 1)
            await ResolveInboundLocationsAsync(token);
        affected += await NormalizeAsync(token);
        // 批次效期冲突（同批号两个效期）必须在**任何写入之前**判掉：归一化之后批号才算定稿，
        // 而 EnsureDepotRowsAsync 一落笔就已经动账，那时再拒绝就成了"写了一半再回滚"。
        if (IsApprove && direct == 1)
            await CheckBatchExpiryAsync(token);
        // 档 3（必填 + 效期）的另一半：该批次最终必须能确定一个有效期。同样是"写之前"的判据。
        await RequireExpiryAsync(token);
        // 位置校验必须在归一化之后：未填位置此时已被归一为哨兵 N'-'。
        await PrecheckLocationsAsync(token);
        affected += await NormalizePriceAsync(token);
        await EnsureDepotRowsAsync(token);
        await RequireBatchesAsync(token);
        await RequireLocationsAsync(token);
        // 加货的一侧才可能造成混放：批核入库 direct*approve=+1，解批出库同样为 +1（把货加回去）。
        if (direct * approveTag == 1)
            await CheckMixingAsync(token);

        if (IsApprove)
            affected += direct == 1 ? await ApplyInAsync(token) : await ApplyOutAsync(token);
        else
            affected += direct == 1 ? await UndoInAsync(token) : await UndoOutAsync(token);

        affected += await UpdateProductAsync(token);
        await UpdateMrpAsync(token);
        if (direct * approveTag == -1)
            affected += await CleanTrailingAsync(token);
        // 可用量维护点（ADR-020 §9.7 D7 的前置）：数量刚动过，把本单动过的格子按可用量口径重算落列。
        // 排在所有余额写入之后、临时表还在的时候（格子清单取自它）。
        await SyncAvailabilityAsync(token);
        await ExecAsync($"DROP TABLE {Tmp}", token);
        await ExecAsync($"DROP TABLE {PolicyTmp}", token);
        return affected;
    }

    /// <summary>
    /// 关账拦截：写流水之前先问"这一笔记账落在哪一期"。
    /// 判据与流水日期**同源**——用同一支 <see cref="LedgerDateExpression"/> 取值，批核是源单的单据日期、
    /// 非批核是当前时间，所以两个方向要判的日期本来就不同，不能各写一份。
    /// 解批另有第二条规矩：这张单**已有**的流水若落在已关账期，反向冲销一样拒绝
    /// （否则可以先解批、把单据日期改到开账期、再批核，等于把已关账期的账洗一遍）。
    /// **落点只在移动引擎写入口** ⇒ 非库存模块不受影响。
    /// </summary>
    /// <summary>
    /// 把本单动过的格子按**可用量口径**重算 `USEABLE_QTY` 并落列（可用量服务的唯一维护出口）。
    /// </summary>
    /// <remarks>
    /// 为什么必须在这里：出库校验读的是 `USEABLE_QTY` 这一列——**数量动过而列没跟上，拦的就是过期快照**
    /// （WS-21 台账里登记的 WS-23 前置就是这条）。格子清单取自临时表，此时归一化已经跑过，
    /// 四键已是规范列名（`PRO_NO` / `DEPOT_ID` / `LOCATION_NO` / `BATCH_NO`），
    /// 与冻结 / 预留 / 释放三处同步走的是同一个出口，这里不自己拼减法。
    /// </remarks>
    private async Task SyncAvailabilityAsync(CancellationToken token)
    {
        var slots = new List<InventoryAvailabilityService.SlotKey>();
        // reader 必须开在自己的作用域里：下一句要在这个连接上发写命令。
        await using (var command = new SqlCommand(
            $"SELECT DISTINCT t.PRO_NO, t.DEPOT_ID, ISNULL(t.LOCATION_NO, N'{InventoryQueryService.LocationSentinel}'), "
            + $"ISNULL(t.BATCH_NO, N'') FROM {Tmp} t;", _connection, _transaction))
        {
            await using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
            {
                slots.Add(new InventoryAvailabilityService.SlotKey(
                    reader.IsDBNull(0) ? string.Empty : reader.GetString(0),
                    reader.IsDBNull(1) ? string.Empty : reader.GetString(1),
                    reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                    reader.IsDBNull(3) ? string.Empty : reader.GetString(3)).Trimmed());
            }
        }
        if (slots.Count > 0)
        {
            await InventoryAvailabilityService.SyncSlotsAsync(_connection, _transaction, slots, token);
        }
    }

    private async Task EnsurePeriodOpenAsync(CancellationToken token)
    {
        var targets = new List<(string BillType, string BillNo, DateTime LedgerDate)>();
        await using (var command = new SqlCommand(
            $"SELECT t.BILL_TYPE, t.BILL_NO, MAX({LedgerDateExpression()}) AS LEDGER_DATE "
            + $"FROM {Tmp} t GROUP BY t.BILL_TYPE, t.BILL_NO;", _connection, _transaction))
        {
            await using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
            {
                if (reader.IsDBNull(2)) continue;
                targets.Add((
                    reader.IsDBNull(0) ? string.Empty : reader.GetString(0).Trim(),
                    reader.IsDBNull(1) ? string.Empty : reader.GetString(1).Trim(),
                    reader.GetDateTime(2)));
            }
        }

        foreach (var (billType, billNo, ledgerDate) in targets)
        {
            await InventoryPeriodService.EnsureLedgerWritableAsync(
                _connection, _transaction, ledgerDate, billType, billNo, token);
        }
    }

    /// <summary>
    /// 入库行的「放哪」（ADR-014 §3.12 / ADR-020 D1c）：单据没给位置时按库别策略解析，写回临时表。
    /// </summary>
    /// <remarks>
    /// 三道边界，都是刻意的：
    /// <list type="bullet">
    /// <item>只处理**批核方向的入库**。出库的位置是"从哪儿拿"，必须由单据指明——系统猜错就是把别人的货发了。</item>
    /// <item>只处理位置为**空 / 哨兵**的行：单据给了位置就照单据走，解析器不改人填的东西。</item>
    /// <item>解批的反向**不在这里重解析**：反向由流水镜像承担（见 <see cref="WriteLogAsync"/>）——
    /// 重解析可能挑到另一个位置（"已占用 / 上次放置 / 空位"都会变），反向行就对不上批核行，同一个库存键被劈成两个。</item>
    /// </list>
    /// </remarks>
    private async Task ResolveInboundLocationsAsync(CancellationToken token)
    {
        var candidates = new List<(string DepotId, string ProNo)>();
        await using (var command = new SqlCommand(
            $"SELECT DISTINCT LTRIM(RTRIM(t.DEPOT_ID)), LTRIM(RTRIM(t.PRO_NO)) FROM {Tmp} t "
            + "WHERE ISNULL(NULLIF(LTRIM(RTRIM(t.LOCATION_NO)), N''), N'-') = N'-' "
            + "AND LTRIM(RTRIM(t.DEPOT_ID)) <> N'' AND LTRIM(RTRIM(t.PRO_NO)) <> N'';",
            _connection, _transaction))
        {
            await using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
            {
                candidates.Add((reader.GetString(0), reader.GetString(1)));
            }
        }

        foreach (var (depotId, proNo) in candidates)
        {
            var policy = await _policies.ResolveAsync(depotId, _connection, _transaction, token);
            var resolution = await DepotLocationService.ResolveInboundAsync(
                _connection, _transaction, policy.LocationMode, policy.StorageMode, depotId, proNo,
                DepotLocationResolver.DefaultSentinel, token);
            if (resolution.IsSentinel)
            {
                continue;   // 解析不出就保持原样，随后归一化成哨兵（缺配置不是单据的失败）
            }

            await ExecAsync(
                $"UPDATE {Tmp} SET LOCATION_NO = @loc "
                + "WHERE LTRIM(RTRIM(DEPOT_ID)) = @d AND LTRIM(RTRIM(PRO_NO)) = @p "
                + "AND ISNULL(NULLIF(LTRIM(RTRIM(LOCATION_NO)), N''), N'-') = N'-';",
                token, ("@loc", resolution.LocationNo), ("@d", depotId), ("@p", proNo));
        }
    }

    private async Task CreateTempAsync(CancellationToken token)
    {
        // EFFECT_DATE 只有在明细表真有这一列、且动作参数把它登记进 fieldMap.detail 时才会被填上；
        // 其余入库单据（回流入库类）这一格恒为 NULL —— 正是"不受效期管控"的语义。
        await ExecAsync(
            $"CREATE TABLE {Tmp}(BILL_TYPE nchar(10),BILL_NO nchar(30),BILL_DATE datetime,SERIAL_NO int,PRO_NO nchar(30),"
            + "DEPOT_ID nchar(10),LOCATION_NO nvarchar(30),QTY float,BASE_QTY float,UNIT_ID nchar(10),PRICE float,BASE_PRICE float,"
            + "CURR_ID nchar(10),CURR_RATE float,AMOUNT float,BATCH_NO nchar(30),EFFECT_DATE datetime)", token);
    }

    /// <summary>
    /// 批次效期冲突拦截（同一批号只能有一个效期）：该批号已是非空效期、而本单给了**不同**的值 ⇒ 拒绝整单。
    /// </summary>
    /// <remarks>
    /// 为什么拒绝而不是"取最早/取最新"：批号存在的意义就是唯一标识一批货，同一批号两个效期本身就说明
    /// 批号编错了；默默改值会把先前的效期悄悄抹掉，而效期错了错的是**实物**（先到期先出、过期拦截都跟着错）。
    ///
    /// 两种情形都判：**跨单**（主档已有 vs 本单）与**单内**（本单同一批号填了两个不同值）。
    /// 单内那一种不判的话，两行之间谁覆盖谁就取决于执行顺序——等于静默取一个。
    ///
    /// **例外（"尚无实际效期"）**：主档行从未有过任何进出量（`IN_SUM` 与 `OUT_SUM` **都是 0**）时不算
    /// "已有有效期"——这种行还没参与过任何一次库存事实，把它当成"已有值"只会让手滑改错的人被迫绕道。
    /// 判据刻意写成两个字段各自为零，**不是"净额为 0"**：进过又出光（净额 0）的批次是实打实用过的，
    /// 它的效期是既成事实，不该被后来的单据覆盖。
    /// </remarks>
    private async Task CheckBatchExpiryAsync(CancellationToken token)
    {
        var conflicts = await QueryListAsync(
            // 跨单冲突：主档已有非空效期且与本单不同（"尚无实际效期"的行已在 WHERE 里排除）
            "SELECT t.BATCH_NO, "
            + "N'该批号已有有效期 ' + CONVERT(nvarchar(10), b.EFFECT_DATE, 120) + N'，本单填的是 ' "
            + "+ CONVERT(nvarchar(10), t.EFFECT_DATE, 120) "
            + $"FROM {Tmp} t JOIN dbo.INV_BATCH_M b ON b.PRO_NO=t.PRO_NO AND b.BATCH_NO=t.BATCH_NO "
            + "WHERE ISNULL(t.BATCH_NO,'') <> '' AND t.EFFECT_DATE IS NOT NULL AND b.EFFECT_DATE IS NOT NULL "
            + "AND b.EFFECT_DATE <> t.EFFECT_DATE "
            + $"AND NOT ({NoBatchActivityPredicate}) "
            + "UNION ALL "
            // 单内冲突：同一批号在本单里被填了两个不同的效期
            + "SELECT x.BATCH_NO, "
            + "N'本单同一批号填了两个有效期（' + CONVERT(nvarchar(10), x.FIRST_DATE, 120) + N' 与 ' "
            + "+ CONVERT(nvarchar(10), x.LAST_DATE, 120) + N'）' "
            + $"FROM (SELECT LTRIM(RTRIM(PRO_NO)) AS PRO_NO, LTRIM(RTRIM(BATCH_NO)) AS BATCH_NO, "
            + "MIN(EFFECT_DATE) AS FIRST_DATE, MAX(EFFECT_DATE) AS LAST_DATE "
            + $"FROM {Tmp} WHERE ISNULL(BATCH_NO,'') <> '' AND EFFECT_DATE IS NOT NULL "
            + "GROUP BY LTRIM(RTRIM(PRO_NO)), LTRIM(RTRIM(BATCH_NO)) "
            + "HAVING COUNT(DISTINCT CONVERT(date, EFFECT_DATE)) > 1) x", token);

        if (conflicts.Count > 0)
            throw new EffectValidationException(
                "批号有效日期不一致（同一批号只能有一个有效期，请改用新批号，或到「料件批号资料」修改）\n"
                + "批号---------------原因\n" + FormatPairs(conflicts));
    }

    /// <summary>
    /// 把本单涉及库别的策略读进临时表，供归一化、批号必填与混放校验三处判据使用。
    ///
    /// 求值一律走 <see cref="DepotStockPolicyService"/>（两跳且整行覆盖）：本类不自行拼默认值，
    /// 否则"库别无行时取什么"就会在同一次单据处理里出现第二个口径。判据必须落在**档位**上
    /// 而不是"该库别有没有策略行"——库别无行时回落到部署级默认，而默认档位未必是 0。
    /// </summary>
    private async Task LoadPolicyAsync(CancellationToken token)
    {
        await ExecAsync($"IF OBJECT_ID('tempdb..{PolicyTmp}') IS NOT NULL DROP TABLE {PolicyTmp}", token);
        await ExecAsync(
            $"CREATE TABLE {PolicyTmp}(DEPOT_ID nchar(10) NOT NULL PRIMARY KEY, "
            + "LOCATION_MODE int NOT NULL, BATCH_MODE int NOT NULL, EXPIRY_MODE int NOT NULL, "
            + "MIX_PRODUCT bit NOT NULL, MIX_BATCH bit NOT NULL)", token);

        var depots = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using (var command = new SqlCommand($"SELECT DISTINCT DEPOT_ID FROM {Tmp}", _connection, _transaction))
        await using (var reader = await command.ExecuteReaderAsync(token))
        {
            while (await reader.ReadAsync(token))
            {
                // 库别未填时按部署级默认求值（与策略服务对空作用域的口径一致）。
                var depot = reader.IsDBNull(0) ? string.Empty : reader.GetString(0).Trim();
                if (seen.Add(depot))
                    depots.Add(depot);
            }
        }

        if (depots.Count == 0)
            return;

        await using var insert = new SqlCommand(
            $"INSERT INTO {PolicyTmp}(DEPOT_ID, LOCATION_MODE, BATCH_MODE, EXPIRY_MODE, MIX_PRODUCT, MIX_BATCH) "
            + "VALUES (@depot, @locationMode, @mode, @expiryMode, @mixProduct, @mixBatch)",
            _connection, _transaction);
        insert.Parameters.Add("@depot", SqlDbType.NChar, 10);
        insert.Parameters.Add("@locationMode", SqlDbType.Int);
        insert.Parameters.Add("@mode", SqlDbType.Int);
        insert.Parameters.Add("@expiryMode", SqlDbType.Int);
        insert.Parameters.Add("@mixProduct", SqlDbType.Bit);
        insert.Parameters.Add("@mixBatch", SqlDbType.Bit);
        foreach (var depot in depots)
        {
            var policy = await _policies.ResolveAsync(depot, _connection, _transaction, token);
            insert.Parameters["@depot"].Value = depot;
            insert.Parameters["@locationMode"].Value = policy.LocationMode;
            insert.Parameters["@mode"].Value = policy.BatchMode;
            insert.Parameters["@expiryMode"].Value = policy.ExpiryMode;
            insert.Parameters["@mixProduct"].Value = policy.MixProduct;
            insert.Parameters["@mixBatch"].Value = policy.MixBatch;
            await insert.ExecuteNonQueryAsync(token);
        }
    }

    /// <summary>
    /// 运行期混放校验：库别策略禁止混品号 / 混批次时，往某个库位里加货之前先确认不会因此混放。
    /// 只在**入库侧**执行——批核入库与解批出库都是"往库位里加货"，出库不会造成混放。
    ///
    /// **哨兵库位（`N'-'`）不参与校验**：它是"未指定位置"的兜底行，存量本来就堆在一起；
    /// 若参与，任何未启用库位管理的库别都会在第二次入库时被判违规。
    ///
    /// 两个方向都要查：与**该库位已有库存**冲突，以及**本单自身**把多个品号 / 批次塞进同一库位。
    /// 只查前者时，一张明细里放两个品号就能绕过去。
    /// </summary>
    private async Task CheckMixingAsync(CancellationToken token)
    {
        var conflicts = await QueryListAsync(
            "SELECT DISTINCT LTRIM(RTRIM(s.DEPOT_ID)) + N' / ' + s.LOCATION_NO, "
            + "CASE WHEN pol.MIX_PRODUCT = 0 AND d.PRO_NO <> s.PRO_NO THEN N'禁止混品号：' ELSE N'禁止混批次：' END "
            + "+ N'该库位已有 ' + LTRIM(RTRIM(d.PRO_NO)) + N'（批次 ' + ISNULL(d.BATCH_NO, N'') + N'），本次入库 ' "
            + "+ LTRIM(RTRIM(s.PRO_NO)) + N'（批次 ' + ISNULL(s.BATCH_NO, N'') + N'）' "
            + $"FROM (SELECT DISTINCT DEPOT_ID, LOCATION_NO, PRO_NO, BATCH_NO FROM {Tmp}) s "
            + $"JOIN {PolicyTmp} pol ON pol.DEPOT_ID = s.DEPOT_ID "
            // 刻意不加脏读提示：这是**阻止写入**的校验，必须看见已提交的真实余额；
            // 读脏数据可能把并发事务尚未提交的库存当成"已经在那里"或"还不存在"。
            + "JOIN dbo.INV_PRO_DEPOT d ON d.DEPOT_ID = s.DEPOT_ID AND d.LOCATION_NO = s.LOCATION_NO "
            + "AND ISNULL(d.QTY,0) <> 0 "
            + "AND ((pol.MIX_PRODUCT = 0 AND d.PRO_NO <> s.PRO_NO) "
            + "  OR (pol.MIX_BATCH = 0 AND ISNULL(d.BATCH_NO, N'') <> ISNULL(s.BATCH_NO, N''))) "
            + "WHERE s.LOCATION_NO <> N'-' "
            + "UNION ALL "
            + "SELECT LTRIM(RTRIM(s.DEPOT_ID)) + N' / ' + s.LOCATION_NO, "
            + "N'禁止混品号：本单把 ' + CAST(COUNT(DISTINCT LTRIM(RTRIM(s.PRO_NO))) AS varchar(10)) + N' 个品号入同一库位' "
            + $"FROM (SELECT DISTINCT DEPOT_ID, LOCATION_NO, PRO_NO FROM {Tmp}) s "
            + $"JOIN {PolicyTmp} pol ON pol.DEPOT_ID = s.DEPOT_ID AND pol.MIX_PRODUCT = 0 "
            + "WHERE s.LOCATION_NO <> N'-' "
            + "GROUP BY s.DEPOT_ID, s.LOCATION_NO HAVING COUNT(DISTINCT LTRIM(RTRIM(s.PRO_NO))) > 1 "
            + "UNION ALL "
            + "SELECT LTRIM(RTRIM(s.DEPOT_ID)) + N' / ' + s.LOCATION_NO, "
            + "N'禁止混批次：本单把 ' + CAST(COUNT(DISTINCT ISNULL(s.BATCH_NO, N'')) AS varchar(10)) + N' 个批次入同一库位' "
            + $"FROM (SELECT DISTINCT DEPOT_ID, LOCATION_NO, BATCH_NO FROM {Tmp}) s "
            + $"JOIN {PolicyTmp} pol ON pol.DEPOT_ID = s.DEPOT_ID AND pol.MIX_BATCH = 0 "
            + "WHERE s.LOCATION_NO <> N'-' "
            + "GROUP BY s.DEPOT_ID, s.LOCATION_NO HAVING COUNT(DISTINCT ISNULL(s.BATCH_NO, N'')) > 1", token);

        if (conflicts.Count > 0)
            throw new EffectValidationException("以下库位不允许混放\n库别/库位---------------原因\n" + FormatPairs(conflicts));
    }

    private async Task<int> PrecheckAsync(CancellationToken token)
    {
        var missingProducts = await QueryListAsync(
            $"SELECT DISTINCT t.SERIAL_NO, t.PRO_NO FROM {Tmp} t WHERE NOT EXISTS (SELECT 1 FROM dbo.PRODUCT p WITH (NOLOCK) WHERE p.PRO_NO=t.PRO_NO)", token);
        if (missingProducts.Count > 0)
            throw new EffectValidationException("以下料号不存在\n序号----料  号\n" + FormatPairs(missingProducts));
        var missingDepots = await QueryListAsync(
            $"SELECT DISTINCT t.SERIAL_NO, t.DEPOT_ID FROM {Tmp} t WHERE ISNULL(t.DEPOT_ID,'') NOT IN (SELECT DEPOT_ID FROM dbo.DEPOT WITH (NOLOCK))", token);
        if (missingDepots.Count > 0)
            throw new EffectValidationException("以下库别不存在\n序号----库  别\n" + FormatPairs(missingDepots));
        return 0;
    }

    /// <summary>
    /// 位置必须在库位主档中存在（余额表有指向它的外键）。必须先由
    /// <see cref="NormalizeAsync"/> 把未填位置归一到哨兵，否则明细里那些 NULL 位置
    /// 会全部命中"主档里找不到"，把正常单据整单拦下。
    /// </summary>
    private async Task PrecheckLocationsAsync(CancellationToken token)
    {
        var missing = await QueryListAsync(
            $"SELECT DISTINCT t.SERIAL_NO, t.LOCATION_NO FROM {Tmp} t "
            + "WHERE NOT EXISTS (SELECT 1 FROM dbo.DEPOT_LOCATION l WITH (NOLOCK) "
            + "WHERE l.DEPOT_ID=t.DEPOT_ID AND l.LOCATION_NO=t.LOCATION_NO)", token);
        if (missing.Count > 0)
            throw new EffectValidationException("以下库位不存在\n序号----库  位\n" + FormatPairs(missing));
    }

    private async Task<int> NormalizeAsync(CancellationToken token) => await ExecAsync(
        // 批号是否归零取决于库别策略档位：档 0（不管）抹掉非批管料件的批号（历史行为），
        // 档 1（记录）保留、档 2（必填）保留后由 RequireBatchesAsync 兜。产品级 MANAGE_BATCH=1
        // 时无论档位如何都保留——策略只能更严，不能把产品级要求抹掉。
        $"UPDATE t SET t.BATCH_NO = CASE WHEN ISNULL(p.MANAGE_BATCH,0)=0 AND ISNULL(pol.BATCH_MODE,0)=0 "
        + "THEN '' ELSE ISNULL(LTRIM(RTRIM(t.BATCH_NO)), '') END, "
        // 未填位置一律归一到哨兵 N'-'：余额表的位置列是主键列，主键不接受 NULL，
        // 且唯一性比较把 NULL 视为相等，多行 NULL 会直接判为重复键冲突。
        + "t.LOCATION_NO = ISNULL(NULLIF(LTRIM(RTRIM(t.LOCATION_NO)), ''), N'-'), "
        + "t.BASE_QTY = t.QTY * CASE t.UNIT_ID WHEN p.UNIT_ID THEN 1 WHEN p.UNIT_ID_1 THEN p.UNIT_RATE_1 "
        + "WHEN p.UNIT_ID_2 THEN p.UNIT_RATE_2 WHEN p.UNIT_ID_3 THEN p.UNIT_RATE_3 WHEN p.UNIT_ID_4 THEN p.UNIT_RATE_4 ELSE 1 END, "
        + "t.CURR_RATE = CASE ISNULL(t.CURR_RATE,0) WHEN 0 THEN 1 ELSE t.CURR_RATE END "
        + $"FROM {Tmp} t JOIN dbo.PRODUCT p ON t.PRO_NO=p.PRO_NO "
        + $"LEFT JOIN {PolicyTmp} pol ON pol.DEPOT_ID=t.DEPOT_ID", token);

    private async Task<int> NormalizePriceAsync(CancellationToken token) => await ExecAsync(
        "UPDATE t SET t.BASE_PRICE = ROUND(CASE ISNULL(t.PRICE,0) WHEN 0 THEN d.COST_PRICE "
        + "ELSE CASE ISNULL(t.BASE_QTY,0) WHEN 0 THEN d.COST_PRICE ELSE (t.AMOUNT*t.CURR_RATE)/t.BASE_QTY END END, 8), "
        + "t.CURR_RATE = CASE ISNULL(t.PRICE,0) WHEN 0 THEN 1 ELSE t.CURR_RATE END "
        + $"FROM {Tmp} t JOIN dbo.INV_PRO_DEPOT d ON t.PRO_NO=d.PRO_NO AND t.DEPOT_ID=d.DEPOT_ID "
        + "AND t.LOCATION_NO=d.LOCATION_NO AND t.BATCH_NO=d.BATCH_NO", token);

    private async Task EnsureDepotRowsAsync(CancellationToken token) => await ExecAsync(
        "INSERT INTO dbo.INV_PRO_DEPOT(PRO_NO, DEPOT_ID, LOCATION_NO, BATCH_NO, QTY, INIT_QTY, COST_PRICE, COST_AMOUNT) "
        + $"SELECT DISTINCT t.PRO_NO, t.DEPOT_ID, t.LOCATION_NO, t.BATCH_NO, 0, 0, 0, 0 FROM {Tmp} t "
        + "WHERE NOT EXISTS (SELECT 1 FROM dbo.INV_PRO_DEPOT d WHERE d.PRO_NO=t.PRO_NO AND d.DEPOT_ID=t.DEPOT_ID "
        + "AND d.LOCATION_NO=t.LOCATION_NO AND d.BATCH_NO=t.BATCH_NO)", token);

    /// <summary>
    /// 批号必填：产品级 <c>MANAGE_BATCH</c> 与库别策略档 2（该库别所有料件必填）取或——
    /// 「取严者胜」，策略不能比产品级更松，产品级也不因策略档 0 而失效。
    /// </summary>
    private async Task RequireBatchesAsync(CancellationToken token)
    {
        var missing = await QueryListAsync(
            $"SELECT DISTINCT t.PRO_NO, '' FROM {Tmp} t JOIN dbo.PRODUCT p ON t.PRO_NO=p.PRO_NO "
            + $"LEFT JOIN {PolicyTmp} pol ON pol.DEPOT_ID=t.DEPOT_ID "
            + "WHERE ISNULL(t.BATCH_NO,'')='' AND (ISNULL(p.MANAGE_BATCH,0)=1 OR ISNULL(pol.BATCH_MODE,0)>=2)", token);
        if (missing.Count > 0)
            throw new EffectValidationException("以下品号需要输入批号信息\n" + FormatPairs(missing));
    }

    /// <summary>
    /// 位置必填：库别策略档 3（强制）时，该库别的过账必须指明库位。判据落在**档位**上而不是
    /// "该库别有没有策略行"——库别无行时回落到部署级默认，而默认档位未必是 0（与批号必填同源）。
    ///
    /// 哨兵行在档 3 下同样不合格：它是"未指定位置"的兜底，不是位置。放在归一化**之后**判定，
    /// 是为了让"压根没填"与"填了哨兵"走同一条判据——否则前者会被归一化悄悄救回来。
    /// </summary>
    private async Task RequireLocationsAsync(CancellationToken token)
    {
        var missing = await QueryListAsync(
            $"SELECT DISTINCT t.SERIAL_NO, LTRIM(RTRIM(t.DEPOT_ID)) FROM {Tmp} t "
            + $"JOIN {PolicyTmp} pol ON pol.DEPOT_ID = t.DEPOT_ID "
            + $"WHERE t.LOCATION_NO = N'-' AND ISNULL(pol.LOCATION_MODE, 0) >= 3", token);
        if (missing.Count > 0)
            throw new EffectValidationException(
                "以下序号项所在库别必须指定库位（位置档位 3 强制）\n序号----库  别\n" + FormatPairs(missing));
    }

    /// <summary>
    /// 库别级加权平均：成本口径固定在 (料号, 库别)，与该库别下有几个库位 / 批次行无关。
    /// 数量按行级字段 QTY 求和；金额取 MAX —— COST_AMOUNT 是库别级字段、每行冗余存储同一个
    /// 值，对它求和会按行数成倍虚增。算出新的库别金额与加权单价后同步写回该库别的**所有**行。
    /// 必须在按行改数量**之前**执行：这里用的是"更新前的合计 + 增量"，顺序颠倒会重复计入。
    /// </summary>
    private Task<int> SyncDepotLevelCostAsync(int sign, CancellationToken token) => ExecAsync(
        "UPDATE d SET d.COST_AMOUNT = a.NEW_AMOUNT, "
        + "d.COST_PRICE = ROUND(CASE WHEN a.NEW_QTY=0 THEN d.COST_PRICE ELSE a.NEW_AMOUNT/a.NEW_QTY END, 8) "
        + "FROM dbo.INV_PRO_DEPOT d JOIN ("
        + "SELECT b.PRO_NO, b.DEPOT_ID, "
        + "SUM(ISNULL(b.QTY,0)) + @sign * ISNULL(x.BASE_QTY,0) AS NEW_QTY, "
        + "MAX(ISNULL(b.COST_AMOUNT,0)) + @sign * ISNULL(x.AMOUNT,0) AS NEW_AMOUNT "
        + $"FROM dbo.INV_PRO_DEPOT b JOIN (SELECT PRO_NO, DEPOT_ID, SUM(BASE_QTY) BASE_QTY, SUM(AMOUNT*CURR_RATE) AMOUNT FROM {Tmp} "
        + "GROUP BY PRO_NO, DEPOT_ID) x ON b.PRO_NO=x.PRO_NO AND b.DEPOT_ID=x.DEPOT_ID "
        + "GROUP BY b.PRO_NO, b.DEPOT_ID, x.BASE_QTY, x.AMOUNT) a "
        + "ON a.PRO_NO=d.PRO_NO AND a.DEPOT_ID=d.DEPOT_ID", token, ("@sign", sign));

    /// <summary>按四键把移动量落到具体库位 / 批次行上。</summary>
    private Task<int> ApplyRowQuantitiesAsync(int sign, CancellationToken token) => ExecAsync(
        "UPDATE d SET d.QTY = ISNULL(d.QTY,0) + @sign * ISNULL(s.BASE_QTY,0) "
        + $"FROM (SELECT PRO_NO, DEPOT_ID, LOCATION_NO, BATCH_NO, SUM(BASE_QTY) BASE_QTY FROM {Tmp} "
        + "GROUP BY PRO_NO, DEPOT_ID, LOCATION_NO, BATCH_NO) s "
        + "JOIN dbo.INV_PRO_DEPOT d ON d.PRO_NO=s.PRO_NO AND d.DEPOT_ID=s.DEPOT_ID "
        + "AND d.LOCATION_NO=s.LOCATION_NO AND d.BATCH_NO=s.BATCH_NO", token, ("@sign", sign));

    private async Task<int> ApplyInAsync(CancellationToken token)
    {
        var affected = await SyncDepotLevelCostAsync(1, token);
        affected += await ApplyRowQuantitiesAsync(1, token);
        affected += await ApplyBatchesAsync(effect: 'I', inSummary: true, token);
        return affected;
    }

    private async Task<int> ApplyOutAsync(CancellationToken token)
    {
        await CheckExpiredBatchesAsync(token);
        await CheckStockAsync(token);
        var affected = await SyncDepotLevelCostAsync(-1, token);
        affected += await ApplyRowQuantitiesAsync(-1, token);
        affected += await ApplyBatchesAsync(effect: 'O', inSummary: false, token);
        return affected;
    }

    private async Task<int> UndoInAsync(CancellationToken token)
    {
        // 解批一张入库单同样是"把货拿走"，过期拦截一视同仁（否则解批就成了绕过过期管控的后门）。
        await CheckExpiredBatchesAsync(token);
        await CheckStockAsync(token);
        var affected = await SyncDepotLevelCostAsync(-1, token);
        affected += await ApplyRowQuantitiesAsync(-1, token);
        affected += await ExecAsync(
            "UPDATE b SET b.IN_SUM = ISNULL(b.IN_SUM,0) - ISNULL(t.BASE_QTY,0) "
            + $"FROM {Tmp} t JOIN dbo.INV_BATCH_M b ON b.PRO_NO=t.PRO_NO AND b.BATCH_NO=t.BATCH_NO", token);
        affected += await WriteReverseBatchesAsync(token);
        return affected;
    }

    private async Task<int> UndoOutAsync(CancellationToken token)
    {
        var affected = await SyncDepotLevelCostAsync(1, token);
        affected += await ApplyRowQuantitiesAsync(1, token);
        affected += await ExecAsync(
            "INSERT INTO dbo.INV_BATCH_M(BATCH_NO, PRO_NO, IN_SUM) "
            + $"SELECT t.BATCH_NO, t.PRO_NO, 0 FROM {Tmp} t WHERE ISNULL(t.BATCH_NO,'') != '' "
            + "AND NOT EXISTS (SELECT 1 FROM dbo.INV_BATCH_M b WHERE b.BATCH_NO=t.BATCH_NO AND b.PRO_NO=t.PRO_NO)", token);
        affected += await ExecAsync(
            "UPDATE b SET b.OUT_SUM = ISNULL(b.OUT_SUM,0) + ISNULL(t.BASE_QTY,0) "
            + $"FROM {Tmp} t JOIN dbo.INV_BATCH_M b ON b.BATCH_NO=t.BATCH_NO AND b.PRO_NO=t.PRO_NO", token);
        affected += await WriteReverseBatchesAsync(token);
        return affected;
    }

    /// <summary>
    /// 过期批次拦截：出库行所涉及的批次**按受益库别的档位**判——档 2 拒绝、档 1 放行但告警、档 0 不查。
    /// </summary>
    /// <remarks>
    /// 参照日期取的是**本笔账的记账日期**（与流水同源的 <see cref="LedgerDateExpression"/>）：
    /// "过期"是相对某一时点的事实，用批核日与用当前时间在跨天补账时会给出不同答案，
    /// 而账上真正记的是记账日期——两处各取一套口径，就会出"账里写着 3 号出的货用了 5 号才过期的批次"。
    ///
    /// **空效期不参与判定**：`EFFECT_DATE IS NULL` 是"该批次不受效期管控"，不是"过期"。
    /// </remarks>
    private async Task CheckExpiredBatchesAsync(CancellationToken token)
    {
        var expired = new List<(int Mode, string BatchNo, string Context, string EffectDate)>();
        await using (var command = new SqlCommand(
            "SELECT DISTINCT pol.EXPIRY_MODE, LTRIM(RTRIM(t.BATCH_NO)), "
            + "LTRIM(RTRIM(t.DEPOT_ID)) + N' / ' + LTRIM(RTRIM(t.PRO_NO)), CONVERT(nvarchar(10), b.EFFECT_DATE, 120) "
            + $"FROM {Tmp} t "
            + "JOIN dbo.INV_BATCH_M b ON b.PRO_NO=t.PRO_NO AND b.BATCH_NO=t.BATCH_NO "
            + $"JOIN {PolicyTmp} pol ON pol.DEPOT_ID = t.DEPOT_ID "
            + "WHERE pol.EXPIRY_MODE >= 1 AND ISNULL(t.BATCH_NO,'') <> '' AND b.EFFECT_DATE IS NOT NULL "
            + $"AND b.EFFECT_DATE < {LedgerDateExpression()};", _connection, _transaction))
        await using (var reader = await command.ExecuteReaderAsync(token))
        {
            while (await reader.ReadAsync(token))
            {
                expired.Add((reader.GetInt32(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)));
            }
        }

        var rejected = expired.Where(row => row.Mode >= 2).ToList();
        if (rejected.Count > 0)
            throw new EffectValidationException(
                "以下批次已过期，本库别不允许出库（过期批次仍在账上，可用库存调整单另行处理）\n"
                + "批号---------------库别 / 料号---------------有效期\n"
                + string.Join("\n", rejected.Select(row => $"{row.BatchNo}    {row.Context}    {row.EffectDate}")));

        foreach (var row in expired.Where(row => row.Mode == 1))
        {
            _warnings?.Add($"批次 {row.BatchNo}（{row.Context}）有效期 {row.EffectDate} 已过：本库别设为「只告警」，本次出库已放行。");
        }
    }

    /// <summary>
    /// 批次档位 3（必填 + 效期）的另一半：该批次**最终必须能确定一个有效期**。
    /// </summary>
    /// <remarks>
    /// 判据按 **(料号, 批号)** 而不是按单据行：效期是批次的属性，同一批次在单内出现多行只该有一个答案。
    /// 满足任一即通过——① 本单这一行填了有效期；② 批次主档已有非空有效期。
    ///
    /// 为什么不是"单据行必须填"：档 3 是**库别**属性，而这个库里只有"首次入库"那几类单据带效期列
    /// （退货、调拨、报废、领料、借出返还等一律不带）。按"行必须填"实现，客户一旦把档位开到 3，
    /// 那些单据就会**整类拒单**——档位成了打不开的开关。而"批次已有有效期"本就是事实的复述，不是放宽。
    /// </remarks>
    private async Task RequireExpiryAsync(CancellationToken token)
    {
        var missing = await QueryListAsync(
            "SELECT x.PRO_NO, x.BATCH_NO FROM ("
            + "  SELECT LTRIM(RTRIM(t.PRO_NO)) AS PRO_NO, LTRIM(RTRIM(t.BATCH_NO)) AS BATCH_NO, "
            + "         MAX(CASE WHEN t.EFFECT_DATE IS NOT NULL THEN 1 ELSE 0 END) AS HAS_DOC_DATE "
            + $"  FROM {Tmp} t JOIN {PolicyTmp} pol ON pol.DEPOT_ID = t.DEPOT_ID "
            + "  WHERE ISNULL(pol.BATCH_MODE,0) >= 3 AND ISNULL(t.BATCH_NO,'') <> '' "
            + "  GROUP BY LTRIM(RTRIM(t.PRO_NO)), LTRIM(RTRIM(t.BATCH_NO))) x "
            + "WHERE x.HAS_DOC_DATE = 0 AND NOT EXISTS (SELECT 1 FROM dbo.INV_BATCH_M b "
            + "  WHERE b.PRO_NO = x.PRO_NO AND b.BATCH_NO = x.BATCH_NO AND b.EFFECT_DATE IS NOT NULL)", token);
        if (missing.Count > 0)
            throw new EffectValidationException(
                "以下批号在批次档位 3（必填 + 效期）下没有有效期：本单未填，批次账上也没有\n"
                + "料  号---------------批  号\n" + FormatPairs(missing)
                + "\n请在本单填写有效期；若该批次早已入库，可先到「料件批号资料」补齐它的有效期。");
    }

    /// <summary>
    /// 批次账：先补主档行（**首次见到该 (料号, 批号) 时把有效期一并写进去**），
    /// 再处理"行已在、但从未有过进出量"的效期补写，然后累加收发累计，最后写批次流水明细。
    /// </summary>
    private async Task<int> ApplyBatchesAsync(char effect, bool inSummary, CancellationToken token)
    {
        var affected = await ExecAsync(
            "INSERT INTO dbo.INV_BATCH_M(BATCH_NO, PRO_NO, IN_SUM, EFFECT_DATE) "
            + $"SELECT t.BATCH_NO, t.PRO_NO, 0, t.EFFECT_DATE FROM {Tmp} t WHERE ISNULL(t.BATCH_NO,'') != '' "
            + "AND NOT EXISTS (SELECT 1 FROM dbo.INV_BATCH_M b WHERE b.BATCH_NO=t.BATCH_NO AND b.PRO_NO=t.PRO_NO)", token);
        // 效期补写必须排在累加之前：IN_SUM 一旦动过，"从未有过进出量"这个前提就不再成立。
        // 只走入库腿——出库单据没有效期可写（行集里那一格恒为 NULL），跑一遍只是空转。
        if (inSummary)
            affected += await FillPendingBatchExpiryAsync(token);
        affected += inSummary
            ? await ExecAsync(
                "UPDATE b SET b.IN_SUM = ISNULL(b.IN_SUM,0) + ISNULL(t.BASE_QTY,0), b.LATELY_IN_DATE = t.BILL_DATE "
                + $"FROM {Tmp} t JOIN dbo.INV_BATCH_M b ON b.BATCH_NO=t.BATCH_NO AND b.PRO_NO=t.PRO_NO", token)
            : await ExecAsync(
                "UPDATE b SET b.OUT_SUM = ISNULL(b.OUT_SUM,0) + ISNULL(t.BASE_QTY,0) "
                + $"FROM {Tmp} t JOIN dbo.INV_BATCH_M b ON b.BATCH_NO=t.BATCH_NO AND b.PRO_NO=t.PRO_NO", token);
        affected += await ExecAsync(
            "INSERT INTO dbo.INV_BATCH_D(BATCH_NO, PRO_NO, BATCH_DATE, BATCH_ORDER_TYPE, BATCH_ORDER_NO, BATCH_SERIAL_NO, DEPOT_ID, EFFECT_DEPOT, QTY, MUTUALITY_QTY, MUTUALITY_UNIT_ID, MUTUALITY_PRICE, MUTUALITY_CURR_ID, MUTUALITY_CURR_RATE, MUTUALITY_AMOUNT) "
            + $"SELECT t.BATCH_NO, t.PRO_NO, t.BILL_DATE, t.BILL_TYPE, t.BILL_NO, t.SERIAL_NO, t.DEPOT_ID, '{effect}', t.BASE_QTY, t.QTY, t.UNIT_ID, t.PRICE, t.CURR_ID, t.CURR_RATE, t.AMOUNT "
            + $"FROM {Tmp} t WHERE ISNULL(t.BATCH_NO,'') != ''", token);
        return affected;
    }

    /// <summary>
    /// 「尚无实际效期」的写入：主档行已在、但 `IN_SUM` 与 `OUT_SUM` **都是 0**（从未有过任何进出量）时，
    /// 把本单的有效期写进去（含**改写**此前写下的值）。
    /// </summary>
    /// <remarks>
    /// 为什么要这一条：批核 → 解批（主档行保留、累计归零）→ 改正效期 → 再批核，是改正填错值的常见路径。
    /// 没有它，第二次批核就会撞上"同批号两个效期"而被拒，把人逼去另一个模块改一行数据。
    ///
    /// 判据刻意写成**两个累计字段各自为零**，不是"净额为 0"：进过又出光的批次是实打实用过的，
    /// 它的效期是既成事实，不该被后来的单据覆盖。
    ///
    /// 覆盖动作**显式独立成 UPDATE**，不塞进上面那条 `INSERT ... WHERE NOT EXISTS`——那条语句的语义是
    /// "建新行"，把改写混进去之后"新建"与"改旧"就再也分不开了（也就无从审计）。改写逐行写审计：
    /// 改的是批次账的属性，不写审计等于悄悄改口径。
    ///
    /// 与"首次建行"那条 INSERT 的分工：值相同时这里不动作（避免重复写与重复审计）。
    /// </remarks>
    private async Task<int> FillPendingBatchExpiryAsync(CancellationToken token)
    {
        const string pendingFilter =
            $"{NoBatchActivityPredicate} "
            + "AND (b.EFFECT_DATE IS NULL OR b.EFFECT_DATE <> t.EFFECT_DATE)";

        var pending = new List<(string ProductNo, string BatchNo, string OldValue, string NewValue)>();
        await using (var command = new SqlCommand(
            "SELECT t.PRO_NO, t.BATCH_NO, ISNULL(CONVERT(nvarchar(10), b.EFFECT_DATE, 120), N'(空)'), "
            + "CONVERT(nvarchar(10), t.EFFECT_DATE, 120) "
            + $"FROM (SELECT LTRIM(RTRIM(PRO_NO)) AS PRO_NO, LTRIM(RTRIM(BATCH_NO)) AS BATCH_NO, "
            + $"MAX(EFFECT_DATE) AS EFFECT_DATE FROM {Tmp} WHERE ISNULL(BATCH_NO,'') <> '' AND EFFECT_DATE IS NOT NULL "
            + "GROUP BY LTRIM(RTRIM(PRO_NO)), LTRIM(RTRIM(BATCH_NO))) t "
            + "JOIN dbo.INV_BATCH_M b ON b.PRO_NO=t.PRO_NO AND b.BATCH_NO=t.BATCH_NO "
            + $"WHERE {pendingFilter};", _connection, _transaction))
        await using (var reader = await command.ExecuteReaderAsync(token))
        {
            while (await reader.ReadAsync(token))
            {
                pending.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)));
            }
        }
        if (pending.Count == 0)
            return 0;

        var affected = await ExecAsync(
            "UPDATE b SET b.EFFECT_DATE = t.EFFECT_DATE, b.LAST_UPDATE_BY = @actor, b.LAST_UPDATE_DATE = SYSDATETIME() "
            + $"FROM (SELECT PRO_NO, BATCH_NO, MAX(EFFECT_DATE) AS EFFECT_DATE FROM {Tmp} "
            + "WHERE ISNULL(BATCH_NO,'') <> '' AND EFFECT_DATE IS NOT NULL GROUP BY PRO_NO, BATCH_NO) t "
            + "JOIN dbo.INV_BATCH_M b ON b.PRO_NO=t.PRO_NO AND b.BATCH_NO=t.BATCH_NO "
            + $"WHERE {pendingFilter}",
            token, ("@actor", _executor));

        foreach (var (productNo, batchNo, oldValue, newValue) in pending)
        {
            await _auditWriter.WriteEventAsync(
                _connection, _transaction, _moduleId, _recordKey,
                "BATCH_EXPIRY",
                $"批号 {batchNo}（料号 {productNo}）有效日期 {oldValue}→{newValue}：该批号此前没有任何进出量，效期由本单确定。",
                _executor, "INV_BATCH_M", result: 1,
                [new AuditFieldChange("EFFECT_DATE", oldValue, newValue, null)], token);
        }
        return affected;
    }

    /// <summary>Reverse semantics: compensating batch detail rows instead of deleting history.</summary>
    private async Task<int> WriteReverseBatchesAsync(CancellationToken token) => await ExecAsync(
        "INSERT INTO dbo.INV_BATCH_D(BATCH_NO, PRO_NO, BATCH_DATE, BATCH_ORDER_TYPE, BATCH_ORDER_NO, BATCH_SERIAL_NO, DEPOT_ID, EFFECT_DEPOT, QTY, MUTUALITY_QTY, MUTUALITY_UNIT_ID, MUTUALITY_PRICE, MUTUALITY_CURR_ID, MUTUALITY_CURR_RATE, MUTUALITY_AMOUNT) "
        + $"SELECT t.BATCH_NO, t.PRO_NO, {LedgerDateExpression()}, t.BILL_TYPE, t.BILL_NO, t.SERIAL_NO, t.DEPOT_ID, '{(_plan.Direction == 1 ? 'O' : 'I')}', -t.BASE_QTY, -t.QTY, t.UNIT_ID, t.PRICE, t.CURR_ID, t.CURR_RATE, -t.AMOUNT "
        + $"FROM {Tmp} t WHERE ISNULL(t.BATCH_NO,'') != ''", token);

    /// <summary>
    /// 流水日期：批核写单据日期；解批写的是"解批当时发生的反向流水"，取当前时间——既如实反映
    /// 发生时刻，也避开 INV_DEPOT_LOG 主键 (单据, 日期, 方向, 行号, 库位) 与批核原始行的重复：
    /// 报废/借出这类"两条腿同库位"的单据，批核会同时写 O、I 两行，解批镜像时沿用单据日期必然撞键。
    /// </summary>
    private string LedgerDateExpression() => IsApprove ? "t.BILL_DATE" : "SYSDATETIME()";

    /// <summary>
    /// Inventory log rows; deapprove writes mirrored rows with flipped direction and negative quantities.
    /// 位置路径是**快照**：位置可以被移动、托盘号可以被复用，因此"当时货在哪"只能记在流水上，
    /// 不能事后按当前位置回溯。解批优先沿用批核当时那一行的快照，取不到才回落到当前位置。
    /// </summary>
    private async Task<int> WriteLogAsync(CancellationToken token) => await ExecAsync(
        "INSERT INTO dbo.INV_DEPOT_LOG(PRO_NO, MUTUALITY_DATE, IN_OUT, MUTUALITY_TYPE, MUTUALITY_NO, MUTUALITY_SERIAL_NO, DEPOT_ID, LOCATION_NO, LOCATION_PATH, QTY, PRICE, AMOUNT, BATCH_NO, MUTUALITY_QTY, MUTUALITY_UNIT_ID, MUTUALITY_PRICE, MUTUALITY_CURR_ID, MUTUALITY_CURR_RATE, MUTUALITY_AMOUNT) "
        + $"SELECT t.PRO_NO, {LedgerDateExpression()}, @io, t.BILL_TYPE, t.BILL_NO, t.SERIAL_NO, t.DEPOT_ID, "
        // 位置：单据给了就照单据写；没给（空 / 哨兵）则**沿用这张单当初那一行的位置**——
        // 批核时位置可能是引擎替它挑的（存放方式解析），反向行落到哨兵就把同一个库存键劈成了两个。
        + "CASE WHEN ISNULL(NULLIF(LTRIM(RTRIM(t.LOCATION_NO)), N''), N'-') <> N'-' THEN t.LOCATION_NO "
        + "ELSE ISNULL(ORIG.LOCATION_NO, t.LOCATION_NO) END, "
        + "CASE WHEN @approve = 1 THEN LK.LOCATION_PATH ELSE ISNULL(SNAP.LOCATION_PATH, LK.LOCATION_PATH) END, "
        + "CASE WHEN @positive = 1 THEN t.BASE_QTY ELSE -t.BASE_QTY END, t.BASE_PRICE, "
        + "CASE WHEN @positive = 1 THEN t.AMOUNT*t.CURR_RATE ELSE -t.AMOUNT*t.CURR_RATE END, t.BATCH_NO, "
        + "CASE WHEN @positive = 1 THEN t.QTY ELSE -t.QTY END, t.UNIT_ID, t.PRICE, t.CURR_ID, t.CURR_RATE, "
        + "CASE WHEN @positive = 1 THEN t.AMOUNT ELSE -t.AMOUNT END "
        + $"FROM {Tmp} t "
        + "LEFT JOIN dbo.DEPOT_LOCATION LK ON LK.DEPOT_ID=t.DEPOT_ID AND LK.LOCATION_NO=t.LOCATION_NO "
        + "OUTER APPLY (SELECT TOP 1 L.LOCATION_PATH FROM dbo.INV_DEPOT_LOG L "
        + "WHERE L.PRO_NO=t.PRO_NO AND L.DEPOT_ID=t.DEPOT_ID AND L.LOCATION_NO=t.LOCATION_NO "
        + "AND L.MUTUALITY_TYPE=t.BILL_TYPE AND L.MUTUALITY_NO=t.BILL_NO "
        + "AND L.MUTUALITY_SERIAL_NO=t.SERIAL_NO AND L.IN_OUT=@originIo "
        + "ORDER BY L.MUTUALITY_DATE DESC) SNAP "
        + "OUTER APPLY (SELECT MAX(L2.LOCATION_NO) AS LOCATION_NO FROM dbo.INV_DEPOT_LOG L2 "
        + "WHERE L2.PRO_NO=t.PRO_NO AND L2.DEPOT_ID=t.DEPOT_ID AND L2.MUTUALITY_TYPE=t.BILL_TYPE "
        + "AND L2.MUTUALITY_NO=t.BILL_NO AND L2.MUTUALITY_SERIAL_NO=t.SERIAL_NO "
        + "AND LTRIM(RTRIM(ISNULL(L2.LOCATION_NO, N''))) <> N'' AND LTRIM(RTRIM(L2.LOCATION_NO)) <> N'-') ORIG",
        token,
        ("@positive", IsApprove ? 1 : 0),
        ("@approve", IsApprove ? 1 : 0),
        ("@io", FlowDirectionChar(_plan.Direction, IsApprove)),
        ("@originIo", FlowDirectionChar(_plan.Direction, true)));

    private async Task<int> UpdateProductAsync(CancellationToken token)
    {
        var affected = await ExecAsync(
            "UPDATE p SET "
            + "p.LAST_IN_DATE = CASE WHEN @inbound = 1 AND s.BILL_DATE > ISNULL(p.LAST_IN_DATE,'1900-01-01') THEN s.BILL_DATE ELSE p.LAST_IN_DATE END, "
            + "p.LAST_OUT_DATE = CASE WHEN @inbound = 0 AND s.BILL_DATE > ISNULL(p.LAST_OUT_DATE,'1900-01-01') THEN s.BILL_DATE ELSE p.LAST_OUT_DATE END, "
            + "p.QTY = ISNULL(p.QTY,0) + ISNULL(s.BASE_QTY,0) * @signedDirect, "
            + "p.PRICE = ROUND(CASE WHEN (p.QTY + s.BASE_QTY*@signedDirect)=0 THEN p.PRICE ELSE (p.AMOUNT + s.AMOUNT*@signedDirect)/(p.QTY + s.BASE_QTY*@signedDirect) END, 8), "
            + "p.AMOUNT = ROUND(p.AMOUNT + s.AMOUNT*@signedDirect, 2) "
            + $"FROM (SELECT PRO_NO, MAX(BILL_DATE) BILL_DATE, SUM(BASE_QTY) BASE_QTY, SUM(AMOUNT*CURR_RATE) AMOUNT FROM {Tmp} GROUP BY PRO_NO) s "
            + "JOIN dbo.PRODUCT p ON p.PRO_NO=s.PRO_NO",
            token,
            ("@inbound", _plan.Direction * (IsApprove ? 1 : -1) == 1 ? 1 : 0),
            ("@signedDirect", _plan.Direction * (IsApprove ? 1 : -1)));
        affected += await WriteLogAsync(token);
        if (IsApprove)
        {
            // Legacy procedure only refreshes the last purchase price on approve;
            // deapprove rolls back stock without touching that stamp.
            affected += await ExecAsync(
                "UPDATE p SET p.LAST_PURCHASE_PRICE = t.PRICE, p.LAST_PURCHASE_UNIT_ID = t.UNIT_ID, p.LAST_PURCHASE_CURR_ID = t.CURR_ID "
                + $"FROM {Tmp} t JOIN dbo.PRODUCT p ON p.PRO_NO=t.PRO_NO", token);
        }
        return affected;
    }

    /// <summary>
    /// Available-quantity refresh: the per-depot delta first, then the full
    /// product-level MRP recompute (in-process since the legacy procedure was retired).
    /// The gate is the <c>SYSSS.PRO_MRP</c> switch
    /// only — the configured <c>mrp</c> parameter is intentionally not read, matching the
    /// legacy <c>P_UPDATE_PRO_DEPOT</c> where the <c>@mrp</c> branch is commented out; it is
    /// kept in the stored parameters as a documented no-op rather than a behaviour switch.
    /// </summary>
    private async Task UpdateMrpAsync(CancellationToken token)
    {
        var enabled = await SystemParameterService.GetBoolAsync(
            _connection, _transaction, SystemParameterService.SystemOwner, "PRO_MRP", fallback: false, token);
        if (!enabled)
            return;
        await ExecAsync(
            "UPDATE p SET p.MRP_QTY = ISNULL(p.MRP_QTY,0) + ISNULL(d.BASE_QTY,0) * @approveTag * @direct "
            + $"FROM (SELECT t.PRO_NO, SUM(t.BASE_QTY) BASE_QTY FROM {Tmp} t JOIN dbo.DEPOT dp ON dp.DEPOT_ID=t.DEPOT_ID AND dp.MRP=1 GROUP BY t.PRO_NO) d "
            + "JOIN dbo.PRODUCT p ON p.PRO_NO=d.PRO_NO",
            token,
            ("@approveTag", IsApprove ? 1 : -1),
            ("@direct", _plan.Direction));
        await MrpRecalcService.RecalcAsync(_connection, _transaction, token);
    }

    private async Task<int> CleanTrailingAsync(CancellationToken token) => await ExecAsync(
        // 成本是库别级字段：清零条件必须取该库别的合计数量，而不是单个库位行的数量，
        // 否则某一个库位刚好出空就会把整个库别的成本抹掉。
        "UPDATE d SET d.COST_PRICE = CASE WHEN (ISNULL(d.COST_AMOUNT,0)<=0 OR ISNULL(g.QTY,0)<=0) THEN 0 ELSE d.COST_PRICE END, "
        + "d.COST_AMOUNT = CASE WHEN ISNULL(g.QTY,0)<=0 THEN 0 ELSE d.COST_AMOUNT END "
        + "FROM dbo.INV_PRO_DEPOT d JOIN ("
        + "SELECT PRO_NO, DEPOT_ID, SUM(ISNULL(QTY,0)) QTY FROM dbo.INV_PRO_DEPOT GROUP BY PRO_NO, DEPOT_ID) g "
        + "ON g.PRO_NO=d.PRO_NO AND g.DEPOT_ID=d.DEPOT_ID "
        + $"WHERE EXISTS (SELECT 1 FROM {Tmp} t WHERE t.PRO_NO=d.PRO_NO AND t.DEPOT_ID=d.DEPOT_ID)", token);

    /// <summary>
    /// 出库充足性：**够不够出按可用量判，不按在库数量判**（ADR-020 §9.7 D7-⑧ / §10 WS-23）。
    /// </summary>
    /// <remarks>
    /// **这是一次行为变更，不是笔误**：口径由 `QTY` 改为 `USEABLE_QTY`（= 数量 − 有效冻结 − 有效预留）。
    /// 冻结与预留的**全部价值**就在于拦得住出库——拦不住的冻结只是报表上的一个数字。
    /// 代价如实写明：在库 100、其中 10 被冻结时，出 95 会**被拒**（旧口径放行），
    /// 而 90 恰好放行；判别性用例就是钉这一对（口径改回 `QTY` ⇒ 用例红）。
    ///
    /// **前提是该列可信**：`USEABLE_QTY` 由 `InventoryAvailabilityService.SyncSlotsAsync` 这一个出口维护，
    /// 移动引擎在所有余额写入之后会调它（见 `SyncAvailabilityAsync`），冻结/预留/释放与来源结案钩子同样走它。
    ///
    /// **批号账那一半刻意不动**：`INV_BATCH_M` 的进出累计是**批次账**的余量，不承载冻结/预留语义，
    /// 所以它仍按余量判——两本账各按自己的语义判，不是"漏改一处"。
    /// </remarks>
    private async Task CheckStockAsync(CancellationToken token)
    {
        var insufficient = await QueryListAsync(
            "SELECT s.PRO_NO, LTRIM(RTRIM(s.DEPOT_ID)) + '/' + LTRIM(RTRIM(s.LOCATION_NO)), CAST(s.BASE_QTY - ISNULL(d.USEABLE_QTY,0) AS varchar(30)) "
            + $"FROM (SELECT PRO_NO, DEPOT_ID, LOCATION_NO, BATCH_NO, SUM(BASE_QTY) BASE_QTY FROM {Tmp} "
            + "GROUP BY PRO_NO, DEPOT_ID, LOCATION_NO, BATCH_NO) s "
            + "JOIN dbo.INV_PRO_DEPOT d ON d.PRO_NO=s.PRO_NO AND d.DEPOT_ID=s.DEPOT_ID "
            + "AND d.LOCATION_NO=s.LOCATION_NO AND d.BATCH_NO=s.BATCH_NO "
            + "WHERE s.BASE_QTY > ISNULL(d.USEABLE_QTY,0) + 0.001", token);
        if (insufficient.Count > 0)
            throw new EffectValidationException("库存数量不足（按可用量判：在库数量扣掉冻结与预留之后不够出）\n"
                + "料  号---------------库别/库位---------------不足数量\n" + FormatPairs(insufficient));
        var batchShort = await QueryListAsync(
            $"SELECT t.PRO_NO, t.BATCH_NO, CAST(t.BASE_QTY - (ISNULL(b.IN_SUM,0)-ISNULL(b.OUT_SUM,0)) AS varchar(30)) "
            + $"FROM {Tmp} t JOIN dbo.INV_BATCH_M b ON b.BATCH_NO=t.BATCH_NO AND b.PRO_NO=t.PRO_NO "
            + "WHERE t.BASE_QTY > (ISNULL(b.IN_SUM,0)-ISNULL(b.OUT_SUM,0))", token);
        if (batchShort.Count > 0)
            throw new EffectValidationException("批号数量不足\n" + FormatPairs(batchShort));
    }

    private async Task<int> ExecAsync(string sql, CancellationToken token, params (string Name, object? Value)[] extra)
    {
        await using var command = new SqlCommand(sql, _connection, _transaction);
        foreach (var (name, value) in extra)
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return await command.ExecuteNonQueryAsync(token);
    }

    private async Task<List<(string, string)>> QueryListAsync(string sql, CancellationToken token)
    {
        var result = new List<(string, string)>();
        await using var command = new SqlCommand(sql, _connection, _transaction);
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
            result.Add((reader.IsDBNull(0) ? "" : reader.GetValue(0).ToString() ?? "", reader.IsDBNull(1) ? "" : reader.GetValue(1).ToString() ?? ""));
        return result;
    }

    private static string FormatPairs(List<(string, string)> rows) =>
        string.Join("\n", rows.Select(row => row.Item1 + "    " + row.Item2));
}
