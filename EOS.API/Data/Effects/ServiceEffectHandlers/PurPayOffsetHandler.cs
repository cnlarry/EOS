using System.Data;
using System.Globalization;
using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace EOS.API.Data.Effects.ServiceEffectHandlers;

/// <summary>
/// pur-pay-offset: 付款单保存后的"预pay冲抵汇总 + 实付校验 + 对帐单已付款校验"有序链
/// （原 `PurDomainRules.PurPayAfterSaveAsync`，来源旧过程 `P_PUR_PAY_After_Save`（受门控段调 `P_PUR_PAY_CHECK`））：
///   ① 主表 `PREPAY_SUM` ＝ 冲抵表明细合计，`PAYOUT_SUM` ＝ 应付价税合计-现金折扣-预付冲帐，并刷新最后更新日期；
///   ② `PAYOUT_SUM < 0` ⇒ 拒绝（"实付金额不能为负数"，无门控）；
///   ③ 受模块开关门控：`PAYOUT_SUM > AMOUNT_TAX-REBATE_SUM-PREPAY_SUM` ⇒ 拒绝；
///   ④ 受模块开关门控：对帐单"已付款 + 本单本次付款"不得超出应付款，命中回报
///      对帐单号/应付款/已付款/本次付款四列（列间 7/10/10 空格，与旧实现逐字一致）。
/// 顺序与旧过程一致（先写后校验），故整链放在一个处理器里：命中即以 `EffectValidationException` 阻断保存。
/// 参数闭合：四张表 + 各列名 + 门控开关列 + 三条文案，全部校验为物理列/非空文案；单据键值只作参数传入。
/// </summary>
public sealed class PurPayOffsetHandler : IEffectServiceHandler
{
    public string EffectKey => "pur-pay-offset";

    public async Task<int> ExecuteAsync(ServiceEffectContext context, CancellationToken token)
    {
        var root = context.Action.Params ?? throw new EffectConfigException("pur-pay-offset 缺少参数。");
        if (context.ExecutionEvent is not (EffectEvent.ApproveEffect or EffectEvent.Save)) return 0;
        if (context.MasterKeyValues.Count < 2)
            throw new EffectConfigException("pur-pay-offset 缺少单据主键值。");
        if (string.IsNullOrWhiteSpace(context.Plan.MasterTable))
            throw new EffectConfigException("pur-pay-offset 需要主表形态。");
        var columns = await new EffectPhysicalColumns().LoadAsync(context.Connection, token, context.Transaction);
        var config = Parse(root, context.Plan.MasterTable, columns);
        var type = context.MasterKeyValues[0] ?? string.Empty;
        var no = context.MasterKeyValues[1] ?? string.Empty;
        var scope = "m." + Q(config.Master.TypeField) + "=@type AND m." + Q(config.Master.NoField) + "=@no";

        // ① 预pay冲抵汇总（旧实现先写后校验，顺序保持一致）
        var offset = "UPDATE m SET m." + Q(config.Master.PrepaySumField) + "=(SELECT SUM(o."
            + Q(config.Offset.AmountField) + ") FROM dbo." + Q(config.Offset.Table) + " o WHERE o."
            + Q(config.Offset.TypeField) + "=@type AND o." + Q(config.Offset.NoField) + "=@no), m."
            + Q(config.Master.PayoutSumField) + "=m." + Q(config.Master.AmountTaxField) + "-m."
            + Q(config.Master.RebateSumField) + "-(SELECT SUM(o." + Q(config.Offset.AmountField) + ") FROM dbo."
            + Q(config.Offset.Table) + " o WHERE o." + Q(config.Offset.TypeField) + "=@type AND o."
            + Q(config.Offset.NoField) + "=@no), m." + Q(config.Master.LastUpdateField)
            + "=GETDATE() FROM dbo." + Q(context.Plan.MasterTable) + " m WHERE " + scope + ";";
        var affected = await ServiceEffectSql.ExecAsync(context.Connection, context.Transaction, offset,
            [new EffectSqlParameter("@type", type), new EffectSqlParameter("@no", no)], token);

        // ② 实付金额不能为负（无门控）
        var negative = await ExistsAsync(context,
            "SELECT TOP 1 1 FROM dbo." + Q(context.Plan.MasterTable) + " m WHERE " + scope + " AND m."
            + Q(config.Master.PayoutSumField) + "<0;", type, no, token);
        if (negative) throw new EffectValidationException(config.NegativeMessage);

        var gated = await ModuleFlagOnAsync(context, config.GateFlag, token);
        if (!gated) return affected;

        // ③ 实付金额不得大于 应付金额-现金折扣-预付冲帐
        var exceed = await ExistsAsync(context,
            "SELECT TOP 1 1 FROM dbo." + Q(context.Plan.MasterTable) + " m WHERE " + scope + " AND m."
            + Q(config.Master.PayoutSumField) + ">m." + Q(config.Master.AmountTaxField) + "-m."
            + Q(config.Master.RebateSumField) + "-m." + Q(config.Master.PrepaySumField) + ";", type, no, token);
        if (exceed) throw new EffectValidationException(config.ExceedMessage);

        // ④ 对帐单已付款 + 本次付款不得超出应付款（旧实现用 7/10/10 空格分隔的四列诊断）
        var lines = new List<string>();
        await using (var command = new SqlCommand(
            "SELECT d." + Q(config.Due.NoField) + ", d." + Q(config.Due.SumAmountField) + ", d."
            + Q(config.Due.PayoutAmountField) + ", s.PAYOUT_AMOUNT FROM dbo." + Q(config.Due.Table) + " d INNER JOIN (SELECT p."
            + Q(config.Detail.DueTypeField) + ", p." + Q(config.Detail.DueNoField) + ", SUM(p."
            + Q(config.Detail.PayoutAmountField) + ") PAYOUT_AMOUNT FROM dbo." + Q(config.Detail.Table)
            + " p WHERE p." + Q(config.Detail.TypeField) + "=@type AND p." + Q(config.Detail.NoField)
            + "=@no GROUP BY p." + Q(config.Detail.DueTypeField) + ", p." + Q(config.Detail.DueNoField)
            + ") s ON s." + Q(config.Detail.DueTypeField) + "=d." + Q(config.Due.TypeField) + " AND s."
            + Q(config.Detail.DueNoField) + "=d." + Q(config.Due.NoField) + " WHERE d."
            + Q(config.Due.PayoutAmountField) + "+s.PAYOUT_AMOUNT>d." + Q(config.Due.SumAmountField) + ";",
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
    internal static PurPayOffsetConfig Parse(JsonElement root, string masterTable, ISet<string> columns)
    {
        var master = Section(root, "master");
        var offset = Section(root, "offset");
        var due = Section(root, "due");
        var detail = Section(root, "detail");
        var config = new PurPayOffsetConfig(
            new PurPayOffsetMaster(Required(master, "typeField"), Required(master, "noField"),
                Required(master, "amountTaxField"), Required(master, "rebateSumField"),
                Required(master, "prepaySumField"), Required(master, "payoutSumField"),
                Required(master, "lastUpdateField")),
            new PurPayOffsetTable(Required(offset, "table"), Required(offset, "typeField"),
                Required(offset, "noField"), Required(offset, "amountField")),
            new PurPayOffsetDue(Required(due, "table"), Required(due, "typeField"), Required(due, "noField"),
                Required(due, "sumAmountField"), Required(due, "payoutAmountField")),
            new PurPayOffsetDetail(Required(detail, "table"), Required(detail, "typeField"),
                Required(detail, "noField"), Required(detail, "dueTypeField"), Required(detail, "dueNoField"),
                Required(detail, "payoutAmountField")),
            Optional(root, "gateFlag"), Required(root, "negativeMessage"), Required(root, "exceedMessage"),
            Required(root, "dueMessage"));
        foreach (var (table, column) in new[]
                 {
                     (masterTable, config.Master.TypeField), (masterTable, config.Master.NoField),
                     (masterTable, config.Master.AmountTaxField), (masterTable, config.Master.RebateSumField),
                     (masterTable, config.Master.PrepaySumField), (masterTable, config.Master.PayoutSumField),
                     (masterTable, config.Master.LastUpdateField),
                     (config.Offset.Table, config.Offset.TypeField), (config.Offset.Table, config.Offset.NoField),
                     (config.Offset.Table, config.Offset.AmountField),
                     (config.Due.Table, config.Due.TypeField), (config.Due.Table, config.Due.NoField),
                     (config.Due.Table, config.Due.SumAmountField), (config.Due.Table, config.Due.PayoutAmountField),
                     (config.Detail.Table, config.Detail.TypeField), (config.Detail.Table, config.Detail.NoField),
                     (config.Detail.Table, config.Detail.DueTypeField), (config.Detail.Table, config.Detail.DueNoField),
                     (config.Detail.Table, config.Detail.PayoutAmountField),
                 })
        {
            if (!columns.Contains(table + "." + column))
                throw new EffectConfigException($"pur-pay-offset 列不存在：{table}.{column}。");
        }
        if (!string.IsNullOrWhiteSpace(config.GateFlag) && !columns.Contains("MODULES." + config.GateFlag))
            throw new EffectConfigException($"pur-pay-offset 门控列不存在：MODULES.{config.GateFlag}。");
        if (!config.DueMessage.Contains("{ROWS}", StringComparison.Ordinal))
            throw new EffectConfigException("pur-pay-offset 的 dueMessage 必须包含 {ROWS} 占位符。");
        return config;
    }

    private static JsonElement Section(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Object
            ? value
            : throw new EffectConfigException($"pur-pay-offset 缺少对象字段 {name}。");

    private static string Required(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(value.GetString())
                ? value.GetString()!
                : throw new EffectConfigException($"pur-pay-offset 缺少字符串字段 {name}。");

    private static string? Optional(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(value.GetString())
                ? value.GetString()!.Trim()
                : null;
}

internal sealed record PurPayOffsetMaster(string TypeField, string NoField, string AmountTaxField,
    string RebateSumField, string PrepaySumField, string PayoutSumField, string LastUpdateField);
internal sealed record PurPayOffsetTable(string Table, string TypeField, string NoField, string AmountField);
internal sealed record PurPayOffsetDue(string Table, string TypeField, string NoField, string SumAmountField,
    string PayoutAmountField);
internal sealed record PurPayOffsetDetail(string Table, string TypeField, string NoField, string DueTypeField,
    string DueNoField, string PayoutAmountField);
internal sealed record PurPayOffsetConfig(PurPayOffsetMaster Master, PurPayOffsetTable Offset, PurPayOffsetDue Due,
    PurPayOffsetDetail Detail, string? GateFlag, string NegativeMessage, string ExceedMessage, string DueMessage);
