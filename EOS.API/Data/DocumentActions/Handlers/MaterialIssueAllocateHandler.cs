using System.Data;
using EOS.API.Data.Effects;
using EOS.API.Data.Effects.ServiceEffectHandlers;
using EOS.API.Data.Inventory;
using EOS.API.Models;
using Microsoft.Data.SqlClient;

namespace EOS.API.Data.DocumentActions.Handlers;

// `material-issue-allocate`（按库别取料）：用户在领料单上点一下，按各行库别的当前库存重算
// 可发料数量，并把待办表的待领量按行分配一遍。
//
// 与既有实现同一件事（`btnDepotGet` → `P_MOC_GET_DEPOT`），两处有意不同：
//   ① 库存侧按（料号, 库别）汇总后取数：既有实现是两键时代的 `JOIN … ON PRO_NO AND DEPOT_ID`，
//      四键扩键后同一（料号, 库别）可能有多行（不同库位/批次），逐行 JOIN 会把其中一行当成
//      全部——改成 `SUM(QTY)` 按库别汇总。`SEND_QTY` 的语义本就是"该库别可发多少"
//      （领料明细行没有库位列），不是"指定库位有多少"，故汇总口径与语义一致；
//   ② 待办分配的游标改 C# 循环：语义逐字一致（按料号重置可用量，按序号逐行分配，
//      分完即停），行数就是单据行数，不存在游标的规模问题。
// 不要求已批核：取数定量的正常时机在批核之前（先算清能发多少再送审）。
internal sealed class MaterialIssueAllocateHandler : IDocumentUserAction, IDocumentActionPlacement
{
    public const string ActionKey = "material-issue-allocate";

    private const string MasterTable = "MOC_GET_M";
    private const string DetailTable = "MOC_GET_D";
    private const string MoreTable = "MOC_GET_MORE";
    private const string TypeField = "GET_TYPE";
    private const string NoField = "GET_NO";
    private const string SerialField = "SERIAL_NO";
    private const string ProField = "PRO_NO";
    private const string DepotField = "DEPOT_ID";
    private const string QtyField = "QTY";
    private const string SendQtyField = "SEND_QTY";
    private const string RequireQtyField = "REQUIRE_QTY";
    private const string StockQtyField = "QTY";

    public string Key => ActionKey;

    public string Label => "按库别取料";

    /// <summary>改的是明细可发数与待办分配，按钮落在子表标题栏。</summary>
    public string Placement => DocumentActionPlacements.Detail;

    public async Task<DocumentActionResult> ExecuteAsync(DocumentActionContext context, CancellationToken token)
    {
        var definition = context.Definition;
        if (definition.DetailTable is null)
        {
            throw new InvalidOperationException("该模块没有明细表，无法按库别取料。");
        }

        var q = ServiceEffectSql.Q;
        var columns = await new EffectPhysicalColumns().LoadAsync(context.Connection, token, context.Transaction);
        foreach (var (table, column) in new[]
                 {
                     (MasterTable, TypeField), (MasterTable, NoField),
                     (DetailTable, TypeField), (DetailTable, NoField),
                     (DetailTable, SerialField), (DetailTable, ProField),
                     (DetailTable, DepotField), (DetailTable, QtyField), (DetailTable, SendQtyField),
                     (MoreTable, TypeField), (MoreTable, NoField),
                     (MoreTable, SerialField), (MoreTable, ProField),
                     (MoreTable, RequireQtyField), (MoreTable, QtyField),
                 })
        {
            if (!columns.Contains(table + "." + column))
            {
                throw new InvalidOperationException($"按库别取料需要 {table}.{column}，该模块或库存表没有这一列，请联系管理员调整。");
            }
        }

        var type = context.KeyValues.Count > 0 ? context.KeyValues[0].Trim() : string.Empty;
        var no = context.KeyValues.Count > 1 ? context.KeyValues[1].Trim() : string.Empty;
        if (type.Length == 0 || no.Length == 0)
        {
            throw new InvalidOperationException("缺少单据主键，禁止无条件取料。");
        }
        if (!await DocumentExistsAsync(context, type, no, token))
        {
            throw new InvalidOperationException($"找不到领料单 {type}/{no}。");
        }

        // 探路与执行走同一事务：两步回写都在调用方事务里，
        // 框架"执行后回滚"即可兜住探路，处理器不必自行分支。
        var lines = await RecalcSendQtyAsync(context, type, no, token);
        var allocated = await AllocateMoreAsync(context, type, no, token);
        await TouchMasterAsync(context, type, no, token);

        return new DocumentActionResult(DocumentActionOutcome.Refreshed,
            $"已按库别库存取料：{lines} 行明细按库别存量重算可发数（无库存记录的行按 0 计），待办表分配 {allocated} 行。");
    }

    private static async Task<bool> DocumentExistsAsync(
        DocumentActionContext context, string type, string no, CancellationToken token)
    {
        var q = ServiceEffectSql.Q;
        await using var command = new SqlCommand(
            $"SELECT TOP 1 1 FROM dbo.{q(MasterTable)} WHERE {q(TypeField)}=@type AND {q(NoField)}=@no;",
            context.Connection, context.Transaction);
        command.Parameters.Add("@type", SqlDbType.NVarChar, 20).Value = type;
        command.Parameters.Add("@no", SqlDbType.NVarChar, 40).Value = no;
        return await command.ExecuteScalarAsync(token) is not null;
    }

    /// <summary>
    /// 第一步：SEND_QTY 归零后按库别存量重算（取 min(需求, 库别合计)）。
    /// 需求或存量任一为空即发 0（旧 `CASE WHEN` 的 NULL 语义：`NULL > x` 不成立，走 ELSE 取需求——
    /// 但需求侧 ISNULL 后恒有值，故只有存量缺失时取需求；存量 0 则取 0）。
    /// </summary>
    private static async Task<int> RecalcSendQtyAsync(
        DocumentActionContext context, string type, string no, CancellationToken token)
    {
        var q = ServiceEffectSql.Q;
        await using var zero = new SqlCommand(
            $"UPDATE dbo.{q(DetailTable)} SET {q(SendQtyField)}=0 "
            + $"WHERE {q(TypeField)}=@type AND {q(NoField)}=@no;",
            context.Connection, context.Transaction);
        zero.Parameters.Add("@type", SqlDbType.NVarChar, 20).Value = type;
        zero.Parameters.Add("@no", SqlDbType.NVarChar, 40).Value = no;
        await zero.ExecuteNonQueryAsync(token);

        // 库存侧经 InventoryQueryService 按 (料号, 库别) 聚合：四键下发合计这件事只有那里知道
        // （明细行没有库位列，`SEND_QTY` 的语义本就是"该库别可发多少"，聚合维度与语义一致）。
        var quantities = await InventoryQueryService.GetQuantitiesAsync(
            context.Connection, context.Transaction,
            new InventoryQueryService.QuantityQuery { GroupByDepot = true },
            InventoryQueryService.ReadLock.None, token);
        if (quantities.Count == 0) return 0;

        // 逐分片按 CASE 取 min(需求, 库别合计)：CASE 里的比较仍在库里做，
        // 只是把"每个 (料号, 库别) 有多少"从派生表换成参数化的 VALUES 关联。
        const int chunkSize = 200;
        var affected = 0;
        for (var offset = 0; offset < quantities.Count; offset += chunkSize)
        {
            var chunk = quantities.Skip(offset).Take(chunkSize).ToList();
            var tuples = string.Join(", ", chunk.Select((_, index) => $"(@p{index}, @d{index}, @q{index})"));
            await using var command = new SqlCommand(
                $"UPDATE d SET d.{q(SendQtyField)}="
                + $"CASE WHEN ISNULL(d.{q(QtyField)}, 0) > ISNULL(v.{q(StockQtyField)}, 0) "
                + $"THEN ISNULL(v.{q(StockQtyField)}, 0) ELSE ISNULL(d.{q(QtyField)}, 0) END "
                + $"FROM dbo.{q(DetailTable)} d "
                + $"JOIN (VALUES {tuples}) v({q(ProField)}, {q(DepotField)}, {q(StockQtyField)}) "
                + $"ON LTRIM(RTRIM(d.{q(ProField)}))=v.{q(ProField)} "
                + $"AND LTRIM(RTRIM(d.{q(DepotField)}))=v.{q(DepotField)} "
                + $"WHERE d.{q(TypeField)}=@type AND d.{q(NoField)}=@no;",
                context.Connection, context.Transaction);
            command.Parameters.Add("@type", SqlDbType.NVarChar, 20).Value = type;
            command.Parameters.Add("@no", SqlDbType.NVarChar, 40).Value = no;
            for (var index = 0; index < chunk.Count; index++)
            {
                command.Parameters.Add($"@p{index}", SqlDbType.NVarChar, 60).Value = chunk[index].ProductNo;
                command.Parameters.Add($"@d{index}", SqlDbType.NVarChar, 20).Value = chunk[index].DepotId ?? string.Empty;
                command.Parameters.Add($"@q{index}", SqlDbType.Float).Value =
                    (object?)chunk[index].Quantity ?? 0d;
            }
            affected += await command.ExecuteNonQueryAsync(token);
        }
        return affected;
    }

    /// <summary>
    /// 第二步：待办表按（料号, 序号）顺序分配。同一料号的可用量取自明细 SEND_QTY，
    /// 逐行取 min(待领, 剩余)，分完即停——与旧游标逐字一致（含"减去待领额"的写法）。
    /// </summary>
    private static async Task<int> AllocateMoreAsync(
        DocumentActionContext context, string type, string no, CancellationToken token)
    {
        var q = ServiceEffectSql.Q;
        await using var zero = new SqlCommand(
            $"UPDATE dbo.{q(MoreTable)} SET {q(QtyField)}=0 "
            + $"WHERE {q(TypeField)}=@type AND {q(NoField)}=@no;",
            context.Connection, context.Transaction);
        zero.Parameters.Add("@type", SqlDbType.NVarChar, 20).Value = type;
        zero.Parameters.Add("@no", SqlDbType.NVarChar, 40).Value = no;
        await zero.ExecuteNonQueryAsync(token);

        await using var query = new SqlCommand(
            $"SELECT {q(SerialField)}, LTRIM(RTRIM({q(ProField)})), ISNULL({q(RequireQtyField)}, 0) "
            + $"FROM dbo.{q(MoreTable)} WHERE {q(TypeField)}=@type AND {q(NoField)}=@no "
            + $"ORDER BY {q(ProField)}, {q(SerialField)};",
            context.Connection, context.Transaction);
        query.Parameters.Add("@type", SqlDbType.NVarChar, 20).Value = type;
        query.Parameters.Add("@no", SqlDbType.NVarChar, 40).Value = no;
        var jobs = new List<(int Serial, string Product, double Require)>();
        await using (var reader = await query.ExecuteReaderAsync(token))
        {
            while (await reader.ReadAsync(token))
            {
                jobs.Add((Convert.ToInt32(reader.GetValue(0)), reader.GetString(1),
                    Convert.ToDouble(reader.GetValue(2))));
            }
        }

        var allocated = 0;
        var currentProduct = string.Empty;
        var remaining = 0d;
        foreach (var job in jobs)
        {
            if (!job.Product.Equals(currentProduct, StringComparison.Ordinal))
            {
                currentProduct = job.Product;
                remaining = await SendQtyOfAsync(context, type, no, job.Product, token);
            }
            double granted;
            if (remaining > job.Require)
            {
                granted = job.Require;
                remaining -= job.Require;
            }
            else if (remaining > 0)
            {
                granted = remaining;
                remaining -= job.Require;
            }
            else
            {
                continue;
            }
            await using var update = new SqlCommand(
                $"UPDATE dbo.{q(MoreTable)} SET {q(QtyField)}=@qty "
                + $"WHERE {q(TypeField)}=@type AND {q(NoField)}=@no AND {q(SerialField)}=@serial;",
                context.Connection, context.Transaction);
            update.Parameters.Add("@qty", SqlDbType.Float).Value = granted;
            update.Parameters.Add("@type", SqlDbType.NVarChar, 20).Value = type;
            update.Parameters.Add("@no", SqlDbType.NVarChar, 40).Value = no;
            update.Parameters.Add("@serial", SqlDbType.Int).Value = job.Serial;
            await update.ExecuteNonQueryAsync(token);
            allocated++;
        }
        return allocated;
    }

    private static async Task<double> SendQtyOfAsync(
        DocumentActionContext context, string type, string no, string product, CancellationToken token)
    {
        var q = ServiceEffectSql.Q;
        await using var command = new SqlCommand(
            $"SELECT ISNULL({q(SendQtyField)}, 0) FROM dbo.{q(DetailTable)} "
            + $"WHERE {q(TypeField)}=@type AND {q(NoField)}=@no AND LTRIM(RTRIM({q(ProField)}))=@pro;",
            context.Connection, context.Transaction);
        command.Parameters.Add("@type", SqlDbType.NVarChar, 20).Value = type;
        command.Parameters.Add("@no", SqlDbType.NVarChar, 40).Value = no;
        command.Parameters.Add("@pro", SqlDbType.NVarChar, 60).Value = product;
        var value = await command.ExecuteScalarAsync(token);
        return value is null or DBNull ? 0d : Convert.ToDouble(value);
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
}
