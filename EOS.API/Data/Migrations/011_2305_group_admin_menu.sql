-- ============================================================================
-- EOS.ERP migration 011: 2305 group admin menu → dedicated page /admin/groups
-- ----------------------------------------------------------------------------
-- Database: EOS.ERP (the single business database of the new system)
--
-- Context: 2305 group management is too complex for the unified form workbench.
-- The menu URL is changed to /admin/groups (dedicated custom page with group
-- permissions, report permissions and members). The group master CRUD still uses
-- the workbench (/workbench/2305) via MODI_URL.
--
-- Scope:
--   1. MODULES 2305: M_URL → /admin/groups, REMARK records the decision;
--   2. MODI_URL kept as /workbench/{moduleId}/edit (master-data workbench still works).
--
-- Idempotent: guarded by WHERE (only updates when M_URL is not the target value).
-- Naming convention: all object names uppercase.
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
SET M_URL = N'/admin/groups',
    REMARK = N'已归入定制页（M92/2026-08-16 决策，EOS-23/2026-08-27 迁移落库）：用户组管理菜单指向专用页 /admin/groups（组权限/报表权限/成员），组主档 CRUD 保留统一表单（页面内「主档」入口 /workbench/2305，MODI_URL 保留编辑模板）',
    LAST_UPDATE_BY = @MIG,
    LAST_UPDATE_DATE = @NOW
WHERE M_IDX = 2305
  AND LTRIM(RTRIM(ISNULL(M_URL, ''))) <> N'/admin/groups';
PRINT N'[EOS-23] MODULES 2305 M_URL → /admin/groups：' + CAST(@@ROWCOUNT AS NVARCHAR(10)) + N' 行';

COMMIT TRANSACTION;
PRINT N'[EOS-23] 2305 用户组管理菜单回归定制页完成。';
