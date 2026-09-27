-- ============================================================================
-- EOS.ERP migration 145: 下线报表族已移植部分 —— HR 分析报表 7 个过程
-- ----------------------------------------------------------------------------
-- 背景：报表没有唯一调用入口，唯一运行期入口是 `REPORT_SORT.SORT_FIELDS` 的花括号引用
-- （形如 `{P_RPT_X.COL}`，由 `ReportRepository` 解析后按名执行过程）。报表族 8 个在册过程里，
-- 7 个（HR 分析报表）已移植为**服务端受控聚合数据源**
-- （`ReportAggregateRegistry`：报表编号 → 参数化聚合 SQL + 声明列 + 默认排序），
-- 报表定义改由注册表解析 ⇒ 运行期不再按名调用这 7 个过程。
-- 于是本迁移做两件事：
--   ① 把 `REPORT_SORT.SORT_FIELDS` 的花括号引用改写为**纯列名**（列即聚合输出列）；
--   ② DROP 这 7 个过程。
-- 保留：`P_RPT_INV_PRO_DEPOT_1`（库存日报，7599 字符、含顺序递推，移植口径待）
-- 与 `P_HRM_WAGE_CALC`（运行期按名调用，退役路径见）。
-- 非钩子对象下线四判据（用户拍板方案 B）：三方核查零引用 / SSDT 快照可还原（7 个逐个确认存在）/
-- 本迁移内置守卫且失败即 THROW / 登记于覆盖率报告 §八。
-- 幂等：REPORT_SORT 改写按当前值幂等（已改写则不再命中）；DROP 前逐个判断存在；末尾断言构成。
-- ============================================================================

SET NOCOUNT ON;

SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET ANSI_PADDING ON;
SET ANSI_WARNINGS ON;
SET CONCAT_NULL_YIELDS_NULL ON;
SET ARITHABORT ON;

DECLARE @GUARD_MESSAGE NVARCHAR(400) = N'本脚本只能在 EOS.ERP 数据库内执行，当前库为 ' + DB_NAME() + N'。';
IF DB_NAME() <> N'EOS.ERP'
    THROW 50000, @GUARD_MESSAGE, 1;

IF OBJECT_ID(N'tempdb..#Target') IS NOT NULL DROP TABLE #Target;
CREATE TABLE #Target (SPROC NVARCHAR(128) PRIMARY KEY);
INSERT INTO #Target (SPROC) VALUES
    (N'P_RPT_HR_EMPLOYEE_1'), (N'P_RPT_HR_EMPLOYEE_3'), (N'P_RPT_HR_EMPLOYEE_4'),
    (N'P_RPT_HR_EMPLOYEE_5'), (N'P_RPT_HR_EMPLOYEE_6'), (N'P_RPT_HR_EMPLOYEE_7'),
    (N'P_RPT_HR_DIARY_1');

/* ---------- 守卫（一）：待下线对象存在且数量相符 ---------- */
IF (SELECT COUNT(*) FROM #Target p JOIN sys.objects o ON o.name = p.SPROC AND o.type = 'P' AND o.schema_id = SCHEMA_ID('dbo')) <> 7
    THROW 50101, N'待下线报表过程清单与库内实际不符（应为 7 个），迁移中止。', 1;

/* ---------- 守卫（二）：集合外库内对象不得引用 ---------- */
IF EXISTS (
    SELECT 1 FROM sys.sql_expression_dependencies d
    JOIN #Target p ON p.SPROC = d.referenced_entity_name
    WHERE d.referenced_class = 1
      AND ISNULL(d.referenced_schema_name, N'dbo') = N'dbo'
      AND d.referencing_id NOT IN (SELECT o.object_id FROM sys.objects o JOIN #Target q ON q.SPROC = o.name)
)
    THROW 50102, N'待下线报表过程仍被集合外的库内对象引用，迁移中止。', 1;

/* ---------- 守卫（三）：运行期元数据不得引用 ---------- */
IF EXISTS (SELECT 1 FROM dbo.MODULES m JOIN #Target p ON m.UPDATE_SP = p.SPROC OR m.AFTERSAVE_SP = p.SPROC)
    THROW 50103, N'待下线报表过程仍被 MODULES 钩子字段引用，迁移中止。', 1;

IF EXISTS (
    SELECT 1 FROM dbo.WORKBENCH_DEFINITION_SNAPSHOT
    WHERE IS_CURRENT = 1
      AND (JSON_VALUE(DEFINITION_JSON, '$.BusinessRule.AfterSaveSproc') IS NOT NULL
           OR JSON_VALUE(DEFINITION_JSON, '$.BusinessRule.WorkflowSproc') IS NOT NULL)
)
    THROW 50104, N'仍有当前快照登记了旧保存后 / 批核过程，迁移中止。', 1;

/* ---------- 改写报表排序引用：`{过程.列}` → `列` ---------- */
UPDATE s
   SET s.SORT_FIELDS = LTRIM(RTRIM(REPLACE(REPLACE(s.SORT_FIELDS, N'{' + p.SPROC + N'.', N''), N'}', N'')))
  FROM dbo.REPORT_SORT s
  JOIN #Target p ON ISNULL(s.SORT_FIELDS, N'') LIKE N'%' + p.SPROC + N'%';

IF EXISTS (SELECT 1 FROM dbo.REPORT_SORT s JOIN #Target p ON ISNULL(s.SORT_FIELDS, N'') LIKE N'%' + p.SPROC + N'%')
    THROW 50105, N'报表排序字段仍引用待下线过程（改写未生效），迁移中止。', 1;

-- 只对本批涉及的报表断言"不再有花括号引用"：保留过程（库存日报）的排序字段仍带花括号，属正常
IF EXISTS (
    SELECT 1 FROM dbo.REPORT_SORT
    WHERE REPORT_ID IN (N'HR_Employee_1', N'HR_Employee_3', N'HR_Employee_4', N'HR_Employee_5',
                        N'HR_Employee_6', N'HR_Employee_7', N'HR_Diary_1')
      AND (ISNULL(SORT_FIELDS, N'') LIKE N'%{%' OR ISNULL(SORT_FIELDS, N'') LIKE N'%}%')
)
    THROW 50106, N'本批报表的排序字段仍有花括号引用（须为纯列名），迁移中止。', 1;

/* ---------- 下线 ---------- */
DECLARE @Dropped INT = 0;
DECLARE @Name NVARCHAR(128);
DECLARE drop_cursor CURSOR LOCAL FAST_FORWARD FOR
    SELECT p.SPROC FROM #Target p
    JOIN sys.objects o ON o.name = p.SPROC AND o.type = 'P' AND o.schema_id = SCHEMA_ID('dbo')
    ORDER BY p.SPROC;
OPEN drop_cursor;
FETCH NEXT FROM drop_cursor INTO @Name;
WHILE @@FETCH_STATUS = 0
BEGIN
    DECLARE @statement NVARCHAR(300) = N'DROP PROCEDURE dbo.' + QUOTENAME(@Name) + N';';
    EXEC sp_executesql @statement;
    SET @Dropped = @Dropped + 1;
    FETCH NEXT FROM drop_cursor INTO @Name;
END
CLOSE drop_cursor;
DEALLOCATE drop_cursor;

/* ---------- 收口断言 ---------- */
IF EXISTS (SELECT 1 FROM sys.objects o JOIN #Target p ON p.SPROC = o.name WHERE o.type = 'P')
    THROW 50107, N'仍有待下线报表过程存在于库内，迁移中止。', 1;

DECLARE @Total INT = (SELECT COUNT(*) FROM sys.objects WHERE type = 'P' AND SCHEMA_NAME(schema_id) = 'dbo');
DECLARE @Reports INT = (SELECT COUNT(*) FROM sys.objects WHERE type = 'P' AND SCHEMA_NAME(schema_id) = 'dbo' AND name LIKE N'P_RPT[_]%');
DECLARE @Wage INT = (SELECT COUNT(*) FROM sys.objects WHERE type = 'P' AND SCHEMA_NAME(schema_id) = 'dbo' AND name = N'P_HRM_WAGE_CALC');

IF @Total <> 2 OR @Reports <> 1 OR @Wage <> 1
    THROW 50108, N'下线后过程构成不是预期的（总数 2 / 报表族 1 / 工资引擎 1），迁移中止（请复核引用清单）。', 1;

IF OBJECT_ID(N'tempdb..#Target') IS NOT NULL DROP TABLE #Target;

PRINT N'== HR 分析报表过程已下线 ' + CAST(@Dropped AS NVARCHAR(10))
    + N' 个；dbo 过程合计 ' + CAST(@Total AS NVARCHAR(10))
    + N' 个（报表族 ' + CAST(@Reports AS NVARCHAR(10))
    + N' / 工资引擎 ' + CAST(@Wage AS NVARCHAR(10)) + N'）==';
