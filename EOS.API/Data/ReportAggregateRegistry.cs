using EOS.API.Models;

namespace EOS.API.Data;

/// <summary>
/// 汇总报表（RptInteg）受控数据源注册表：报表编号 → 参数化聚合 SQL + 输出列。
/// </summary>
/// <remarks>
/// 报表数据源全部是服务端常量（不接受客户端 SQL 片段、表名或列名）；参数值按查询条件序号
/// 绑定并参数化传入。列清单在此声明，既作为前端/PDF 的列元数据，也是排序字段的白名单
/// （排序只能用这里声明过的列）。
/// </remarks>
public static class ReportAggregateRegistry
{
    /// <summary>按报表编号取聚合数据源；未注册（或未指定报表）返回 null，回落主表查询。</summary>
    public static ReportAggregate? Find(string? reportId)
        => string.IsNullOrWhiteSpace(reportId) ? null : Map.GetValueOrDefault(reportId.Trim());

    public static IReadOnlyCollection<string> RegisteredReportIds => Map.Keys;

    /// <summary>人力状况分析表：在职人数与本月入职/离职（本月请假列旧实现从未填充，保持恒 0）。</summary>
    private static readonly ReportAggregate HrEmployeeStatus = new(
        "HR_Employee_1",
        """
        SELECT t.DEPT_ID, t.RENSHU, ISNULL(r.BENYUE_RU, 0) AS BENYUE_RU, ISNULL(l.BENYUE_LI, 0) AS BENYUE_LI,
               CAST(0 AS int) AS BENYUE_QJ, d.DEPT_NAME
        FROM (
            SELECT e.DEPT_ID, COUNT(*) AS RENSHU
            FROM dbo.HR_EMPLOYEE e
            WHERE e.STATE<4
            GROUP BY e.DEPT_ID
        ) t
        LEFT JOIN (
            SELECT e.DEPT_ID, COUNT(*) AS BENYUE_RU
            FROM dbo.HR_EMPLOYEE e
            WHERE YEAR(e.IN_DATE)=YEAR(GETDATE()) AND MONTH(e.IN_DATE)=MONTH(GETDATE())
            GROUP BY e.DEPT_ID
        ) r ON r.DEPT_ID=t.DEPT_ID
        LEFT JOIN (
            SELECT e.DEPT_ID, COUNT(*) AS BENYUE_LI
            FROM dbo.HR_EMPLOYEE e
            WHERE YEAR(e.DIMISSION_DATE)=YEAR(GETDATE()) AND MONTH(e.DIMISSION_DATE)=MONTH(GETDATE())
            GROUP BY e.DEPT_ID
        ) l ON l.DEPT_ID=t.DEPT_ID
        LEFT JOIN dbo.DEPT d ON d.DEPT_ID=t.DEPT_ID
        """,
        "DEPT_ID",
        [],
        [
            new ReportColumn("DEPT_ID", "部门编号", "nchar"),
            new ReportColumn("RENSHU", "人数", "int"),
            new ReportColumn("BENYUE_RU", "本月入职", "int"),
            new ReportColumn("BENYUE_LI", "本月离职", "int"),
            new ReportColumn("BENYUE_QJ", "本月请假", "int"),
            new ReportColumn("DEPT_NAME", "部门", "nvarchar"),
        ]);

    /// <summary>部门籍贯分析表：按部门 + 籍贯统计人数与占部门比例。</summary>
    private static readonly ReportAggregate HrEmployeeProvince = new(
        "HR_Employee_3",
        """
        SELECT t.DEPT_ID, t.PROVINCE_ID, t.MAN_COUNT, t.MAN_PERCENT, d.DEPT_NAME,
               ISNULL(p.PROVINCE_NAME, '----') + ' 占%' AS PROVINCE_NAME
        FROM (
            SELECT e.DEPT_ID, e.PROVINCE_ID, COUNT(*) AS MAN_COUNT,
                   CAST(CAST(COUNT(*) AS float) / NULLIF(SUM(COUNT(*)) OVER (PARTITION BY e.DEPT_ID), 0) * 100.0 AS decimal(18,1)) AS MAN_PERCENT
            FROM dbo.HR_EMPLOYEE e
            WHERE e.STATE<=4
            GROUP BY e.DEPT_ID, e.PROVINCE_ID
        ) t
        LEFT JOIN dbo.DEPT d ON d.DEPT_ID=t.DEPT_ID
        LEFT JOIN dbo.HR_PROVINCE p ON p.PROVINCE_ID=t.PROVINCE_ID
        """,
        "DEPT_ID, PROVINCE_ID",
        [],
        [
            new ReportColumn("DEPT_ID", "部门编号", "nchar"),
            new ReportColumn("PROVINCE_ID", "籍贯编号", "nchar"),
            new ReportColumn("MAN_COUNT", "人数", "int"),
            new ReportColumn("MAN_PERCENT", "占比(%)", "decimal"),
            new ReportColumn("DEPT_NAME", "部门", "nvarchar"),
            new ReportColumn("PROVINCE_NAME", "籍贯", "nvarchar"),
        ]);

    /// <summary>部门民族分析表：按部门 + 民族统计人数与占部门比例。</summary>
    private static readonly ReportAggregate HrEmployeeNation = new(
        "HR_Employee_4",
        """
        SELECT t.DEPT_ID, t.NATION_ID, t.MAN_COUNT, t.MAN_PERCENT, d.DEPT_NAME,
               ISNULL(n.NATION_NAME, '----') + ' 占%' AS NATION_NAME
        FROM (
            SELECT e.DEPT_ID, e.NATION_ID, COUNT(*) AS MAN_COUNT,
                   CAST(CAST(COUNT(*) AS float) / NULLIF(SUM(COUNT(*)) OVER (PARTITION BY e.DEPT_ID), 0) * 100.0 AS decimal(18,1)) AS MAN_PERCENT
            FROM dbo.HR_EMPLOYEE e
            WHERE e.STATE<=4
            GROUP BY e.DEPT_ID, e.NATION_ID
        ) t
        LEFT JOIN dbo.DEPT d ON d.DEPT_ID=t.DEPT_ID
        LEFT JOIN dbo.HR_NATION n ON n.NATION_ID=t.NATION_ID
        """,
        "DEPT_ID, NATION_ID",
        [],
        [
            new ReportColumn("DEPT_ID", "部门编号", "nchar"),
            new ReportColumn("NATION_ID", "民族编号", "nchar"),
            new ReportColumn("MAN_COUNT", "人数", "int"),
            new ReportColumn("MAN_PERCENT", "占比(%)", "decimal"),
            new ReportColumn("DEPT_NAME", "部门", "nvarchar"),
            new ReportColumn("NATION_NAME", "民族", "nvarchar"),
        ]);

    /// <summary>部门学历分析表：按部门 + 学历统计人数与占部门比例。</summary>
    private static readonly ReportAggregate HrEmployeeDiploma = new(
        "HR_Employee_5",
        """
        SELECT t.DEPT_ID, t.DIPLOMA_ID, t.MAN_COUNT, t.MAN_PERCENT, d.DEPT_NAME,
               ISNULL(dm.DIPLOMA_NAME, '----') + ' 占%' AS DIPLOMA_NAME
        FROM (
            SELECT e.DEPT_ID, e.DIPLOMA_ID, COUNT(*) AS MAN_COUNT,
                   CAST(CAST(COUNT(*) AS float) / NULLIF(SUM(COUNT(*)) OVER (PARTITION BY e.DEPT_ID), 0) * 100.0 AS decimal(18,1)) AS MAN_PERCENT
            FROM dbo.HR_EMPLOYEE e
            WHERE e.STATE<=4
            GROUP BY e.DEPT_ID, e.DIPLOMA_ID
        ) t
        LEFT JOIN dbo.DEPT d ON d.DEPT_ID=t.DEPT_ID
        LEFT JOIN dbo.HR_DIPLOMA dm ON dm.DIPLOMA_ID=t.DIPLOMA_ID
        """,
        "DEPT_ID, DIPLOMA_ID",
        [],
        [
            new ReportColumn("DEPT_ID", "部门编号", "nchar"),
            new ReportColumn("DIPLOMA_ID", "学历编号", "nchar"),
            new ReportColumn("MAN_COUNT", "人数", "int"),
            new ReportColumn("MAN_PERCENT", "占比(%)", "decimal"),
            new ReportColumn("DEPT_NAME", "部门", "nvarchar"),
            new ReportColumn("DIPLOMA_NAME", "学历", "nvarchar"),
        ]);

    /// <summary>部门年龄分析表：按部门统计各年龄段人数（按整月龄计算，生日为空计入 16 以下）。</summary>
    private static readonly ReportAggregate HrEmployeeAge = new(
        "HR_Employee_6",
        """
        WITH AGE_MONTH AS (
            SELECT e.DEPT_ID,
                   (YEAR(GETDATE()) - YEAR(e.BIRTHDAY)) * 12 - MONTH(e.BIRTHDAY) + MONTH(GETDATE()) AS MONTHS,
                   e.BIRTHDAY
            FROM dbo.HR_EMPLOYEE e
            WHERE e.STATE<=4
        )
        SELECT t.DEPT_ID, t.AGE_ID, t.MAN_COUNT, d.DEPT_NAME
        FROM (
            SELECT DEPT_ID, '16 以下' AS AGE_ID, COUNT(*) AS MAN_COUNT FROM AGE_MONTH WHERE MONTHS/12<16 OR BIRTHDAY IS NULL GROUP BY DEPT_ID
            UNION ALL
            SELECT DEPT_ID, '16~19', COUNT(*) FROM AGE_MONTH WHERE MONTHS/12 BETWEEN 16 AND 19 GROUP BY DEPT_ID
            UNION ALL
            SELECT DEPT_ID, '20~29', COUNT(*) FROM AGE_MONTH WHERE MONTHS/12 BETWEEN 20 AND 29 GROUP BY DEPT_ID
            UNION ALL
            SELECT DEPT_ID, '30~39', COUNT(*) FROM AGE_MONTH WHERE MONTHS/12 BETWEEN 30 AND 39 GROUP BY DEPT_ID
            UNION ALL
            SELECT DEPT_ID, '40以上', COUNT(*) FROM AGE_MONTH WHERE MONTHS/12>39 GROUP BY DEPT_ID
        ) t
        LEFT JOIN dbo.DEPT d ON d.DEPT_ID=t.DEPT_ID
        """,
        "AGE_ID, DEPT_ID",
        [],
        [
            new ReportColumn("DEPT_ID", "部门编号", "nchar"),
            new ReportColumn("AGE_ID", "年龄段", "nvarchar"),
            new ReportColumn("MAN_COUNT", "人数", "int"),
            new ReportColumn("DEPT_NAME", "部门", "nvarchar"),
        ]);

    /// <summary>部门工龄分析表：按部门统计各工龄段人数（按整月龄计算，入职日期为空计入 3 月以下）。</summary>
    private static readonly ReportAggregate HrEmployeeSeniority = new(
        "HR_Employee_7",
        """
        WITH WORK_MONTH AS (
            SELECT e.DEPT_ID,
                   (YEAR(GETDATE()) - YEAR(e.IN_DATE)) * 12 - MONTH(e.IN_DATE) + MONTH(GETDATE()) AS MONTHS,
                   e.IN_DATE
            FROM dbo.HR_EMPLOYEE e
            WHERE e.STATE<=4
        )
        SELECT t.DEPT_ID, t.AGE_ID, t.MAN_COUNT, d.DEPT_NAME
        FROM (
            SELECT DEPT_ID, 'A.3月以下' AS AGE_ID, COUNT(*) AS MAN_COUNT FROM WORK_MONTH WHERE MONTHS<3 OR IN_DATE IS NULL GROUP BY DEPT_ID
            UNION ALL
            SELECT DEPT_ID, 'B.3~6月', COUNT(*) FROM WORK_MONTH WHERE MONTHS BETWEEN 3 AND 5 GROUP BY DEPT_ID
            UNION ALL
            SELECT DEPT_ID, 'C.6月~1年', COUNT(*) FROM WORK_MONTH WHERE MONTHS BETWEEN 6 AND 11 GROUP BY DEPT_ID
            UNION ALL
            SELECT DEPT_ID, 'D.1~3年', COUNT(*) FROM WORK_MONTH WHERE MONTHS BETWEEN 12 AND 35 GROUP BY DEPT_ID
            UNION ALL
            SELECT DEPT_ID, 'E.3~5年', COUNT(*) FROM WORK_MONTH WHERE MONTHS BETWEEN 36 AND 59 GROUP BY DEPT_ID
            UNION ALL
            SELECT DEPT_ID, 'F.5~10年', COUNT(*) FROM WORK_MONTH WHERE MONTHS BETWEEN 60 AND 119 GROUP BY DEPT_ID
            UNION ALL
            SELECT DEPT_ID, 'G.10年以上', COUNT(*) FROM WORK_MONTH WHERE MONTHS>119 GROUP BY DEPT_ID
        ) t
        LEFT JOIN dbo.DEPT d ON d.DEPT_ID=t.DEPT_ID
        """,
        "AGE_ID, DEPT_ID",
        [],
        [
            new ReportColumn("DEPT_ID", "部门编号", "nchar"),
            new ReportColumn("AGE_ID", "工龄段", "nvarchar"),
            new ReportColumn("MAN_COUNT", "人数", "int"),
            new ReportColumn("DEPT_NAME", "部门", "nvarchar"),
        ]);

    /// <summary>
    /// 考勤分析表：应到（期初在职）/ 实到（区间内有出勤或加班）/ 请假与迟到人员名单。
    /// 日期区间由考勤日期条件（序号 1）的起止值绑定；人员名单按工号排序拼接
    /// （旧实现用游标拼接、顺序不确定且带尾空格，此处为确定性的等价表述）。
    /// </summary>
    private static readonly ReportAggregate HrDiary = new(
        "HR_Diary_1",
        """
        WITH YING AS (
            SELECT e.DEPT_ID, COUNT(*) AS YINGDAO
            FROM dbo.HR_EMPLOYEE e
            WHERE e.IN_DATE<@date1 AND (e.DIMISSION_DATE IS NULL OR e.DIMISSION_DATE>@date1)
            GROUP BY e.DEPT_ID
        ),
        SHI AS (
            SELECT e.DEPT_ID, COUNT(DISTINCT e.EMP_ID) AS SHIDAO
            FROM dbo.HR_DIARY d
            JOIN dbo.HR_EMPLOYEE e ON e.EMP_ID=d.EMP_ID
            WHERE d.COUNT_DATE BETWEEN @date1 AND @date2 AND (d.WORKTIME>0 OR d.OVERTIME>0)
            GROUP BY e.DEPT_ID
        ),
        QJ AS (
            SELECT e.DEPT_ID, STRING_AGG(e.EMP_NAME, ' ') WITHIN GROUP (ORDER BY e.EMP_ID) AS QINGJIA
            FROM (SELECT DISTINCT d.EMP_ID FROM dbo.HR_DIARY d WHERE d.COUNT_DATE BETWEEN @date1 AND @date2 AND d.REMARK LIKE '%{请假}%') x
            JOIN dbo.HR_EMPLOYEE e ON e.EMP_ID=x.EMP_ID
            GROUP BY e.DEPT_ID
        ),
        CD AS (
            SELECT e.DEPT_ID, STRING_AGG(e.EMP_NAME, ' ') WITHIN GROUP (ORDER BY e.EMP_ID) AS CHIDAO
            FROM (SELECT DISTINCT d.EMP_ID FROM dbo.HR_DIARY d WHERE d.COUNT_DATE BETWEEN @date1 AND @date2 AND d.LATE_TIMES>0) x
            JOIN dbo.HR_EMPLOYEE e ON e.EMP_ID=x.EMP_ID
            GROUP BY e.DEPT_ID
        )
        SELECT y.DEPT_ID, y.YINGDAO, ISNULL(s.SHIDAO, 0) AS SHIDAO, q.QINGJIA, c.CHIDAO, d.DEPT_NAME
        FROM YING y
        LEFT JOIN SHI s ON s.DEPT_ID=y.DEPT_ID
        LEFT JOIN QJ q ON q.DEPT_ID=y.DEPT_ID
        LEFT JOIN CD c ON c.DEPT_ID=y.DEPT_ID
        LEFT JOIN dbo.DEPT d ON d.DEPT_ID=y.DEPT_ID
        """,
        "DEPT_ID",
        [
            new ReportAggregateParameter("date1", "nvarchar", 20, SerialNo: 1),
            new ReportAggregateParameter("date2", "nvarchar", 20, SerialNo: 1, IsTo: true),
        ],
        [
            new ReportColumn("DEPT_ID", "部门编号", "nchar"),
            new ReportColumn("YINGDAO", "应到人数", "int"),
            new ReportColumn("SHIDAO", "实到人数", "int"),
            new ReportColumn("QINGJIA", "请假人员", "nvarchar"),
            new ReportColumn("CHIDAO", "迟到人员", "nvarchar"),
            new ReportColumn("DEPT_NAME", "部门", "nvarchar"),
        ]);

    private static readonly Dictionary<string, ReportAggregate> Map =
        new(StringComparer.OrdinalIgnoreCase)
        {
            [HrEmployeeStatus.ReportId] = HrEmployeeStatus,
            [HrEmployeeProvince.ReportId] = HrEmployeeProvince,
            [HrEmployeeNation.ReportId] = HrEmployeeNation,
            [HrEmployeeDiploma.ReportId] = HrEmployeeDiploma,
            [HrEmployeeAge.ReportId] = HrEmployeeAge,
            [HrEmployeeSeniority.ReportId] = HrEmployeeSeniority,
            [HrDiary.ReportId] = HrDiary,
        };
}
