using System.Data;
using Microsoft.Data.SqlClient;

namespace EOS.API.Data;

/// <summary>
/// Mould domain rules executed after module saves (mould batch, get, check, production, batch-in and assessment documents). Methods run inside the caller's transaction.
/// </summary>
public static class MouDomainRules
{
    /// <summary>模房领料/耗料单（P_MOU_GET / P_MOU_GET2）AfterSave：批号条件必填。</summary>
    public static Task<SprocResult> MouGetAfterSaveAsync(
        SqlConnection connection, SqlTransaction transaction,
        IReadOnlyList<string> pkColumns, IReadOnlyList<string> keyValues, CancellationToken token)
        => DomainRuleService.ValidateDetailAsync(connection, transaction, pkColumns, keyValues,
            "MOU_GET_D", "GET_TYPE", "GET_NO",
            [
                ("ISNULL(t.BATCH_NO,'')='' AND EXISTS (SELECT 1 FROM dbo.PRODUCT p WHERE p.PRO_NO=t.PRO_NO AND p.MANAGE_BATCH=1)", "以下序号项需要输入批号 "),
            ], token);

    /// <summary>产品模具对照表（P_MOU_PRO）AfterSave：按产品回写所用模具汇总。</summary>
    public static async Task<SprocResult> MouProAfterSaveAsync(
        SqlConnection connection, SqlTransaction transaction,
        IReadOnlyList<string> pkColumns, IReadOnlyList<string> keyValues, CancellationToken token)
    {
        if (pkColumns.Count < 1 || keyValues.Count < 1) return new(false, "产品模具对照领域规则缺少主键。");
        var proNo = (keyValues[0] ?? string.Empty).Trim();
        await using var update = new SqlCommand(
            "UPDATE dbo.MOU_PRO_M SET MOULD_IDS=dbo.f_get_pro_moulds(PRO_NO) WHERE PRO_NO=@ProNo;", connection, transaction);
        update.Parameters.Add("@ProNo", SqlDbType.NVarChar, 30).Value = proNo;
        await update.ExecuteNonQueryAsync(token);
        return new(true, null);
    }

    /// <summary>出口报关单（P_CUS_EXPORT）AfterSave：ERROR_NO_SAVE 门控的报关不超合同检查。</summary>
}
