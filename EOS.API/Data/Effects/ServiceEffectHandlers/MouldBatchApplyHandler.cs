using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace EOS.API.Data.Effects.ServiceEffectHandlers;

/// <summary>
/// mould-batch-apply: on approval a mass-production mould application drives the
/// accept/scrap state machines (ported from P_WF_MOU_BATCH; no cursor — the
/// procedure reads one master row and branches on BATCH_SORT):
///   - sort &lt;= '2' (accept): MOU_ACCEPT_M.BATCH_STATE := sort,
///     FINISHED_QTY += qty; PRODUCT.P_LENGTH := LIAOCHANG (overwrite),
///     P_WIDTH += qty; then the over-apply guard fails the run when
///     QTY &lt; FINISHED_QTY (surfaced as a validation error, same transaction).
///   - sort &gt; '2' (scrap): MOU_SCRAP_D.BATCH_STATE := 1.
/// Deapprove is a branch state machine, not a mirror (reverse kind stays
/// "auto-reverse" per the landed configuration):
///   - sort '1': accept STATE '0', FINISHED_QTY -= qty, PRODUCT overwritten back;
///   - sort '2': accept STATE '1', FINISHED_QTY -= qty, PRODUCT untouched (H3);
///   - else: scrap STATE 0 only — the legacy extra FINISHED_QTY decrement is
///     dropped (H2: approve never added it, decrementing drifts negative).
/// All identifiers come from closed configuration checked against physical
/// columns; only key values travel as parameters. A NULL batch quantity
/// propagates NULL exactly like the legacy ISNULL arithmetic.
/// </summary>
public sealed class MouldBatchApplyHandler : IEffectServiceHandler
{
    public string EffectKey => "mould-batch-apply";

    public async Task<int> ExecuteAsync(ServiceEffectContext context, CancellationToken token)
    {
        var root = context.Action.Params ?? throw new EffectConfigException("mould-batch-apply 缺少参数。");
        var plan = context.Plan;
        if (plan.MasterTable is null)
            throw new EffectConfigException("mould-batch-apply 需要主表形态。");
        if (context.MasterKeyValues.Count < 2)
            throw new EffectConfigException("mould-batch-apply 缺少单据主键值。");
        var columns = await new EffectPhysicalColumns().LoadAsync(context.Connection, token, context.Transaction);
        var spec = MouldBatchApplySpec.Parse(root, plan, columns);

        var doc = await ReadDocumentAsync(context, token);
        if (doc is null)
            throw new EffectConfigException("mould-batch-apply 读不到本单主表行。");
        var sort = (doc.BatchSort ?? string.Empty).Trim();
        var parameters = new List<EffectSqlParameter>
        {
            new("@bt", context.MasterKeyValues[0]),
            new("@bn", context.MasterKeyValues[1]),
            new("@at", doc.AcceptType),
            new("@an", doc.AcceptNo),
            new("@sct", doc.ScrapType),
            new("@scn", doc.ScrapNo),
            new("@ssn", doc.ScrapSerialNo),
            new("@qty", doc.Qty),
        };

        bool approve;
        if (context.ExecutionEvent is EffectEvent.ApproveEffect or EffectEvent.Save)
        {
            approve = true;
        }
        else
        {
            var kind = ReverseKind(context);
            if (!kind.Equals("auto-reverse", StringComparison.OrdinalIgnoreCase))
                throw new EffectConfigException($"mould-batch-apply 解批 reverse.kind 仅支持 auto-reverse（当前 '{kind}'）。");
            approve = false;
        }

        var affected = 0;
        foreach (var sql in approve ? BuildApproveStatements(spec, sort, parameters) : BuildDeapproveStatements(spec, sort, parameters))
            affected += await ServiceEffectSql.ExecAsync(context.Connection, context.Transaction, sql, parameters, token);
        if (approve && string.Compare(sort, "2", StringComparison.Ordinal) <= 0)
            await CheckOverApplyAsync(context, parameters, token);
        return affected;
    }

    private static async Task<MouldBatchDocument?> ReadDocumentAsync(ServiceEffectContext context, CancellationToken token)
    {
        var plan = context.Plan;
        var parameters = new List<EffectSqlParameter>();
        var q = ServiceEffectSql.Q;
        var sql = $"SELECT M.{q("ACCEPT_TYPE")}, M.{q("ACCEPT_NO")}, M.{q("SCRAP_TYPE")}, M.{q("SCRAP_NO")}, "
            + $"M.{q("SCRAP_SERIAL_NO")}, M.{q("BATCH_SORT")}, M.{q("QTY")} "
            + $"FROM dbo.{q(plan.MasterTable!)} M "
            + $"WHERE {ServiceEffectSql.MasterKeyFilter(plan, context.MasterKeyValues, parameters)}";
        await using var command = new SqlCommand(sql, context.Connection, context.Transaction);
        foreach (var parameter in parameters)
            command.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);
        await using var reader = await command.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token))
            return null;
        static string? Text(SqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);
        var doc = new MouldBatchDocument(
            Text(reader, 0), Text(reader, 1), Text(reader, 2), Text(reader, 3),
            reader.IsDBNull(4) ? (short?)null : reader.GetInt16(4),
            Text(reader, 5),
            reader.IsDBNull(6) ? null : Convert.ToDouble(reader.GetValue(6)));
        await reader.DisposeAsync();
        return doc;
    }

    /// <summary>Legacy over-apply guard, verbatim predicate: fails when the accept
    /// quantity is already below the finished quantity (NULL-safe three-valued
    /// logic identical to the procedure).</summary>
    internal static string BuildOverApplyGuard() =>
        $"SELECT TOP 1 1 FROM dbo.{ServiceEffectSql.Q("MOU_ACCEPT_M")} "
        + $"WHERE {ServiceEffectSql.Q("ACCEPT_TYPE")} = @at AND {ServiceEffectSql.Q("ACCEPT_NO")} = @an "
        + $"AND {ServiceEffectSql.Q("QTY")} < {ServiceEffectSql.Q("FINISHED_QTY")}";

    private static async Task CheckOverApplyAsync(
        ServiceEffectContext context,
        IReadOnlyList<EffectSqlParameter> parameters,
        CancellationToken token)
    {
        await using var command = new SqlCommand(BuildOverApplyGuard(), context.Connection, context.Transaction);
        foreach (var parameter in parameters)
            command.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);
        if (await command.ExecuteScalarAsync(token) is not null)
            throw new EffectValidationException("申请数量已超过承认单可申请数量");
    }

    internal static IReadOnlyList<string> BuildApproveStatements(
        MouldBatchApplySpec spec, string sort, List<EffectSqlParameter> parameters)
    {
        var q = ServiceEffectSql.Q;
        parameters.Add(new EffectSqlParameter("@st", sort));
        if (string.Compare(sort, "2", StringComparison.Ordinal) <= 0)
        {
            return new[]
            {
                $"UPDATE dbo.{q("MOU_ACCEPT_M")} SET {q(spec.AcceptStateField)} = @st, "
                    + $"{q(spec.AcceptFinishedField)} = ISNULL({q(spec.AcceptFinishedField)}, 0) + @qty "
                    + $"WHERE {q("ACCEPT_TYPE")} = @at AND {q("ACCEPT_NO")} = @an",
                $"UPDATE P SET {q(spec.ProductLengthField)} = a.{q("LIAOCHANG")}, "
                    + $"{q(spec.ProductWidthField)} = P.{q(spec.ProductWidthField)} + ISNULL(a.{q("QTY")}, 0) "
                    + $"FROM dbo.{q("PRODUCT")} P JOIN dbo.{q("MOU_BATCH_M")} a ON P.{q("PRO_NO")} = a.{q("PRO_NO")} "
                    + $"WHERE a.{q("BATCH_TYPE")} = @bt AND a.{q("BATCH_NO")} = @bn",
            };
        }
        return new[]
        {
            $"UPDATE dbo.{q(spec.ScrapTarget)} SET {q("BATCH_STATE")} = 1 "
                + $"WHERE {q("SCRAP_TYPE")} = @sct AND {q("SCRAP_NO")} = @scn AND {q("SERIAL_NO")} = @ssn",
        };
    }

    internal static IReadOnlyList<string> BuildDeapproveStatements(
        MouldBatchApplySpec spec, string sort, List<EffectSqlParameter> parameters)
    {
        var q = ServiceEffectSql.Q;
        if (sort == "1")
        {
            parameters.Add(new EffectSqlParameter("@st", "0"));
            return new[]
            {
                $"UPDATE dbo.{q("MOU_ACCEPT_M")} SET {q(spec.AcceptStateField)} = @st, "
                    + $"{q(spec.AcceptFinishedField)} = ISNULL({q(spec.AcceptFinishedField)}, 0) - @qty "
                    + $"WHERE {q("ACCEPT_TYPE")} = @at AND {q("ACCEPT_NO")} = @an",
                $"UPDATE P SET {q(spec.ProductLengthField)} = a.{q("LIAOCHANG")}, "
                    + $"{q(spec.ProductWidthField)} = P.{q(spec.ProductWidthField)} - ISNULL(a.{q("QTY")}, 0) "
                    + $"FROM dbo.{q("PRODUCT")} P JOIN dbo.{q("MOU_BATCH_M")} a ON P.{q("PRO_NO")} = a.{q("PRO_NO")} "
                    + $"WHERE a.{q("BATCH_TYPE")} = @bt AND a.{q("BATCH_NO")} = @bn",
            };
        }
        if (sort == "2")
        {
            parameters.Add(new EffectSqlParameter("@st", "1"));
            return new[]
            {
                $"UPDATE dbo.{q("MOU_ACCEPT_M")} SET {q(spec.AcceptStateField)} = @st, "
                    + $"{q(spec.AcceptFinishedField)} = ISNULL({q(spec.AcceptFinishedField)}, 0) - @qty "
                    + $"WHERE {q("ACCEPT_TYPE")} = @at AND {q("ACCEPT_NO")} = @an",
            };
        }
        return new[]
        {
            $"UPDATE dbo.{q(spec.ScrapTarget)} SET {q("BATCH_STATE")} = 0 "
                + $"WHERE {q("SCRAP_TYPE")} = @sct AND {q("SCRAP_NO")} = @scn AND {q("SERIAL_NO")} = @ssn",
        };
    }

    private static string ReverseKind(ServiceEffectContext context)
    {
        if (context.Action.Reverse is not { } reverse
            || reverse.ValueKind != JsonValueKind.Object
            || !reverse.TryGetProperty("kind", out var kind)
            || kind.ValueKind != JsonValueKind.String)
            throw new EffectConfigException("mould-batch-apply 解批缺少 reverse.kind，禁止无守卫执行。");
        return kind.GetString()!;
    }
}

internal sealed record MouldBatchDocument(
    string? AcceptType,
    string? AcceptNo,
    string? ScrapType,
    string? ScrapNo,
    short? ScrapSerialNo,
    string? BatchSort,
    double? Qty);

/// <summary>Parsed mould-batch-apply configuration with closed shapes.</summary>
internal sealed record MouldBatchApplySpec(
    string AcceptStateField,
    string AcceptFinishedField,
    string ProductLengthField,
    string ProductWidthField,
    string ScrapTarget)
{
    public static MouldBatchApplySpec Parse(JsonElement root, ModuleEffectPlan plan, ISet<string> columns)
    {
        if (root.ValueKind != JsonValueKind.Object)
            throw new EffectConfigException("mould-batch-apply 参数必须是 JSON 对象。");
        var branch = Req(root, "branchField");
        if (!branch.Equals("BATCH_SORT", StringComparison.OrdinalIgnoreCase))
            throw new EffectConfigException($"mould-batch-apply.branchField 仅支持 BATCH_SORT（当前 '{branch}'）。");
        if (!root.TryGetProperty("acceptTargets", out var accept) || accept.ValueKind != JsonValueKind.Object)
            throw new EffectConfigException("mould-batch-apply.acceptTargets 必须是对象。");
        var stateField = accept.TryGetProperty("stateField", out var state) && state.ValueKind == JsonValueKind.String
            ? state.GetString()!.Trim()
            : throw new EffectConfigException("mould-batch-apply.acceptTargets.stateField 缺失。");
        var finishedField = accept.TryGetProperty("finishedField", out var finished) && finished.ValueKind == JsonValueKind.String
            ? finished.GetString()!.Trim()
            : throw new EffectConfigException("mould-batch-apply.acceptTargets.finishedField 缺失。");
        if (!stateField.Equals("BATCH_STATE", StringComparison.OrdinalIgnoreCase)
            || !finishedField.Equals("FINISHED_QTY", StringComparison.OrdinalIgnoreCase))
            throw new EffectConfigException("mould-batch-apply.acceptTargets 仅支持 {stateField:BATCH_STATE, finishedField:FINISHED_QTY}。");
        if (!root.TryGetProperty("productFields", out var products) || products.ValueKind != JsonValueKind.Array)
            throw new EffectConfigException("mould-batch-apply.productFields 必须是数组。");
        var productList = products.EnumerateArray()
            .Select(item => item.ValueKind == JsonValueKind.String ? item.GetString()!.Trim() : string.Empty)
            .ToList();
        if (productList.Count != 2
            || !productList.Contains("P_LENGTH", StringComparer.OrdinalIgnoreCase)
            || !productList.Contains("P_WIDTH", StringComparer.OrdinalIgnoreCase))
            throw new EffectConfigException("mould-batch-apply.productFields 必须为 [P_LENGTH, P_WIDTH]。");
        var scrap = Req(root, "scrapTarget");
        if (!scrap.Equals("MOU_SCRAP_D", StringComparison.OrdinalIgnoreCase))
            throw new EffectConfigException($"mould-batch-apply.scrapTarget 仅支持 MOU_SCRAP_D（当前 '{scrap}'）。");
        if (plan.MasterTable is null || !plan.MasterTable.Equals("MOU_BATCH_M", StringComparison.OrdinalIgnoreCase))
            throw new EffectConfigException("mould-batch-apply 仅支持 MOU_BATCH_M 主表形态。");
        if (plan.MasterPkOrder.Count < 2)
            throw new EffectConfigException("mould-batch-apply 缺少主表主键序。");
        foreach (var reference in new[]
        {
            "MOU_BATCH_M", "MOU_BATCH_M.BATCH_TYPE", "MOU_BATCH_M.BATCH_NO",
            "MOU_BATCH_M.ACCEPT_TYPE", "MOU_BATCH_M.ACCEPT_NO",
            "MOU_BATCH_M.SCRAP_TYPE", "MOU_BATCH_M.SCRAP_NO", "MOU_BATCH_M.SCRAP_SERIAL_NO",
            "MOU_BATCH_M.BATCH_SORT", "MOU_BATCH_M.QTY", "MOU_BATCH_M.PRO_NO", "MOU_BATCH_M.LIAOCHANG",
            "MOU_ACCEPT_M", "MOU_ACCEPT_M.ACCEPT_TYPE", "MOU_ACCEPT_M.ACCEPT_NO",
            "MOU_ACCEPT_M.BATCH_STATE", "MOU_ACCEPT_M.FINISHED_QTY", "MOU_ACCEPT_M.QTY",
            "MOU_SCRAP_D", "MOU_SCRAP_D.SCRAP_TYPE", "MOU_SCRAP_D.SCRAP_NO",
            "MOU_SCRAP_D.SERIAL_NO", "MOU_SCRAP_D.BATCH_STATE",
            "PRODUCT", "PRODUCT.PRO_NO", "PRODUCT.P_LENGTH", "PRODUCT.P_WIDTH",
        })
        {
            if (!columns.Contains(reference))
                throw new EffectConfigException($"mould-batch-apply 列不存在：{reference}。");
        }
        return new MouldBatchApplySpec(stateField, finishedField, "P_LENGTH", "P_WIDTH", scrap);
    }

    private static string Req(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()!.Trim()
            : throw new EffectConfigException($"mould-batch-apply 缺少字符串字段 '{name}'。");
}
