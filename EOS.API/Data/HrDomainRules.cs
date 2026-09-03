using System.Data;
using Microsoft.Data.SqlClient;

namespace EOS.API.Data;

/// <summary>
/// Human-resource domain rules executed after module saves (employee, contract, safety, certification, plan, wage, apply and work-time documents). Methods run inside the caller's transaction.
/// </summary>
public static class HrDomainRules
{

    /// <summary>
    /// 货币资料（110103）AfterSave：本位币唯一 + 本位币汇率必须为 1。
    /// 仅校验，无落库副作用。
    /// </summary>
    /// <summary>
    /// 员工发卡（180208）AfterSave：失效日期不得早于生效日期 + 原卡到期日联动
    /// 。
    /// </summary>
    public static async Task<SprocResult> EmployeeCardAfterSaveAsync(
        SqlConnection connection, SqlTransaction transaction,
        IReadOnlyList<string> pkColumns, IReadOnlyList<string> keyValues, CancellationToken token)
    {
        if (pkColumns.Count < 2 || keyValues.Count < 2) return new(false, "员工发卡领域规则缺少主键。");
        var empId = (keyValues[0] ?? string.Empty).Trim();
        var cardId = (keyValues[1] ?? string.Empty).Trim();
        DateTime? beginDate; DateTime? endDate;
        await using (var read = new SqlCommand(
            "SELECT BEGIN_DATE, END_DATE FROM dbo.HR_EMPLOYEE_CARD WHERE EMP_ID=@EmpId AND CARD_ID=@CardId;",
            connection, transaction))
        {
            read.Parameters.Add("@EmpId", SqlDbType.NChar, 10).Value = empId;
            read.Parameters.Add("@CardId", SqlDbType.NChar, 20).Value = cardId;
            await using var reader = await read.ExecuteReaderAsync(token);
            if (!await reader.ReadAsync(token)) return new(true, null);
            beginDate = reader.IsDBNull(0) ? null : reader.GetDateTime(0);
            endDate = reader.IsDBNull(1) ? null : reader.GetDateTime(1);
        }
        if (endDate < beginDate) return new(false, "失于日期应在生效日期后");
        if (beginDate is not null)
        {
            var expires = beginDate.Value.AddDays(-1);
            await using (var updCard = new SqlCommand("""
                UPDATE dbo.HR_EMPLOYEE_CARD SET END_DATE=@Expires
                WHERE CARD_ID=@CardId AND EMP_ID<>@EmpId AND (END_DATE IS NULL OR END_DATE>=@BeginDate);
                """, connection, transaction))
            {
                updCard.Parameters.Add("@Expires", SqlDbType.DateTime).Value = expires;
                updCard.Parameters.Add("@CardId", SqlDbType.NChar, 20).Value = cardId;
                updCard.Parameters.Add("@EmpId", SqlDbType.NChar, 10).Value = empId;
                updCard.Parameters.Add("@BeginDate", SqlDbType.DateTime).Value = beginDate.Value;
                await updCard.ExecuteNonQueryAsync(token);
            }
            await using (var updEmp = new SqlCommand("""
                UPDATE dbo.HR_EMPLOYEE_CARD SET END_DATE=@Expires
                WHERE EMP_ID=@EmpId AND CARD_ID<>@CardId AND (END_DATE IS NULL OR END_DATE>=@BeginDate);
                """, connection, transaction))
            {
                updEmp.Parameters.Add("@Expires", SqlDbType.DateTime).Value = expires;
                updEmp.Parameters.Add("@EmpId", SqlDbType.NChar, 10).Value = empId;
                updEmp.Parameters.Add("@CardId", SqlDbType.NChar, 20).Value = cardId;
                updEmp.Parameters.Add("@BeginDate", SqlDbType.DateTime).Value = beginDate.Value;
                await updEmp.ExecuteNonQueryAsync(token);
            }
        }
        return new(true, null);
    }

    /// <summary>库存单 AfterSave 通用校验：库别/产品/批号。</summary>

    public static async Task<SprocResult> HrEmployeeAfterSaveAsync(
        SqlConnection connection, SqlTransaction transaction,
        IReadOnlyList<string> pkColumns, IReadOnlyList<string> keyValues, CancellationToken token)
    {
        if (pkColumns.Count < 1 || keyValues.Count < 1) return new(false, "员工资料领域规则缺少主键。");
        var empId = (keyValues[0] ?? string.Empty).Trim();
        string? empNo;
        await using (var read = new SqlCommand(
            "SELECT LTRIM(RTRIM(ISNULL(EMP_NO,''))) FROM dbo.HR_EMPLOYEE WHERE EMP_ID=@EmpId;", connection, transaction))
        {
            read.Parameters.Add("@EmpId", SqlDbType.NChar, 10).Value = empId;
            empNo = (string?)await read.ExecuteScalarAsync(token);
        }
        if (string.IsNullOrWhiteSpace(empNo)) return new(true, null);
        string? owner;
        await using (var dup = new SqlCommand(
            "SELECT TOP 1 LTRIM(RTRIM(EMP_NAME)) FROM dbo.HR_EMPLOYEE WHERE EMP_ID<>@EmpId AND EMP_NO=@EmpNo AND STATE<>5;",
            connection, transaction))
        {
            dup.Parameters.Add("@EmpId", SqlDbType.NChar, 10).Value = empId;
            dup.Parameters.Add("@EmpNo", SqlDbType.NChar, 20).Value = empNo;
            owner = (string?)await dup.ExecuteScalarAsync(token);
        }
        return string.IsNullOrEmpty(owner)
            ? new(true, null)
            : new(false, "\r\n员工工号：" + empNo + "\r\n已分配给：" + owner);
    }

    /// <summary>每月出勤参数（P_HR_ENACTMENT）AfterSave：每人每月一笔。</summary>


    /// <summary>每月出勤参数（P_HR_ENACTMENT）AfterSave：每人每月一笔。</summary>
    public static async Task<SprocResult> HrEnactmentAfterSaveAsync(
        SqlConnection connection, SqlTransaction transaction,
        IReadOnlyList<string> pkColumns, IReadOnlyList<string> keyValues, CancellationToken token)
    {
        await using var cmd = new SqlCommand("""
            SELECT TOP 1 1 FROM (
                SELECT COUNT(*) C FROM dbo.HR_ENACTMENT_M m
                INNER JOIN dbo.HR_ENACTMENT_D d ON d.ENACTMENT_TYPE=m.ENACTMENT_TYPE AND d.ENACTMENT_NO=m.ENACTMENT_NO
                GROUP BY m.COUNT_MONTH, d.EMP_ID
            ) g WHERE g.C >= 2;
            """, connection, transaction);
        if (await cmd.ExecuteScalarAsync(token) is not null)
            return new(false, "资料重复!每个员工每个月份只可有一笔资料");
        return new(true, null);
    }

    /// <summary>工资项目设定（P_HR_WAGE_ITEM / P_HRM_WAGE_ITEM）AfterSave：FIELDS 元数据联动（显隐/名称/格式/备注）。</summary>


    /// <summary>工资项目设定（P_HR_WAGE_ITEM / P_HRM_WAGE_ITEM）AfterSave：FIELDS 元数据联动（显隐/名称/格式/备注）。</summary>
    public static async Task<SprocResult> HrWageItemAfterSaveAsync(
        SqlConnection connection, SqlTransaction transaction,
        string fieldsTable, string wageTable, CancellationToken token)
    {
        await using (var reset = new SqlCommand(
            "UPDATE dbo.FIELDS SET IS_VISIBLE=0 WHERE T_ID=@TId AND F_ID LIKE 'WAGE_ITEM%';", connection, transaction))
        {
            reset.Parameters.Add("@TId", SqlDbType.NVarChar, 50).Value = fieldsTable;
            await reset.ExecuteNonQueryAsync(token);
        }
        await using (var sync = new SqlCommand($"""
            UPDATE f SET f.IS_VISIBLE=w.IS_USED, f.F_DESC=w.WAGE_NAME, f.DISPLAY_FORMAT=w.DISPLAY_FORMAT, f.F_REMARK=w.SQL_REMARK
            FROM dbo.FIELDS f INNER JOIN dbo.[{wageTable}] w ON f.F_ID=w.WAGE_FIELD
            WHERE f.T_ID=@TId;
            """, connection, transaction))
        {
            sync.Parameters.Add("@TId", SqlDbType.NVarChar, 50).Value = fieldsTable;
            await sync.ExecuteNonQueryAsync(token);
        }
        return new(true, null);
    }

    /// <summary>合同签订（P_HR_CONTRACT）AfterSave：同单人员重复 + 与他单日期重叠。</summary>


    /// <summary>合同签订（P_HR_CONTRACT）AfterSave：同单人员重复 + 与他单日期重叠。</summary>
    public static async Task<SprocResult> HrContractAfterSaveAsync(
        SqlConnection connection, SqlTransaction transaction,
        IReadOnlyList<string> pkColumns, IReadOnlyList<string> keyValues, CancellationToken token)
    {
        if (pkColumns.Count < 2 || keyValues.Count < 2) return new(false, "合同签订领域规则缺少主键。");
        var type = (keyValues[0] ?? string.Empty).Trim();
        var no = (keyValues[1] ?? string.Empty).Trim();
        var dup = await DomainRuleService.FindLinesAsync(connection, transaction,
            """
            SELECT MAX(e.EMP_NAME) FROM dbo.HR_CONTRACT_D d
            INNER JOIN dbo.HR_EMPLOYEE e ON e.EMP_ID=d.EMP_ID
            WHERE d.CONT_TYPE=@Type AND d.CONT_NO=@No
            GROUP BY d.EMP_ID HAVING COUNT(*)>1;
            """, type, no, token, line: r => r.GetString(0).Trim() + "\t");
        if (dup is not null) return new(false, "以下人员资料重复 \r\n" + dup);
        var overlap = await DomainRuleService.FindLinesAsync(connection, transaction,
            """
            SELECT DISTINCT e.EMP_NAME FROM dbo.HR_CONTRACT_D d
            INNER JOIN dbo.HR_EMPLOYEE e ON e.EMP_ID=d.EMP_ID
            WHERE d.CONT_TYPE=@Type AND d.CONT_NO=@No
              AND EXISTS (SELECT 1 FROM dbo.HR_CONTRACT_D x
                          WHERE x.EMP_ID=d.EMP_ID AND NOT (x.CONT_TYPE=@Type AND x.CONT_NO=@No)
                            AND (d.BEGIN_DATE BETWEEN x.BEGIN_DATE AND x.END_DATE
                              OR d.END_DATE BETWEEN x.BEGIN_DATE AND x.END_DATE));
            """, type, no, token, line: r => r.GetString(0).Trim() + "\t");
        if (overlap is not null) return new(false, "以下人员在此期间内已签订合同 \r\n" + overlap);
        return new(true, null);
    }

    /// <summary>保险投保（P_HR_SAFE）AfterSave：同人同险重复 + 与他单日期重叠。</summary>


    /// <summary>保险投保（P_HR_SAFE）AfterSave：同人同险重复 + 与他单日期重叠。</summary>
    public static async Task<SprocResult> HrSafeAfterSaveAsync(
        SqlConnection connection, SqlTransaction transaction,
        IReadOnlyList<string> pkColumns, IReadOnlyList<string> keyValues, CancellationToken token)
    {
        if (pkColumns.Count < 2 || keyValues.Count < 2) return new(false, "保险投保领域规则缺少主键。");
        var type = (keyValues[0] ?? string.Empty).Trim();
        var no = (keyValues[1] ?? string.Empty).Trim();
        var dup = await DomainRuleService.FindLinesAsync(connection, transaction,
            """
            SELECT MAX(e.EMP_NAME), MAX(d.SAFE_ID) FROM dbo.HR_SAFE_D d
            INNER JOIN dbo.HR_EMPLOYEE e ON e.EMP_ID=d.EMP_ID
            WHERE d.SAFE_TYPE=@Type AND d.SAFE_NO=@No
            GROUP BY d.EMP_ID, d.SAFE_ID HAVING COUNT(*)>1;
            """, type, no, token, line: r => r.GetString(0).Trim() + "\t" + r.GetString(1).Trim());
        if (dup is not null) return new(false, "以下人员重复投保 \r\n" + dup);
        var overlap = await DomainRuleService.FindLinesAsync(connection, transaction,
            """
            SELECT DISTINCT e.EMP_NAME FROM dbo.HR_SAFE_D d
            INNER JOIN dbo.HR_EMPLOYEE e ON e.EMP_ID=d.EMP_ID
            WHERE d.SAFE_TYPE=@Type AND d.SAFE_NO=@No
              AND EXISTS (SELECT 1 FROM dbo.HR_SAFE_D x
                          WHERE x.EMP_ID=d.EMP_ID AND x.SAFE_ID=d.SAFE_ID
                            AND NOT (x.SAFE_TYPE=@Type AND x.SAFE_NO=@No)
                            AND (d.BEGIN_DATE BETWEEN x.BEGIN_DATE AND x.END_DATE
                              OR d.END_DATE BETWEEN x.BEGIN_DATE AND x.END_DATE));
            """, type, no, token, line: r => r.GetString(0).Trim() + "\t");
        if (overlap is not null) return new(false, "以下人员在此期间重复投保 \r\n" + overlap);
        return new(true, null);
    }

    /// <summary>证件资料（P_HR_CERTIFY）AfterSave：同人证件重复 + 与他单日期重叠。</summary>


    /// <summary>证件资料（P_HR_CERTIFY）AfterSave：同人证件重复 + 与他单日期重叠。</summary>
    public static async Task<SprocResult> HrCertifyAfterSaveAsync(
        SqlConnection connection, SqlTransaction transaction,
        IReadOnlyList<string> pkColumns, IReadOnlyList<string> keyValues, CancellationToken token)
    {
        if (pkColumns.Count < 2 || keyValues.Count < 2) return new(false, "证件资料领域规则缺少主键。");
        var type = (keyValues[0] ?? string.Empty).Trim();
        var no = (keyValues[1] ?? string.Empty).Trim();
        var dup = await DomainRuleService.FindLinesAsync(connection, transaction,
            """
            SELECT MAX(e.EMP_NAME), MAX(d.CERTIFY_ID) FROM dbo.HR_CERTIFY_D d
            INNER JOIN dbo.HR_EMPLOYEE e ON e.EMP_ID=d.EMP_ID
            WHERE d.CERTIFY_TYPE=@Type AND d.CERTIFY_NO=@No
            GROUP BY d.EMP_ID, d.CERTIFY_ID HAVING COUNT(*)>1;
            """, type, no, token, line: r => r.GetString(0).Trim() + "\t" + r.GetString(1).Trim());
        if (dup is not null) return new(false, "以下人员证件重复 \r\n" + dup);
        var overlap = await DomainRuleService.FindLinesAsync(connection, transaction,
            """
            SELECT DISTINCT e.EMP_NAME FROM dbo.HR_CERTIFY_D d
            INNER JOIN dbo.HR_EMPLOYEE e ON e.EMP_ID=d.EMP_ID
            WHERE d.CERTIFY_TYPE=@Type AND d.CERTIFY_NO=@No
              AND EXISTS (SELECT 1 FROM dbo.HR_CERTIFY_D x
                          WHERE x.EMP_ID=d.EMP_ID AND x.CERTIFY_ID=d.CERTIFY_ID
                            AND NOT (x.CERTIFY_TYPE=@Type AND x.CERTIFY_NO=@No)
                            AND (d.BEGIN_DATE BETWEEN x.BEGIN_DATE AND x.END_DATE
                              OR d.END_DATE BETWEEN x.BEGIN_DATE AND x.END_DATE));
            """, type, no, token, line: r => r.GetString(0).Trim() + "\t");
        if (overlap is not null) return new(false, "以下人员在此期间证件重复 \r\n" + overlap);
        return new(true, null);
    }

    /// <summary>排班（P_HR_PLAN / P_HRM_PLAN）AfterSave：每月每人一排班。</summary>


    /// <summary>排班（P_HR_PLAN / P_HRM_PLAN）AfterSave：每月每人一排班。</summary>
    public static async Task<SprocResult> HrPlanAfterSaveAsync(
        SqlConnection connection, SqlTransaction transaction,
        string masterTable, string detailTable,
        IReadOnlyList<string> pkColumns, IReadOnlyList<string> keyValues, CancellationToken token)
    {
        if (pkColumns.Count < 2 || keyValues.Count < 2) return new(false, "排班领域规则缺少主键。");
        var type = (keyValues[0] ?? string.Empty).Trim();
        var no = (keyValues[1] ?? string.Empty).Trim();
        string? countMonth;
        await using (var read = new SqlCommand(
            $"SELECT LTRIM(RTRIM(ISNULL(COUNT_MONTH,''))) FROM dbo.[{masterTable}] WHERE PLAN_TYPE=@Type AND PLAN_NO=@No;",
            connection, transaction))
        {
            read.Parameters.Add("@Type", SqlDbType.NChar, 10).Value = type;
            read.Parameters.Add("@No", SqlDbType.NChar, 20).Value = no;
            countMonth = (string?)await read.ExecuteScalarAsync(token);
        }
        if (string.IsNullOrWhiteSpace(countMonth)) return new(true, null);
        var dup = await FindMonthDupAsync(connection, transaction,
            $"""
            SELECT MAX(d.SERIAL_NO) FROM dbo.[{masterTable}] m
            INNER JOIN dbo.[{detailTable}] d ON d.PLAN_TYPE=m.PLAN_TYPE AND d.PLAN_NO=m.PLAN_NO
            WHERE m.COUNT_MONTH=@CountMonth
              AND EXISTS (SELECT 1 FROM dbo.[{detailTable}] x WHERE x.PLAN_TYPE=@Type AND x.PLAN_NO=@No AND x.EMP_ID=d.EMP_ID)
            GROUP BY d.EMP_ID HAVING COUNT(*)>1;
            """, type, no, countMonth, token,
            line: r => Convert.ToInt32(r.GetValue(0)).ToString() + "\t");
        return dup is null
            ? new(true, null)
            : new(false, "以下序号项人员当月排班重复 \r\n" + dup);
    }

    /// <summary>工资表（P_HR_WAGE / P_HRM_WAGE / P_HR_WAGE_LZ）AfterSave：每月每人一份；离职工资表先删旧档。</summary>


    /// <summary>工资表（P_HR_WAGE / P_HRM_WAGE / P_HR_WAGE_LZ）AfterSave：每月每人一份；离职工资表先删旧档。</summary>
    public static async Task<SprocResult> HrWageAfterSaveAsync(
        SqlConnection connection, SqlTransaction transaction,
        string masterTable, string detailTable,
        IReadOnlyList<string> pkColumns, IReadOnlyList<string> keyValues, CancellationToken token, bool deleteDup)
    {
        if (pkColumns.Count < 2 || keyValues.Count < 2) return new(false, "工资表领域规则缺少主键。");
        var type = (keyValues[0] ?? string.Empty).Trim();
        var no = (keyValues[1] ?? string.Empty).Trim();
        string? countMonth;
        await using (var read = new SqlCommand(
            $"SELECT LTRIM(RTRIM(ISNULL(COUNT_MONTH,''))) FROM dbo.[{masterTable}] WHERE WAGE_TYPE=@Type AND WAGE_NO=@No;",
            connection, transaction))
        {
            read.Parameters.Add("@Type", SqlDbType.NChar, 10).Value = type;
            read.Parameters.Add("@No", SqlDbType.NChar, 20).Value = no;
            countMonth = (string?)await read.ExecuteScalarAsync(token);
        }
        if (string.IsNullOrWhiteSpace(countMonth)) return new(true, null);
        if (deleteDup)
        {
            await using var clean = new SqlCommand($"""
                DELETE d FROM dbo.[{detailTable}] d
                WHERE EXISTS (SELECT 1 FROM dbo.[{masterTable}] m
                              WHERE m.COUNT_MONTH=@CountMonth AND m.WAGE_TYPE=d.WAGE_TYPE AND m.WAGE_NO=d.WAGE_NO)
                  AND NOT (d.WAGE_TYPE=@Type AND d.WAGE_NO=@No)
                  AND d.EMP_ID IN (SELECT EMP_ID FROM dbo.[{detailTable}] WHERE WAGE_TYPE=@Type AND WAGE_NO=@No);
                """, connection, transaction);
            clean.Parameters.Add("@CountMonth", SqlDbType.NChar, 6).Value = countMonth;
            clean.Parameters.Add("@Type", SqlDbType.NChar, 10).Value = type;
            clean.Parameters.Add("@No", SqlDbType.NChar, 20).Value = no;
            await clean.ExecuteNonQueryAsync(token);
        }
        var dup = await FindMonthDupAsync(connection, transaction,
            $"""
            SELECT d.EMP_ID FROM dbo.[{masterTable}] m
            INNER JOIN dbo.[{detailTable}] d ON d.WAGE_TYPE=m.WAGE_TYPE AND d.WAGE_NO=m.WAGE_NO
            WHERE m.COUNT_MONTH=@CountMonth
            GROUP BY d.EMP_ID HAVING COUNT(*)>1;
            """, type, no, countMonth, token,
            line: r => r.GetString(0).Trim() + "\t");
        return dup is null
            ? new(true, null)
            : new(false, "以下人员当月工资表重复 \r\n" + dup);
    }

    /// <summary>按（类型/单号/月份）收集明细行的辅助。</summary>


    /// <summary>按（类型/单号/月份）收集明细行的辅助。</summary>
    public static async Task<string?> FindMonthDupAsync(
        SqlConnection connection, SqlTransaction transaction, string sql,
        string type, string no, string countMonth, CancellationToken token, Func<SqlDataReader, string> line)
    {
        await using var cmd = new SqlCommand(sql, connection, transaction);
        cmd.Parameters.Add("@Type", SqlDbType.NChar, 10).Value = type;
        cmd.Parameters.Add("@No", SqlDbType.NChar, 20).Value = no;
        cmd.Parameters.Add("@CountMonth", SqlDbType.NChar, 6).Value = countMonth;
        await using var reader = await cmd.ExecuteReaderAsync(token);
        var lines = new List<string>();
        while (await reader.ReadAsync(token)) lines.Add(line(reader));
        return lines.Count > 0 ? string.Join("\r\n", lines.Take(10)) : null;
    }

    /// <summary>加班申请单（P_HR_APPLY）AfterSave：每日每人一单 + 不超过每月加班额。</summary>


    /// <summary>加班申请单（P_HR_APPLY）AfterSave：每日每人一单 + 不超过每月加班额。</summary>
    public static async Task<SprocResult> HrApplyAfterSaveAsync(
        SqlConnection connection, SqlTransaction transaction,
        IReadOnlyList<string> pkColumns, IReadOnlyList<string> keyValues, CancellationToken token)
    {
        if (pkColumns.Count < 2 || keyValues.Count < 2) return new(false, "加班申请领域规则缺少主键。");
        var type = (keyValues[0] ?? string.Empty).Trim();
        var no = (keyValues[1] ?? string.Empty).Trim();
        DateTime? countDate;
        string? enaType = null; string? enaNo = null;
        await using (var read = new SqlCommand(
            "SELECT COUNT_DATE FROM dbo.HR_APPLY_M WHERE APPLY_TYPE=@Type AND APPLY_NO=@No;", connection, transaction))
        {
            read.Parameters.Add("@Type", SqlDbType.NChar, 10).Value = type;
            read.Parameters.Add("@No", SqlDbType.NChar, 20).Value = no;
            var v = await read.ExecuteScalarAsync(token);
            countDate = v is null or DBNull ? null : Convert.ToDateTime(v);
        }
        if (countDate is null) return new(true, null);
        // 1. 每日每人一单
        string? dup = null;
        await using (var dupCmd = new SqlCommand("""
            SELECT d.EMP_ID FROM dbo.HR_APPLY_M m
            INNER JOIN dbo.HR_APPLY_D d ON d.APPLY_TYPE=m.APPLY_TYPE AND d.APPLY_NO=m.APPLY_NO
            WHERE m.COUNT_DATE=@CountDate
              AND EXISTS (SELECT 1 FROM dbo.HR_APPLY_D x WHERE x.APPLY_TYPE=@Type AND x.APPLY_NO=@No AND x.EMP_ID=d.EMP_ID)
            GROUP BY d.EMP_ID HAVING COUNT(*)>1;
            """, connection, transaction))
        {
            dupCmd.Parameters.Add("@CountDate", SqlDbType.DateTime).Value = countDate.Value;
            dupCmd.Parameters.Add("@Type", SqlDbType.NChar, 10).Value = type;
            dupCmd.Parameters.Add("@No", SqlDbType.NChar, 20).Value = no;
            await using var reader = await dupCmd.ExecuteReaderAsync(token);
            var lines = new List<string>();
            while (await reader.ReadAsync(token)) lines.Add(reader.GetString(0).Trim() + "\t");
            if (lines.Count > 0) dup = string.Join("\r\n", lines.Take(10));
        }
        if (dup is not null) return new(false, "以下人员当日加班申请重复 \r\n" + dup);
        // 2. 每月加班额（当月出勤参数限额）
        string? countMonth;
        await using (var month = new SqlCommand(
            "SELECT LTRIM(RTRIM(ISNULL(COUNT_MONTH,''))) FROM dbo.HR_ENACTMENT_M WHERE COUNT_MONTH=CONVERT(varchar(6),@CountDate,112);",
            connection, transaction))
        {
            month.Parameters.Add("@CountDate", SqlDbType.DateTime).Value = countDate.Value;
            countMonth = (string?)await month.ExecuteScalarAsync(token);
        }
        if (string.IsNullOrWhiteSpace(countMonth)) return new(true, null);
        await using (var ena = new SqlCommand(
            "SELECT TOP 1 LTRIM(RTRIM(ENACTMENT_TYPE)), LTRIM(RTRIM(ENACTMENT_NO)) FROM dbo.HR_ENACTMENT_M WHERE COUNT_MONTH=@CountMonth;",
            connection, transaction))
        {
            ena.Parameters.Add("@CountMonth", SqlDbType.NChar, 6).Value = countMonth;
            await using var reader = await ena.ExecuteReaderAsync(token);
            if (await reader.ReadAsync(token)) { enaType = reader.GetString(0); enaNo = reader.GetString(1); }
        }
        if (enaType is null) return new(true, null);
        string? exceeded = null;
        await using (var cmd = new SqlCommand("""
            SELECT t.EMP_ID, ISNULL(e.OVERTIME,0), ISNULL(e.REST_OVERTIME,0), ISNULL(e.HOLIDAY_OVERTIME,0),
                   t.OVERTIME, t.REST_OVERTIME, t.HOLIDAY_OVERTIME
            FROM (
                SELECT d.EMP_ID, SUM(d.OVERTIME) OVERTIME, SUM(d.REST_OVERTIME) REST_OVERTIME, SUM(d.HOLIDAY_OVERTIME) HOLIDAY_OVERTIME
                FROM dbo.HR_APPLY_M m INNER JOIN dbo.HR_APPLY_D d ON d.APPLY_TYPE=m.APPLY_TYPE AND d.APPLY_NO=m.APPLY_NO
                WHERE YEAR(m.COUNT_DATE)=YEAR(@CountDate) AND MONTH(m.COUNT_DATE)=MONTH(@CountDate)
                  AND d.EMP_ID IN (SELECT EMP_ID FROM dbo.HR_APPLY_D WHERE APPLY_TYPE=@Type AND APPLY_NO=@No)
                GROUP BY d.EMP_ID
            ) t LEFT JOIN dbo.HR_ENACTMENT_D e
              ON e.EMP_ID=t.EMP_ID AND e.ENACTMENT_TYPE=@EnaType AND e.ENACTMENT_NO=@EnaNo
            WHERE ISNULL(e.OVERTIME,0) < t.OVERTIME OR ISNULL(e.REST_OVERTIME,0) < t.REST_OVERTIME
               OR ISNULL(e.HOLIDAY_OVERTIME,0) < t.HOLIDAY_OVERTIME
               OR (e.EMP_ID IS NULL AND (t.OVERTIME>0 OR t.REST_OVERTIME>0 OR t.HOLIDAY_OVERTIME>0));
            """, connection, transaction))
        {
            cmd.Parameters.Add("@CountDate", SqlDbType.DateTime).Value = countDate.Value;
            cmd.Parameters.Add("@Type", SqlDbType.NChar, 10).Value = type;
            cmd.Parameters.Add("@No", SqlDbType.NChar, 20).Value = no;
            cmd.Parameters.Add("@EnaType", SqlDbType.NChar, 10).Value = enaType;
            cmd.Parameters.Add("@EnaNo", SqlDbType.NChar, 20).Value = enaNo;
            await using var reader = await cmd.ExecuteReaderAsync(token);
            var lines = new List<string>();
            while (await reader.ReadAsync(token))
                lines.Add($"{reader.GetString(0).Trim()}  {Convert.ToString(reader.GetValue(1))}  {Convert.ToString(reader.GetValue(2))}  {Convert.ToString(reader.GetValue(3))}  已录入  {Convert.ToString(reader.GetValue(4))}  {Convert.ToString(reader.GetValue(5))}  {Convert.ToString(reader.GetValue(6))}");
            if (lines.Count > 0) exceeded = string.Join("\r\n", lines.Take(10));
        }
        return exceeded is null
            ? new(true, null)
            : new(false, "以下人员时间超出:\r\n工号--加班时--休息日加班时--节假日加班时\r\n" + exceeded);
    }

    /// <summary>借出单（P_INV_LOAN）AfterSave：库别/产品/批号校验。</summary>

    public static async Task<SprocResult> HrWorktimeAfterSaveAsync(
        SqlConnection connection, SqlTransaction transaction,
        IReadOnlyList<string> pkColumns, IReadOnlyList<string> keyValues, CancellationToken token)
    {
        var requireEnactment = await DomainRuleService.ExistsAsync(connection, transaction,
            "SELECT TOP 1 1 FROM dbo.HR_SETUP WHERE REQUIRE_ENACTMENT=1;", keyValues[0], keyValues[1], token);
        if (!requireEnactment) return new(true, null);
        var type = keyValues[0]; var no = keyValues[1];
        var lines = await DomainRuleService.FindLinesAsync(connection, transaction,
            """
            SELECT a.EMP_ID, a.OVERTIME, a.REST_OVERTIME, a.HOLIDAY_OVERTIME, w.OVERTIME, w.REST_OVERTIME, w.HOLIDAY_OVERTIME
            FROM (SELECT m.EMP_ID, SUM(m.OVERTIME) OVERTIME, SUM(m.REST_OVERTIME) REST_OVERTIME, SUM(m.HOLIDAY_OVERTIME) HOLIDAY_OVERTIME
                  FROM dbo.HR_WORKTIME_M m INNER JOIN dbo.HR_WORKTIME_D d
                    ON d.WORKTIME_TYPE=m.WORKTIME_TYPE AND d.WORKTIME_NO=m.WORKTIME_NO
                  WHERE m.WORKTIME_TYPE=@Type AND m.WORKTIME_NO=@No
                  GROUP BY m.EMP_ID) w
            LEFT JOIN (SELECT d.EMP_ID, SUM(d.OVERTIME) OVERTIME, SUM(d.REST_OVERTIME) REST_OVERTIME, SUM(d.HOLIDAY_OVERTIME) HOLIDAY_OVERTIME
                       FROM dbo.HR_APPLY_M m INNER JOIN dbo.HR_APPLY_D d
                         ON d.APPLY_TYPE=m.APPLY_TYPE AND d.APPLY_NO=m.APPLY_NO
                       WHERE m.COUNT_DATE=(SELECT TOP 1 COUNT_DATE FROM dbo.HR_WORKTIME_M WHERE WORKTIME_TYPE=@Type AND WORKTIME_NO=@No)
                       GROUP BY d.EMP_ID) a ON a.EMP_ID=w.EMP_ID
            WHERE w.OVERTIME > ISNULL(a.OVERTIME,0) OR w.REST_OVERTIME > ISNULL(a.REST_OVERTIME,0)
               OR w.HOLIDAY_OVERTIME > ISNULL(a.HOLIDAY_OVERTIME,0);
            """, type, no, token,
            line: r => $"{r.GetString(0).Trim()}  {Convert.ToDouble(r.GetValue(1))}  {Convert.ToDouble(r.GetValue(2))}  {Convert.ToDouble(r.GetValue(3))}  已录入   {Convert.ToDouble(r.GetValue(4))}  {Convert.ToDouble(r.GetValue(5))}  {Convert.ToDouble(r.GetValue(6))}");
        return lines is null
            ? new(true, null)
            : new(false, "以下人员时间超出:\r\n工号---加班时--休息日加班时--节假日加班时\r\n" + lines);
    }
}
