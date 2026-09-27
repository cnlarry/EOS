using System.Data;
using EOS.API.Data.Effects;
using EOS.API.Data.Effects.ServiceEffectHandlers;
using EOS.API.Models;
using Microsoft.Data.SqlClient;

namespace EOS.API.Data.DocumentActions.Handlers;

// `produce-calc-materials`（计算用料）：用户在制令单上点一下，按工单 BOM 把本单的用料明细、
// 订单待办汇总与申购应购量重算一遍。
//
// 与既有实现同一件事（`btnCalc` → `P_PRODUCE_CALC`），四处有意不同：
//   ① BOM 爆炸不调已退役的 `P_BOM_LIST`：它当时是"单层叶子汇总"（`@step=1, @mode=3` 落到
//      直接子件按料号分组），现代实现是同一语义的直查；版次取最新（与 BOM 展开页同口径），
//      `BASE_QTY = 0` 的 BOM 行直接拒绝而不是除零炸事务；
//   ② 新增用料行的序号取"已用最大号 + 1"：既有实现从 0 起排，可能撞上既有行的序号（主键冲突），
//      且与"项次是行身份、不复用空洞"的口径冲突；
//   ③ 订单为空时跳过待办与申购段：既有实现拿空单别/单号去清零 `COP_ORDER_MORE`，会误伤别的单据
//      留下的空键行——空键本就不该参与汇总；
//   ④ 已完工结案（`FINISHED_TAG = 1`）的制令单拒绝：结案后的用料是追溯凭据，既有实现没有这道门。
// 不要求已批核：算料的正常时机在批核之前（先算清用料再送审）。
internal sealed class ProduceCalcMaterialsHandler : IDocumentUserAction, IDocumentActionPlacement
{
    public const string ActionKey = "produce-calc-materials";

    private const string MasterTable = "MOC_PRODUCE_M";
    private const string DetailTable = "MOC_PRODUCE_D";
    private const string BomTable = "BOM_STRU_D";
    private const string OrderDetailTable = "COP_ORDER_D";
    private const string OrderMoreTable = "COP_ORDER_MORE";
    private const string ApplyDetailTable = "PUR_APPLY_D";
    private const string TypeField = "PRODUCE_TYPE";
    private const string NoField = "PRODUCE_NO";
    private const string SerialField = "SERIAL_NO";
    private const string ProField = "PRO_NO";
    private const string QtyField = "QTY";
    private const string SpareQtyField = "SPARE_QTY";
    private const string NeedQtyField = "NEED_QTY";
    private const string UsedQtyField = "USED_QTY";
    private const string ApplyQtyField = "APPLY_QTY";
    private const string PurchaseQtyField = "PURCHASE_QTY";
    private const string ReceiveQtyField = "RECEIVE_QTY";
    private const string LostQtyField = "LOST_QTY";
    private const string ProQtyField = "PRO_QTY";
    private const string ComponentQtyField = "COMPONENT_QTY";
    private const string LostRateField = "LOST_RATE";
    private const string ElementQtyField = "ELEMENT_QTY";
    private const string BaseQtyField = "BASE_QTY";
    private const string ElementProField = "ELEMENT_PRO_NO";
    private const string OrderTypeField = "ORDER_TYPE";
    private const string OrderNoField = "ORDER_NO";
    private const string OrderSerialField = "ORDER_SERIAL_NO";
    private const string FinishedField = "FINISHED_TAG";

    public string Key => ActionKey;

    public string Label => "计算用料";

    /// <summary>算的是明细用料，按钮落在子表标题栏。</summary>
    public string Placement => DocumentActionPlacements.Detail;

    public async Task<DocumentActionResult> ExecuteAsync(DocumentActionContext context, CancellationToken token)
    {
        var definition = context.Definition;
        if (definition.DetailTable is null)
        {
            throw new InvalidOperationException("该模块没有明细表，无法计算用料。");
        }

        var q = ServiceEffectSql.Q;
        var columns = await new EffectPhysicalColumns().LoadAsync(context.Connection, token, context.Transaction);
        foreach (var (table, column) in new[]
                 {
                     (MasterTable, TypeField), (MasterTable, NoField),
                     (MasterTable, ProField), (MasterTable, QtyField), (MasterTable, SpareQtyField),
                     (MasterTable, OrderTypeField), (MasterTable, OrderNoField), (MasterTable, OrderSerialField),
                     (MasterTable, FinishedField),
                     (DetailTable, TypeField), (DetailTable, NoField),
                     (DetailTable, SerialField), (DetailTable, ProField),
                     (DetailTable, NeedQtyField), (DetailTable, UsedQtyField),
                     (DetailTable, ApplyQtyField), (DetailTable, PurchaseQtyField),
                     (DetailTable, ProQtyField), (DetailTable, ComponentQtyField), (DetailTable, LostRateField),
                     (DetailTable, OrderTypeField), (DetailTable, OrderNoField), (DetailTable, OrderSerialField),
                     (BomTable, ProField), (BomTable, ElementProField),
                     (BomTable, ElementQtyField), (BomTable, BaseQtyField), (BomTable, LostRateField),
                     (BomTable, "EDITION"),
                     (OrderDetailTable, OrderTypeField), (OrderDetailTable, OrderNoField),
                     (OrderDetailTable, SerialField), (OrderDetailTable, TypeField), (OrderDetailTable, NoField),
                     (OrderMoreTable, OrderTypeField), (OrderMoreTable, OrderNoField), (OrderMoreTable, ProField),
                     (OrderMoreTable, NeedQtyField), (OrderMoreTable, ApplyQtyField), (OrderMoreTable, UsedQtyField),
                     (OrderMoreTable, PurchaseQtyField), (OrderMoreTable, ReceiveQtyField), (OrderMoreTable, LostQtyField),
                     (ApplyDetailTable, OrderTypeField), (ApplyDetailTable, OrderNoField),
                     (ApplyDetailTable, ProField), (ApplyDetailTable, "REQUIRE_QTY"),
                 })
        {
            if (!columns.Contains(table + "." + column))
            {
                throw new InvalidOperationException($"计算用料需要 {table}.{column}，相关表没有这一列，请联系管理员调整。");
            }
        }

        var type = context.KeyValues.Count > 0 ? context.KeyValues[0].Trim() : string.Empty;
        var no = context.KeyValues.Count > 1 ? context.KeyValues[1].Trim() : string.Empty;
        if (type.Length == 0 || no.Length == 0)
        {
            throw new InvalidOperationException("缺少单据主键，禁止无条件计算用料。");
        }

        var master = await ReadMasterAsync(context, type, no, token);
        if (master.Finished)
        {
            throw new InvalidOperationException("该制令单已完工结案，用料是追溯凭据，不能重新计算。");
        }

        // 探路与执行走同一事务：下面五步都在调用方事务里，
        // 框架"执行后回滚"即可兜住探路，处理器不必自行分支。
        var explosion = await ExplodeBomAsync(context, master.ProductNo, token);
        await TouchMasterAsync(context, type, no, token);
        await StampOrderAsync(context, type, no, master, token);
        var detailStats = await SyncDetailsAsync(context, type, no, master, explosion, token);
        var moreStats = (0, 0);
        if (master.OrderType.Length > 0 && master.OrderNo.Length > 0)
        {
            moreStats = await SyncOrderMoreAsync(context, master, token);
            await SyncApplyAsync(context, master, token);
        }

        return new DocumentActionResult(DocumentActionOutcome.Refreshed,
            $"已计算用料：BOM 展开 {explosion.Count} 种料，明细补 {detailStats.Inserted} 行、重算 {detailStats.Updated} 行"
            + (master.OrderType.Length > 0
                ? $"；订单待办补 {moreStats.Item1} 行、重算 {moreStats.Item2} 行，申购应购量已同步"
                : "；本单无订单引用，待办与申购段已跳过")
            + "。");
    }

    private sealed record ProduceMaster(
        string ProductNo, double Qty, string OrderType, string OrderNo, int OrderSerial, bool Finished);

    private static async Task<ProduceMaster> ReadMasterAsync(
        DocumentActionContext context, string type, string no, CancellationToken token)
    {
        var q = ServiceEffectSql.Q;
        await using var command = new SqlCommand(
            $"SELECT ISNULL({q(ProField)}, N''), ISNULL({q(QtyField)}, 0)+ISNULL({q(SpareQtyField)}, 0), "
            + $"ISNULL({q(OrderTypeField)}, N''), ISNULL({q(OrderNoField)}, N''), "
            + $"ISNULL({q(OrderSerialField)}, 0), ISNULL({q(FinishedField)}, 0) "
            + $"FROM dbo.{q(MasterTable)} WHERE {q(TypeField)}=@type AND {q(NoField)}=@no;",
            context.Connection, context.Transaction);
        command.Parameters.Add("@type", SqlDbType.NVarChar, 20).Value = type;
        command.Parameters.Add("@no", SqlDbType.NVarChar, 40).Value = no;
        await using var reader = await command.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token))
        {
            throw new InvalidOperationException($"找不到制令单 {type}/{no}。");
        }
        return new ProduceMaster(reader.GetString(0).Trim(), Convert.ToDouble(reader.GetValue(1)),
            reader.GetString(2).Trim(), reader.GetString(3).Trim(),
            Convert.ToInt32(reader.GetValue(4)), Convert.ToBoolean(reader.GetValue(5)));
    }

    private sealed record BomElement(string ElementProNo, double ElementQty, double LostRate, double NeedTotal);

    /// <summary>
    /// 单层叶子汇总（旧 `P_BOM_LIST @pro,1,3` 的同语义直查）：直接子件按料号分组，
    /// 用量按 `ELEMENT_QTY/BASE_QTY`、损耗按 `MAX(LOST_RATE)`。版次取最新（与 BOM 展开页同口径）。
    /// `BASE_QTY = 0` 的 BOM 行直接拒绝——既有实现在这里除零炸事务，同样 fail-closed，但要给出料号。
    /// </summary>
    private static async Task<IReadOnlyList<BomElement>> ExplodeBomAsync(
        DocumentActionContext context, string productNo, CancellationToken token)
    {
        var q = ServiceEffectSql.Q;
        var latestEdition =
            $"(SELECT MAX({q("EDITION")}) FROM dbo.{q(BomTable)} WHERE LTRIM(RTRIM({q(ProField)}))=@pro)";
        await using var guard = new SqlCommand(
            $"SELECT TOP 1 LTRIM(RTRIM({q(ElementProField)})) FROM dbo.{q(BomTable)} "
            + $"WHERE LTRIM(RTRIM({q(ProField)}))=@pro AND {q("EDITION")}={latestEdition} "
            + $"AND ISNULL({q(BaseQtyField)}, 0)=0;",
            context.Connection, context.Transaction);
        guard.Parameters.Add("@pro", SqlDbType.NVarChar, 60).Value = productNo;
        var bad = await guard.ExecuteScalarAsync(token);
        if (bad is not null && bad is not DBNull)
        {
            throw new InvalidOperationException($"BOM 有底数为 0 的行（料号 {((string)bad).Trim()}），无法计算用料，请先修正 BOM。");
        }

        await using var command = new SqlCommand(
            $"SELECT LTRIM(RTRIM({q(ElementProField)})), "
            + $"SUM(ISNULL({q(ElementQtyField)}, 0)/{q(BaseQtyField)}), MAX(ISNULL({q(LostRateField)}, 0)), "
            + $"SUM(ISNULL({q(ElementQtyField)}, 0)/{q(BaseQtyField)}*(1+ISNULL({q(LostRateField)}, 0)/100)) "
            + $"FROM dbo.{q(BomTable)} WHERE LTRIM(RTRIM({q(ProField)}))=@pro AND {q("EDITION")}={latestEdition} "
            + $"GROUP BY LTRIM(RTRIM({q(ElementProField)}));",
            context.Connection, context.Transaction);
        command.Parameters.Add("@pro", SqlDbType.NVarChar, 60).Value = productNo;
        var rows = new List<BomElement>();
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            rows.Add(new BomElement(reader.GetString(0),
                Convert.ToDouble(reader.GetValue(1)), Convert.ToDouble(reader.GetValue(2)),
                Convert.ToDouble(reader.GetValue(3))));
        }
        return rows;
    }

    private static async Task TouchMasterAsync(
        DocumentActionContext context, string type, string no, CancellationToken token)
    {
        var q = ServiceEffectSql.Q;
        await using var command = new SqlCommand(
            $"UPDATE dbo.{q(MasterTable)} SET {q("LAST_UPDATE_DATE")}=SYSDATETIME() "
            + $"WHERE {q(TypeField)}=@type AND {q(NoField)}=@no;",
            context.Connection, context.Transaction);
        command.Parameters.Add("@type", SqlDbType.NVarChar, 20).Value = type;
        command.Parameters.Add("@no", SqlDbType.NVarChar, 40).Value = no;
        await command.ExecuteNonQueryAsync(token);
    }

    /// <summary>订单明细回写本单制令号（既有实现与 link-stamp 同形：按订单三键定位目标行）。</summary>
    private static async Task StampOrderAsync(
        DocumentActionContext context, string type, string no, ProduceMaster master, CancellationToken token)
    {
        if (master.OrderType.Length == 0 || master.OrderNo.Length == 0)
        {
            return;
        }
        var q = ServiceEffectSql.Q;
        await using var command = new SqlCommand(
            $"UPDATE dbo.{q(OrderDetailTable)} SET {q(TypeField)}=@type, {q(NoField)}=@no "
            + $"FROM dbo.{q(OrderDetailTable)} o JOIN dbo.{q(MasterTable)} d "
            + $"ON o.{q(OrderTypeField)}=d.{q(OrderTypeField)} AND o.{q(OrderNoField)}=d.{q(OrderNoField)} "
            + $"AND o.{q(SerialField)}=d.{q(OrderSerialField)} "
            + $"WHERE d.{q(TypeField)}=@type AND d.{q(NoField)}=@no;",
            context.Connection, context.Transaction);
        command.Parameters.Add("@type", SqlDbType.NVarChar, 20).Value = type;
        command.Parameters.Add("@no", SqlDbType.NVarChar, 40).Value = no;
        await command.ExecuteNonQueryAsync(token);
    }

    /// <summary>
    /// 明细同步：需求/产量清零 → 缺的料号按"已用最大号 + 1"补行 → 逐料重算需求/产量/单位用量/损耗率 →
    /// 删"需求/已用/申购/采购全零"的行（既有实现逐字如此；序号不复用空洞，与行身份口径一致）。
    /// </summary>
    private static async Task<(int Inserted, int Updated)> SyncDetailsAsync(
        DocumentActionContext context, string type, string no, ProduceMaster master,
        IReadOnlyList<BomElement> explosion, CancellationToken token)
    {
        var q = ServiceEffectSql.Q;
        await using (var zero = new SqlCommand(
            $"UPDATE dbo.{q(DetailTable)} SET {q(NeedQtyField)}=0, {q(ProQtyField)}=0 "
            + $"WHERE {q(TypeField)}=@type AND {q(NoField)}=@no;",
            context.Connection, context.Transaction))
        {
            zero.Parameters.Add("@type", SqlDbType.NVarChar, 20).Value = type;
            zero.Parameters.Add("@no", SqlDbType.NVarChar, 40).Value = no;
            await zero.ExecuteNonQueryAsync(token);
        }

        var nextSerial = await MaxSerialAsync(context, type, no, token) + 1;
        var inserted = 0;
        foreach (var element in explosion)
        {
            await using var command = new SqlCommand(
                $"IF NOT EXISTS (SELECT 1 FROM dbo.{q(DetailTable)} "
                + $"WHERE {q(TypeField)}=@type AND {q(NoField)}=@no "
                + $"AND LTRIM(RTRIM({q(ProField)}))=@pro) "
                + $"INSERT INTO dbo.{q(DetailTable)} ({q(TypeField)}, {q(NoField)}, {q(SerialField)}, "
                + $"{q(ProField)}, {q(NeedQtyField)}, {q(ProQtyField)}, {q(ComponentQtyField)}, "
                + $"{q(LostRateField)}, {q(OrderTypeField)}, {q(OrderNoField)}, {q(OrderSerialField)}, "
                + $"{q(ApplyQtyField)}, {q(PurchaseQtyField)}) "
                + $"VALUES (@type, @no, @serial, @pro, 0, 0, @component, @lost, "
                + $"@orderType, @orderNo, @orderSerial, 0, 0);",
                context.Connection, context.Transaction);
            command.Parameters.Add("@type", SqlDbType.NVarChar, 20).Value = type;
            command.Parameters.Add("@no", SqlDbType.NVarChar, 40).Value = no;
            command.Parameters.Add("@serial", SqlDbType.Int).Value = nextSerial;
            command.Parameters.Add("@pro", SqlDbType.NVarChar, 60).Value = element.ElementProNo;
            command.Parameters.Add("@component", SqlDbType.Float).Value = element.ElementQty;
            command.Parameters.Add("@lost", SqlDbType.Float).Value = element.LostRate;
            command.Parameters.Add("@orderType", SqlDbType.NVarChar, 20).Value = master.OrderType;
            command.Parameters.Add("@orderNo", SqlDbType.NVarChar, 40).Value = master.OrderNo;
            command.Parameters.Add("@orderSerial", SqlDbType.Int).Value = master.OrderSerial;
            if (await command.ExecuteNonQueryAsync(token) > 0)
            {
                inserted++;
                nextSerial++;
            }
        }

        await using var update = new SqlCommand(
            $"UPDATE d SET d.{q(NeedQtyField)}=x.Need, d.{q(ProQtyField)}=@qty, "
            + $"d.{q(ComponentQtyField)}=x.Component, d.{q(LostRateField)}=x.Lost "
            + $"FROM dbo.{q(DetailTable)} d "
            + $"JOIN (VALUES {string.Join(", ", explosion.Select((_, index) => $"(@p{index}, @n{index}, @c{index}, @l{index})"))}"
            + $") x(P, Need, Component, Lost) ON x.P=LTRIM(RTRIM(d.{q(ProField)})) "
            + $"WHERE d.{q(TypeField)}=@type AND d.{q(NoField)}=@no;",
            context.Connection, context.Transaction);
        // 空爆炸（BOM 无子件）时 VALUES 表为空：跳过重算，直接进删除段。
        var updated = 0;
        if (explosion.Count > 0)
        {
            update.Parameters.Add("@type", SqlDbType.NVarChar, 20).Value = type;
            update.Parameters.Add("@no", SqlDbType.NVarChar, 40).Value = no;
            update.Parameters.Add("@qty", SqlDbType.Float).Value = master.Qty;
            for (var index = 0; index < explosion.Count; index++)
            {
                update.Parameters.Add($"@p{index}", SqlDbType.NVarChar, 60).Value = explosion[index].ElementProNo;
                update.Parameters.Add($"@n{index}", SqlDbType.Float).Value = explosion[index].NeedTotal * master.Qty;
                update.Parameters.Add($"@c{index}", SqlDbType.Float).Value = explosion[index].ElementQty;
                update.Parameters.Add($"@l{index}", SqlDbType.Float).Value = explosion[index].LostRate;
            }
            updated = await update.ExecuteNonQueryAsync(token);
        }

        await using var delete = new SqlCommand(
            $"DELETE FROM dbo.{q(DetailTable)} WHERE {q(TypeField)}=@type AND {q(NoField)}=@no "
            + $"AND ISNULL({q(NeedQtyField)}, 0)+ISNULL({q(UsedQtyField)}, 0)"
            + $"+ISNULL({q(ApplyQtyField)}, 0)+ISNULL({q(PurchaseQtyField)}, 0)=0;",
            context.Connection, context.Transaction);
        delete.Parameters.Add("@type", SqlDbType.NVarChar, 20).Value = type;
        delete.Parameters.Add("@no", SqlDbType.NVarChar, 40).Value = no;
        await delete.ExecuteNonQueryAsync(token);

        return (inserted, updated);
    }

    private static async Task<int> MaxSerialAsync(
        DocumentActionContext context, string type, string no, CancellationToken token)
    {
        var q = ServiceEffectSql.Q;
        await using var command = new SqlCommand(
            $"SELECT ISNULL(MAX({q(SerialField)}), 0) FROM dbo.{q(DetailTable)} "
            + $"WHERE {q(TypeField)}=@type AND {q(NoField)}=@no;",
            context.Connection, context.Transaction);
        command.Parameters.Add("@type", SqlDbType.NVarChar, 20).Value = type;
        command.Parameters.Add("@no", SqlDbType.NVarChar, 40).Value = no;
        return Convert.ToInt32(await command.ExecuteScalarAsync(token));
    }

    /// <summary>
    /// 订单待办同步：本单引用订单的三列汇总清零 → 缺的料号补行 → 按汇总重算 →
    /// 删全零行（既有实现逐字如此；注意汇总范围是该订单的**全部**制令单，不止本单）。
    /// </summary>
    private static async Task<(int Inserted, int Updated)> SyncOrderMoreAsync(
        DocumentActionContext context, ProduceMaster master, CancellationToken token)
    {
        var q = ServiceEffectSql.Q;
        await using (var zero = new SqlCommand(
            $"UPDATE dbo.{q(OrderMoreTable)} SET {q(NeedQtyField)}=0, {q(LostQtyField)}=0 "
            + $"WHERE {q(OrderTypeField)}=@orderType AND {q(OrderNoField)}=@orderNo;",
            context.Connection, context.Transaction))
        {
            zero.Parameters.Add("@orderType", SqlDbType.NVarChar, 20).Value = master.OrderType;
            zero.Parameters.Add("@orderNo", SqlDbType.NVarChar, 40).Value = master.OrderNo;
            await zero.ExecuteNonQueryAsync(token);
        }

        await using var insert = new SqlCommand(
            $"INSERT INTO dbo.{q(OrderMoreTable)} ({q(OrderTypeField)}, {q(OrderNoField)}, {q(ProField)}, "
            + $"{q(NeedQtyField)}, {q(ApplyQtyField)}, {q(UsedQtyField)}, {q(PurchaseQtyField)}, "
            + $"{q(ReceiveQtyField)}, {q(LostQtyField)}) "
            + $"SELECT {q(OrderTypeField)}, {q(OrderNoField)}, {q(ProField)}, "
            + $"SUM({q(NeedQtyField)}), 0, 0, 0, 0, SUM({q(NeedQtyField)}*{q(LostRateField)}/100) "
            + $"FROM dbo.{q(DetailTable)} "
            + $"WHERE {q(OrderTypeField)}=@orderType AND {q(OrderNoField)}=@orderNo "
            + $"GROUP BY {q(OrderTypeField)}, {q(OrderNoField)}, {q(ProField)} "
            + $"HAVING NOT EXISTS (SELECT 1 FROM dbo.{q(OrderMoreTable)} m "
            + $"WHERE m.{q(OrderTypeField)}=@orderType AND m.{q(OrderNoField)}=@orderNo "
            + $"AND LTRIM(RTRIM(m.{q(ProField)}))=LTRIM(RTRIM({q(ProField)})));",
            context.Connection, context.Transaction);
        // 既有实现按明细的 ORDER_* 归集（与主表引用一致时即本单范围），分组键取明细列。
        insert.Parameters.Add("@orderType", SqlDbType.NVarChar, 20).Value = master.OrderType;
        insert.Parameters.Add("@orderNo", SqlDbType.NVarChar, 40).Value = master.OrderNo;
        var inserted = await insert.ExecuteNonQueryAsync(token);

        await using var update = new SqlCommand(
            $"UPDATE dbo.{q(OrderMoreTable)} SET {q(NeedQtyField)}=x.Need, {q(LostQtyField)}=x.Lost "
            + $"FROM dbo.{q(OrderMoreTable)} m JOIN (SELECT {q(OrderTypeField)}, {q(OrderNoField)}, {q(ProField)}, "
            + $"SUM({q(NeedQtyField)}) AS Need, SUM({q(NeedQtyField)}*{q(LostRateField)}/100) AS Lost "
            + $"FROM dbo.{q(DetailTable)} WHERE {q(OrderTypeField)}=@orderType AND {q(OrderNoField)}=@orderNo "
            + $"GROUP BY {q(OrderTypeField)}, {q(OrderNoField)}, {q(ProField)}) x "
            + $"ON x.{q(OrderTypeField)}=m.{q(OrderTypeField)} AND x.{q(OrderNoField)}=m.{q(OrderNoField)} "
            + $"AND LTRIM(RTRIM(x.{q(ProField)}))=LTRIM(RTRIM(m.{q(ProField)})) "
            + $"WHERE m.{q(OrderTypeField)}=@orderType AND m.{q(OrderNoField)}=@orderNo;",
            context.Connection, context.Transaction);
        update.Parameters.Add("@orderType", SqlDbType.NVarChar, 20).Value = master.OrderType;
        update.Parameters.Add("@orderNo", SqlDbType.NVarChar, 40).Value = master.OrderNo;
        var updated = await update.ExecuteNonQueryAsync(token);

        await using var delete = new SqlCommand(
            $"DELETE FROM dbo.{q(OrderMoreTable)} WHERE {q(OrderTypeField)}=@orderType AND {q(OrderNoField)}=@orderNo "
            + $"AND ISNULL({q(NeedQtyField)}, 0)+ISNULL({q(ApplyQtyField)}, 0)+ISNULL({q(UsedQtyField)}, 0)"
            + $"+ISNULL({q(PurchaseQtyField)}, 0)+ISNULL({q(ReceiveQtyField)}, 0)=0;",
            context.Connection, context.Transaction);
        delete.Parameters.Add("@orderType", SqlDbType.NVarChar, 20).Value = master.OrderType;
        delete.Parameters.Add("@orderNo", SqlDbType.NVarChar, 40).Value = master.OrderNo;
        await delete.ExecuteNonQueryAsync(token);

        return (inserted, updated);
    }

    /// <summary>申购应购量＝待办需求量（按订单三键对齐；既有实现逐字如此）。</summary>
    private static async Task SyncApplyAsync(
        DocumentActionContext context, ProduceMaster master, CancellationToken token)
    {
        var q = ServiceEffectSql.Q;
        await using var command = new SqlCommand(
            $"UPDATE dbo.{q(ApplyDetailTable)} SET REQUIRE_QTY=m.{q(NeedQtyField)} "
            + $"FROM dbo.{q(ApplyDetailTable)} a JOIN dbo.{q(OrderMoreTable)} m "
            + $"ON a.{q(OrderTypeField)}=m.{q(OrderTypeField)} AND a.{q(OrderNoField)}=m.{q(OrderNoField)} "
            + $"AND LTRIM(RTRIM(a.{q(ProField)}))=LTRIM(RTRIM(m.{q(ProField)}));",
            context.Connection, context.Transaction);
        await command.ExecuteNonQueryAsync(token);
    }
}
