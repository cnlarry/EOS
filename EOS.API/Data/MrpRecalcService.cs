using System.Data;
using EOS.API.Data.Inventory;
using Microsoft.Data.SqlClient;

namespace EOS.API.Data;

/// <summary>
/// 产品可用量 / MRP 重算（原 `P_UPDATE_PRO_MRP_ALL`，230901 作业与每次库存移动后调用）。
/// 口径：先把 `PRODUCT` 的五类占用清零，再逐类按来源单据汇总回填——
/// 库存量取 MRP 库别（`DEPOT.MRP=1`）的 `SUM(QTY)`，经 <see cref="Inventory.InventoryQueryService"/> 读取
/// （四键口径的唯一出口）；未送货取已批核未结案且未送满的订单行；
/// 未入库取已批核未完工的制令；未领料取已批核未结案的制令明细应领量；请购未采购 + 采购未收货
/// 合并进 `IN_BUY_QTY`；最后 `MRP_QTY = QTY - SAFETY_QTY - NOT_SEND_QTY - NOT_GET_QTY + NOT_IN_QTY + IN_BUY_QTY`。
/// 语句与顺序逐条对照原过程本体。**两处有意差异**：① 原过程自带 `BEGIN/COMMIT`（在调用方事务内
/// 只是嵌套计数，在事务外才自开自提），这里一律在**调用方事务**内执行——失败整链回滚，符合项目
/// "同一事务"口径；② 原过程的 `#tmp` 临时表所有用途都已被注释掉，属死代码，不再保留。
/// </summary>
public static class MrpRecalcService
{
    /// <summary>链路起点：五类占用清零（库存量的回填紧跟其后）。</summary>
    internal const string ResetSql = """
        UPDATE dbo.PRODUCT SET NOT_SEND_QTY=0, NOT_IN_QTY=0, NOT_GET_QTY=0, IN_BUY_QTY=0, MRP_QTY=0;
        """;

    /// <summary>
    /// 四类占用回填与 `MRP_QTY` 收尾；产品库存量的回填在 <see cref="ApplyStockQuantityAsync"/>，
    /// 由 <see cref="RecalcAsync"/> 插在本段之前。
    /// </summary>
    internal const string RecalcSql = """
        UPDATE dbo.PRODUCT SET NOT_SEND_QTY=ROUND(t.QTY,2)
            FROM (SELECT PRO_NO, SUM(d.QTY+d.SPARE_QTY-d.FINISHED_SEND_QTY-d.FINISHED_SPARE_QTY) QTY
                  FROM dbo.COP_ORDER_M m WITH(TABLOCKX), dbo.COP_ORDER_D d WITH(TABLOCKX)
                  WHERE m.ORDER_TYPE=d.ORDER_TYPE AND m.ORDER_NO=d.ORDER_NO AND m.CONFIRM_TAG=1 AND d.FINISHED_TAG=0
                    AND d.QTY+d.SPARE_QTY>d.FINISHED_SEND_QTY+d.FINISHED_SPARE_QTY
                  GROUP BY PRO_NO) t
            WHERE dbo.PRODUCT.PRO_NO=t.PRO_NO;
        UPDATE dbo.PRODUCT SET NOT_IN_QTY=ROUND(t.QTY,2)
            FROM (SELECT PRO_NO, SUM(QTY+SPARE_QTY-FINISHED_QTY-FINISHED_SPARE_QTY) QTY
                  FROM dbo.MOC_PRODUCE_M WITH(TABLOCKX)
                  WHERE CONFIRM_TAG=1 AND FINISHED_TAG=0 AND QTY+SPARE_QTY>FINISHED_QTY+FINISHED_SPARE_QTY
                  GROUP BY PRO_NO) t
            WHERE dbo.PRODUCT.PRO_NO=t.PRO_NO;
        UPDATE dbo.PRODUCT SET NOT_GET_QTY=ROUND(t.QTY,2)
            FROM (SELECT d.PRO_NO, SUM(d.NEED_QTY-d.USED_QTY) QTY
                  FROM dbo.MOC_PRODUCE_M m WITH(TABLOCKX), dbo.MOC_PRODUCE_D d WITH(TABLOCKX)
                  WHERE m.PRODUCE_TYPE=d.PRODUCE_TYPE AND m.PRODUCE_NO=d.PRODUCE_NO AND m.CONFIRM_TAG=1
                    AND d.FINISHED_TAG=0 AND d.NEED_QTY>d.USED_QTY
                  GROUP BY d.PRO_NO) t
            WHERE dbo.PRODUCT.PRO_NO=t.PRO_NO;
        UPDATE dbo.PRODUCT SET IN_BUY_QTY=ROUND(t.QTY,2)
            FROM (SELECT d.PRO_NO, SUM(d.QTY-d.PURCHASE_QTY) QTY
                  FROM dbo.PUR_APPLY_M m WITH(TABLOCKX), dbo.PUR_APPLY_D d WITH(TABLOCKX)
                  WHERE m.APPLY_TYPE=d.APPLY_TYPE AND m.APPLY_NO=d.APPLY_NO AND m.CONFIRM_TAG=1
                    AND d.FINISHED_TAG=0 AND d.QTY>d.PURCHASE_QTY
                  GROUP BY d.PRO_NO) t
            WHERE dbo.PRODUCT.PRO_NO=t.PRO_NO;
        UPDATE dbo.PRODUCT SET IN_BUY_QTY=IN_BUY_QTY+ROUND(t.QTY,2)
            FROM (SELECT d.PRO_NO, SUM(d.QTY+d.SPARE_QTY-d.RECEIVE_QTY-d.RECEIVE_SPARE_QTY) QTY
                  FROM dbo.PUR_PURCHASE_M m WITH(TABLOCKX), dbo.PUR_PURCHASE_D d WITH(TABLOCKX)
                  WHERE m.PURCHASE_TYPE=d.PURCHASE_TYPE AND m.PURCHASE_NO=d.PURCHASE_NO AND m.CONFIRM_TAG=1
                    AND d.FINISHED_TAG=0 AND d.QTY+d.SPARE_QTY>d.RECEIVE_QTY+d.RECEIVE_SPARE_QTY
                  GROUP BY d.PRO_NO) t
            WHERE dbo.PRODUCT.PRO_NO=t.PRO_NO;
        UPDATE dbo.PRODUCT SET MRP_QTY=ROUND(QTY-SAFETY_QTY-NOT_SEND_QTY-NOT_GET_QTY+NOT_IN_QTY+IN_BUY_QTY,2);
        """;

    private const string ProductKeyColumn = "PRO_NO";
    private const string ProductQuantityColumn = "QTY";

    /// <summary>分片写回的每组行数：一条语句包住一批产品，避免"一个产品一条语句"的往返放大。</summary>
    private const int ApplyChunkSize = 200;

    /// <summary>在调用方事务内执行重算；返回受影响行数合计（仅用于观测，不作为判据）。</summary>
    public static async Task<int> RecalcAsync(
        SqlConnection connection, SqlTransaction? transaction, CancellationToken token)
    {
        var affected = await ExecuteAsync(ResetSql, connection, transaction, token);
        affected += await ApplyStockQuantityAsync(connection, transaction, token);
        affected += await ExecuteAsync(RecalcSql, connection, transaction, token);
        return affected;
    }

    /// <summary>
    /// 产品库存量＝MRP 库别（`DEPOT.MRP=1`）的余额合计。
    ///
    /// 读侧经 <see cref="InventoryQueryService"/>：四键聚合与锁提示都在那里统一决定，
    /// 这里只负责把合计写回 `PRODUCT.QTY`。原实现是一条
    /// `UPDATE … FROM (SELECT PRO_NO, SUM(QTY) … WITH(TABLOCKX))`，改成"取回合计 + 分片写回"后：
    /// ① 排他锁仍在同一连接同一事务上取，持有到调用方事务结束 ⇒ 并发语义不变；
    /// ② 合计为 NULL（该产品在库里没有任何数量）时仍写 NULL，与原实现一致。
    /// </summary>
    private static async Task<int> ApplyStockQuantityAsync(
        SqlConnection connection, SqlTransaction? transaction, CancellationToken token)
    {
        var quantities = await InventoryQueryService.GetQuantitiesAsync(
            connection, transaction,
            new InventoryQueryService.QuantityQuery { MrpDepotsOnly = true },
            InventoryQueryService.ReadLock.ExclusiveTable, token);
        if (quantities.Count == 0) return 0;

        var affected = 0;
        for (var offset = 0; offset < quantities.Count; offset += ApplyChunkSize)
        {
            var chunk = quantities.Skip(offset).Take(ApplyChunkSize).ToList();
            var rows = string.Join(", ", chunk.Select((_, index) => $"(@p{index}, @q{index})"));
            await using var command = new SqlCommand(
                $"UPDATE p SET {ProductQuantityColumn}=ROUND(v.{ProductQuantityColumn},2) "
                + $"FROM dbo.PRODUCT p JOIN (VALUES {rows}) v({ProductKeyColumn}, {ProductQuantityColumn}) "
                + $"ON p.{ProductKeyColumn}=v.{ProductKeyColumn};", connection, transaction);
            for (var index = 0; index < chunk.Count; index++)
            {
                command.Parameters.Add($"@p{index}", SqlDbType.NChar, 30).Value = chunk[index].ProductNo;
                command.Parameters.Add($"@q{index}", SqlDbType.Float).Value =
                    (object?)chunk[index].Quantity ?? DBNull.Value;
            }
            affected += await command.ExecuteNonQueryAsync(token);
        }
        return affected;
    }

    private static async Task<int> ExecuteAsync(
        string sql, SqlConnection connection, SqlTransaction? transaction, CancellationToken token)
    {
        await using var command = new SqlCommand(sql, connection, transaction);
        return await command.ExecuteNonQueryAsync(token);
    }
}
