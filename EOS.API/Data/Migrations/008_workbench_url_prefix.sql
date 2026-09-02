-- ============================================================================
-- EOS.ERP migration 008: workbench browser route prefix /document-workbench → /workbench
-- ----------------------------------------------------------------------------
-- Database: EOS.ERP (the single business database of the new system)
--
-- Context: the browser route prefix converges from /document-workbench to /workbench
-- (shorter, closer to document semantics); record keys move from query to path segments
-- (/workbench/{moduleId}/view/{key-segments}). This migration only updates MODULES
-- metadata (M_URL host page, NEW_URL/MODI_URL action templates) so menu navigation and
-- form routes match the new front-end routes. API paths /api/v1/document-workbench stay
-- unchanged (not user-visible; controller routes and apiClient calls are untouched).
--
-- Scope:
--   1. MODULES.M_URL: '/document-workbench' → '/workbench';
--   2. MODULES.MODI_URL: template '/document-workbench/{moduleId}/edit' → '/workbench/...';
--   3. MODULES.NEW_URL / HELP_URL: same REPLACE (currently no hits, idempotent fallback).
--
-- Idempotent: REPLACE is naturally idempotent; LAST_UPDATE_BY/DATE record execution time.
-- Naming convention: all object names uppercase.
-- ============================================================================

SET NOCOUNT ON;
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;

DECLARE @GUARD_MESSAGE NVARCHAR(400) = N'本脚本只能在 EOS.ERP 数据库内执行，当前库为 ' + DB_NAME() + N'。';
IF DB_NAME() <> N'EOS.ERP'
    THROW 50000, @GUARD_MESSAGE, 1;

BEGIN TRANSACTION;

DECLARE @MIG NVARCHAR(20) = N'EOS-21';
DECLARE @NOW DATETIME = GETDATE();

UPDATE dbo.MODULES
SET M_URL = REPLACE(M_URL, '/document-workbench', '/workbench'),
    LAST_UPDATE_BY = @MIG,
    LAST_UPDATE_DATE = @NOW
WHERE ISNULL(M_URL, '') LIKE '%/document-workbench%';
PRINT N'[EOS-21] MODULES.M_URL /document-workbench → /workbench：' + CAST(@@ROWCOUNT AS NVARCHAR(10)) + N' 行';

UPDATE dbo.MODULES
SET MODI_URL = REPLACE(MODI_URL, '/document-workbench', '/workbench'),
    LAST_UPDATE_BY = @MIG,
    LAST_UPDATE_DATE = @NOW
WHERE ISNULL(MODI_URL, '') LIKE '%/document-workbench%';
PRINT N'[EOS-21] MODULES.MODI_URL /document-workbench → /workbench：' + CAST(@@ROWCOUNT AS NVARCHAR(10)) + N' 行';

UPDATE dbo.MODULES
SET NEW_URL = REPLACE(NEW_URL, '/document-workbench', '/workbench'),
    LAST_UPDATE_BY = @MIG,
    LAST_UPDATE_DATE = @NOW
WHERE ISNULL(NEW_URL, '') LIKE '%/document-workbench%';
PRINT N'[EOS-21] MODULES.NEW_URL /document-workbench → /workbench：' + CAST(@@ROWCOUNT AS NVARCHAR(10)) + N' 行';

UPDATE dbo.MODULES
SET HELP_URL = REPLACE(HELP_URL, '/document-workbench', '/workbench'),
    LAST_UPDATE_BY = @MIG,
    LAST_UPDATE_DATE = @NOW
WHERE ISNULL(HELP_URL, '') LIKE '%/document-workbench%';
PRINT N'[EOS-21] MODULES.HELP_URL /document-workbench → /workbench：' + CAST(@@ROWCOUNT AS NVARCHAR(10)) + N' 行';

-- 2. Refresh Definition snapshot JSON (the runtime snapshot cached routes derived from the
--    old M_URL metadata; without refreshing, unified-form "new/edit" would navigate to old paths).
IF OBJECT_ID('dbo.WORKBENCH_DEFINITION_SNAPSHOT', 'U') IS NOT NULL
BEGIN
    UPDATE dbo.WORKBENCH_DEFINITION_SNAPSHOT
    SET DEFINITION_JSON = REPLACE(CAST(DEFINITION_JSON AS NVARCHAR(MAX)), '/document-workbench', '/workbench')
    WHERE CAST(DEFINITION_JSON AS NVARCHAR(MAX)) LIKE '%/document-workbench%';
    PRINT N'[EOS-21] WORKBENCH_DEFINITION_SNAPSHOT 快照 JSON 刷新：' + CAST(@@ROWCOUNT AS NVARCHAR(10)) + N' 行';
END

COMMIT TRANSACTION;
PRINT N'[EOS-21] 工作台路由前缀迁移完成（M_URL/MODI_URL/NEW_URL/HELP_URL + Definition 快照）。';