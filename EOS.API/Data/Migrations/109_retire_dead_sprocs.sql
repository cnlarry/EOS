-- ============================================================================
-- EOS.ERP migration 109: 退役全部无引用旧过程（批 3）
-- ----------------------------------------------------------------------------
-- 判定依据：`scripts/adr012-dead-sproc-report.ps1`（只读）的三方引用核查 + 库内传递闭包。
--  ① 保留集：
--     - 系统过程 `xp_*`（SQL Server 自带，永不处理）；
--     - 报表族 `P_RPT_*`（`ReportRepository` 按前缀查找后执行）；
--     - 代码/脚本/验收夹具**按名调用**的过程（下方清单：C# 的 EXEC/CommandText、
--       `scripts/`、`EOS.API.Tests/`、以及 `logs/` 下的 ADR-012 夹具）；
--     - `MODULES.UPDATE_SP`/`AFTERSAVE_SP` 引用的过程（当前为 0）；
--     - 上述集合在库内的**传递闭包**（保留对象引用的过程必须一并保留）。
--  ② 下线集：其余全部 dbo 过程。
-- 守卫（不满足即 THROW，一个对象都不删；重复执行时下线集为空走空转分支）：
--    保留集引用候选集 = 0、无模块引用候选集、且**关键过程名单**（验收脚本与运行期代码按名依赖者）
--    不得落入下线集；关键名单见下方 THROW 50005 处。
-- 复现/重放说明（2026-09-17）：本清单由 `scripts/adr012-dead-sproc-report.ps1` 生成。首次执行时该扫描器
--    **只匹配全大写名字**，漏掉了验收脚本以位置参数传入的 15 个混合大小写基准过程
--    （`P_INV_OCCUR_*_After_Save`、`P_COP_RETURN/FITIN/FITOUT/BACK_After_Save`、`P_PUR_CANCEL/APPLY_After_Save`、
--    `P_HR_CONTRACT/SAFE/CERTIFY_After_Save`），它们被一并删除、验收脚本随即 EXIT=1；扫描器已改为大小写不敏感，
--    清单亦已补齐（含夹具引用的 `*_CHECK` 与依赖项），本库中这 15 个已由
--    `scripts/restore-legacy-groundtruth-sprocs.ps1` 自 SSDT 快照还原（当前 112 个过程，故本迁移在此库上是空转）。
--    **重放纪律**：若在含旧过程的其它库上执行，先跑修正后的报告脚本核对保留/下线清单，再按本文件的守卫判定。
-- 注意：`sys.sql_expression_dependencies` 对**未限定 schema** 的引用 `referenced_schema_name` 为 NULL，
--    必须按 `ISNULL(...,'dbo')` 判定，否则会把过程间引用整批漏掉（本库实测 56 处）。
-- 源码保留在 `EOS.Database` SSDT 快照（`dbo/Stored Procedures/*.sql`）供考古。
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

IF OBJECT_ID(N'tempdb..#Keep') IS NOT NULL DROP TABLE #Keep;
CREATE TABLE #Keep (SPROC NVARCHAR(128) PRIMARY KEY);

/* 系统过程 + 报表族 */
INSERT INTO #Keep (SPROC)
SELECT name FROM sys.objects
WHERE type = 'P' AND schema_id = SCHEMA_ID('dbo') AND (name LIKE 'xp[_]%' OR name LIKE 'P_RPT[_]%');

/* 代码/脚本/夹具按名调用（由 adr012-dead-sproc-report.ps1 扫描生成，勿手工增删） */
INSERT INTO #Keep (SPROC)
SELECT o.name FROM sys.objects o
WHERE o.type = 'P' AND o.schema_id = SCHEMA_ID('dbo')
  AND o.name IN (
            N'P_BOM_CHECK', N'P_BOM_STRU_AFTER_SAVE', N'P_CHANGE_M_IDX', N'P_COP_ACCOUNT_CHECK',
            N'P_COP_ACCOUNT_FINISHED', N'P_COP_BACK_CHECK', N'P_COP_CALLBACK_CHECK', N'P_COP_FITIN_CHECK',
            N'P_COP_FITOUT_CHECK', N'P_COP_FITOUT_FINISHED', N'P_COP_ORDER_CHANGE_CHECK', N'P_COP_ORDER_CHECK',
            N'P_COP_ORDER_FINISHED', N'P_COP_PREPAY_FINISHED', N'P_COP_QUOTE_AFTER_SAVE', N'P_COP_RECEIPT_CHECK',
            N'P_COP_RETURN_FINISHED', N'P_COP_SEND_CHECK', N'P_COP_SEND_FINISHED', N'P_COP_SHIPMENT_FINISHED',
            N'P_CURR_AFTER_SAVE', N'P_CUS_ACCOUNT_CHECK', N'P_CUS_EXPORT_CHECK', N'P_CUS_IMPORT_CHECK',
            N'P_EMPLOYEE_CARD_AFTER_SAVE', N'P_HR_EMPLOYEE_AFTER_SAVE', N'P_HR_ENACTMENT_AFTER_SAVE',
            N'P_HR_WAGE_ITEM_AFTER_SAVE', N'P_HRM_WAGE_CALC', N'P_INV_OCCUR_TRANSFER_CHECK',
            N'P_MOC_GET_AFTER_SAVE', N'P_MOC_PLAN_CHECK', N'P_MOC_PRODUCE_AFTER_SAVE', N'P_MOC_PRODUCE_CHANGE_CHECK',
            N'P_MOC_PRODUCE_CHECK', N'P_MOC_PRODUCT_IN_AFTER_SAVE', N'P_MOC_PRODUCT_IN_CHECK',
            N'P_MOC_PRODUCT_OUT_CHECK', N'P_MOC_WORK_CHECK', N'P_MOC_WORK_IN_CHECK', N'P_MOC_WORK_OUT_CHECK',
            N'P_MOU_BATCHIN_CHECK', N'P_PUR_APPLY_FINISHED', N'P_PUR_CANCEL_FINISHED', N'P_PUR_DUE_CHECK',
            N'P_PUR_DUE_FINISHED', N'P_PUR_PAY_CHECK', N'P_PUR_PREPAY_CHECK', N'P_PUR_PREPAY_FINISHED',
            N'P_PUR_PURCHASE_CHANGE_CHECK', N'P_PUR_PURCHASE_FINISHED', N'P_PUR_RECEIVE_CHECK',
            N'P_PUR_RECEIVE_FINISHED', N'P_QC_ANALYSIS_CHECK', N'P_SFC_DAILY_AFTER_SAVE',
            N'P_SFC_PROCESS_AFTER_SAVE', N'P_SYSDG_AFTER_SAVE', N'P_SYSDL_AFTER_SAVE',
            N'P_SYSQR_DEFAULT_AFTER_SAVE', N'P_UPDATE_PRO_MRP_ALL',
            /* 验收脚本以位置参数传入的混合大小写基准过程（扫描器须大小写不敏感才看得到） */
            N'P_INV_OCCUR_INIT_After_Save', N'P_INV_OCCUR_IN_After_Save', N'P_INV_OCCUR_OUT_After_Save',
            N'P_INV_OCCUR_TRANSFER_After_Save', N'P_INV_OCCUR_SCRAP_After_Save', N'P_INV_OCCUR_ADJUST_After_Save',
            N'P_COP_RETURN_After_Save', N'P_COP_FITOUT_After_Save', N'P_COP_FITIN_After_Save', N'P_COP_BACK_After_Save',
            N'P_PUR_CANCEL_After_Save', N'P_PUR_APPLY_After_Save', N'P_HR_CONTRACT_After_Save', N'P_HR_SAFE_After_Save',
            N'P_HR_CERTIFY_After_Save',
            /* 依赖/夹具引用（扫描器在修正后补入：旧钩子过程体与夹具按名引用） */
            N'P_COP_RETURN_CHECK', N'P_WF_MOU_OUT', N'P_WF_MOU_SCRAP')
EXCEPT SELECT SPROC FROM #Keep;

/* 模块钩子字段引用的过程（当前为 0，登记为保留以免将来误删） */
INSERT INTO #Keep (SPROC)
SELECT o.name FROM sys.objects o
WHERE o.type = 'P' AND o.schema_id = SCHEMA_ID('dbo')
  AND (o.name IN (SELECT LTRIM(RTRIM(UPDATE_SP)) FROM dbo.MODULES WHERE LTRIM(RTRIM(ISNULL(UPDATE_SP,''))) <> '')
       OR o.name IN (SELECT LTRIM(RTRIM(AFTERSAVE_SP)) FROM dbo.MODULES WHERE LTRIM(RTRIM(ISNULL(AFTERSAVE_SP,''))) <> ''))
EXCEPT SELECT SPROC FROM #Keep;

/* 传递闭包：被保留对象引用的过程一并保留 */
DECLARE @ADDED INT = 1;
WHILE @ADDED > 0
BEGIN
    INSERT INTO #Keep (SPROC)
    SELECT DISTINCT o2.name
    FROM sys.sql_expression_dependencies d
    JOIN sys.objects o ON o.object_id = d.referencing_id
    JOIN #Keep K ON K.SPROC = o.name
    JOIN sys.objects o2 ON o2.name = d.referenced_entity_name AND o2.type = 'P' AND o2.schema_id = SCHEMA_ID('dbo')
    WHERE d.referenced_class = 1 AND ISNULL(d.referenced_schema_name, N'dbo') = N'dbo'
    EXCEPT SELECT SPROC FROM #Keep;
    SET @ADDED = @@ROWCOUNT;
END;

IF OBJECT_ID(N'tempdb..#Drop') IS NOT NULL DROP TABLE #Drop;
SELECT o.name AS SPROC INTO #Drop
FROM sys.objects o
WHERE o.type = 'P' AND o.schema_id = SCHEMA_ID('dbo') AND o.name NOT IN (SELECT SPROC FROM #Keep);

DECLARE @KEEP_COUNT INT = (SELECT COUNT(*) FROM #Keep);
DECLARE @DROP_COUNT INT = (SELECT COUNT(*) FROM #Drop);

/* 保留集不得引用下线集 */
DECLARE @RESIDUAL INT = (
    SELECT COUNT(*)
    FROM sys.sql_expression_dependencies d
    JOIN sys.objects o ON o.object_id = d.referencing_id
    JOIN #Keep K ON K.SPROC = o.name
    JOIN #Drop DX ON DX.SPROC = d.referenced_entity_name
    WHERE d.referenced_class = 1);

/* 不得有模块仍引用下线集 */
DECLARE @MODULE_REFS INT = (
    SELECT COUNT(*) FROM dbo.MODULES M
    WHERE LTRIM(RTRIM(ISNULL(M.UPDATE_SP, N''))) IN (SELECT SPROC FROM #Drop)
       OR LTRIM(RTRIM(ISNULL(M.AFTERSAVE_SP, N''))) IN (SELECT SPROC FROM #Drop));

IF @RESIDUAL <> 0
    THROW 50003, N'保留集仍引用待下线过程，迁移中止。', 1;
IF @MODULE_REFS <> 0
    THROW 50004, N'仍有模块引用待下线过程，迁移中止。', 1;
/* 关键保留名单：这些过程被验收脚本（EOS.API.Tests/CompareAfterSave.ps1）或运行期 C# 按名调用，
   一旦落入下线集立即中止。比"总数断言"更直接地守住真正要守的东西。 */
IF EXISTS (
    SELECT 1 FROM #Drop DX WHERE DX.SPROC IN (
        N'P_Change_M_IDX', N'P_HRM_WAGE_CALC', N'P_UPDATE_PRO_MRP_ALL', N'P_Run_After_Save', N'P_BOM_CHECK',
        N'P_CURR_After_Save', N'P_BOM_STRU_After_Save', N'P_Employee_Card_After_Save', N'P_SYSQR_DEFAULT_After_Save',
        N'P_SYSDG_After_Save', N'P_SYSDL_After_Save', N'P_INV_OCCUR_INIT_After_Save', N'P_INV_OCCUR_IN_After_Save',
        N'P_INV_OCCUR_OUT_After_Save', N'P_INV_OCCUR_TRANSFER_After_Save', N'P_INV_OCCUR_SCRAP_After_Save',
        N'P_INV_OCCUR_ADJUST_After_Save', N'P_MOC_GET_After_Save', N'P_MOC_PRODUCE_After_Save',
        N'P_MOC_PRODUCT_IN_After_Save', N'P_SFC_PROCESS_After_Save', N'P_SFC_DAILY_After_Save',
        N'P_COP_QUOTE_After_Save', N'P_COP_RETURN_After_Save', N'P_COP_FITOUT_After_Save', N'P_COP_FITIN_After_Save',
        N'P_COP_BACK_After_Save', N'P_PUR_CANCEL_After_Save', N'P_PUR_APPLY_After_Save', N'P_HR_EMPLOYEE_After_Save',
        N'P_HR_ENACTMENT_After_Save', N'P_HR_WAGE_ITEM_After_Save', N'P_HR_CONTRACT_After_Save',
        N'P_HR_SAFE_After_Save', N'P_HR_CERTIFY_After_Save'))
    THROW 50005, N'关键过程进入下线集（验收脚本或运行期代码按名依赖），迁移中止：请重跑 scripts/adr012-dead-sproc-report.ps1 重生成清单。', 1;

DECLARE @SPROC NVARCHAR(128), @STMT NVARCHAR(300), @DROPPED INT = 0;
DECLARE cur_dead CURSOR LOCAL FAST_FORWARD FOR SELECT SPROC FROM #Drop ORDER BY SPROC;
OPEN cur_dead;
FETCH NEXT FROM cur_dead INTO @SPROC;
WHILE @@FETCH_STATUS = 0
BEGIN
    SET @STMT = N'DROP PROCEDURE dbo.' + QUOTENAME(@SPROC) + N';';
    EXEC sys.sp_executesql @STMT;
    SET @DROPPED = @DROPPED + 1;
    FETCH NEXT FROM cur_dead INTO @SPROC;
END
CLOSE cur_dead;
DEALLOCATE cur_dead;

IF OBJECT_ID(N'tempdb..#Keep') IS NOT NULL DROP TABLE #Keep;
IF OBJECT_ID(N'tempdb..#Drop') IS NOT NULL DROP TABLE #Drop;

PRINT N'== 批 3 完成：保留 ' + CAST(@KEEP_COUNT AS NVARCHAR(10)) + N' 个过程，下线 ' + CAST(@DROPPED AS NVARCHAR(10)) + N' 个 ==';
