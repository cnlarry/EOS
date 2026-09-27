-- ============================================================================
-- EOS.ERP migration 202: retire unreachable report leaves 309801-309804
-- ----------------------------------------------------------------------------
-- Database: EOS.ERP (the single business database of the new system)
--
-- Context: MODULES 309801-309804 are four report leaves (M_URL = '/reports')
-- whose parent node 3098 does not exist in MODULES any more, so the menu tree
-- can never render them (309804 even points at itself as root). None of them
-- carries business-action / validation-rule wiring, and their report
-- definitions are historic print layouts of the retired /reports pages. They are-- dead configuration left behind by an earlier menu revision and are therefore
-- removed physically instead of being hidden.
--
-- Scope:
--   1. Guard: abort when one of the four rows has regained an existing parent,
--      or when a child node still points at one of them;
--   2. Cascade cleanup of the report definitions bound to these modules
--      (REPORT / REPORT_SORT / SYSQR / SYSQR_* templates / inbox / subscription);
--   3. Cascade cleanup of module-scoped metadata (permissions, workbench dirty
--      flags, business actions, validation rules, task/workflow/attachment rows);
--   4. MODULES: physical delete of the four rows;
--   5. TABLES.T_REMARK and table-level extended properties: drop the stale
--      "3098xx:" module references still listed by five HR tables;
--   6. History (SYSDF / AUDIT_EVENT) is kept intact for traceability.
-- Idempotent: every step guarded by OBJECT_ID / EXISTS. All objects uppercase.
-- ============================================================================

SET NOCOUNT ON;

/* 部分元数据表带筛选索引/索引视图，删除与更新必须带正确的 SET 选项。 */
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET ANSI_PADDING ON;
SET ANSI_WARNINGS ON;

DECLARE @GUARD_MESSAGE NVARCHAR(400) = N'本脚本只能在 EOS.ERP 数据库内执行，当前库为 ' + DB_NAME() + N'。';
IF DB_NAME() <> N'EOS.ERP'
    THROW 50000, @GUARD_MESSAGE, 1;

-- Guard: refuse to remove rows that became reachable again (re-attached to an
-- existing parent) or that still have children of their own.
IF EXISTS (
    SELECT 1
    FROM dbo.MODULES m
    WHERE m.M_IDX IN (309801, 309802, 309803, 309804)
      AND m.M_P_IDX IS NOT NULL
      AND EXISTS (SELECT 1 FROM dbo.MODULES p WHERE p.M_IDX = m.M_P_IDX))
    THROW 50001, N'309801-309804 中已有行挂回存在的父节点，拒绝自动删除。', 1;

IF EXISTS (SELECT 1 FROM dbo.MODULES WHERE M_P_IDX IN (309801, 309802, 309803, 309804))
    THROW 50002, N'309801-309804 下仍有子节点，拒绝自动删除。', 1;

BEGIN TRANSACTION;

DECLARE @n INT;

-- ---------------------------------------------------------------------------
-- 1. Report definitions bound to the four modules (children first).
-- ---------------------------------------------------------------------------
IF OBJECT_ID('dbo.REPORT', 'U') IS NOT NULL
BEGIN
    SELECT REPORT_ID
    INTO #DEAD_REPORT
    FROM dbo.REPORT
    WHERE R_M_IDX IN (309801, 309802, 309803, 309804)
       OR Q_M_IDX IN (309801, 309802, 309803, 309804);

    IF OBJECT_ID('dbo.REPORT_SORT', 'U') IS NOT NULL
    BEGIN
        DELETE FROM dbo.REPORT_SORT WHERE REPORT_ID IN (SELECT REPORT_ID FROM #DEAD_REPORT);
        SET @n = @@ROWCOUNT;
        PRINT N'[202] REPORT_SORT 删除：' + CAST(@n AS NVARCHAR(10)) + N' 行';
    END

    IF OBJECT_ID('dbo.SYSQR', 'U') IS NOT NULL
    BEGIN
        DELETE FROM dbo.SYSQR
        WHERE R_M_IDX IN (309801, 309802, 309803, 309804)
           OR REPORT_ID IN (SELECT REPORT_ID FROM #DEAD_REPORT);
        SET @n = @@ROWCOUNT;
        PRINT N'[202] SYSQR 删除：' + CAST(@n AS NVARCHAR(10)) + N' 行';
    END

    IF OBJECT_ID('dbo.REPORT_INBOX', 'U') IS NOT NULL
    BEGIN
        DELETE FROM dbo.REPORT_INBOX
        WHERE MODULE_ID IN (309801, 309802, 309803, 309804)
           OR REPORT_ID IN (SELECT REPORT_ID FROM #DEAD_REPORT);
        SET @n = @@ROWCOUNT;
        PRINT N'[202] REPORT_INBOX 删除：' + CAST(@n AS NVARCHAR(10)) + N' 行';
    END

    IF OBJECT_ID('dbo.REPORT_SUBSCRIPTION', 'U') IS NOT NULL
    BEGIN
        DELETE FROM dbo.REPORT_SUBSCRIPTION
        WHERE MODULE_ID IN (309801, 309802, 309803, 309804)
           OR REPORT_ID IN (SELECT REPORT_ID FROM #DEAD_REPORT);
        SET @n = @@ROWCOUNT;
        PRINT N'[202] REPORT_SUBSCRIPTION 删除：' + CAST(@n AS NVARCHAR(10)) + N' 行';
    END

    DELETE FROM dbo.REPORT WHERE REPORT_ID IN (SELECT REPORT_ID FROM #DEAD_REPORT);
    SET @n = @@ROWCOUNT;
    PRINT N'[202] REPORT 删除：' + CAST(@n AS NVARCHAR(10)) + N' 行';

    DROP TABLE #DEAD_REPORT;
END

-- ---------------------------------------------------------------------------
-- 2. Module-scoped metadata: permissions and report query templates.
-- ---------------------------------------------------------------------------
IF OBJECT_ID('dbo.SYSDD', 'U') IS NOT NULL
BEGIN
    DELETE FROM dbo.SYSDD WHERE M_IDX IN (309801, 309802, 309803, 309804);
    SET @n = @@ROWCOUNT;
    PRINT N'[202] SYSDD 个人权限行删除：' + CAST(@n AS NVARCHAR(10)) + N' 行';
END

IF OBJECT_ID('dbo.SYSDH', 'U') IS NOT NULL
BEGIN
    DELETE FROM dbo.SYSDH WHERE M_IDX IN (309801, 309802, 309803, 309804);
    SET @n = @@ROWCOUNT;
    PRINT N'[202] SYSDH 组权限行删除：' + CAST(@n AS NVARCHAR(10)) + N' 行';
END

IF OBJECT_ID('dbo.SYSDD_REPORT', 'U') IS NOT NULL
BEGIN
    DELETE FROM dbo.SYSDD_REPORT WHERE M_IDX IN (309801, 309802, 309803, 309804);
    SET @n = @@ROWCOUNT;
    PRINT N'[202] SYSDD_REPORT 报表权限行删除：' + CAST(@n AS NVARCHAR(10)) + N' 行';
END

IF OBJECT_ID('dbo.SYSDH_REPORT', 'U') IS NOT NULL
BEGIN
    DELETE FROM dbo.SYSDH_REPORT WHERE M_IDX IN (309801, 309802, 309803, 309804);
    SET @n = @@ROWCOUNT;
    PRINT N'[202] SYSDH_REPORT 报表权限行删除：' + CAST(@n AS NVARCHAR(10)) + N' 行';
END

IF OBJECT_ID('dbo.SYSQR_DA', 'U') IS NOT NULL
BEGIN
    DELETE FROM dbo.SYSQR_DA WHERE M_IDX IN (309801, 309802, 309803, 309804);
    SET @n = @@ROWCOUNT;
    PRINT N'[202] SYSQR_DA 查询条件模板删除：' + CAST(@n AS NVARCHAR(10)) + N' 行';
END

IF OBJECT_ID('dbo.SYSQR_DEFAULT', 'U') IS NOT NULL
BEGIN
    DELETE FROM dbo.SYSQR_DEFAULT WHERE M_IDX IN (309801, 309802, 309803, 309804);
    SET @n = @@ROWCOUNT;
    PRINT N'[202] SYSQR_DEFAULT 查询条件模板删除：' + CAST(@n AS NVARCHAR(10)) + N' 行';
END

IF OBJECT_ID('dbo.SYSQR_USER', 'U') IS NOT NULL
BEGIN
    DELETE FROM dbo.SYSQR_USER WHERE M_IDX IN (309801, 309802, 309803, 309804);
    SET @n = @@ROWCOUNT;
    PRINT N'[202] SYSQR_USER 用户查询条件删除：' + CAST(@n AS NVARCHAR(10)) + N' 行';
END

-- ---------------------------------------------------------------------------
-- 3. Module-scoped runtime metadata and business wiring.
-- ---------------------------------------------------------------------------
IF OBJECT_ID('dbo.WORKBENCH_MODULE_DIRTY', 'U') IS NOT NULL
BEGIN
    DELETE FROM dbo.WORKBENCH_MODULE_DIRTY WHERE MODULE_ID IN (309801, 309802, 309803, 309804);
    SET @n = @@ROWCOUNT;
    PRINT N'[202] WORKBENCH_MODULE_DIRTY 删除：' + CAST(@n AS NVARCHAR(10)) + N' 行';
END

IF OBJECT_ID('dbo.WORKBENCH_DEFINITION_SNAPSHOT', 'U') IS NOT NULL
BEGIN
    DELETE FROM dbo.WORKBENCH_DEFINITION_SNAPSHOT WHERE MODULE_ID IN (309801, 309802, 309803, 309804);
    SET @n = @@ROWCOUNT;
    PRINT N'[202] WORKBENCH_DEFINITION_SNAPSHOT 删除：' + CAST(@n AS NVARCHAR(10)) + N' 行';
END

IF OBJECT_ID('dbo.WORKBENCH_IDEMPOTENCY', 'U') IS NOT NULL
BEGIN
    DELETE FROM dbo.WORKBENCH_IDEMPOTENCY WHERE MODULE_ID IN (309801, 309802, 309803, 309804);
    SET @n = @@ROWCOUNT;
    PRINT N'[202] WORKBENCH_IDEMPOTENCY 删除：' + CAST(@n AS NVARCHAR(10)) + N' 行';
END

IF OBJECT_ID('dbo.MODULE_BUSINESS_ACTION', 'U') IS NOT NULL
BEGIN
    DELETE FROM dbo.MODULE_BUSINESS_ACTION WHERE MODULE_ID IN (309801, 309802, 309803, 309804);
    SET @n = @@ROWCOUNT;
    PRINT N'[202] MODULE_BUSINESS_ACTION 删除：' + CAST(@n AS NVARCHAR(10)) + N' 行';
END

IF OBJECT_ID('dbo.MODULE_VALIDATION_RULE', 'U') IS NOT NULL
BEGIN
    DELETE FROM dbo.MODULE_VALIDATION_RULE WHERE MODULE_ID IN (309801, 309802, 309803, 309804);
    SET @n = @@ROWCOUNT;
    PRINT N'[202] MODULE_VALIDATION_RULE 删除：' + CAST(@n AS NVARCHAR(10)) + N' 行';
END

IF OBJECT_ID('dbo.TASK', 'U') IS NOT NULL
BEGIN
    DELETE FROM dbo.TASK WHERE M_IDX IN (309801, 309802, 309803, 309804);
    SET @n = @@ROWCOUNT;
    PRINT N'[202] TASK 删除：' + CAST(@n AS NVARCHAR(10)) + N' 行';
END

IF OBJECT_ID('dbo.WF_APPROVE', 'U') IS NOT NULL
BEGIN
    DELETE FROM dbo.WF_APPROVE WHERE M_IDX IN (309801, 309802, 309803, 309804);
    SET @n = @@ROWCOUNT;
    PRINT N'[202] WF_APPROVE 删除：' + CAST(@n AS NVARCHAR(10)) + N' 行';
END

IF OBJECT_ID('dbo.SYSTEMP', 'U') IS NOT NULL
BEGIN
    DELETE FROM dbo.SYSTEMP WHERE M_IDX IN (309801, 309802, 309803, 309804);
    SET @n = @@ROWCOUNT;
    PRINT N'[202] SYSTEMP 删除：' + CAST(@n AS NVARCHAR(10)) + N' 行';
END

IF OBJECT_ID('dbo.ATTACHMENT', 'U') IS NOT NULL
BEGIN
    DELETE FROM dbo.ATTACHMENT WHERE MODULE_ID IN (309801, 309802, 309803, 309804);
    SET @n = @@ROWCOUNT;
    PRINT N'[202] ATTACHMENT 删除：' + CAST(@n AS NVARCHAR(10)) + N' 行';
END

-- ---------------------------------------------------------------------------
-- 4. MODULES: physical delete of the four unreachable leaves.
-- ---------------------------------------------------------------------------
DELETE FROM dbo.MODULES WHERE M_IDX IN (309801, 309802, 309803, 309804);
SET @n = @@ROWCOUNT;
PRINT N'[202] MODULES 物理删除：' + CAST(@n AS NVARCHAR(10)) + N' 行';

-- ---------------------------------------------------------------------------
-- 5. Metadata text: drop the stale module references kept by five HR tables.
-- ---------------------------------------------------------------------------
UPDATE dbo.TABLES
SET T_REMARK = REPLACE(T_REMARK, N'309803:考勤日报表；', N''),
    LAST_UPDATE_BY = N'EOS-MIG',
    LAST_UPDATE_DATE = GETDATE()
WHERE T_ID = N'HRM_DIARY' AND T_REMARK LIKE N'%309803%';

UPDATE dbo.TABLES
SET T_REMARK = REPLACE(T_REMARK, N'309802:排班明细表；', N''),
    LAST_UPDATE_BY = N'EOS-MIG',
    LAST_UPDATE_DATE = GETDATE()
WHERE T_ID IN (N'HRM_PLAN_D', N'HRM_PLAN_M') AND T_REMARK LIKE N'%309802%';

UPDATE dbo.TABLES
SET T_REMARK = REPLACE(T_REMARK, N'309804:请假单明细表；', N''),
    LAST_UPDATE_BY = N'EOS-MIG',
    LAST_UPDATE_DATE = GETDATE()
WHERE T_ID IN (N'HRM_LEAVE_M', N'HR_LEAVE_D') AND T_REMARK LIKE N'%309804%';

SET @n = @@ROWCOUNT;
PRINT N'[202] TABLES.T_REMARK 模块号残留清理完成。';

IF EXISTS (SELECT 1 FROM dbo.TABLES WHERE T_REMARK LIKE N'%309801%' OR T_REMARK LIKE N'%309802%' OR T_REMARK LIKE N'%309803%' OR T_REMARK LIKE N'%309804%')
    PRINT N'[202] 警告：TABLES.T_REMARK 仍有 3098xx 残留，请检查上方 REPLACE 片段。';

-- Table-level extended properties (historic class=1 / minor_id=0 convention).IF EXISTS (SELECT 1 FROM sys.extended_properties WHERE class = 1 AND minor_id = 0 AND major_id = OBJECT_ID(N'dbo.HRM_DIARY') AND name = N'MS_Description')
    EXEC sys.sp_updateextendedproperty @name = N'MS_Description',
        @value = N'出勤日报表（人事薪资考勤；180652:考勤日报；180654:考勤模拟生成；180659:考勤真实抽取生成）',
        @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'HRM_DIARY';

IF EXISTS (SELECT 1 FROM sys.extended_properties WHERE class = 1 AND minor_id = 0 AND major_id = OBJECT_ID(N'dbo.HRM_PLAN_D') AND name = N'MS_Description')
    EXEC sys.sp_updateextendedproperty @name = N'MS_Description',
        @value = N'排班资料明细（人事薪资考勤；180651:排班；18069807:排班明细表）',
        @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'HRM_PLAN_D';

IF EXISTS (SELECT 1 FROM sys.extended_properties WHERE class = 1 AND minor_id = 0 AND major_id = OBJECT_ID(N'dbo.HRM_PLAN_M') AND name = N'MS_Description')
    EXEC sys.sp_updateextendedproperty @name = N'MS_Description',
        @value = N'排班资料主表（人事薪资考勤；180651:排班；18069807:排班明细表）',
        @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'HRM_PLAN_M';

IF EXISTS (SELECT 1 FROM sys.extended_properties WHERE class = 1 AND minor_id = 0 AND major_id = OBJECT_ID(N'dbo.HRM_LEAVE_M') AND name = N'MS_Description')
    EXEC sys.sp_updateextendedproperty @name = N'MS_Description',
        @value = N'请假单主表（人事薪资考勤；180655:请假单；18069804:请假单明细表）',
        @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'HRM_LEAVE_M';

IF EXISTS (SELECT 1 FROM sys.extended_properties WHERE class = 1 AND minor_id = 0 AND major_id = OBJECT_ID(N'dbo.HR_LEAVE_D') AND name = N'MS_Description')
    EXEC sys.sp_updateextendedproperty @name = N'MS_Description',
        @value = N'请假单明细（人事薪资考勤；180203:请假单；18029803:请假单明细表）',
        @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'HR_LEAVE_D';

-- ---------------------------------------------------------------------------
-- 6. History kept for traceability (counts only, no delete).
-- ---------------------------------------------------------------------------
DECLARE @SYSDF_CNT INT = 0;
IF OBJECT_ID('dbo.SYSDF', 'U') IS NOT NULL
    SELECT @SYSDF_CNT = COUNT_BIG(1) FROM dbo.SYSDF WITH (NOLOCK) WHERE M_IDX IN (309801, 309802, 309803, 309804);
PRINT N'[202] 历史审计 SYSDF 保留 ' + CAST(@SYSDF_CNT AS NVARCHAR(10)) + N' 行（不删除，供追溯）';

DECLARE @AUDIT_CNT INT = 0;
IF OBJECT_ID('dbo.AUDIT_EVENT', 'U') IS NOT NULL
    SELECT @AUDIT_CNT = COUNT_BIG(1) FROM dbo.AUDIT_EVENT WITH (NOLOCK) WHERE MODULE_ID IN (309801, 309802, 309803, 309804);
PRINT N'[202] 历史审计 AUDIT_EVENT 保留 ' + CAST(@AUDIT_CNT AS NVARCHAR(10)) + N' 行（不删除，供追溯）';

COMMIT TRANSACTION;

PRINT N'[202] 孤儿报表叶子 309801-309804 下线完成（物理删除）。';
