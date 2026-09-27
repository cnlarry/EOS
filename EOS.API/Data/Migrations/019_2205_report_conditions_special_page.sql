-- ============================================================================
-- EOS.ERP migration 019: 2205 report conditions page reclassified as special page
-- ----------------------------------------------------------------------------
-- Database: EOS.ERP (the single business database of the new system)
--
-- Context: 2205 (formerly ) maintains default condition definitions
-- for the report viewer's condition panel (SYSQR_DA master + SYSQR_DEFAULT condition rows).
-- Conditions use a domain-specific DSL (F_TYPE 1-5) that the unified form cannot handle
-- with typed editing and server-side validation. It is reclassified as a custom page
-- at /admin/report-conditions.
--
-- Scope:
--   1. MODULES 2205: M_URL → /admin/report-conditions, clear NEW_URL/MODI_URL;
--   2. WORKBENCH_MODULE_DIRTY / WORKBENCH_DEFINITION_SNAPSHOT: defensive cleanup.
-- Idempotent: guarded by WHERE. Naming convention: all uppercase.
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
SET M_URL = N'/admin/report-conditions',
    NEW_URL = NULL,
    MODI_URL = NULL,
    REMARK = N'已归入纯定制页（2026-08-28 用户拍板）：报表过滤条件设置由 /admin/report-conditions 承载（SYSQR_DA + SYSQR_DEFAULT 按模块维护），已从统一表单白名单剔除并清空 NEW_URL/MODI_URL，不与工作台关联',
    LAST_UPDATE_BY = @MIG,
    LAST_UPDATE_DATE = @NOW
WHERE M_IDX = 2205;
PRINT N'[EOS-23] MODULES 2205 纯定制页化（M_URL=/admin/report-conditions，清空 NEW_URL/MODI_URL）：' + CAST(@@ROWCOUNT AS NVARCHAR(10)) + N' 行';

IF OBJECT_ID('dbo.WORKBENCH_MODULE_DIRTY','U') IS NOT NULL
BEGIN
    DELETE FROM dbo.WORKBENCH_MODULE_DIRTY WHERE MODULE_ID = 2205;
    PRINT N'[EOS-23] WORKBENCH_MODULE_DIRTY 2205 清理：' + CAST(@@ROWCOUNT AS NVARCHAR(10)) + N' 行';
END

IF OBJECT_ID('dbo.WORKBENCH_DEFINITION_SNAPSHOT','U') IS NOT NULL
BEGIN
    DELETE FROM dbo.WORKBENCH_DEFINITION_SNAPSHOT WHERE MODULE_ID = 2205;
    PRINT N'[EOS-23] WORKBENCH_DEFINITION_SNAPSHOT 2205 清理：' + CAST(@@ROWCOUNT AS NVARCHAR(10)) + N' 行';
END

COMMIT TRANSACTION;
PRINT N'[EOS-23] 2205 报表过滤条件设置归类纯定制页完成（页面/端点/路由代码随本次提交落地）。';
