using System.Data;
using Microsoft.Data.SqlClient;

namespace EOS.API.Data;

/// <summary>
/// Customer-order domain rules executed after module saves (quotes, orders, deliveries, returns, fit-out, callback and account documents). Methods run inside the caller's transaction.
/// </summary>
public static class CopDomainRules
{
    /// <summary>备货单（1411）AfterSave：量纲校验与批号必填均由校验目录承担。</summary>

    /// <summary>P_COP_FITIN_CHECK 内联：返仓不超备货/送货不超备货（订单/工单/调拨，SYSSS 门控）。</summary>

    /// <summary>P_PUR_CANCEL 退料不超收料。</summary>


    /// <summary>送货回执（1413）AfterSave：送/退货已有回执由校验目录（reference-exists）承担。</summary>

    /// <summary>工时录入（180207）AfterSave：HR_SETUP.REQUIRE_ENACTMENT=1 时校验加班不超申请（当前环境=0，跳过）。</summary>


    /// <summary>客户订单变更（1418）AfterSave：原单批核、变更量下限与订单号唯一由校验目录承担。</summary>

    /// <summary>送货单（1406）保存期判据与包装标记已由校验目录/效果目录承接。</summary>



    public static async Task<Dictionary<string, int>> ReadSysssFlagsAsync(
        SqlConnection connection, SqlTransaction transaction, CancellationToken token)
    {
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        await using var command = new SqlCommand("""
            SELECT TOP 1 ISNULL(CAST(SEND_TAG AS int),0), ISNULL(CAST(SEND_ORDER_TAG AS int),0)
            FROM dbo.SYSSS;
            """, connection, transaction);
        await using var reader = await command.ExecuteReaderAsync(token);
        if (await reader.ReadAsync(token))
        {
            result["SEND_TAG"] = reader.GetInt32(0);
            result["SEND_ORDER_TAG"] = reader.GetInt32(1);
        }
        return result;
    }

    /// <summary>
    /// 采购单（1606）AfterSave：厂商/计价有效期/预交日期/申购单/产品校验 +
    /// PUR_PURCHASE_MORE 同步（补明细、单价回填、金额重算 I/O/N、数量分配、单号汇总）。
    /// </summary>



    /// <summary>厂商报价单（1604）AfterSave：厂商校验 + 询价单一致性校验（镜像 cop-quote，厂商侧）。</summary>

    /// <summary>收款单（170102）保存后动作已由效果目录承接（cop-receipt-offset）。</summary>
    /// <summary>付款单（170202）保存后动作已由效果目录承接（pur-pay-offset）。</summary>
}
