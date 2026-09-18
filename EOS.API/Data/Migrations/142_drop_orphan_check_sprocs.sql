-- ============================================================================
-- EOS.ERP migration 143: 下线孤儿族 —— 原 AfterSave/Workflow 包装器的依赖闭包（43 个）
-- ----------------------------------------------------------------------------
-- 背景：`*_CHECK` / `*_FINISHED` 这批过程原本只被 `P_*_After_Save` / `P_WF_*` 包装器调用；
-- 那批包装器已随迁移 105–109 / 142 全部退役（保存侧钩子清零、验收基准转冻结期望），
-- 于是这批过程在库内**已无任何调用方**（实测：`sys.sql_expression_dependencies` 0、
-- `MODULES.UPDATE_SP/AFTERSAVE_SP` 0、`REPORT_SORT` 0；当前快照的
-- `BusinessRule.AfterSaveSproc`/`WorkflowSproc` 为 null）。
-- 复核要点：
--   · 快照 JSON 与 `MODULE_VALIDATION_RULE.SOURCE_REF` 里可能出现这些过程名，那是**溯源标签**
--     （规则出处登记），运行期不解析、不构成调用 ⇒ 守卫按 JSON 取值 / 调用字段精确判定，
--     不用 `LIKE` 粗扫（粗扫会误报 18 处标签命中）；
--   · 仓库侧对这批名字只有注释（`CopDomainRules.cs` 的"判据源自哪个过程"说明）与生成物
--     （`publish/overlay/db/bootstrap/*`）提及，无任何可执行引用。
-- 保留同族中的 `P_HRM_WAGE_CALC`：它是**运行期按名调用**的工资引擎（`HumanResourceJobsService`），
-- 退役路径见决策 #117，不属本次范围。
-- 非钩子对象下线四判据（用户拍板方案 B）：三方核查零引用 / SSDT 快照可还原（43 个逐个确认存在）/
-- 本迁移内置守卫且失败即 THROW / 登记于覆盖率报告 §八。
-- 幂等：逐个判断存在后 DROP；末尾断言 dbo 过程总数为 18。
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

IF OBJECT_ID(N'tempdb..#Orphan') IS NOT NULL DROP TABLE #Orphan;
CREATE TABLE #Orphan (SPROC NVARCHAR(128) PRIMARY KEY);
INSERT INTO #Orphan (SPROC) VALUES
    (N'P_COP_ACCOUNT_CHECK'), (N'P_COP_ACCOUNT_FINISHED'), (N'P_COP_BACK_CHECK'),
    (N'P_COP_CALLBACK_CHECK'), (N'P_COP_FITIN_CHECK'), (N'P_COP_FITOUT_CHECK'),
    (N'P_COP_FITOUT_FINISHED'), (N'P_COP_ORDER_CHANGE_CHECK'), (N'P_COP_ORDER_CHECK'),
    (N'P_COP_ORDER_FINISHED'), (N'P_COP_PREPAY_FINISHED'), (N'P_COP_RECEIPT_CHECK'),
    (N'P_COP_RETURN_CHECK'), (N'P_COP_RETURN_FINISHED'), (N'P_COP_SEND_CHECK'),
    (N'P_COP_SEND_FINISHED'), (N'P_COP_SHIPMENT_FINISHED'), (N'P_CUS_ACCOUNT_CHECK'),
    (N'P_CUS_EXPORT_CHECK'), (N'P_CUS_IMPORT_CHECK'), (N'P_MOC_PLAN_CHECK'),
    (N'P_MOC_PRODUCE_CHANGE_CHECK'), (N'P_MOC_PRODUCE_CHECK'), (N'P_MOC_PRODUCT_IN_CHECK'),
    (N'P_MOC_PRODUCT_OUT_CHECK'), (N'P_MOC_WORK_CHECK'), (N'P_MOC_WORK_IN_CHECK'),
    (N'P_MOC_WORK_OUT_CHECK'), (N'P_MOU_BATCHIN_CHECK'), (N'P_PUR_APPLY_FINISHED'),
    (N'P_PUR_CANCEL_FINISHED'), (N'P_PUR_DUE_CHECK'), (N'P_PUR_DUE_FINISHED'),
    (N'P_PUR_PAY_CHECK'), (N'P_PUR_PREPAY_CHECK'), (N'P_PUR_PREPAY_FINISHED'),
    (N'P_PUR_PURCHASE_CHANGE_CHECK'), (N'P_PUR_PURCHASE_FINISHED'), (N'P_PUR_RECEIVE_CHECK'),
    (N'P_PUR_RECEIVE_FINISHED'), (N'P_QC_ANALYSIS_CHECK'), (N'P_WF_MOU_OUT'),
    (N'P_WF_MOU_SCRAP');

/* ---------- 守卫 ---------- */
IF (SELECT COUNT(*) FROM #Orphan p JOIN sys.objects o ON o.name = p.SPROC AND o.type = 'P' AND o.schema_id = SCHEMA_ID('dbo')) <> 43
    THROW 50001, N'待下线孤儿过程清单与库内实际不符（应为 43 个），迁移中止。', 1;

IF EXISTS (
    SELECT 1 FROM sys.sql_expression_dependencies d
    JOIN #Orphan p ON p.SPROC = d.referenced_entity_name
    WHERE d.referenced_class = 1
      AND ISNULL(d.referenced_schema_name, N'dbo') = N'dbo'
      AND d.referencing_id NOT IN (SELECT o.object_id FROM sys.objects o JOIN #Orphan q ON q.SPROC = o.name)
)
    THROW 50002, N'待下线孤儿过程仍被集合外的库内对象引用，迁移中止。', 1;

IF EXISTS (SELECT 1 FROM dbo.MODULES m JOIN #Orphan p ON m.UPDATE_SP = p.SPROC OR m.AFTERSAVE_SP = p.SPROC)
    THROW 50003, N'待下线孤儿过程仍被 MODULES 钩子字段引用，迁移中止。', 1;

IF EXISTS (SELECT 1 FROM dbo.REPORT_SORT s JOIN #Orphan p ON ISNULL(s.SORT_FIELDS, N'') LIKE N'%' + p.SPROC + N'%')
    THROW 50004, N'待下线孤儿过程仍被报表排序字段引用，迁移中止。', 1;

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
    SELECT p.SPROC FROM #Orphan p
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
IF EXISTS (SELECT 1 FROM sys.objects o JOIN #Orphan p ON p.SPROC = o.name WHERE o.type = 'P')
    THROW 50006, N'仍有待下线孤儿过程存在于库内，迁移中止。', 1;

DECLARE @Total INT = (SELECT COUNT(*) FROM sys.objects WHERE type = 'P' AND SCHEMA_NAME(schema_id) = 'dbo');
DECLARE @Reports INT = (SELECT COUNT(*) FROM sys.objects WHERE type = 'P' AND SCHEMA_NAME(schema_id) = 'dbo' AND name LIKE N'P_RPT[_]%');
DECLARE @System INT = (SELECT COUNT(*) FROM sys.objects WHERE type = 'P' AND SCHEMA_NAME(schema_id) = 'dbo' AND name LIKE N'xp[_]%');
DECLARE @Wage INT = (SELECT COUNT(*) FROM sys.objects WHERE type = 'P' AND name = N'P_HRM_WAGE_CALC');

IF @Total <> 18 OR @Reports <> 8 OR @System <> 9 OR @Wage <> 1
    THROW 50007, N'下线后过程构成不是预期的（总数 18 / 报表族 8 / 系统 9 / 工资引擎 1），迁移中止（请复核引用清单）。', 1;

IF OBJECT_ID(N'tempdb..#Orphan') IS NOT NULL DROP TABLE #Orphan;

PRINT N'== 孤儿族已下线 ' + CAST(@Dropped AS NVARCHAR(10))
    + N' 个；dbo 过程合计 ' + CAST(@Total AS NVARCHAR(10))
    + N' 个（报表族 ' + CAST(@Reports AS NVARCHAR(10))
    + N' / 系统 ' + CAST(@System AS NVARCHAR(10))
    + N' / 工资引擎 ' + CAST(@Wage AS NVARCHAR(10)) + N'）==';
