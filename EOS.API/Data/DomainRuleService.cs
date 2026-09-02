using Microsoft.Data.SqlClient;
using System.Data;

namespace EOS.API.Data;

/// <summary>
/// Deterministic domain rule dispatcher. Each registered rule name maps to a domain rule
/// implementation (sales/procurement/production/HR/inventory/customs) that runs inside the
/// caller's transaction after a module save. Table and column names come from server-side
/// metadata and all values are parameterized.
/// </summary>
public sealed class DomainRuleService(ILogger<DomainRuleService> logger)
{
    internal static (string TypeColumn, string NoColumn) KeyColumns(IReadOnlyList<string> pkColumns)
        => (pkColumns[0], pkColumns[1]);

    /// <summary>
    /// 执行领域规则（保存后）。返回 false 表示业务校验失败（message 给用户）。
    /// </summary>
    public async Task<SprocResult> RunAfterSaveAsync(
        string ruleName,
        SqlConnection connection,
        SqlTransaction transaction,
        WorkbenchDefinition definition,
        IReadOnlyList<string> pkColumns,
        IReadOnlyList<string> keyValues,
        CancellationToken token)
    {
        try
        {
            return ruleName.ToLowerInvariant() switch
            {
                "purchase-due" => await PurDomainRules.PurchaseDueAfterSaveAsync(connection, transaction, definition, pkColumns, keyValues, token),
                "cop-receipt" => await CopDomainRules.CopReceiptAfterSaveAsync(connection, transaction, pkColumns, keyValues, token),
                "cop-prepay" => await CopDomainRules.CopPrepayAfterSaveAsync(connection, transaction, pkColumns, keyValues, token),
                "cop-quote" => await CopDomainRules.CopQuoteAfterSaveAsync(connection, transaction, pkColumns, keyValues, token),
                "cop-account" => await CopDomainRules.CopAccountAfterSaveAsync(connection, transaction, pkColumns, keyValues, token),
                "cop-order" => await CopDomainRules.CopOrderAfterSaveAsync(connection, transaction, pkColumns, keyValues, token),
                "cop-send" => await CopDomainRules.CopSendAfterSaveAsync(connection, transaction, pkColumns, keyValues, token),
                "moc-work" => await MocDomainRules.MocWorkAfterSaveAsync(connection, transaction, pkColumns, keyValues, token),
                "moc-work-in" => await MocDomainRules.MocWorkInAfterSaveAsync(connection, transaction, pkColumns, keyValues, token),
                "pur-callback" => await PurDomainRules.PurCallbackAfterSaveAsync(connection, transaction, pkColumns, keyValues, token),
                "moc-produce-change" => await MocDomainRules.MocProduceChangeAfterSaveAsync(connection, transaction, pkColumns, keyValues, token),
                "pur-purchase-change" => await PurDomainRules.PurPurchaseChangeAfterSaveAsync(connection, transaction, pkColumns, keyValues, token),
                "sam-out" => await CusDomainRules.SamOutAfterSaveAsync(connection, transaction, pkColumns, keyValues, token),
                "cop-order-change" => await CopDomainRules.CopOrderChangeAfterSaveAsync(connection, transaction, pkColumns, keyValues, token),
                "cop-callback" => await CopDomainRules.CopCallbackAfterSaveAsync(connection, transaction, pkColumns, keyValues, token),
                "hr-worktime" => await HrDomainRules.HrWorktimeAfterSaveAsync(connection, transaction, pkColumns, keyValues, token),
                "inv-check-stock" => await InvDomainRules.InvCheckStockAfterSaveAsync(connection, transaction, pkColumns, keyValues, token),
                "sfc-plan" => await SfcDomainRules.SfcPlanAfterSaveAsync(connection, transaction, pkColumns, keyValues, token),
                "pur-quote" => await PurDomainRules.PurQuoteAfterSaveAsync(connection, transaction, pkColumns, keyValues, token),
                "pur-receive" => await PurDomainRules.PurReceiveAfterSaveAsync(connection, transaction, pkColumns, keyValues, token),
                "pur-apply" => await PurDomainRules.PurApplyAfterSaveAsync(connection, transaction, pkColumns, keyValues, token),
                "pur-purchase" => await PurDomainRules.PurPurchaseAfterSaveAsync(connection, transaction, pkColumns, keyValues, token),
                "pur-pay" => await PurDomainRules.PurPayAfterSaveAsync(connection, transaction, pkColumns, keyValues, token),
                "pur-prepay" => await PurDomainRules.PurPrepayAfterSaveAsync(connection, transaction, pkColumns, keyValues, token),
                "curr" => await SysDomainRules.CurrAfterSaveAsync(connection, transaction, pkColumns, keyValues, token),
                "bom-stru" => await SysDomainRules.BomStruAfterSaveAsync(connection, transaction, pkColumns, keyValues, token),
                "sysdg" => await SysDomainRules.SysdgAfterSaveAsync(connection, transaction, pkColumns, keyValues, token),
                "sysdl" => await SysDomainRules.SysdlAfterSaveAsync(connection, transaction, pkColumns, keyValues, token),
                "employee-card" => await HrDomainRules.EmployeeCardAfterSaveAsync(connection, transaction, pkColumns, keyValues, token),
                "inv-in" => await InvDomainRules.InvOccurInAfterSaveAsync(connection, transaction, pkColumns, keyValues, token),
                "inv-out" => await InvDomainRules.InvOccurOutAfterSaveAsync(connection, transaction, pkColumns, keyValues, token),
                "inv-transfer" => await InvDomainRules.InvOccurTransferAfterSaveAsync(connection, transaction, pkColumns, keyValues, token),
                "inv-scrap" => await InvDomainRules.InvOccurScrapAfterSaveAsync(connection, transaction, pkColumns, keyValues, token),
                "inv-adjust" => await InvDomainRules.InvOccurAdjustAfterSaveAsync(connection, transaction, pkColumns, keyValues, token),
                "inv-init" => await InvDomainRules.InvOccurInitAfterSaveAsync(connection, transaction, pkColumns, keyValues, token),
                "moc-get" => await MocDomainRules.MocGetAfterSaveAsync(connection, transaction, pkColumns, keyValues, token),
                "moc-produce" => await MocDomainRules.MocProduceAfterSaveAsync(connection, transaction, definition.ModuleId, pkColumns, keyValues, token),
                "moc-product-in" => await MocDomainRules.MocProductInAfterSaveAsync(connection, transaction, definition.ModuleId, pkColumns, keyValues, token),
                "moc-product-out" => await MocDomainRules.MocProductOutAfterSaveAsync(connection, transaction, definition.ModuleId, pkColumns, keyValues, token),
                "sfc-process" => await SfcDomainRules.SfcProcessAfterSaveAsync(connection, transaction, pkColumns, keyValues, token),
                "sfc-daily" => await SfcDomainRules.SfcDailyAfterSaveAsync(connection, transaction, pkColumns, keyValues, token),
                "cop-return" => await CopDomainRules.CopReturnAfterSaveAsync(connection, transaction, pkColumns, keyValues, token),
                "cop-fitout" => await CopDomainRules.CopFitoutAfterSaveAsync(connection, transaction, definition.ModuleId, pkColumns, keyValues, token),
                "cop-fitin" => await CopDomainRules.CopFitinAfterSaveAsync(connection, transaction, definition.ModuleId, pkColumns, keyValues, token),
                "cop-back" => await CopDomainRules.CopBackAfterSaveAsync(connection, transaction, definition.ModuleId, pkColumns, keyValues, token),
                "pur-cancel" => await PurDomainRules.PurCancelAfterSaveAsync(connection, transaction, pkColumns, keyValues, token),
                "hr-employee" => await HrDomainRules.HrEmployeeAfterSaveAsync(connection, transaction, pkColumns, keyValues, token),
                "hr-enactment" => await HrDomainRules.HrEnactmentAfterSaveAsync(connection, transaction, pkColumns, keyValues, token),
                "hr-wage-item" => await HrDomainRules.HrWageItemAfterSaveAsync(connection, transaction, "HR_WAGE_D", "HR_WAGE", token),
                "hrm-wage-item" => await HrDomainRules.HrWageItemAfterSaveAsync(connection, transaction, "HRM_WAGE_D", "HRM_WAGE", token),
                "hr-contract" => await HrDomainRules.HrContractAfterSaveAsync(connection, transaction, pkColumns, keyValues, token),
                "hr-safe" => await HrDomainRules.HrSafeAfterSaveAsync(connection, transaction, pkColumns, keyValues, token),
                "hr-certify" => await HrDomainRules.HrCertifyAfterSaveAsync(connection, transaction, pkColumns, keyValues, token),
                "hr-plan" => await HrDomainRules.HrPlanAfterSaveAsync(connection, transaction, "HR_PLAN_M", "HR_PLAN_D", pkColumns, keyValues, token),
                "hrm-plan" => await HrDomainRules.HrPlanAfterSaveAsync(connection, transaction, "HRM_PLAN_M", "HRM_PLAN_D", pkColumns, keyValues, token),
                "hr-wage" => await HrDomainRules.HrWageAfterSaveAsync(connection, transaction, "HR_WAGE_M", "HR_WAGE_D", pkColumns, keyValues, token, deleteDup: false),
                "hrm-wage" => await HrDomainRules.HrWageAfterSaveAsync(connection, transaction, "HRM_WAGE_M", "HRM_WAGE_D", pkColumns, keyValues, token, deleteDup: false),
                "hr-wage-lz" => await HrDomainRules.HrWageAfterSaveAsync(connection, transaction, "HR_WAGE_M", "HR_WAGE_D", pkColumns, keyValues, token, deleteDup: true),
                "hr-apply" => await HrDomainRules.HrApplyAfterSaveAsync(connection, transaction, pkColumns, keyValues, token),
                "inv-loan" => await InvDomainRules.InvLoanAfterSaveAsync(connection, transaction, pkColumns, keyValues, token),
                "inv-return" => await InvDomainRules.InvReturnAfterSaveAsync(connection, transaction, pkColumns, keyValues, token),
                "moc-bom-stru" => await MocDomainRules.MocBomStruAfterSaveAsync(connection, transaction, pkColumns, keyValues, token),
                "moc-plan" => await MocDomainRules.MocPlanAfterSaveAsync(connection, transaction, definition.ModuleId, pkColumns, keyValues, token),
                "moc-produce-process" => await MocDomainRules.MocProduceProcessAfterSaveAsync(connection, transaction, pkColumns, keyValues, token),
                "moc-work-out" => await MocDomainRules.MocWorkOutAfterSaveAsync(connection, transaction, definition.ModuleId, pkColumns, keyValues, token),
                "mou-apply" => await MouDomainRules.MouldNoopAfterSaveAsync(token),
                "mou-accept" => await MouDomainRules.MouldNoopAfterSaveAsync(token),
                "mou-batch" => await MouDomainRules.MouBatchAfterSaveAsync(connection, transaction, pkColumns, keyValues, token),
                "mou-get" => await MouDomainRules.MouGetAfterSaveAsync(connection, transaction, pkColumns, keyValues, token),
                "mou-out" => await MouDomainRules.MouMouldCheckAfterSaveAsync(connection, transaction, pkColumns, keyValues, "MOU_OUT_D", "OUT_TYPE", "OUT_NO", token),
                "mou-in" => await MouDomainRules.MouMouldCheckAfterSaveAsync(connection, transaction, pkColumns, keyValues, "MOU_IN_D", "IN_TYPE", "IN_NO", token),
                "mou-scrap" => await MouDomainRules.MouMouldCheckAfterSaveAsync(connection, transaction, pkColumns, keyValues, "MOU_SCRAP_D", "SCRAP_TYPE", "SCRAP_NO", token),
                "mou-pro" => await MouDomainRules.MouProAfterSaveAsync(connection, transaction, pkColumns, keyValues, token),
                "mou-batchin" => await MouDomainRules.MouBatchinAfterSaveAsync(connection, transaction, definition.ModuleId, pkColumns, keyValues, token),
                "mou-get2" => await MouDomainRules.MouGet2AfterSaveAsync(connection, transaction, pkColumns, keyValues, token),
                "mou-assess" => await MouDomainRules.MouAssessAfterSaveAsync(connection, transaction, pkColumns, keyValues, token),
                "cus-export" => await CusDomainRules.CusExportAfterSaveAsync(connection, transaction, definition.ModuleId, pkColumns, keyValues, token),
                "cus-import" => await CusDomainRules.CusImportAfterSaveAsync(connection, transaction, definition.ModuleId, pkColumns, keyValues, token),
                "cus-manual" => await CusDomainRules.CusManualAfterSaveAsync(connection, transaction, pkColumns, keyValues, token),
                "cus-account" => await CusDomainRules.CusAccountAfterSaveAsync(connection, transaction, definition.ModuleId, pkColumns, keyValues, token),
                "qc-analysis" => await CusDomainRules.QcAnalysisAfterSaveAsync(connection, transaction, definition.ModuleId, pkColumns, keyValues, token),
                _ => new(false, $"未登记的领域规则：{ruleName}"),
            };
        }
        catch (SqlException ex)
        {
            logger.LogWarning("领域规则执行异常 rule={Rule} message={Message}", ruleName, ex.Message);
            return new(false, ex.Message);
        }
    }
    internal static async Task<SprocResult> ValidateDetailAsync(
        SqlConnection connection, SqlTransaction transaction,
        IReadOnlyList<string> pkColumns, IReadOnlyList<string> keyValues,
        string detailTable, string typeColumn, string noColumn,
        IReadOnlyList<(string WhereClause, string Message)> checks, CancellationToken token)
    {
        if (pkColumns.Count < 2 || keyValues.Count < 2) return new(false, "单据领域规则缺少主键。");
        if (detailTable.Length == 0 || detailTable.Length > 64 || !detailTable.All(c => char.IsLetterOrDigit(c) || c == '_'))
            return new(false, "明细表名非法。");
        if (typeColumn.Length == 0 || typeColumn.Length > 64 || !typeColumn.All(c => char.IsLetterOrDigit(c) || c == '_'))
            return new(false, "类型列名非法。");
        if (noColumn.Length == 0 || noColumn.Length > 64 || !noColumn.All(c => char.IsLetterOrDigit(c) || c == '_'))
            return new(false, "单号列名非法。");
        var type = (keyValues[0] ?? string.Empty).Trim();
        var no = (keyValues[1] ?? string.Empty).Trim();
        foreach (var (whereClause, message) in checks)
        {
            await using var cmd = new SqlCommand($"""
                SELECT TOP 11 SERIAL_NO FROM dbo.[{detailTable}] t
                WHERE t.[{typeColumn}]=@Type AND t.[{noColumn}]=@No AND {whereClause} ORDER BY SERIAL_NO;
                """, connection, transaction);
            cmd.Parameters.Add("@Type", SqlDbType.NChar, 10).Value = type;
            cmd.Parameters.Add("@No", SqlDbType.NChar, 20).Value = no;
            await using var reader = await cmd.ExecuteReaderAsync(token);
            var lines = new List<string>();
            while (await reader.ReadAsync(token)) lines.Add(Convert.ToInt32(reader.GetValue(0)).ToString());
            if (lines.Count > 0) return new(false, message + "\r\n" + string.Join("\r\n", lines.Take(10)));
        }
        return new(true, null);
    }

    /// <summary>模块是否启用 ERROR_NO_SAVE（旧 SP 决定是否执行 P_*_CHECK 的开关）。</summary>
    internal static async Task<bool> HasErrorNoSaveAsync(
        SqlConnection connection, SqlTransaction transaction, int moduleId, CancellationToken token)
    {
        await using var cmd = new SqlCommand(
            "SELECT TOP 1 1 FROM dbo.MODULES WHERE M_IDX=@ModuleId AND ERROR_NO_SAVE=1;", connection, transaction);
        cmd.Parameters.Add("@ModuleId", SqlDbType.Int).Value = moduleId;
        return await cmd.ExecuteScalarAsync(token) is not null;
    }
    internal static async Task<int> ScalarIntAsync(
        SqlConnection connection, SqlTransaction transaction, string sql, string type, string no, CancellationToken token)
    {
        await using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.Add("@Type", SqlDbType.NVarChar, 10).Value = type;
        command.Parameters.Add("@No", SqlDbType.NVarChar, 20).Value = no;
        var value = await command.ExecuteScalarAsync(token);
        return value is null ? 0 : Convert.ToInt32(value);
    }

    internal static async Task<List<string>> ReadStringsAsync(
        SqlConnection connection, SqlTransaction transaction, string sql, string type, string no, CancellationToken token)
    {
        var result = new List<string>();
        await using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.Add("@Type", SqlDbType.NVarChar, 10).Value = type;
        command.Parameters.Add("@No", SqlDbType.NVarChar, 20).Value = no;
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token)) result.Add(reader.GetString(0));
        return result;
    }

    internal static async Task UpdateMoreQtyAsync(
        SqlConnection connection, SqlTransaction transaction,
        string type, string no, int serialNo, decimal qty, CancellationToken token)
    {
        await using var command = new SqlCommand("""
            UPDATE dbo.PUR_APPLY_MORE SET QTY=@Qty
            WHERE APPLY_TYPE=@Type AND APPLY_NO=@No AND SERIAL_NO=@Serial;
            """, connection, transaction);
        command.Parameters.Add("@Qty", SqlDbType.Decimal).Value = qty;
        command.Parameters.Add("@Type", SqlDbType.NVarChar, 10).Value = type;
        command.Parameters.Add("@No", SqlDbType.NVarChar, 20).Value = no;
        command.Parameters.Add("@Serial", SqlDbType.Int).Value = serialNo;
        await command.ExecuteNonQueryAsync(token);
    }

    internal static async Task UpdateDistinctFieldAsync(
        SqlConnection connection, SqlTransaction transaction,
        string type, string no, string masterColumn, string moreTable, string moreColumn,
        string moreTypeColumn, string moreNoColumn, CancellationToken token)
    {
        var values = await ReadStringsAsync(connection, transaction,
            $"SELECT DISTINCT LTRIM(RTRIM(ISNULL([{moreColumn}],''))) FROM dbo.[{moreTable}] " +
            $"WHERE [{moreTypeColumn}]=@Type AND [{moreNoColumn}]=@No AND ISNULL([{moreColumn}],'')<>'' ORDER BY 1;",
            type, no, token);
        if (values.Count == 0) return;
        await using var command = new SqlCommand(
            $"UPDATE dbo.PUR_APPLY_M SET [{masterColumn}]=@Value WHERE APPLY_TYPE=@Type AND APPLY_NO=@No;",
            connection, transaction);
        command.Parameters.Add("@Value", SqlDbType.NVarChar, 300).Value = string.Join(',', values);
        command.Parameters.Add("@Type", SqlDbType.NVarChar, 10).Value = type;
        command.Parameters.Add("@No", SqlDbType.NVarChar, 20).Value = no;
        await command.ExecuteNonQueryAsync(token);
    }
    internal static async Task<string?> FindExceededAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string dueType,
        string dueNo,
        CancellationToken token,
        string detailTable,
        string detailTypeColumn,
        string detailNoColumn,
        string detailGroupTypeColumn,
        string detailGroupNoColumn,
        string detailGroupSerialColumn,
        string sourceTable,
        string sourceTypeColumn,
        string sourceNoColumn)
    {
        var sql = $"""
            SELECT od.{sourceNoColumn}, od.QTY, od.FINISHED_QTY, sd.QTY AS MY_QTY
            FROM dbo.[{sourceTable}] od
            INNER JOIN (
                SELECT [{detailGroupTypeColumn}], [{detailGroupNoColumn}], [{detailGroupSerialColumn}], SUM(QTY) QTY
                FROM dbo.[{detailTable}]
                WHERE [{detailTypeColumn}]=@Type AND [{detailNoColumn}]=@No
                GROUP BY [{detailGroupTypeColumn}], [{detailGroupNoColumn}], [{detailGroupSerialColumn}]
            ) sd
              ON od.{sourceTypeColumn}=sd.[{detailGroupTypeColumn}] AND od.{sourceNoColumn}=sd.[{detailGroupNoColumn}]
             AND od.SERIAL_NO=sd.[{detailGroupSerialColumn}]
            WHERE od.FINISHED_QTY+sd.QTY > od.QTY;
            """;
        await using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.Add("@Type", SqlDbType.NVarChar, 10).Value = dueType;
        command.Parameters.Add("@No", SqlDbType.NVarChar, 20).Value = dueNo;
        await using var reader = await command.ExecuteReaderAsync(token);
        var lines = new List<string>();
        while (await reader.ReadAsync(token))
        {
            var no = reader.GetString(0).Trim();
            var qty = Convert.ToDouble(reader.GetValue(1));
            var finished = Convert.ToDouble(reader.GetValue(2));
            var myQty = Convert.ToDouble(reader.GetValue(3));
            lines.Add($"{no}    {qty}    {finished}    {myQty}");
        }
        return lines.Count > 0 ? string.Join("\r\n", lines) : null;
    }
    internal static async Task<bool> ExistsAsync(
        SqlConnection connection, SqlTransaction transaction, string sql, string type, string no, CancellationToken token)
    {
        await using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.Add("@Type", SqlDbType.NVarChar, 10).Value = type;
        command.Parameters.Add("@No", SqlDbType.NVarChar, 20).Value = no;
        return await command.ExecuteScalarAsync(token) is not null;
    }

    internal static async Task<string?> FindLinesAsync(
        SqlConnection connection, SqlTransaction transaction, string sql, string type, string no, CancellationToken token,
        Func<SqlDataReader, string> line)
    {
        await using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.Add("@Type", SqlDbType.NVarChar, 10).Value = type;
        command.Parameters.Add("@No", SqlDbType.NVarChar, 20).Value = no;
        await using var reader = await command.ExecuteReaderAsync(token);
        var lines = new List<string>();
        while (await reader.ReadAsync(token)) lines.Add(line(reader));
        return lines.Count > 0 ? string.Join("\r\n", lines) : null;
    }
}
