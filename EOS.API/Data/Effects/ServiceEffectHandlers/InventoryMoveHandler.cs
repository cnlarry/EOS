using System.Data;
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

    public InventoryMoveHandler(EffectPhysicalColumns columns) => _columns = columns;

    public async Task<int> ExecuteAsync(ServiceEffectContext context, CancellationToken token)
    {
        var plan = InventoryMovePlan.Parse(context.Action.Params ?? JsonSerializer.SerializeToElement(new { }));
        var columns = await _columns.LoadAsync(context.Connection, token);
        var sql = plan.BuildRowSet(context.Plan, context.MasterKeyValues, columns);
        var executor = new InventoryMoveSql(
            context.Connection, context.Transaction, plan, context.ExecutionEvent,
            context.Plan.ModuleId.ToString());
        return await executor.RunAsync(sql, token);
    }
}

/// <summary>Parsed inventory-move parameters with closed shapes only.</summary>
public sealed record InventoryMovePlan(
    int Direction,
    string MasterDateField,
    string DepotField,
    IReadOnlyList<EffectTerm> QuantityTerms,
    IReadOnlyList<string> DetailFields)
{
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

        var detailFields = new List<string>();
        if (fieldMap.TryGetProperty("detail", out var detail) && detail.ValueKind == JsonValueKind.Array)
            foreach (var item in detail.EnumerateArray())
                if (item.ValueKind == JsonValueKind.String)
                    detailFields.Add(item.GetString()!.Trim());

        return new InventoryMovePlan(
            direction == "IN" ? 1 : -1,
            masterDate,
            depotField,
            terms,
            detailFields);
    }

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

        var check = new[] { MasterDateField, DepotField }.Concat(DetailFields).Concat(QuantityTerms.Select(t => t.Field))
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
            ("BILL_TYPE", "@billType"),
            ("BILL_NO", $"M.{Q(plan.MasterPkOrder[0])}"),
            ("BILL_DATE", $"M.{Q(MasterDateField)}"),
            ("SERIAL_NO", "D.[SERIAL_NO]"),
            ("PRO_NO", "D.[PRO_NO]"),
            ("DEPOT_ID", $"D.{Q(DepotField)}"),
        };
        foreach (var field in DetailFields)
        {
            if (field is "SERIAL_NO" or "PRO_NO")
                continue;
            if (rowColumns.Any(item => item.Column == field))
                continue;
            rowColumns.Add((field, $"D.{Q(field)}"));
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

    private readonly SqlConnection _connection;
    private readonly SqlTransaction _transaction;
    private readonly InventoryMovePlan _plan;
    private readonly EffectEvent _event;
    private readonly string _billType;

    public InventoryMoveSql(
        SqlConnection connection,
        SqlTransaction transaction,
        InventoryMovePlan plan,
        EffectEvent executionEvent,
        string billType)
    {
        _connection = connection;
        _transaction = transaction;
        _plan = plan;
        _event = executionEvent;
        _billType = billType;
    }

    private bool IsApprove => _event is EffectEvent.ApproveEffect or EffectEvent.Save;

    public async Task<int> RunAsync(InventoryMovePlan.RowSetStatement rowSet, CancellationToken token)
    {
        var approveTag = IsApprove ? 1 : -1;
        var direct = _plan.Direction;
        await ExecAsync($"IF OBJECT_ID('tempdb..{Tmp}') IS NOT NULL DROP TABLE {Tmp}", token);
        await CreateTempAsync(token);

        var insertSql = $"INSERT INTO {Tmp} (" + string.Join(",", rowSet.Columns.Select(InventoryMovePlan.Q)) + ") " + rowSet.Sql;
        var fill = new SqlCommand(insertSql, _connection, _transaction);
        fill.Parameters.Add("@billType", SqlDbType.NVarChar, 10).Value = _billType;
        foreach (var parameter in rowSet.Parameters)
            fill.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);
        await fill.ExecuteNonQueryAsync(token);

        var affected = 0;
        if (IsApprove)
            affected += await PrecheckAsync(token);
        affected += await NormalizeAsync(token);
        affected += await NormalizePriceAsync(token);
        await EnsureDepotRowsAsync(token);
        await RequireBatchesAsync(token);

        if (IsApprove)
            affected += direct == 1 ? await ApplyInAsync(token) : await ApplyOutAsync(token);
        else
            affected += direct == 1 ? await UndoInAsync(token) : await UndoOutAsync(token);

        affected += await UpdateProductAsync(token);
        await UpdateMrpAsync(token);
        if (direct * approveTag == -1)
            affected += await CleanTrailingAsync(token);
        await ExecAsync($"DROP TABLE {Tmp}", token);
        return affected;
    }

    private async Task CreateTempAsync(CancellationToken token)
    {
        await ExecAsync(
            $"CREATE TABLE {Tmp}(BILL_TYPE nchar(10),BILL_NO nchar(30),BILL_DATE datetime,SERIAL_NO int,PRO_NO nchar(30),"
            + "DEPOT_ID nchar(10),QTY float,BASE_QTY float,UNIT_ID nchar(10),PRICE float,BASE_PRICE float,"
            + "CURR_ID nchar(10),CURR_RATE float,AMOUNT float,BATCH_NO nchar(30))", token);
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

    private async Task<int> NormalizeAsync(CancellationToken token) => await ExecAsync(
        $"UPDATE t SET t.BATCH_NO = CASE ISNULL(p.MANAGE_BATCH,0) WHEN 0 THEN '' ELSE t.BATCH_NO END, "
        + "t.BASE_QTY = t.QTY * CASE t.UNIT_ID WHEN p.UNIT_ID THEN 1 WHEN p.UNIT_ID_1 THEN p.UNIT_RATE_1 "
        + "WHEN p.UNIT_ID_2 THEN p.UNIT_RATE_2 WHEN p.UNIT_ID_3 THEN p.UNIT_RATE_3 WHEN p.UNIT_ID_4 THEN p.UNIT_RATE_4 ELSE 1 END, "
        + "t.CURR_RATE = CASE ISNULL(t.CURR_RATE,0) WHEN 0 THEN 1 ELSE t.CURR_RATE END "
        + $"FROM {Tmp} t JOIN dbo.PRODUCT p ON t.PRO_NO=p.PRO_NO", token);

    private async Task<int> NormalizePriceAsync(CancellationToken token) => await ExecAsync(
        "UPDATE t SET t.BASE_PRICE = ROUND(CASE ISNULL(t.PRICE,0) WHEN 0 THEN d.COST_PRICE "
        + "ELSE CASE ISNULL(t.BASE_QTY,0) WHEN 0 THEN d.COST_PRICE ELSE (t.AMOUNT*t.CURR_RATE)/t.BASE_QTY END END, 8), "
        + "t.CURR_RATE = CASE ISNULL(t.PRICE,0) WHEN 0 THEN 1 ELSE t.CURR_RATE END "
        + $"FROM {Tmp} t JOIN dbo.INV_PRO_DEPOT d ON t.PRO_NO=d.PRO_NO AND t.DEPOT_ID=d.DEPOT_ID", token);

    private async Task EnsureDepotRowsAsync(CancellationToken token) => await ExecAsync(
        "INSERT INTO dbo.INV_PRO_DEPOT(PRO_NO, DEPOT_ID, QTY, INIT_QTY, COST_PRICE, COST_AMOUNT) "
        + $"SELECT DISTINCT t.PRO_NO, t.DEPOT_ID, 0, 0, 0, 0 FROM {Tmp} t "
        + "WHERE NOT EXISTS (SELECT 1 FROM dbo.INV_PRO_DEPOT d WHERE d.PRO_NO=t.PRO_NO AND d.DEPOT_ID=t.DEPOT_ID)", token);

    private async Task RequireBatchesAsync(CancellationToken token)
    {
        var missing = await QueryListAsync(
            $"SELECT DISTINCT t.PRO_NO, '' FROM {Tmp} t JOIN dbo.PRODUCT p ON t.PRO_NO=p.PRO_NO "
            + "WHERE ISNULL(t.BATCH_NO,'')='' AND ISNULL(p.MANAGE_BATCH,0)=1", token);
        if (missing.Count > 0)
            throw new EffectValidationException("以下品号需要输入批号信息\n" + FormatPairs(missing));
    }

    private async Task<int> ApplyInAsync(CancellationToken token)
    {
        var affected = await ExecAsync(
            "UPDATE d SET d.QTY = ISNULL(d.QTY,0) + ISNULL(s.BASE_QTY,0), "
            + "d.COST_PRICE = ROUND(CASE WHEN (d.QTY + s.BASE_QTY)=0 THEN d.COST_PRICE ELSE (d.COST_AMOUNT + s.AMOUNT)/(d.QTY + s.BASE_QTY) END, 8), "
            + "d.COST_AMOUNT = ISNULL(d.COST_AMOUNT,0) + ISNULL(s.AMOUNT,0) "
            + $"FROM (SELECT PRO_NO, DEPOT_ID, SUM(BASE_QTY) BASE_QTY, SUM(AMOUNT*CURR_RATE) AMOUNT FROM {Tmp} GROUP BY PRO_NO, DEPOT_ID) s "
            + "JOIN dbo.INV_PRO_DEPOT d ON d.PRO_NO=s.PRO_NO AND d.DEPOT_ID=s.DEPOT_ID", token);
        affected += await ApplyBatchesAsync(effect: 'I', inSummary: true, token);
        return affected;
    }

    private async Task<int> ApplyOutAsync(CancellationToken token)
    {
        await CheckStockAsync(token);
        var affected = await ExecAsync(
            "UPDATE d SET d.QTY = ISNULL(d.QTY,0) - ISNULL(s.BASE_QTY,0), "
            + "d.COST_PRICE = ROUND(CASE WHEN (d.QTY - s.BASE_QTY)=0 THEN d.COST_PRICE ELSE (d.COST_AMOUNT - s.AMOUNT)/(d.QTY - s.BASE_QTY) END, 8), "
            + "d.COST_AMOUNT = ISNULL(d.COST_AMOUNT,0) - ISNULL(s.AMOUNT,0) "
            + $"FROM (SELECT PRO_NO, DEPOT_ID, SUM(BASE_QTY) BASE_QTY, SUM(AMOUNT*CURR_RATE) AMOUNT FROM {Tmp} GROUP BY PRO_NO, DEPOT_ID) s "
            + "JOIN dbo.INV_PRO_DEPOT d ON d.PRO_NO=s.PRO_NO AND d.DEPOT_ID=s.DEPOT_ID", token);
        affected += await ApplyBatchesAsync(effect: 'O', inSummary: false, token);
        return affected;
    }

    private async Task<int> UndoInAsync(CancellationToken token)
    {
        await CheckStockAsync(token);
        var affected = await ExecAsync(
            "UPDATE d SET d.QTY = ISNULL(d.QTY,0) - ISNULL(s.BASE_QTY,0), "
            + "d.COST_PRICE = ROUND(CASE WHEN (d.QTY - s.BASE_QTY)=0 THEN d.COST_PRICE ELSE (d.COST_AMOUNT - s.AMOUNT)/(d.QTY - s.BASE_QTY) END, 8), "
            + "d.COST_AMOUNT = ISNULL(d.COST_AMOUNT,0) - ISNULL(s.AMOUNT,0) "
            + $"FROM (SELECT PRO_NO, DEPOT_ID, SUM(BASE_QTY) BASE_QTY, SUM(AMOUNT*CURR_RATE) AMOUNT FROM {Tmp} GROUP BY PRO_NO, DEPOT_ID) s "
            + "JOIN dbo.INV_PRO_DEPOT d ON d.PRO_NO=s.PRO_NO AND d.DEPOT_ID=s.DEPOT_ID", token);
        affected += await ExecAsync(
            "UPDATE b SET b.IN_SUM = ISNULL(b.IN_SUM,0) - ISNULL(t.BASE_QTY,0) "
            + $"FROM {Tmp} t JOIN dbo.INV_BATCH_M b ON b.PRO_NO=t.PRO_NO AND b.BATCH_NO=t.BATCH_NO", token);
        affected += await WriteReverseBatchesAsync(token);
        return affected;
    }

    private async Task<int> UndoOutAsync(CancellationToken token)
    {
        var affected = await ExecAsync(
            "UPDATE d SET d.QTY = ISNULL(d.QTY,0) + ISNULL(s.BASE_QTY,0), "
            + "d.COST_PRICE = CASE WHEN (d.QTY + s.BASE_QTY)=0 THEN d.COST_PRICE ELSE (d.COST_AMOUNT + ISNULL(s.AMOUNT,0)) / (d.QTY + s.BASE_QTY) END, "
            + "d.COST_AMOUNT = ISNULL(d.COST_AMOUNT,0) + ISNULL(s.AMOUNT,0) "
            + $"FROM (SELECT PRO_NO, DEPOT_ID, SUM(BASE_QTY) BASE_QTY, SUM(AMOUNT*CURR_RATE) AMOUNT FROM {Tmp} GROUP BY PRO_NO, DEPOT_ID) s "
            + "JOIN dbo.INV_PRO_DEPOT d ON d.PRO_NO=s.PRO_NO AND d.DEPOT_ID=s.DEPOT_ID", token);
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
        + $"SELECT t.BATCH_NO, t.PRO_NO, t.BILL_DATE, t.BILL_TYPE, t.BILL_NO, t.SERIAL_NO, t.DEPOT_ID, '{(_plan.Direction == 1 ? 'O' : 'I')}', -t.BASE_QTY, -t.QTY, t.UNIT_ID, t.PRICE, t.CURR_ID, t.CURR_RATE, -t.AMOUNT "
        + $"FROM {Tmp} t WHERE ISNULL(t.BATCH_NO,'') != ''", token);

    /// <summary>Inventory log rows; deapprove writes mirrored rows with flipped direction and negative quantities.</summary>
    private async Task<int> WriteLogAsync(CancellationToken token) => await ExecAsync(
        "INSERT INTO dbo.INV_DEPOT_LOG(PRO_NO, MUTUALITY_DATE, IN_OUT, MUTUALITY_TYPE, MUTUALITY_NO, MUTUALITY_SERIAL_NO, DEPOT_ID, QTY, PRICE, AMOUNT, BATCH_NO, MUTUALITY_QTY, MUTUALITY_UNIT_ID, MUTUALITY_PRICE, MUTUALITY_CURR_ID, MUTUALITY_CURR_RATE, MUTUALITY_AMOUNT) "
        + "SELECT t.PRO_NO, t.BILL_DATE, CASE WHEN @positive = 1 THEN 'I' ELSE 'O' END, t.BILL_TYPE, t.BILL_NO, t.SERIAL_NO, t.DEPOT_ID, "
        + "CASE WHEN @positive = 1 THEN t.BASE_QTY ELSE -t.BASE_QTY END, t.BASE_PRICE, "
        + "CASE WHEN @positive = 1 THEN t.AMOUNT*t.CURR_RATE ELSE -t.AMOUNT*t.CURR_RATE END, t.BATCH_NO, "
        + "CASE WHEN @positive = 1 THEN t.QTY ELSE -t.QTY END, t.UNIT_ID, t.PRICE, t.CURR_ID, t.CURR_RATE, "
        + "CASE WHEN @positive = 1 THEN t.AMOUNT ELSE -t.AMOUNT END "
        + $"FROM {Tmp} t", token, ("@positive", IsApprove ? 1 : 0));

    private async Task<int> UpdateProductAsync(CancellationToken token)
    {
        var affected = await ExecAsync(
            "UPDATE p SET "
            + "p.LAST_IN_DATE = CASE WHEN @positive = 1 AND s.BILL_DATE > ISNULL(p.LAST_IN_DATE,'1900-01-01') THEN s.BILL_DATE ELSE p.LAST_IN_DATE END, "
            + "p.LAST_OUT_DATE = CASE WHEN @positive = 0 AND s.BILL_DATE > ISNULL(p.LAST_OUT_DATE,'1900-01-01') THEN s.BILL_DATE ELSE p.LAST_OUT_DATE END, "
            + "p.QTY = ISNULL(p.QTY,0) + ISNULL(s.BASE_QTY,0) * @signedDirect, "
            + "p.PRICE = ROUND(CASE WHEN (p.QTY + s.BASE_QTY*@signedDirect)=0 THEN p.PRICE ELSE (p.AMOUNT + s.AMOUNT*@signedDirect)/(p.QTY + s.BASE_QTY*@signedDirect) END, 8), "
            + "p.AMOUNT = ROUND(p.AMOUNT + s.AMOUNT*@signedDirect, 2) "
            + $"FROM (SELECT PRO_NO, MAX(BILL_DATE) BILL_DATE, SUM(BASE_QTY) BASE_QTY, SUM(AMOUNT*CURR_RATE) AMOUNT FROM {Tmp} GROUP BY PRO_NO) s "
            + "JOIN dbo.PRODUCT p ON p.PRO_NO=s.PRO_NO",
            token,
            ("@positive", IsApprove ? 1 : 0),
            ("@signedDirect", _plan.Direction));
        affected += await WriteLogAsync(token);
        affected += await ExecAsync(
            "UPDATE p SET p.LAST_PURCHASE_PRICE = t.PRICE, p.LAST_PURCHASE_UNIT_ID = t.UNIT_ID, p.LAST_PURCHASE_CURR_ID = t.CURR_ID "
            + $"FROM {Tmp} t JOIN dbo.PRODUCT p ON p.PRO_NO=t.PRO_NO", token);
        return affected;
    }

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
        await ExecAsync("EXEC dbo.P_UPDATE_PRO_MRP_ALL", token);
    }

    private async Task<int> CleanTrailingAsync(CancellationToken token) => await ExecAsync(
        "UPDATE d SET d.COST_PRICE = CASE WHEN (d.COST_AMOUNT<=0 OR d.QTY<=0) THEN 0 ELSE d.COST_PRICE END, "
        + "d.COST_AMOUNT = CASE WHEN d.QTY<=0 THEN 0 ELSE d.COST_AMOUNT END "
        + $"FROM dbo.INV_PRO_DEPOT d WHERE EXISTS (SELECT 1 FROM {Tmp} t WHERE t.PRO_NO=d.PRO_NO AND t.DEPOT_ID=d.DEPOT_ID)", token);

    private async Task CheckStockAsync(CancellationToken token)
    {
        var insufficient = await QueryListAsync(
            "SELECT s.PRO_NO, s.DEPOT_ID, CAST(s.BASE_QTY - ISNULL(d.QTY,0) AS varchar(30)) "
            + $"FROM (SELECT PRO_NO, DEPOT_ID, SUM(BASE_QTY) BASE_QTY FROM {Tmp} GROUP BY PRO_NO, DEPOT_ID) s "
            + "JOIN dbo.INV_PRO_DEPOT d ON d.PRO_NO=s.PRO_NO AND d.DEPOT_ID=s.DEPOT_ID "
            + "WHERE s.BASE_QTY > ISNULL(d.QTY,0) + 0.001", token);
        if (insufficient.Count > 0)
            throw new EffectValidationException("库存数量不足\n料  号---------------库别----不足数量\n" + FormatPairs(insufficient));
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
