-- ============================================================================
-- EOS.ERP migration 015: 2306 user admin reclassified as a pure custom page
-- ----------------------------------------------------------------------------
-- Database: EOS.ERP (the single business database of the new system)
--
-- Context: 2306 user permission settings is a dedicated custom page (/admin/users)
-- and should not be associated with the workbench. M_URL is already /admin/users.
-- This migration clears NEW_URL/MODI_URL (MODI_URL was /admin/users pointing to the
-- list itself, redundant), cleans up Definition snapshots and dirty markers
-- (2306 is already removed from the unified form whitelist).
--
-- Scope:
--   1. MODULES 2306: clear NEW_URL/MODI_URL, record decision in REMARK;
--   2. Clean WORKBENCH_MODULE_DIRTY / WORKBENCH_DEFINITION_SNAPSHOT for 2306.
-- Idempotent: guarded by WHERE clauses. Naming convention: all uppercase.
-- ============================================================================

SET NOCOUNT ON;
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;

DECLARE @GUARD_MESSAGE NVARCHAR(400) = N'本脚本只能在 EOS.ERP 数据库内执行，当前库为 ' + DB_NAME() + N'。';
IF DB_NAME() <> N'EOS.ERP'
    THROW 50000, @GUARD_MESSAGE, 1;

BEGIN TRANSACTION;

DECLARE @MIG NVARCHAR(20) = N'EOS-23';
DECLARE @NOW DATETIME = GETDATE();

UPDATE dbo.MODULES
SET NEW_URL = NULL,
    MODI_URL = NULL,
    REMARK = N'已归入纯定制页（EOS-23/2026-08-27 用户拍板）：用户权限设定由 /admin/users 承载，用户权限/报表权限为完整子页面，所属组为弹窗；已从统一表单白名单剔除并清空 NEW_URL/MODI_URL，不与工作台关联',
    LAST_UPDATE_BY = @MIG,
    LAST_UPDATE_DATE = @NOW
WHERE M_IDX = 2306;
PRINT N'[EOS-23] MODULES 2306 纯定制页化（清空 NEW_URL/MODI_URL）：' + CAST(@@ROWCOUNT AS NVARCHAR(10)) + N' 行';

IF OBJECT_ID('dbo.WORKBENCH_MODULE_DIRTY','U') IS NOT NULL
BEGIN
    DELETE FROM dbo.WORKBENCH_MODULE_DIRTY WHERE MODULE_ID = 2306;
    PRINT N'[EOS-23] WORKBENCH_MODULE_DIRTY 2306 清理：' + CAST(@@ROWCOUNT AS NVARCHAR(10)) + N' 行';
END

IF OBJECT_ID('dbo.WORKBENCH_DEFINITION_SNAPSHOT','U') IS NOT NULL
BEGIN
    DELETE FROM dbo.WORKBENCH_DEFINITION_SNAPSHOT WHERE MODULE_ID = 2306;
    PRINT N'[EOS-23] WORKBENCH_DEFINITION_SNAPSHOT 2306 清理：' + CAST(@@ROWCOUNT AS NVARCHAR(10)) + N' 行';
END

COMMIT TRANSACTION;
PRINT N'[EOS-23] 2306 用户权限设定归类纯定制页完成。';
