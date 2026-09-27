-- ============================================================================
-- EOS.ERP migration 030: physically drop the four "user-stored SQL" tables
-- ----------------------------------------------------------------------------
-- Context: three isomorphic "user-stored SQL" tables — SYSQD / SYSQL / LISTREPORT
-- (PKs all include USER_ID+T_ID, columns T_SQL/T_CONDITION) — plus LISTREPORT's
-- WHERE-syntax child table LISTREPORT_CONDITION are dropped. An audit confirmed the
-- modern system (EOS.API/EOS.Web) consumes none of them, and their historic consumer-- pages were not ported. Direction: no longer allow user runtime-stored SQL, so drop.
--
-- Dropped: SYSQD / SYSQL / LISTREPORT / LISTREPORT_CONDITION
--
-- Kept (live tables used by workbench query/column config, do not delete):
--   SYSQD_CONDITION / SYSQL_FIELDS / SYSQL_CONDITION / SYSQL_COND_DFT / SYSQL_DEFAULT
--   (FieldAdminRepository field-delete cleanup / reference counting depends on these)
--
-- Note: the historic stored procedure xp_user_listrpt_fields references LISTREPORT_CONDITION-- and becomes a dangling reference after the drop; the modern system never calls it.
--
-- Idempotent: IF OBJECT_ID guards — skips when table already gone, safe to re-run.
-- ============================================================================

SET NOCOUNT ON;
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;

DECLARE @GUARD_MESSAGE NVARCHAR(400) = N'本脚本只能在 EOS.ERP 数据库内执行，当前库为 ' + DB_NAME() + N'。';
IF DB_NAME() <> N'EOS.ERP'
    THROW 50000, @GUARD_MESSAGE, 1;

BEGIN TRANSACTION;

-- 1. 物理删除四表（存在才删）
IF OBJECT_ID('dbo.LISTREPORT_CONDITION') IS NOT NULL DROP TABLE dbo.LISTREPORT_CONDITION;
IF OBJECT_ID('dbo.LISTREPORT') IS NOT NULL DROP TABLE dbo.LISTREPORT;
IF OBJECT_ID('dbo.SYSQL') IS NOT NULL DROP TABLE dbo.SYSQL;
IF OBJECT_ID('dbo.SYSQD') IS NOT NULL DROP TABLE dbo.SYSQD;

-- 2. 审计计数
SELECT 'LISTREPORT_CONDITION' AS OBJECT_NAME, COUNT(*) AS REMAINING
FROM sys.tables WHERE name = 'LISTREPORT_CONDITION';
SELECT 'LISTREPORT' AS OBJECT_NAME, COUNT(*) AS REMAINING
FROM sys.tables WHERE name = 'LISTREPORT';
SELECT 'SYSQL' AS OBJECT_NAME, COUNT(*) AS REMAINING
FROM sys.tables WHERE name = 'SYSQL';
SELECT 'SYSQD' AS OBJECT_NAME, COUNT(*) AS REMAINING
FROM sys.tables WHERE name = 'SYSQD';

COMMIT TRANSACTION;
