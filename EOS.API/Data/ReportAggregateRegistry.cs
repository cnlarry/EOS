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

    /// <summary>
    /// 库存日报：期初结存（最近一期已确认月结 + 其后到区间起点的流水，按加权平均单价）+
    /// 本期收发明细 + 每日发出成本（当日累计加权平均）+ 品名/规格/单据名称/类别/颜色。
    /// </summary>
    /// <remarks>
    /// 参数按查询条件绑定：仓库/类别/料号/日期四个范围条件取起止值，成本计法（`@cb1`）取单值。
    /// **范围条件的空值语义**：旧实现把空上界替换为 `char(255)` 哨兵，而该哨兵在
    /// `Chinese_PRC_CI_AS` 下排序位置并不在末尾（`N'Z9' &lt;= NCHAR(255)` 实测为假），
    /// 会**静默截断上界**；此处改为"空值即无界"的显式谓词（语义即旧实现的本意，且可走索引）。
    /// **有意差异（决策 #118，两处旧实现公式缺陷）**：① 旧期初单价的累加写成
    /// `@price_sum=(@price_sum*@qty_sum+@price*@qty)/(@qty_sum+@qty)`，而 SQL Server 在同一条 SELECT 内
    /// 变量赋值"左到右立即生效"（实测 `SELECT @a=@a+1,@b=@a` ⇒ `@b=2`），实际分母是 `Q_old+2q`，
    /// 使每笔进价权重失真（首笔尤为明显）；此处按**加权平均** `SUM(QTY*PRICE)/SUM(QTY)` 实现。
    /// ② 旧"每日发出成本"的游标首行（排序第一对的期初行）被首次取值消费掉、未进入累计，
    /// 且算出均价后把当日发出量从累计里再扣一次；此处按当日累计加权平均实现
    /// （与 `INV_PRO_DEPOT.COST_PRICE` 的维护口径一致）。
    /// **保留的旧行为**：`@jc1`（全部/有结存/无结存）在旧实现里整段被注释 ⇒ 参数无效，此处同样不参与；
    /// 料件类别硬编码 `PRO_TYPE='3'`（原料），与旧实现一致。
    /// </remarks>
    private static readonly ReportAggregate InventoryDaily = new(
        "INV_Pro_Depot_1",
        """
        WITH PAIR AS (
            -- 同一 (库别, 料号) 在余额表中可能对应多行，先取出唯一组合，
            -- 否则后续按这两列关联流水时会成倍放大期初与本期收发。
            SELECT DISTINCT i.DEPOT_ID, i.PRO_NO
            FROM dbo.INV_PRO_DEPOT i
            JOIN dbo.PRODUCT pr ON pr.PRO_NO = i.PRO_NO
            WHERE pr.PRO_TYPE = '3'
              AND (@depot1 IS NULL OR @depot1 = '' OR i.DEPOT_ID >= @depot1)
              AND (@depot2 IS NULL OR @depot2 = '' OR i.DEPOT_ID <= @depot2)
              AND (@pro1 IS NULL OR @pro1 = '' OR i.PRO_NO >= @pro1)
              AND (@pro2 IS NULL OR @pro2 = '' OR i.PRO_NO <= @pro2)
              AND (@sort1 IS NULL OR @sort1 = '' OR pr.SORT_ID >= @sort1)
              AND (@sort2 IS NULL OR @sort2 = '' OR pr.SORT_ID <= @sort2)
        ),
        MONTHROW AS (
            SELECT pa.DEPOT_ID, pa.PRO_NO, x.MONTH_DATE, x.QTY, x.PRICE
            FROM PAIR pa
            CROSS APPLY (
                SELECT TOP 1 m.MONTH_DATE, d.QTY, d.PRICE
                FROM dbo.INV_PRO_MONTH_M m
                JOIN dbo.INV_PRO_MONTH_D d ON d.MONTH_TYPE = m.MONTH_TYPE AND d.MONTH_NO = m.MONTH_NO
                WHERE d.DEPOT_ID = pa.DEPOT_ID AND d.PRO_NO = pa.PRO_NO AND m.MONTH_DATE < @date1
                ORDER BY m.MONTH_DATE DESC
            ) x
            WHERE EXISTS (
                SELECT 1 FROM dbo.INV_PRO_MONTH_M m
                JOIN dbo.INV_PRO_MONTH_D d ON d.MONTH_TYPE = m.MONTH_TYPE AND d.MONTH_NO = m.MONTH_NO
                WHERE d.DEPOT_ID = pa.DEPOT_ID AND d.PRO_NO = pa.PRO_NO
                  AND m.MONTH_DATE < @date1 AND m.CONFIRM_TAG = 1
            )
        ),
        OPENROWS AS (
            SELECT DEPOT_ID, PRO_NO, MONTH_DATE AS APP_DATE, QTY, PRICE FROM MONTHROW
            UNION ALL
            SELECT l.DEPOT_ID, l.PRO_NO, l.MUTUALITY_DATE,
                   CASE WHEN l.IN_OUT = 'I' THEN l.QTY ELSE -l.QTY END, l.PRICE
            FROM dbo.INV_DEPOT_LOG l
            JOIN MONTHROW mr ON mr.DEPOT_ID = l.DEPOT_ID AND mr.PRO_NO = l.PRO_NO
            WHERE l.MUTUALITY_DATE > mr.MONTH_DATE AND l.MUTUALITY_DATE < @date1
            UNION ALL
            SELECT l.DEPOT_ID, l.PRO_NO, l.MUTUALITY_DATE,
                   CASE WHEN l.IN_OUT = 'I' THEN l.QTY ELSE -l.QTY END, l.PRICE
            FROM dbo.INV_DEPOT_LOG l
            JOIN PAIR pa ON pa.DEPOT_ID = l.DEPOT_ID AND pa.PRO_NO = l.PRO_NO
            WHERE l.MUTUALITY_DATE < @date1
              AND NOT EXISTS (SELECT 1 FROM MONTHROW mr WHERE mr.DEPOT_ID = l.DEPOT_ID AND mr.PRO_NO = l.PRO_NO)
        ),
        OPENING AS (
            SELECT o.DEPOT_ID, o.PRO_NO,
                   SUM(o.QTY) AS QTY_Q,
                   CASE WHEN SUM(o.QTY) = 0 THEN 0 ELSE SUM(o.QTY * o.PRICE) / SUM(o.QTY) END AS PRICE_Q
            FROM (
                SELECT DEPOT_ID, PRO_NO, APP_DATE, QTY, PRICE FROM OPENROWS
                UNION ALL
                SELECT pa.DEPOT_ID, pa.PRO_NO, CAST('1900-01-01' AS datetime), CAST(0 AS float), CAST(0 AS float)
                FROM PAIR pa
            ) o
            GROUP BY o.DEPOT_ID, o.PRO_NO
        ),
        PERIOD AS (
            SELECT l.DEPOT_ID, l.PRO_NO, l.MUTUALITY_DATE AS APP_DATE,
                   CASE WHEN l.IN_OUT = 'I' THEN l.QTY ELSE 0 END AS QTY_J,
                   CASE WHEN l.IN_OUT = 'I' THEN l.PRICE ELSE 0 END AS PRICE_J,
                   CASE WHEN l.IN_OUT = 'O' THEN l.QTY ELSE 0 END AS QTY_X,
                   CASE WHEN l.IN_OUT = 'O' THEN l.PRICE ELSE 0 END AS PRICE_X,
                   l.MUTUALITY_TYPE AS BILL_CODE, l.MUTUALITY_NO AS BILL_NO
            FROM dbo.INV_DEPOT_LOG l
            JOIN PAIR pa ON pa.DEPOT_ID = l.DEPOT_ID AND pa.PRO_NO = l.PRO_NO
            WHERE l.MUTUALITY_DATE BETWEEN @date1 AND @date2
        ),
        LIST AS (
            SELECT o.DEPOT_ID, o.PRO_NO, CAST('1900-01-01' AS datetime) AS APP_DATE, o.QTY_Q,
                   CAST(0 AS float) AS QTY_J, CAST(0 AS float) AS QTY_X, o.PRICE_Q,
                   CAST(0 AS float) AS PRICE_J, CAST(0 AS float) AS PRICE_X,
                   CAST(NULL AS nchar(10)) AS BILL_CODE, CAST(NULL AS nchar(20)) AS BILL_NO
            FROM OPENING o
            UNION ALL
            SELECT p.DEPOT_ID, p.PRO_NO, p.APP_DATE, CAST(0 AS float), p.QTY_J, p.QTY_X,
                   CAST(0 AS float), p.PRICE_J, p.PRICE_X, p.BILL_CODE, p.BILL_NO
            FROM PERIOD p
        ),
        DAILY AS (
            SELECT DEPOT_ID, PRO_NO, APP_DATE,
                   SUM(QTY_Q + QTY_J) AS QTY_IN,
                   SUM(QTY_Q * PRICE_Q + QTY_J * PRICE_J) AS AMT_IN,
                   SUM(QTY_X) AS QTY_X
            FROM LIST
            GROUP BY DEPOT_ID, PRO_NO, APP_DATE
        ),
        COST AS (
            SELECT d.DEPOT_ID, d.PRO_NO, d.APP_DATE, d.QTY_X,
                   SUM(d.QTY_IN) OVER (PARTITION BY d.DEPOT_ID, d.PRO_NO ORDER BY d.APP_DATE ROWS UNBOUNDED PRECEDING) AS QTY_RUN,
                   SUM(d.AMT_IN) OVER (PARTITION BY d.DEPOT_ID, d.PRO_NO ORDER BY d.APP_DATE ROWS UNBOUNDED PRECEDING) AS AMT_RUN
            FROM DAILY d
        )
        SELECT l.DEPOT_ID, l.PRO_NO, l.APP_DATE, l.QTY_Q, l.QTY_J, l.QTY_X,
               CASE WHEN @cb1 = 1 THEN CASE WHEN ISNULL(pr.LAST_PURCHASE_PRICE, 0) > 0 AND ISNULL(cu.CURR_RATE, 0) > 0
                                            THEN pr.LAST_PURCHASE_PRICE * cu.CURR_RATE ELSE 0 END
                    ELSE l.PRICE_Q END AS PRICE_Q,
               CASE WHEN @cb1 = 1 THEN CASE WHEN ISNULL(pr.LAST_PURCHASE_PRICE, 0) > 0 AND ISNULL(cu.CURR_RATE, 0) > 0
                                            THEN pr.LAST_PURCHASE_PRICE * cu.CURR_RATE ELSE 0 END
                    ELSE l.PRICE_J END AS PRICE_J,
               CASE WHEN @cb1 = 1 THEN CASE WHEN ISNULL(pr.LAST_PURCHASE_PRICE, 0) > 0 AND ISNULL(cu.CURR_RATE, 0) > 0
                                            THEN pr.LAST_PURCHASE_PRICE * cu.CURR_RATE ELSE 0 END
                    ELSE CASE WHEN l.QTY_X > 0
                              THEN CASE WHEN ISNULL(c.QTY_RUN, 0) > 0 THEN c.AMT_RUN / c.QTY_RUN ELSE 0 END
                              ELSE 0 END END AS PRICE_X,
               l.BILL_CODE, l.BILL_NO, pr.PRO_NAME, pr.PRO_SPEC, b.BILL_NAME,
               pr.SORT_ID, pr.COLOR_ID, s.SORT_NAME, cl.COLOR_NAME
        FROM LIST l
        LEFT JOIN COST c ON c.DEPOT_ID = l.DEPOT_ID AND c.PRO_NO = l.PRO_NO AND c.APP_DATE = l.APP_DATE
        LEFT JOIN dbo.PRODUCT pr ON pr.PRO_NO = l.PRO_NO
        LEFT JOIN dbo.BILLKIND b ON b.BILL_CODE = l.BILL_CODE
        LEFT JOIN dbo.[SORT] s ON s.SORT_ID = pr.SORT_ID
        LEFT JOIN dbo.COLOR cl ON cl.COLOR_ID = pr.COLOR_ID
        LEFT JOIN dbo.CURR cu ON cu.CURR_ID = pr.LAST_PURCHASE_CURR_ID
        """,
        "DEPOT_ID, PRO_NO, APP_DATE",
        [
            new ReportAggregateParameter("depot1", "nchar", 10, SerialNo: 1),
            new ReportAggregateParameter("depot2", "nchar", 10, SerialNo: 1, IsTo: true),
            new ReportAggregateParameter("sort1", "nchar", 10, SerialNo: 2),
            new ReportAggregateParameter("sort2", "nchar", 10, SerialNo: 2, IsTo: true),
            new ReportAggregateParameter("pro1", "nvarchar", 30, SerialNo: 3),
            new ReportAggregateParameter("pro2", "nvarchar", 30, SerialNo: 3, IsTo: true),
            new ReportAggregateParameter("date1", "datetime", 8, SerialNo: 4),
            new ReportAggregateParameter("date2", "datetime", 8, SerialNo: 4, IsTo: true),
            new ReportAggregateParameter("cb1", "int", 4, SerialNo: 6),
        ],
        [
            new ReportColumn("DEPOT_ID", "仓库编号", "nchar"),
            new ReportColumn("PRO_NO", "料号", "nchar"),
            new ReportColumn("APP_DATE", "日期", "datetime"),
            new ReportColumn("QTY_Q", "期初数量", "float"),
            new ReportColumn("QTY_J", "本期收入", "float"),
            new ReportColumn("QTY_X", "本期发出", "float"),
            new ReportColumn("PRICE_Q", "期初单价", "float"),
            new ReportColumn("PRICE_J", "收入单价", "float"),
            new ReportColumn("PRICE_X", "发出单价", "float"),
            new ReportColumn("BILL_CODE", "单据类别", "nchar"),
            new ReportColumn("BILL_NO", "单据号码", "nchar"),
            new ReportColumn("PRO_NAME", "品名", "nvarchar"),
            new ReportColumn("PRO_SPEC", "规格", "nvarchar"),
            new ReportColumn("BILL_NAME", "单据名称", "nvarchar"),
            new ReportColumn("SORT_ID", "类别编号", "nchar"),
            new ReportColumn("COLOR_ID", "颜色编号", "nchar"),
            new ReportColumn("SORT_NAME", "类别", "nvarchar"),
            new ReportColumn("COLOR_NAME", "颜色", "nvarchar"),
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
            // 库存日报三个变体（正表 / 横表 / 汇总）在旧实现里共用同一个过程、同参数、同输出列，
            // 差异只存在于旧打印模板 ⇒ 现代引擎下三者数据相同
            [InventoryDaily.ReportId] = InventoryDaily,
            ["INV_Pro_Depot_1_H"] = InventoryDaily with { ReportId = "INV_Pro_Depot_1_H" },
            ["INV_Pro_Depot_1_sum"] = InventoryDaily with { ReportId = "INV_Pro_Depot_1_sum" },
        };
}
