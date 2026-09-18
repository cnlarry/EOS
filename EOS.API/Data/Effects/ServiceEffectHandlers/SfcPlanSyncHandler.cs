using System.Data;
using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace EOS.API.Data.Effects.ServiceEffectHandlers;

/// <summary>
/// sfc-plan-sync: 工序生产计划保存后的"明细行补全 + 五步回填"（原
/// `SfcDomainRules.SfcPlanAfterSaveAsync` 的忠实移植）：
///   ① 取本单明细最大序号；
///   ② 从"计划待排表 ⋈ 制程明细"找出**尚未成行**的（产品，工序）组合，按
///      `SUM(待排数量 × 单位用量 × 单人时)` 算出工时并**追加明细行**（序号递增，客户取产品档案）；
///   ③ 本单明细数量清零；
///   ④ 回填 数量/生产数量/工时/单人时（工时：标准时间>0 时取标准时间，否则取上式的和）；
///   ⑤ 回填工序类别（取工序主档）；⑥ 固定时间工序（`USE_STAND_TIME=1`）数量归一为 1；
///   ⑦ 生产号追加"待排表生产号后四位"（两侧任一为空即整体为空，与既有行为一致）。
/// 参数闭合：六张表与各列名分组声明，全部校验为物理列；单据键值只作参数传入。
/// ② 需要逐行 INSERT，故在事务内读取后循环执行（与原实现一致）。
/// </summary>
public sealed class SfcPlanSyncHandler : IEffectServiceHandler
{
    public string EffectKey => "sfc-plan-sync";

    public async Task<int> ExecuteAsync(ServiceEffectContext context, CancellationToken token)
    {
        var root = context.Action.Params ?? throw new EffectConfigException("sfc-plan-sync 缺少参数。");
        if (context.ExecutionEvent is not (EffectEvent.ApproveEffect or EffectEvent.Save)) return 0;
        if (context.MasterKeyValues.Count < 2)
            throw new EffectConfigException("sfc-plan-sync 缺少单据主键值。");
        var columns = await new EffectPhysicalColumns().LoadAsync(context.Connection, token, context.Transaction);
        var config = Parse(root, columns);
        var type = context.MasterKeyValues[0] ?? string.Empty;
        var no = context.MasterKeyValues[1] ?? string.Empty;
        var affected = 0;

        var maxSerial = await ScalarAsync(context, "SELECT ISNULL(MAX(" + Q(config.Detail.SerialField) + "),0) FROM dbo."
            + Q(config.Detail.Table) + " WHERE " + Q(config.Master.TypeField) + "=@type AND "
            + Q(config.Master.NoField) + "=@no;", type, no, token);

        // ② 待补的（产品，工序）组合及其工时
        var missing = new List<(string Product, string Procedure, double Hours)>();
        var missingSql = "SELECT m." + Q(config.Detail.ProductField) + ", d." + Q(config.ProcessDetail.ProcedureField)
            + ", SUM(m." + Q(config.More.QtyField) + "*d." + Q(config.ProcessDetail.ProcessQtyField) + "*d."
            + Q(config.ProcessDetail.PersonHourUnitField) + ") AS HOURS"
            + " FROM dbo." + Q(config.More.Table) + " m INNER JOIN dbo." + Q(config.ProcessDetail.Table)
            + " d ON d." + Q(config.ProcessDetail.ProductField) + "=m." + Q(config.Detail.ProductField)
            + " WHERE m." + Q(config.Master.TypeField) + "=@type AND m." + Q(config.Master.NoField) + "=@no"
            + " AND m." + Q(config.Detail.ProductField) + "+d." + Q(config.ProcessDetail.ProcedureField)
            + " NOT IN (SELECT " + Q(config.Detail.ProductField) + "+" + Q(config.Detail.ProcedureField)
            + " FROM dbo." + Q(config.Detail.Table) + " WHERE " + Q(config.Master.TypeField) + "=@type AND "
            + Q(config.Master.NoField) + "=@no)"
            + " GROUP BY m." + Q(config.Master.TypeField) + ", m." + Q(config.Master.NoField) + ", m."
            + Q(config.Detail.ProductField) + ", d." + Q(config.ProcessDetail.ProcedureField) + ";";
        await using (var command = new SqlCommand(missingSql, context.Connection, context.Transaction))
        {
            command.Parameters.AddWithValue("@type", type);
            command.Parameters.AddWithValue("@no", no);
            await using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
                missing.Add((reader.GetValue(0)?.ToString()?.Trim() ?? string.Empty,
                    reader.GetValue(1)?.ToString()?.Trim() ?? string.Empty,
                    Convert.ToDouble(reader.GetValue(2))));
        }
        foreach (var row in missing)
        {
            maxSerial++;
            var insert = "INSERT INTO dbo." + Q(config.Detail.Table) + " (" + Q(config.Master.TypeField) + ","
                + Q(config.Master.NoField) + "," + Q(config.Detail.SerialField) + "," + Q(config.Detail.ProductField)
                + "," + Q(config.Detail.ProcedureField) + "," + Q(config.Detail.QtyField) + ","
                + Q(config.Product.ClientField) + ") SELECT @type, @no, @serial, @product, @procedure, @hours,"
                + " (SELECT p." + Q(config.Product.ClientField) + " FROM dbo." + Q(config.Product.Table) + " p WHERE p."
                + Q(config.Product.ProductField) + "=@product);";
            affected += await ServiceEffectSql.ExecAsync(context.Connection, context.Transaction, insert,
                [
                    new EffectSqlParameter("@type", type), new EffectSqlParameter("@no", no),
                    new EffectSqlParameter("@serial", maxSerial), new EffectSqlParameter("@product", row.Product),
                    new EffectSqlParameter("@procedure", row.Procedure), new EffectSqlParameter("@hours", row.Hours),
                ], token);
        }

        var scope = Q(config.Master.TypeField) + "=@type AND " + Q(config.Master.NoField) + "=@no";
        var docScope = "d." + Q(config.Master.TypeField) + "=@type AND d." + Q(config.Master.NoField) + "=@no";
        // 汇总子查询的源表同时含待排表与制程明细，且派生表带出同名列，故各自都要别名限定
        var moreScope = "m." + Q(config.Master.TypeField) + "=@type AND m." + Q(config.Master.NoField) + "=@no";
        affected += await ExecAsync(context, "UPDATE dbo." + Q(config.Detail.Table) + " SET " + Q(config.Detail.QtyField)
            + "=0 WHERE " + scope + ";", type, no, token);

        var backfill = "UPDATE d SET d." + Q(config.Detail.QtyField) + "=s.QTY_SUM, d." + Q(config.Detail.ProduceQtyField)
            + "=s.PRODUCE_SUM, d." + Q(config.Detail.HoursField) + "=s.HOURS, d." + Q(config.Detail.PersonUnitHourField)
            + "=s.PERSON_UNIT_HOUR FROM dbo." + Q(config.Detail.Table) + " d INNER JOIN (SELECT m."
            + Q(config.Master.TypeField) + ", m." + Q(config.Master.NoField) + ", m." + Q(config.Detail.ProductField)
            + ", d." + Q(config.Detail.ProcedureField) + ", d." + Q(config.ProcessDetail.StandardTimeField) + ", d."
            + Q(config.Detail.PersonUnitHourField) + ", SUM(m." + Q(config.More.ProduceQtyField) + ") PRODUCE_SUM, SUM(m."
            + Q(config.More.QtyField) + ") QTY_SUM, CASE WHEN ISNULL(d." + Q(config.ProcessDetail.StandardTimeField)
            + ",0)>0 THEN MAX(d." + Q(config.ProcessDetail.StandardTimeField) + ") ELSE SUM(m." + Q(config.More.QtyField)
            + "*d." + Q(config.ProcessDetail.ProcessQtyField) + "*d." + Q(config.ProcessDetail.PersonHourUnitField)
            + ") END HOURS FROM dbo." + Q(config.More.Table) + " m INNER JOIN dbo." + Q(config.ProcessDetail.Table)
            + " d ON d." + Q(config.ProcessDetail.ProductField) + "=m." + Q(config.Detail.ProductField) + " WHERE "
            + moreScope + " GROUP BY m." + Q(config.Master.TypeField) + ", m." + Q(config.Master.NoField) + ", m."
            + Q(config.Detail.ProductField) + ", d." + Q(config.ProcessDetail.ProcedureField) + ", d."
            + Q(config.ProcessDetail.StandardTimeField) + ", d." + Q(config.Detail.PersonUnitHourField) + ") s ON d."
            + Q(config.Master.TypeField) + "=s." + Q(config.Master.TypeField) + " AND d." + Q(config.Master.NoField)
            + "=s." + Q(config.Master.NoField) + " AND d." + Q(config.Detail.ProductField) + "=s."
            + Q(config.Detail.ProductField) + " AND d." + Q(config.Detail.ProcedureField) + "=s."
            + Q(config.Detail.ProcedureField) + " WHERE " + docScope + ";";
        affected += await ExecAsync(context, backfill, type, no, token);

        affected += await ExecAsync(context, "UPDATE d SET d." + Q(config.Detail.ProcTypeField) + "=p."
            + Q(config.Process.TypeField) + " FROM dbo." + Q(config.Detail.Table) + " d INNER JOIN dbo."
            + Q(config.Process.Table) + " p ON p." + Q(config.Process.ProcedureField) + "=d."
            + Q(config.Detail.ProcedureField) + " WHERE " + docScope + ";", type, no, token);

        affected += await ExecAsync(context, "UPDATE d SET d." + Q(config.Detail.QtyField) + "=1, d."
            + Q(config.Detail.ProduceQtyField) + "=1 FROM dbo." + Q(config.Detail.Table) + " d INNER JOIN dbo."
            + Q(config.Process.Table) + " p ON p." + Q(config.Process.ProcedureField) + "=d."
            + Q(config.Detail.ProcedureField) + " WHERE p." + Q(config.Process.UseStandTimeField) + "=1 AND "
            + docScope + ";", type, no, token);

        affected += await ExecAsync(context, "UPDATE d SET d." + Q(config.Detail.ProduceNoField) + "=RTRIM(d."
            + Q(config.Detail.ProduceNoField) + ")+RIGHT(RTRIM(m." + Q(config.More.ProduceNoField)
            + "),4) FROM dbo." + Q(config.Detail.Table) + " d INNER JOIN dbo." + Q(config.More.Table) + " m ON m."
            + Q(config.Master.TypeField) + "=d." + Q(config.Master.TypeField) + " AND m." + Q(config.Master.NoField)
            + "=d." + Q(config.Master.NoField) + " AND m." + Q(config.Detail.ProductField) + "=d."
            + Q(config.Detail.ProductField) + " WHERE " + docScope + ";", type, no, token);
        return affected;
    }

    private static string Q(string identifier) => ServiceEffectSql.Q(identifier);

    private static async Task<int> ExecAsync(
        ServiceEffectContext context, string sql, string type, string no, CancellationToken token)
        => await ServiceEffectSql.ExecAsync(context.Connection, context.Transaction, sql,
            [new EffectSqlParameter("@type", type), new EffectSqlParameter("@no", no)], token);

    private static async Task<int> ScalarAsync(
        ServiceEffectContext context, string sql, string type, string no, CancellationToken token)
    {
        await using var command = new SqlCommand(sql, context.Connection, context.Transaction);
        command.Parameters.AddWithValue("@type", type);
        command.Parameters.AddWithValue("@no", no);
        return Convert.ToInt32(await command.ExecuteScalarAsync(token) ?? 0);
    }

    /// <summary>参数解析（fail-closed：六张表的每个列名都必须物理存在）。</summary>
    internal static SfcPlanSyncConfig Parse(JsonElement root, ISet<string> columns)
    {
        var master = Section(root, "master");
        var detail = Section(root, "detail");
        var more = Section(root, "more");
        var process = Section(root, "process");
        var processDetail = Section(root, "processDetail");
        var product = Section(root, "product");
        var config = new SfcPlanSyncConfig(
            new SfcPlanMaster(Required(master, "table"), Required(master, "typeField"), Required(master, "noField")),
            new SfcPlanDetail(Required(detail, "table"), Required(detail, "productField"), Required(detail, "procedureField"),
                Required(detail, "serialField"), Required(detail, "qtyField"), Required(detail, "produceQtyField"),
                Required(detail, "hoursField"), Required(detail, "personUnitHourField"),
                Required(detail, "procTypeField"), Required(detail, "produceNoField")),
            new SfcPlanMore(Required(more, "table"), Required(more, "qtyField"), Required(more, "produceQtyField"),
                Required(more, "produceNoField")),
            new SfcPlanProcess(Required(process, "table"), Required(process, "procedureField"),
                Required(process, "typeField"), Required(process, "useStandTimeField")),
            new SfcPlanProcessDetail(Required(processDetail, "table"), Required(processDetail, "productField"),
                Required(processDetail, "procedureField"), Required(processDetail, "standardTimeField"),
                Required(processDetail, "personUnitHourField"), Required(processDetail, "processQtyField"),
                Required(processDetail, "personHourUnitField")),
            new SfcPlanProduct(Required(product, "table"), Required(product, "productField"),
                Required(product, "clientField")));
        foreach (var (table, column) in new[]
                 {
                     (config.Master.Table, config.Master.TypeField), (config.Master.Table, config.Master.NoField),
                     (config.Detail.Table, config.Master.TypeField), (config.Detail.Table, config.Master.NoField),
                     (config.Detail.Table, config.Detail.ProductField), (config.Detail.Table, config.Detail.ProcedureField),
                     (config.Detail.Table, config.Detail.SerialField), (config.Detail.Table, config.Detail.QtyField),
                     (config.Detail.Table, config.Detail.ProduceQtyField), (config.Detail.Table, config.Detail.HoursField),
                     (config.Detail.Table, config.Detail.PersonUnitHourField), (config.Detail.Table, config.Detail.ProcTypeField),
                     (config.Detail.Table, config.Detail.ProduceNoField),
                     (config.More.Table, config.Master.TypeField), (config.More.Table, config.Master.NoField),
                     (config.More.Table, config.Detail.ProductField), (config.More.Table, config.More.QtyField),
                     (config.More.Table, config.More.ProduceQtyField), (config.More.Table, config.More.ProduceNoField),
                     (config.Process.Table, config.Process.ProcedureField), (config.Process.Table, config.Process.TypeField),
                     (config.Process.Table, config.Process.UseStandTimeField),
                     (config.ProcessDetail.Table, config.ProcessDetail.ProductField),
                     (config.ProcessDetail.Table, config.ProcessDetail.ProcedureField),
                     (config.ProcessDetail.Table, config.ProcessDetail.StandardTimeField),
                     (config.ProcessDetail.Table, config.ProcessDetail.PersonUnitHourField),
                     (config.ProcessDetail.Table, config.ProcessDetail.ProcessQtyField),
                     (config.ProcessDetail.Table, config.ProcessDetail.PersonHourUnitField),
                     (config.Product.Table, config.Product.ProductField), (config.Product.Table, config.Product.ClientField),
                 })
        {
            if (!columns.Contains(table + "." + column))
                throw new EffectConfigException($"sfc-plan-sync 列不存在：{table}.{column}。");
        }
        return config;
    }

    private static JsonElement Section(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Object
            ? value
            : throw new EffectConfigException($"sfc-plan-sync 缺少对象字段 {name}。");

    private static string Required(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(value.GetString())
                ? value.GetString()!.Trim()
                : throw new EffectConfigException($"sfc-plan-sync 缺少字符串字段 {name}。");
}

internal sealed record SfcPlanMaster(string Table, string TypeField, string NoField);
internal sealed record SfcPlanDetail(string Table, string ProductField, string ProcedureField, string SerialField,
    string QtyField, string ProduceQtyField, string HoursField, string PersonUnitHourField, string ProcTypeField,
    string ProduceNoField);
internal sealed record SfcPlanMore(string Table, string QtyField, string ProduceQtyField, string ProduceNoField);
internal sealed record SfcPlanProcess(string Table, string ProcedureField, string TypeField, string UseStandTimeField);
internal sealed record SfcPlanProcessDetail(string Table, string ProductField, string ProcedureField,
    string StandardTimeField, string PersonUnitHourField, string ProcessQtyField, string PersonHourUnitField);
internal sealed record SfcPlanProduct(string Table, string ProductField, string ClientField);
internal sealed record SfcPlanSyncConfig(
    SfcPlanMaster Master, SfcPlanDetail Detail, SfcPlanMore More, SfcPlanProcess Process,
    SfcPlanProcessDetail ProcessDetail, SfcPlanProduct Product);
