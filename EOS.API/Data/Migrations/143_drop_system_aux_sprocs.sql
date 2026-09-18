-- ============================================================================
-- EOS.ERP migration 144: 下线最后一批孤儿 —— 系统 `xp_*` 辅助过程（9 个）
-- ----------------------------------------------------------------------------
-- 背景：这 9 个 `xp_*` 是旧系统"字段 / 用户字段 / 选型器参数 / 菜单排序 / 权限临时表 /
-- 权限复制"的辅助过程，供旧 Web Forms 的元数据管理页调用（旧系统已停用，仅存于 `ERP/` 考古）。
-- 本系统对应能力由 `EOS.API` 的元数据 / 权限 / 选单仓库承担，仓库内**无任何按名引用**。
-- 此前它们只是被 `scripts/adr012-dead-sproc-report.ps1` 的"前缀整族保留"策略护住；本轮把该策略
-- 改为**证据式**（保留 = 被引用，而非按前缀），并据此下线。
-- 四方核查（2026-09-18 实测）：
--   · 库内依赖 `sys.sql_expression_dependencies` = 0（含未限定 schema 的引用）；
--   · 运行期元数据：`MODULES.UPDATE_SP`/`AFTERSAVE_SP` = 0、`REPORT_SORT` = 0、
--     当前快照 `BusinessRule.AfterSaveSproc`/`WorkflowSproc` 为 null、**SQL Agent 作业步骤** = 0；
--   · 代码与脚本字面量（`EOS.API`/`EOS.API.Tests`/`scripts`/`EOS.Web`/`publish`，排除注释与生成物）= 0；
--   · 本体在 `EOS.Database/dbo/Stored Procedures/<名>.sql` 可还原（9 个逐个确认存在）。
-- 幂等：逐个判断存在后 DROP；末尾断言 dbo 过程总数为 9（= 报表族 8 + 工资引擎 1），`xp_*` 为 0。
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

IF OBJECT_ID(N'tempdb..#SystemAux') IS NOT NULL DROP TABLE #SystemAux;
CREATE TABLE #SystemAux (SPROC NVARCHAR(128) PRIMARY KEY);
INSERT INTO #SystemAux (SPROC) VALUES
    (N'xp_copy_right_temp'), (N'xp_copy_table_fields'), (N'xp_fields'), (N'xp_fields_choose'),
    (N'xp_get_selectorpars'), (N'xp_menu_sort'), (N'xp_right_temp'), (N'xp_user_fields'),
    (N'xp_user_listrpt_fields');

/* ---------- 守卫 ---------- */
IF (SELECT COUNT(*) FROM #SystemAux a JOIN sys.objects o ON o.name = a.SPROC AND o.type = 'P' AND o.schema_id = SCHEMA_ID('dbo')) <> 9
    THROW 50001, N'待下线系统辅助过程清单与库内实际不符（应为 9 个），迁移中止。', 1;

IF EXISTS (
    SELECT 1 FROM sys.sql_expression_dependencies d
    JOIN #SystemAux a ON a.SPROC = d.referenced_entity_name
    WHERE d.referenced_class = 1
      AND ISNULL(d.referenced_schema_name, N'dbo') = N'dbo'
      AND d.referencing_id NOT IN (SELECT o.object_id FROM sys.objects o JOIN #SystemAux b ON b.SPROC = o.name)
)
    THROW 50002, N'待下线系统辅助过程仍被集合外的库内对象引用，迁移中止。', 1;

IF EXISTS (SELECT 1 FROM dbo.MODULES m JOIN #SystemAux a ON m.UPDATE_SP = a.SPROC OR m.AFTERSAVE_SP = a.SPROC)
    THROW 50003, N'待下线系统辅助过程仍被 MODULES 钩子字段引用，迁移中止。', 1;

IF EXISTS (SELECT 1 FROM dbo.REPORT_SORT s JOIN #SystemAux a ON ISNULL(s.SORT_FIELDS, N'') LIKE N'%' + a.SPROC + N'%')
    THROW 50004, N'待下线系统辅助过程仍被报表排序字段引用，迁移中止。', 1;

IF EXISTS (
    SELECT 1 FROM dbo.WORKBENCH_DEFINITION_SNAPSHOT
    WHERE IS_CURRENT = 1
      AND (JSON_VALUE(DEFINITION_JSON, '$.BusinessRule.AfterSaveSproc') IS NOT NULL
           OR JSON_VALUE(DEFINITION_JSON, '$.BusinessRule.WorkflowSproc') IS NOT NULL)
)
    THROW 50005, N'仍有当前快照登记了旧保存后 / 批核过程，迁移中止。', 1;

IF EXISTS (
    SELECT 1 FROM msdb.dbo.sysjobsteps s JOIN #SystemAux a ON s.command LIKE N'%' + a.SPROC + N'%'
)
    THROW 50006, N'待下线系统辅助过程仍被 SQL Agent 作业步骤调用，迁移中止。', 1;

/* ---------- 下线 ---------- */
DECLARE @Dropped INT = 0;
DECLARE @Name NVARCHAR(128);
DECLARE drop_cursor CURSOR LOCAL FAST_FORWARD FOR
    SELECT a.SPROC FROM #SystemAux a
    JOIN sys.objects o ON o.name = a.SPROC AND o.type = 'P' AND o.schema_id = SCHEMA_ID('dbo')
    ORDER BY a.SPROC;
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
IF EXISTS (SELECT 1 FROM sys.objects o JOIN #SystemAux a ON a.SPROC = o.name WHERE o.type = 'P')
    THROW 50007, N'仍有待下线系统辅助过程存在于库内，迁移中止。', 1;

DECLARE @Total INT = (SELECT COUNT(*) FROM sys.objects WHERE type = 'P' AND SCHEMA_NAME(schema_id) = 'dbo');
DECLARE @Reports INT = (SELECT COUNT(*) FROM sys.objects WHERE type = 'P' AND SCHEMA_NAME(schema_id) = 'dbo' AND name LIKE N'P_RPT[_]%');
DECLARE @SystemAux INT = (SELECT COUNT(*) FROM sys.objects WHERE type = 'P' AND SCHEMA_NAME(schema_id) = 'dbo' AND name LIKE N'xp[_]%');
DECLARE @Wage INT = (SELECT COUNT(*) FROM sys.objects WHERE type = 'P' AND name = N'P_HRM_WAGE_CALC');

IF @Total <> 9 OR @Reports <> 8 OR @SystemAux <> 0 OR @Wage <> 1
    THROW 50008, N'下线后过程构成不是预期的（总数 9 / 报表族 8 / 系统辅助 0 / 工资引擎 1），迁移中止（请复核引用清单）。', 1;

IF OBJECT_ID(N'tempdb..#SystemAux') IS NOT NULL DROP TABLE #SystemAux;

PRINT N'== 系统辅助过程已下线 ' + CAST(@Dropped AS NVARCHAR(10))
    + N' 个；dbo 过程合计 ' + CAST(@Total AS NVARCHAR(10))
    + N' 个（报表族 ' + CAST(@Reports AS NVARCHAR(10))
    + N' / 工资引擎 ' + CAST(@Wage AS NVARCHAR(10)) + N'）==';
