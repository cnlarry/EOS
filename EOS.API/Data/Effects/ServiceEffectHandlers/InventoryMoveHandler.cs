using System.Data;
using System.Globalization;
using System.Text;
using System.Text.Json;
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

    public InventoryMoveHandler(EffectPhysicalColumns columns, DepotStockPolicyService policies)
    {
        _columns = columns;
        _policies = policies;
    }

    public async Task<int> ExecuteAsync(ServiceEffectContext context, CancellationToken token)
    {
        var plan = InventoryMovePlan.Parse(context.Action.Params ?? JsonSerializer.SerializeToElement(new { }));
        var columns = await _columns.LoadAsync(context.Connection, token, context.Transaction);
        var sql = plan.BuildRowSet(context.Plan, context.MasterKeyValues, columns);
        var executor = new InventoryMoveSql(
            context.Connection, context.Transaction, plan, context.ExecutionEvent, _policies);
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

    private readonly SqlConnection _connection;
    private readonly SqlTransaction _transaction;
    private readonly InventoryMovePlan _plan;
    private readonly EffectEvent _event;
    private readonly DepotStockPolicyService _policies;

    public InventoryMoveSql(
        SqlConnection connection,
        SqlTransaction transaction,
        InventoryMovePlan plan,
        EffectEvent executionEvent,
        DepotStockPolicyService policies)
    {
        _connection = connection;
        _transaction = transaction;
        _plan = plan;
        _event = executionEvent;
        _policies = policies;
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

        // 必须在行集落进 Tmp 之后：库别清单是从 Tmp 里读的，先读只会读到空表，
        // 于是每个库别都拿不到策略行、一律按档 0 处理（表面正常，档位静默失效）。
        await LoadPolicyAsync(token);

        var affected = 0;
        if (IsApprove)
            affected += await PrecheckAsync(token);
        affected += await NormalizeAsync(token);
        // 位置校验必须在归一化之后：未填位置此时已被归一为哨兵 N'-'。
        await PrecheckLocationsAsync(token);
        affected += await NormalizePriceAsync(token);
        await EnsureDepotRowsAsync(token);
        await RequireBatchesAsync(token);
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
        await ExecAsync($"DROP TABLE {Tmp}", token);
        await ExecAsync($"DROP TABLE {PolicyTmp}", token);
        return affected;
    }

    private async Task CreateTempAsync(CancellationToken token)
    {
        await ExecAsync(
            $"CREATE TABLE {Tmp}(BILL_TYPE nchar(10),BILL_NO nchar(30),BILL_DATE datetime,SERIAL_NO int,PRO_NO nchar(30),"
            + "DEPOT_ID nchar(10),LOCATION_NO nvarchar(30),QTY float,BASE_QTY float,UNIT_ID nchar(10),PRICE float,BASE_PRICE float,"
            + "CURR_ID nchar(10),CURR_RATE float,AMOUNT float,BATCH_NO nchar(30))", token);
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
            + "BATCH_MODE int NOT NULL, MIX_PRODUCT bit NOT NULL, MIX_BATCH bit NOT NULL)", token);

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
            $"INSERT INTO {PolicyTmp}(DEPOT_ID, BATCH_MODE, MIX_PRODUCT, MIX_BATCH) VALUES (@depot, @mode, @mixProduct, @mixBatch)",
            _connection, _transaction);
        insert.Parameters.Add("@depot", SqlDbType.NChar, 10);
        insert.Parameters.Add("@mode", SqlDbType.Int);
        insert.Parameters.Add("@mixProduct", SqlDbType.Bit);
        insert.Parameters.Add("@mixBatch", SqlDbType.Bit);
        foreach (var depot in depots)
        {
            var policy = await _policies.ResolveAsync(depot, _connection, _transaction, token);
            insert.Parameters["@depot"].Value = depot;
            insert.Parameters["@mode"].Value = policy.BatchMode;
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
        await CheckStockAsync(token);
        var affected = await SyncDepotLevelCostAsync(-1, token);
        affected += await ApplyRowQuantitiesAsync(-1, token);
        affected += await ApplyBatchesAsync(effect: 'O', inSummary: false, token);
        return affected;
    }

    private async Task<int> UndoInAsync(CancellationToken token)
    {
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

    /// <summary>Batch ledgers: insert missing masters, then the movement detail rows.</summary>
    private async Task<int> ApplyBatchesAsync(char effect, bool inSummary, CancellationToken token)
    {
        var affected = await ExecAsync(
            "INSERT INTO dbo.INV_BATCH_M(BATCH_NO, PRO_NO, IN_SUM) "
            + $"SELECT t.BATCH_NO, t.PRO_NO, 0 FROM {Tmp} t WHERE ISNULL(t.BATCH_NO,'') != '' "
            + "AND NOT EXISTS (SELECT 1 FROM dbo.INV_BATCH_M b WHERE b.BATCH_NO=t.BATCH_NO AND b.PRO_NO=t.PRO_NO)", token);
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
        + $"SELECT t.PRO_NO, {LedgerDateExpression()}, @io, t.BILL_TYPE, t.BILL_NO, t.SERIAL_NO, t.DEPOT_ID, t.LOCATION_NO, "
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
        + "ORDER BY L.MUTUALITY_DATE DESC) SNAP",
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
        var gate = await ScalarAsync("SELECT COUNT(*) FROM dbo.SYSSS WITH (NOLOCK) WHERE PRO_MRP=1", token);
        if (Convert.ToInt32(gate) == 0)
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

    private async Task CheckStockAsync(CancellationToken token)
    {
        var insufficient = await QueryListAsync(
            "SELECT s.PRO_NO, LTRIM(RTRIM(s.DEPOT_ID)) + '/' + LTRIM(RTRIM(s.LOCATION_NO)), CAST(s.BASE_QTY - ISNULL(d.QTY,0) AS varchar(30)) "
            + $"FROM (SELECT PRO_NO, DEPOT_ID, LOCATION_NO, BATCH_NO, SUM(BASE_QTY) BASE_QTY FROM {Tmp} "
            + "GROUP BY PRO_NO, DEPOT_ID, LOCATION_NO, BATCH_NO) s "
            + "JOIN dbo.INV_PRO_DEPOT d ON d.PRO_NO=s.PRO_NO AND d.DEPOT_ID=s.DEPOT_ID "
            + "AND d.LOCATION_NO=s.LOCATION_NO AND d.BATCH_NO=s.BATCH_NO "
            + "WHERE s.BASE_QTY > ISNULL(d.QTY,0) + 0.001", token);
        if (insufficient.Count > 0)
            throw new EffectValidationException("库存数量不足\n料  号---------------库别/库位---------------不足数量\n" + FormatPairs(insufficient));
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

    private async Task<object?> ScalarAsync(string sql, CancellationToken token)
    {
        await using var command = new SqlCommand(sql, _connection, _transaction);
        return await command.ExecuteScalarAsync(token);
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
