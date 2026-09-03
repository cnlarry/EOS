-- Drop the one-off ghost-cleaning backup tables FIELDS_SysGhostBackup / SYSQL_FIELDS_GhostBackup.
-- Background: both were created by a legacy metadata cleaning step (2026-08-18,
-- docs/migrations/update.sql CLEAN section) as rollback insurance while removing FIELDS
-- sysconstraints/syssegments metadata leftovers and SYSQL_FIELDS ghost column references.
-- Both tables are empty today and have no runtime consumers; the cleanup is long finished,
-- so the rollback insurance is no longer of any use. Idempotent: drop only if present.

SET NOCOUNT ON;

DECLARE @GUARD NVARCHAR(400) = N'本脚本只能在 EOS.ERP 数据库内执行，当前库为 ' + DB_NAME() + N'。';
IF DB_NAME() <> N'EOS.ERP'
    THROW 50000, @GUARD, 1;

BEGIN TRANSACTION;

IF OBJECT_ID(N'dbo.FIELDS_SysGhostBackup', N'U') IS NOT NULL
    DROP TABLE dbo.FIELDS_SysGhostBackup;

IF OBJECT_ID(N'dbo.SYSQL_FIELDS_GhostBackup', N'U') IS NOT NULL
    DROP TABLE dbo.SYSQL_FIELDS_GhostBackup;

COMMIT TRANSACTION;

PRINT N'[042] 幽灵清理备份表已删除（FIELDS_SysGhostBackup / SYSQL_FIELDS_GhostBackup）。';
