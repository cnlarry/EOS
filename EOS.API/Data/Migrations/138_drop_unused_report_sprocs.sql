-- ============================================================================
-- EOS.ERP migration 138: 下线未被引用的报表过程（P_RPT_* 报表族收口第一批）
-- ----------------------------------------------------------------------------
-- 报表族的调用链只有一条：`dbo.REPORT_SORT.SORT_FIELDS` 里的花括号引用
-- （形如 `{P_RPT_XXX_1.COL}`）经 `ReportRepository` 解析后按名执行。据此逐条核查后，
-- 25 个 `P_RPT_*` 中只有 8 个仍被报表元数据引用：
--   P_RPT_HR_DIARY_1 / P_RPT_HR_EMPLOYEE_1 / _3 / _4 / _5 / _6 / _7 / P_RPT_INV_PRO_DEPOT_1
-- 其余 17 个程序**三方核查均为零引用**（2026-09-18 实测）：
--   ① 库内依赖：`sys.sql_expression_dependencies` 无任何对象引用（未限定 schema 的引用一并覆盖）；
--   ② 运行期元数据：MODULES（UPDATE_SP/AFTERSAVE_SP/SORT_FIELDS/FILTER）、REPORT_SORT、
--      REPORT、FIELDS.DATASOURCE_SQL、SYSDD/SYSDH/SYSDD_REPORT/SYSDF 的过滤字段、
--      FIELD_DATASOURCE、SYSQR_DEFAULT、TABLES.QUERY_RELATION、WF_FUNCTION_INFO、
--      WORKBENCH_DEFINITION_SNAPSHOT、REPORT_FORM_LAYOUT 全部命中 0；
--   ③ 代码与脚本按名调用：EOS.API / EOS.API.Tests / scripts / EOS.Web / docs / publish
--      无任何字面量（同名报表在元数据里走的是新版查询构建路径，不再依赖这些过程）。
-- 迁移自带同样的三道守卫：任一条件在目标库不成立即中止（避免在有引用的库上误删）。
-- 幂等：已不存在的过程跳过。
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

DECLARE @Unused TABLE (SPROC NVARCHAR(128) PRIMARY KEY);
INSERT INTO @Unused (SPROC) VALUES
    (N'P_RPT_COP_ACCOUNT_1'), (N'P_RPT_COP_MONTH_1'), (N'P_RPT_COP_MONTH_2'), (N'P_RPT_COP_MONTH_3'),
    (N'P_RPT_CUS_BALANCE_1'), (N'P_RPT_CUS_MATER_1'), (N'P_RPT_CUS_MATERIN_1'), (N'P_RPT_CUS_MATEROUT_1'),
    (N'P_RPT_CUS_PRO_1'), (N'P_RPT_CUS_PRO_2'), (N'P_RPT_HR_EMPLOYEE_2'), (N'P_RPT_HR_WAGE_1'),
    (N'P_RPT_PRODUCE_LABEL'), (N'P_RPT_PUR_MONTH_1'), (N'P_RPT_PUR_MONTH_2'), (N'P_RPT_PUR_MONTH_3'),
    (N'P_RPT_SEND_RETURN');

/* 守卫一：库内依赖 */
IF EXISTS (
    SELECT 1 FROM sys.sql_expression_dependencies d
    JOIN @Unused u ON u.SPROC = d.referenced_entity_name
    WHERE d.referenced_class = 1 AND ISNULL(d.referenced_schema_name, N'dbo') = N'dbo'
)
    THROW 50001, N'待下线报表过程仍被库内对象引用，迁移中止。', 1;

/* 守卫二：运行期元数据（报表排序字段里的花括号引用是最关键的调用入口） */
IF EXISTS (
    SELECT 1 FROM dbo.REPORT_SORT s JOIN @Unused u ON s.SORT_FIELDS LIKE N'%' + u.SPROC + N'%'
)
    THROW 50002, N'待下线报表过程仍被 REPORT_SORT.SORT_FIELDS 引用，迁移中止。', 1;
IF EXISTS (
    SELECT 1 FROM dbo.MODULES m JOIN @Unused u
      ON ISNULL(m.UPDATE_SP, N'') + ISNULL(m.AFTERSAVE_SP, N'') + ISNULL(m.SORT_FIELDS, N'') + ISNULL(m.FILTER, N'')
         LIKE N'%' + u.SPROC + N'%'
)
    THROW 50003, N'待下线报表过程仍被 MODULES 元数据引用，迁移中止。', 1;

/* 守卫三：已发布快照（运行期只读当前版本；先做一次"是否出现任何报表过程引用"的粗扫，
   命中才逐名核对——逐名 LIKE 全表快照在本库要 80 秒以上，会撞上迁移命令超时）。 */
IF EXISTS (
    SELECT 1 FROM dbo.WORKBENCH_DEFINITION_SNAPSHOT
    WHERE IS_CURRENT = 1 AND DEFINITION_JSON LIKE N'%P_RPT[_]%'
)
    AND EXISTS (
        SELECT 1 FROM dbo.WORKBENCH_DEFINITION_SNAPSHOT s JOIN @Unused u
          ON s.DEFINITION_JSON LIKE N'%' + u.SPROC + N'%'
        WHERE s.IS_CURRENT = 1
    )
    THROW 50004, N'待下线报表过程仍被已发布快照引用，迁移中止。', 1;

DECLARE @Dropped INT = 0;
DECLARE @Name NVARCHAR(128);
DECLARE drop_cursor CURSOR LOCAL FAST_FORWARD FOR
    SELECT u.SPROC FROM @Unused u
    JOIN sys.objects o ON o.name = u.SPROC AND o.type = 'P' AND o.schema_id = SCHEMA_ID('dbo')
    ORDER BY u.SPROC;
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

DECLARE @RemainingReport INT = (SELECT COUNT(*) FROM sys.objects
                                WHERE type='P' AND SCHEMA_NAME(schema_id)='dbo' AND name LIKE N'P_RPT[_]%');
DECLARE @Total INT = (SELECT COUNT(*) FROM sys.objects WHERE type='P' AND SCHEMA_NAME(schema_id)='dbo');
IF @RemainingReport <> 8
    THROW 50005, N'下线后报表过程数不是预期的 8 个，迁移中止（请复核引用清单）。', 1;

PRINT N'== 未被引用的报表过程已下线 ' + CAST(@Dropped AS NVARCHAR(10)) + N' 个；剩余 P_RPT_* '
    + CAST(@RemainingReport AS NVARCHAR(10)) + N' 个，dbo 过程合计 ' + CAST(@Total AS NVARCHAR(10)) + N' 个 ==';
