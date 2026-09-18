using System.Data;
using Microsoft.Data.SqlClient;

namespace EOS.API.Data;

/// <summary>
/// 产品可用量 / MRP 重算（原 `P_UPDATE_PRO_MRP_ALL`，230901 作业与每次库存移动后调用）。
/// 口径：先把 `PRODUCT` 的五类占用清零，再逐类按来源单据汇总回填——
/// 库存量取 MRP 库别（`DEPOT.MRP=1`）的 `SUM(QTY)`；未送货取已批核未结案且未送满的订单行；
/// 未入库取已批核未完工的制令；未领料取已批核未结案的制令明细应领量；请购未采购 + 采购未收货
/// 合并进 `IN_BUY_QTY`；最后 `MRP_QTY = QTY - SAFETY_QTY - NOT_SEND_QTY - NOT_GET_QTY + NOT_IN_QTY + IN_BUY_QTY`。
/// 语句与顺序逐条对照原过程本体。**两处有意差异**：① 原过程自带 `BEGIN/COMMIT`（在调用方事务内
/// 只是嵌套计数，在事务外才自开自提），这里一律在**调用方事务**内执行——失败整链回滚，符合项目
/// "同一事务"口径；② 原过程的 `#tmp` 临时表所有用途都已被注释掉，属死代码，不再保留。
/// </summary>
public static class MrpRecalcService
{
    internal const string RecalcSql = """
        UPDATE dbo.PRODUCT SET NOT_SEND_QTY=0, NOT_IN_QTY=0, NOT_GET_QTY=0, IN_BUY_QTY=0, MRP_QTY=0;
        UPDATE dbo.PRODUCT SET QTY=ROUND(d.QTY,2)
            FROM (SELECT PRO_NO, SUM(QTY) QTY FROM dbo.INV_PRO_DEPOT WITH(TABLOCKX)
                  WHERE DEPOT_ID IN (SELECT DEPOT_ID FROM dbo.DEPOT WHERE MRP=1) GROUP BY PRO_NO) d
            WHERE dbo.PRODUCT.PRO_NO=d.PRO_NO;
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

    /// <summary>在调用方事务内执行重算；返回受影响行数合计（仅用于观测，不作为判据）。</summary>
    public static async Task<int> RecalcAsync(
        SqlConnection connection, SqlTransaction? transaction, CancellationToken token)
    {
        await using var command = new SqlCommand(RecalcSql, connection, transaction);
        return await command.ExecuteNonQueryAsync(token);
    }
}
