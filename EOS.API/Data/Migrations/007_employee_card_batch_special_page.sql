-- ============================================================================
-- EOS.ERP migration 007: 180218 employee card batch delivery as a special page
-- ----------------------------------------------------------------------------
-- Database: EOS.ERP (the single business database of the new system)
--
-- Context: 180218 employee card batch is delivered as a batch job page
-- (/jobs/card-batch + JobPage), but MODULES.MODI_URL still pointed to the unified
-- form route (/document-workbench/{moduleId}/edit). 180218 is not in the unified
-- form whitelist, so triggering edit/double-click from the workbench fell into an
-- unmigrated form (404/unusable). It is reclassified as a special page by clearing
-- MODI_URL (and NEW_URL) to prevent that; single-card maintenance 180208 already
-- follows the employee-card domain rules and stays in the whitelist.
--
-- Scope:
--   1. MODULES 180218: clear MODI_URL and NEW_URL, record the special-page decision in REMARK;
--   2. Clean WORKBENCH_MODULE_DIRTY / WORKBENCH_DEFINITION_SNAPSHOT for 180218 (defensive;
--      180218 was never in the whitelist, so usually no rows).
--
-- Idempotent: guarded by WHERE clauses, safe to re-run.
-- Naming convention: all object names uppercase.
-- ============================================================================

SET NOCOUNT ON;
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;

DECLARE @GUARD_MESSAGE NVARCHAR(400) = N'本脚本只能在 EOS.ERP 数据库内执行，当前库为 ' + DB_NAME() + N'。';
IF DB_NAME() <> N'EOS.ERP'
    THROW 50000, @GUARD_MESSAGE, 1;

BEGIN TRANSACTION;

DECLARE @MIG NVARCHAR(20) = N'EOS-20';
DECLARE @NOW DATETIME = GETDATE();

-- 1. MODULES 180218 归类特殊页：清空统一表单路由，登记结论
UPDATE dbo.MODULES
SET MODI_URL = NULL,
    NEW_URL = NULL,
    REMARK = N'已归类特殊页（2026-08-26，EOS-20）：员工批量发卡以批量作业页交付（/jobs/card-batch + JobPage），清空统一表单 MODI_URL/NEW_URL 防误入未移植表单；单卡维护 180208 走 employee-card 领域规则',
    LAST_UPDATE_BY = @MIG,
    LAST_UPDATE_DATE = @NOW
WHERE M_IDX = 180218;
PRINT N'[EOS-20] MODULES 180218 特殊页化（清空 MODI_URL/NEW_URL）：' + CAST(@@ROWCOUNT AS NVARCHAR(10)) + N' 行';

-- 2. 防御性清理工作台脏标记 / Definition 快照（180218 从未入白名单，通常无记录）
IF OBJECT_ID('dbo.WORKBENCH_MODULE_DIRTY','U') IS NOT NULL
BEGIN
    DELETE FROM dbo.WORKBENCH_MODULE_DIRTY WHERE MODULE_ID = 180218;
    PRINT N'[EOS-20] WORKBENCH_MODULE_DIRTY 180218 清理：' + CAST(@@ROWCOUNT AS NVARCHAR(10)) + N' 行';
END

IF OBJECT_ID('dbo.WORKBENCH_DEFINITION_SNAPSHOT','U') IS NOT NULL
BEGIN
    DELETE FROM dbo.WORKBENCH_DEFINITION_SNAPSHOT WHERE MODULE_ID = 180218;
    PRINT N'[EOS-20] WORKBENCH_DEFINITION_SNAPSHOT 180218 清理：' + CAST(@@ROWCOUNT AS NVARCHAR(10)) + N' 行';
END

COMMIT TRANSACTION;
PRINT N'[EOS-20] 180218 员工批量发卡归类特殊页完成（清空 MODI_URL/NEW_URL）。';