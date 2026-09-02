-- ============================================================================
-- EOS.ERP migration 014: clean up legacy deny-list sentinel value '0'
-- ----------------------------------------------------------------------------
-- Database: EOS.ERP (the single business database of the new system)
--
-- Context: legacy data stored "no denied fields" as '0' (the old grid split by ';'
-- and hid columns by name — '0' was never a valid column name, so it had no effect).
-- The new permission matrix validates against the field whitelist on save, so '0'
-- causes a 400 error ("deny-view field 0 is not a field of module X"). This migration
-- sets those '0' values to NULL in SYSDH/SYSDD six deny-list columns. The code side
-- (RightsAdminLogic.ParseDenyList) already treats '0' as empty.
-- Idempotent: guarded by WHERE for exact match '0'. Naming convention: all uppercase.
-- ============================================================================

SET NOCOUNT ON;
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;

DECLARE @GUARD_MESSAGE NVARCHAR(400) = N'本脚本只能在 EOS.ERP 数据库内执行，当前库为 ' + DB_NAME() + N'。';
IF DB_NAME() <> N'EOS.ERP'
    THROW 50000, @GUARD_MESSAGE, 1;

BEGIN TRANSACTION;

DECLARE @DENY_COLUMNS TABLE (COLUMN_NAME sysname);
INSERT INTO @DENY_COLUMNS (COLUMN_NAME) VALUES
    (N'DENY_VIEW_FIELD_MASTER'), (N'DENY_VIEW_FIELD_DETAIL'),
    (N'DENY_NEW_FIELD_MASTER'), (N'DENY_NEW_FIELD_DETAIL'),
    (N'DENY_MODI_FIELD_MASTER'), (N'DENY_MODI_FIELD_DETAIL');

DECLARE @TABLE_SQL NVARCHAR(MAX) = N'';
SELECT @TABLE_SQL = @TABLE_SQL +
    N'UPDATE dbo.' + t.TABLE_NAME + N' SET ' + QUOTENAME(c.COLUMN_NAME) + N' = NULL WHERE LTRIM(RTRIM(ISNULL(' + QUOTENAME(c.COLUMN_NAME) + N', N''''))) = N''0'';' + CHAR(10)
FROM @DENY_COLUMNS c
CROSS JOIN (VALUES (N'SYSDH'), (N'SYSDD')) AS t(TABLE_NAME);

EXEC sys.sp_executesql @TABLE_SQL;

PRINT N'[EOS-23] 字段级拒绝遗留哨兵 ''0'' 清理完成（SYSDH/SYSDD 六列）。';

COMMIT TRANSACTION;
