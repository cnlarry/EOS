-- ============================================================================
-- EOS.ERP migration 142: 下线 CompareAfterSave 的 30 个验收基准过程（+ 被牵连的 P_BOM_CHECK）
-- ----------------------------------------------------------------------------
-- 背景：`EOS.API.Tests/CompareAfterSave.ps1` 原以这 30 个"旧保存后过程"做活体对照；
-- 自本轮起该脚本**默认走冻结模式**（`-Frozen`，不再执行旧过程，旧侧结论由
-- `EOS.API.Tests/Fixtures/compare-after-save-golden.json` 的 50 条期望供给），活体对照降级为
-- 显式 `-Live`（考古用，需先用 `scripts/restore-historic-groundtruth-sprocs.ps1` 还原）。-- 因此这批"只作测试基准"的过程可以下线：
--   · 30 个基准：`P_BOM_STRU_After_Save` / `P_COP_BACK_After_Save` / `P_COP_FITIN_After_Save` /
--     `P_COP_FITOUT_After_Save` / `P_COP_QUOTE_After_Save` / `P_COP_RETURN_After_Save` /
--     `P_CURR_After_Save` / `P_Employee_Card_After_Save` / `P_HR_CERTIFY_After_Save` /
--     `P_HR_CONTRACT_After_Save` / `P_HR_EMPLOYEE_After_Save` / `P_HR_ENACTMENT_After_Save` /
--     `P_HR_SAFE_After_Save` / `P_HR_WAGE_ITEM_After_Save` / `P_INV_OCCUR_ADJUST_After_Save` /
--     `P_INV_OCCUR_IN_After_Save` / `P_INV_OCCUR_INIT_After_Save` / `P_INV_OCCUR_OUT_After_Save` /
--     `P_INV_OCCUR_SCRAP_After_Save` / `P_INV_OCCUR_TRANSFER_After_Save` / `P_MOC_GET_After_Save` /
--     `P_MOC_PRODUCE_After_Save` / `P_MOC_PRODUCT_IN_After_Save` / `P_PUR_APPLY_After_Save` /
--     `P_PUR_CANCEL_After_Save` / `P_SFC_DAILY_After_Save` / `P_SFC_PROCESS_After_Save` /
--     `P_SYSDG_After_Save` / `P_SYSDL_After_Save` / `P_SYSQR_DEFAULT_After_Save`
--   · 1 个被基准牵连者：`P_BOM_CHECK`（`P_BOM_STRU_After_Save` 无条件 exec 它；基准下线后引用归零，
--     其调用点早已移植为受控 SQL 批 `SysDomainRules.BomCycleSql`，见迁移 140 的回退记录）
-- 非钩子对象下线四判据（用户拍板方案 B）：
--   ⒜ 三方核查零引用：**集合外**库内依赖 0；`MODULES.UPDATE_SP/AFTERSAVE_SP` 0；`REPORT_SORT` 0；
--      当前快照的 `BusinessRule.AfterSaveSproc`/`WorkflowSproc` 为 null（注：快照里可能出现这些
--      过程名的**溯源标签** `sourceRef`，那是校验规则的出处登记、不构成调用，故守卫用 JSON 取值精确判定）
--      ；仓库侧仅 `CompareAfterSave.ps1`（默认已不执行）与还原脚本提及；
--   ⒝ 本体在 `EOS.Database/dbo/Stored Procedures/<名>.sql` 可还原（31 个逐个确认存在）；
--   ⒞ 本迁移内置同样守卫，任一条不成立即 THROW；
--   ⒟ 登记于 `docs/plans/两级覆盖率报告.md` §八"非钩子遗留对象下线登记"。
-- 幂等：逐个判断存在后再 DROP；末尾断言 dbo 过程总数为 61。
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

IF OBJECT_ID(N'tempdb..#Baseline') IS NOT NULL DROP TABLE #Baseline;
CREATE TABLE #Baseline (SPROC NVARCHAR(128) PRIMARY KEY);
INSERT INTO #Baseline (SPROC) VALUES
    (N'P_BOM_STRU_After_Save'), (N'P_COP_BACK_After_Save'), (N'P_COP_FITIN_After_Save'),
    (N'P_COP_FITOUT_After_Save'), (N'P_COP_QUOTE_After_Save'), (N'P_COP_RETURN_After_Save'),
    (N'P_CURR_After_Save'), (N'P_Employee_Card_After_Save'), (N'P_HR_CERTIFY_After_Save'),
    (N'P_HR_CONTRACT_After_Save'), (N'P_HR_EMPLOYEE_After_Save'), (N'P_HR_ENACTMENT_After_Save'),
    (N'P_HR_SAFE_After_Save'), (N'P_HR_WAGE_ITEM_After_Save'), (N'P_INV_OCCUR_ADJUST_After_Save'),
    (N'P_INV_OCCUR_IN_After_Save'), (N'P_INV_OCCUR_INIT_After_Save'), (N'P_INV_OCCUR_OUT_After_Save'),
    (N'P_INV_OCCUR_SCRAP_After_Save'), (N'P_INV_OCCUR_TRANSFER_After_Save'), (N'P_MOC_GET_After_Save'),
    (N'P_MOC_PRODUCE_After_Save'), (N'P_MOC_PRODUCT_IN_After_Save'), (N'P_PUR_APPLY_After_Save'),
    (N'P_PUR_CANCEL_After_Save'), (N'P_SFC_DAILY_After_Save'), (N'P_SFC_PROCESS_After_Save'),
    (N'P_SYSDG_After_Save'), (N'P_SYSDL_After_Save'), (N'P_SYSQR_DEFAULT_After_Save'),
    (N'P_BOM_CHECK');

IF OBJECT_ID(N'tempdb..#Ported') IS NOT NULL DROP TABLE #Ported;
-- 已由受控实现接管、不再需要旧过程的清单（与 #Baseline 同批下线）
SELECT SPROC INTO #Ported FROM #Baseline;

/* ---------- 守卫 ---------- */
IF (SELECT COUNT(*) FROM #Ported p JOIN sys.objects o ON o.name = p.SPROC AND o.type = 'P' AND o.schema_id = SCHEMA_ID('dbo')) <> 31
    THROW 50001, N'待下线过程清单与库内实际不符（应为 31 个），迁移中止。', 1;

IF EXISTS (
    SELECT 1 FROM sys.sql_expression_dependencies d
    JOIN #Ported p ON p.SPROC = d.referenced_entity_name
    WHERE d.referenced_class = 1
      AND ISNULL(d.referenced_schema_name, N'dbo') = N'dbo'
      AND d.referencing_id NOT IN (SELECT o.object_id FROM sys.objects o JOIN #Ported q ON q.SPROC = o.name)
)
    THROW 50002, N'待下线过程仍被集合外的库内对象引用，迁移中止。', 1;

IF EXISTS (SELECT 1 FROM dbo.MODULES m JOIN #Ported p ON m.UPDATE_SP = p.SPROC OR m.AFTERSAVE_SP = p.SPROC)
    THROW 50003, N'待下线过程仍被 MODULES 钩子字段引用，迁移中止。', 1;

IF EXISTS (
    SELECT 1 FROM dbo.REPORT_SORT s JOIN #Ported p ON ISNULL(s.SORT_FIELDS, N'') LIKE N'%' + p.SPROC + N'%'
)
    THROW 50004, N'待下线过程仍被报表排序字段引用，迁移中止。', 1;

IF EXISTS (
    SELECT 1 FROM dbo.WORKBENCH_DEFINITION_SNAPSHOT
    WHERE IS_CURRENT = 1
      AND (JSON_VALUE(DEFINITION_JSON, '$.BusinessRule.AfterSaveSproc') IS NOT NULL
           OR JSON_VALUE(DEFINITION_JSON, '$.BusinessRule.WorkflowSproc') IS NOT NULL)
)
    THROW 50005, N'仍有当前快照登记了旧保存后 / 批核过程，迁移中止。', 1;

/* ---------- 下线 ---------- */
DECLARE @Dropped INT = 0;
DECLARE @Name NVARCHAR(128);
DECLARE drop_cursor CURSOR LOCAL FAST_FORWARD FOR
    SELECT p.SPROC FROM #Ported p
    JOIN sys.objects o ON o.name = p.SPROC AND o.type = 'P' AND o.schema_id = SCHEMA_ID('dbo')
    ORDER BY CASE WHEN p.SPROC = N'P_BOM_CHECK' THEN 1 ELSE 0 END, p.SPROC;  -- 先删基准，再删被牵连者
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
IF EXISTS (SELECT 1 FROM sys.objects o JOIN #Ported p ON p.SPROC = o.name WHERE o.type = 'P')
    THROW 50006, N'仍有待下线过程存在于库内，迁移中止。', 1;

DECLARE @Total INT = (SELECT COUNT(*) FROM sys.objects WHERE type = 'P' AND SCHEMA_NAME(schema_id) = 'dbo');
IF @Total <> 61
    THROW 50007, N'下线后 dbo 过程总数不是预期的 61 个，迁移中止（请复核引用清单）。', 1;

IF OBJECT_ID(N'tempdb..#Baseline') IS NOT NULL DROP TABLE #Baseline;
IF OBJECT_ID(N'tempdb..#Ported') IS NOT NULL DROP TABLE #Ported;

PRINT N'== CompareAfterSave 验收基准过程已下线 ' + CAST(@Dropped AS NVARCHAR(10))
    + N' 个（含被牵连的 P_BOM_CHECK）；dbo 过程合计 ' + CAST(@Total AS NVARCHAR(10)) + N' 个 ==';
