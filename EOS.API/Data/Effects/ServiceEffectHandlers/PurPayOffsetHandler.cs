using System.Data;
using System.Globalization;
using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace EOS.API.Data.Effects.ServiceEffectHandlers;

/// <summary>
/// 收/付款单共用的"冲抵汇总 + 结算额校验 + 被引用对帐单超付校验"有序链（先写后校验，与旧过程一致）：
///   ① 主表 冲抵合计 ＝ 冲抵表明细合计，结算额 ＝ 价款税合计-现金折扣-冲抵合计，并刷新最后更新日期；
///   ② 结算额为负 ⇒ 拒绝（无门控）；
///   ③ 受模块开关门控：结算额 &gt; 价款税合计-现金折扣-冲抵合计（+容差）⇒ 拒绝；
///   ④ 受同一门控：对帐单"已结算 + 本单本次结算"不得超出应结算额，命中回报
///      对帐单号/应结算额/已结算/本次结算四列（列间 7/10/10 空格，与旧实现逐字一致）。
/// **不拆成"目录校验 + 写动作"**：SAVE 期目录校验跑在效果之前，而②③依赖①刚写入的两列，
/// 拆开会让校验读到旧值；故整链在一个处理器内按旧顺序执行，命中即抛 `EffectValidationException` 阻断保存。
/// 参数闭合：四张表 + 各列名 + 门控开关列 + 三条文案 + 容差，全部校验为物理列/非空文案；单据键值只作参数传入。
/// </summary>
internal static class PrepayOffsetRunner
{
    public static async Task<int> RunAsync(ServiceEffectContext context, string label,
        JsonElement root, CancellationToken token)
    {
        if (context.ExecutionEvent is not (EffectEvent.ApproveEffect or EffectEvent.Save)) return 0;
        if (context.MasterKeyValues.Count < 2)
            throw new EffectConfigException($"{label} 缺少单据主键值。");
        if (string.IsNullOrWhiteSpace(context.Plan.MasterTable))
            throw new EffectConfigException($"{label} 需要主表形态。");
        var columns = await new EffectPhysicalColumns().LoadAsync(context.Connection, token, context.Transaction);
        var config = Parse(root, context.Plan.MasterTable, columns, label);
        var type = context.MasterKeyValues[0] ?? string.Empty;
        var no = context.MasterKeyValues[1] ?? string.Empty;
        var scope = "m." + Q(config.Master.TypeField) + "=@type AND m." + Q(config.Master.NoField) + "=@no";

        // ① 冲抵汇总（旧实现先写后校验，顺序保持一致）
        var offset = "UPDATE m SET m." + Q(config.Master.OffsetSumField) + "=(SELECT SUM(o."
            + Q(config.Offset.AmountField) + ") FROM dbo." + Q(config.Offset.Table) + " o WHERE o."
            + Q(config.Offset.TypeField) + "=@type AND o." + Q(config.Offset.NoField) + "=@no), m."
            + Q(config.Master.SettleSumField) + "=m." + Q(config.Master.AmountTaxField) + "-m."
            + Q(config.Master.RebateSumField) + "-(SELECT SUM(o." + Q(config.Offset.AmountField) + ") FROM dbo."
            + Q(config.Offset.Table) + " o WHERE o." + Q(config.Offset.TypeField) + "=@type AND o."
            + Q(config.Offset.NoField) + "=@no), m." + Q(config.Master.LastUpdateField)
            + "=GETDATE() FROM dbo." + Q(context.Plan.MasterTable) + " m WHERE " + scope + ";";
        var affected = await ServiceEffectSql.ExecAsync(context.Connection, context.Transaction, offset,
            [new EffectSqlParameter("@type", type), new EffectSqlParameter("@no", no)], token);

        // ② 结算额不能为负（无门控）
        if (await ExistsAsync(context,
            "SELECT TOP 1 1 FROM dbo." + Q(context.Plan.MasterTable) + " m WHERE " + scope + " AND m."
            + Q(config.Master.SettleSumField) + "<0;", type, no, token))
            throw new EffectValidationException(config.NegativeMessage);

        if (!await ModuleFlagOnAsync(context, config.GateFlag, token)) return affected;

        // ③ 结算额不得大于 价款税合计-现金折扣-冲抵合计（旧收款单带 0.1 容差）
        var tolerance = config.ExceedOffset == 0m
            ? string.Empty
            : "+" + config.ExceedOffset.ToString(CultureInfo.InvariantCulture);
        if (await ExistsAsync(context,
            "SELECT TOP 1 1 FROM dbo." + Q(context.Plan.MasterTable) + " m WHERE " + scope + " AND m."
            + Q(config.Master.SettleSumField) + ">m." + Q(config.Master.AmountTaxField) + "-m."
            + Q(config.Master.RebateSumField) + "-m." + Q(config.Master.OffsetSumField) + tolerance + ";",
            type, no, token))
            throw new EffectValidationException(config.ExceedMessage);

        // ④ 对帐单已结算 + 本次结算不得超出应结算额（旧实现用 7/10/10 空格分隔的四列诊断）
        var lines = new List<string>();
        await using (var command = new SqlCommand(
            "SELECT d." + Q(config.Due.NoField) + ", d." + Q(config.Due.SumAmountField) + ", d."
            + Q(config.Due.SettledAmountField) + ", s.SETTLED_SUM FROM dbo." + Q(config.Due.Table) + " d INNER JOIN (SELECT p."
            + Q(config.Detail.DueTypeField) + ", p." + Q(config.Detail.DueNoField) + ", SUM(p."
            + Q(config.Detail.SettledAmountField) + ") SETTLED_SUM FROM dbo." + Q(config.Detail.Table)
            + " p WHERE p." + Q(config.Detail.TypeField) + "=@type AND p." + Q(config.Detail.NoField)
            + "=@no GROUP BY p." + Q(config.Detail.DueTypeField) + ", p." + Q(config.Detail.DueNoField)
            + ") s ON s." + Q(config.Detail.DueTypeField) + "=d." + Q(config.Due.TypeField) + " AND s."
            + Q(config.Detail.DueNoField) + "=d." + Q(config.Due.NoField) + " WHERE d."
            + Q(config.Due.SettledAmountField) + "+s.SETTLED_SUM>d." + Q(config.Due.SumAmountField) + ";",
            context.Connection, context.Transaction))
        {
            command.Parameters.AddWithValue("@type", type);
            command.Parameters.AddWithValue("@no", no);
            await using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
            {
                lines.Add((reader.IsDBNull(0) ? string.Empty : reader.GetString(0).Trim())
                    + "       " + Num(reader, 1) + "          " + Num(reader, 2) + "          " + Num(reader, 3));
            }
        }
        if (lines.Count > 0)
            throw new EffectValidationException(config.DueMessage.Replace("{ROWS}", string.Join("\r\n", lines)));
        return affected;
    }

    private static string Q(string identifier) => ServiceEffectSql.Q(identifier);

    private static string Num(SqlDataReader reader, int index)
        => reader.IsDBNull(index)
            ? "0"
            : Convert.ToDouble(reader.GetValue(index)).ToString("0.######", CultureInfo.InvariantCulture);

    private static async Task<bool> ExistsAsync(
        ServiceEffectContext context, string sql, string type, string no, CancellationToken token)
    {
        await using var command = new SqlCommand(sql, context.Connection, context.Transaction);
        command.Parameters.AddWithValue("@type", type);
        command.Parameters.AddWithValue("@no", no);
        return await command.ExecuteScalarAsync(token) is not null;
    }

    private static async Task<bool> ModuleFlagOnAsync(
        ServiceEffectContext context, string? flag, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(flag)) return true;
        await using var command = new SqlCommand("SELECT ISNULL(" + Q(flag)
            + ",0) FROM dbo.MODULES WHERE M_IDX=@moduleId;", context.Connection, context.Transaction);
        command.Parameters.AddWithValue("@moduleId", context.Plan.ModuleId);
        var value = await command.ExecuteScalarAsync(token);
        return value is not null && Convert.ToInt32(value) != 0;
    }

    /// <summary>参数解析（fail-closed：四张表的每个列名都必须物理存在）。</summary>
    internal static PrepayOffsetConfig Parse(JsonElement root, string masterTable, ISet<string> columns, string label)
    {
        var master = Section(root, "master", label);
        var offset = Section(root, "offset", label);
        var due = Section(root, "due", label);
        var detail = Section(root, "detail", label);
        var config = new PrepayOffsetConfig(
            new PrepayOffsetMaster(Required(master, "typeField", label), Required(master, "noField", label),
                Required(master, "amountTaxField", label), Required(master, "rebateSumField", label),
                Required(master, "offsetSumField", label), Required(master, "settleSumField", label),
                Required(master, "lastUpdateField", label)),
            new PrepayOffsetTable(Required(offset, "table", label), Required(offset, "typeField", label),
                Required(offset, "noField", label), Required(offset, "amountField", label)),
            new PrepayOffsetDue(Required(due, "table", label), Required(due, "typeField", label),
                Required(due, "noField", label), Required(due, "sumAmountField", label),
                Required(due, "settledAmountField", label)),
            new PrepayOffsetDetail(Required(detail, "table", label), Required(detail, "typeField", label),
                Required(detail, "noField", label), Required(detail, "dueTypeField", label),
                Required(detail, "dueNoField", label), Required(detail, "settledAmountField", label)),
            Optional(root, "gateFlag"), Required(root, "negativeMessage", label),
            Required(root, "exceedMessage", label), Required(root, "dueMessage", label),
            root.TryGetProperty("exceedOffset", out var exceedOffset) && exceedOffset.ValueKind == JsonValueKind.Number
                && exceedOffset.TryGetDecimal(out var offsetValue) ? offsetValue : 0m);
        foreach (var (table, column) in new[]
                 {
                     (masterTable, config.Master.TypeField), (masterTable, config.Master.NoField),
                     (masterTable, config.Master.AmountTaxField), (masterTable, config.Master.RebateSumField),
                     (masterTable, config.Master.OffsetSumField), (masterTable, config.Master.SettleSumField),
                     (masterTable, config.Master.LastUpdateField),
                     (config.Offset.Table, config.Offset.TypeField), (config.Offset.Table, config.Offset.NoField),
                     (config.Offset.Table, config.Offset.AmountField),
                     (config.Due.Table, config.Due.TypeField), (config.Due.Table, config.Due.NoField),
                     (config.Due.Table, config.Due.SumAmountField), (config.Due.Table, config.Due.SettledAmountField),
                     (config.Detail.Table, config.Detail.TypeField), (config.Detail.Table, config.Detail.NoField),
                     (config.Detail.Table, config.Detail.DueTypeField), (config.Detail.Table, config.Detail.DueNoField),
                     (config.Detail.Table, config.Detail.SettledAmountField),
                 })
        {
            if (!columns.Contains(table + "." + column))
                throw new EffectConfigException($"{label} 列不存在：{table}.{column}。");
        }
        if (!string.IsNullOrWhiteSpace(config.GateFlag) && !columns.Contains("MODULES." + config.GateFlag))
            throw new EffectConfigException($"{label} 门控列不存在：MODULES.{config.GateFlag}。");
        if (!config.DueMessage.Contains("{ROWS}", StringComparison.Ordinal))
            throw new EffectConfigException($"{label} 的 dueMessage 必须包含 {{ROWS}} 占位符。");
        return config;
    }

    private static JsonElement Section(JsonElement root, string name, string label)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Object
            ? value
            : throw new EffectConfigException($"{label} 缺少对象字段 {name}。");

    private static string Required(JsonElement element, string name, string label)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(value.GetString())
                ? value.GetString()!
                : throw new EffectConfigException($"{label} 缺少字符串字段 {name}。");

    private static string? Optional(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(value.GetString())
                ? value.GetString()!.Trim()
                : null;
}

internal sealed record PrepayOffsetMaster(string TypeField, string NoField, string AmountTaxField,
    string RebateSumField, string OffsetSumField, string SettleSumField, string LastUpdateField);
internal sealed record PrepayOffsetTable(string Table, string TypeField, string NoField, string AmountField);
internal sealed record PrepayOffsetDue(string Table, string TypeField, string NoField, string SumAmountField,
    string SettledAmountField);
internal sealed record PrepayOffsetDetail(string Table, string TypeField, string NoField, string DueTypeField,
    string DueNoField, string SettledAmountField);
internal sealed record PrepayOffsetConfig(PrepayOffsetMaster Master, PrepayOffsetTable Offset, PrepayOffsetDue Due,
    PrepayOffsetDetail Detail, string? GateFlag, string NegativeMessage, string ExceedMessage, string DueMessage,
    decimal ExceedOffset);

/// <summary>付款单（170202）：冲抵表为 `PUR_PAY_PREPAY`，结算列 `PAYOUT_SUM`，被引用对帐单为应付对帐单。</summary>
public sealed class PurPayOffsetHandler : IEffectServiceHandler
{
    public string EffectKey => "pur-pay-offset";

    public Task<int> ExecuteAsync(ServiceEffectContext context, CancellationToken token)
        => PrepayOffsetRunner.RunAsync(context, "pur-pay-offset",
            context.Action.Params ?? throw new EffectConfigException("pur-pay-offset 缺少参数。"), token);
}

/// <summary>收款单（170102）：冲抵表为 `COP_RECEIPT_PREPAY`，结算列 `RECEIVE_SUM`，被引用对帐单为应收对帐单。</summary>
public sealed class CopReceiptOffsetHandler : IEffectServiceHandler
{
    public string EffectKey => "cop-receipt-offset";

    public Task<int> ExecuteAsync(ServiceEffectContext context, CancellationToken token)
        => PrepayOffsetRunner.RunAsync(context, "cop-receipt-offset",
            context.Action.Params ?? throw new EffectConfigException("cop-receipt-offset 缺少参数。"), token);
}
