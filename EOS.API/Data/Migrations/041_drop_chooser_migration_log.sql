-- Drop the one-off migration audit queue CHOOSER_FILTER_MIGRATION_LOG.
-- Background: it was the audit/pending-rebuild queue for the CHOOSE_FILTER → FILTER_STRUCT
-- conversion. All rows are now converged (CONVERTED / EXEMPTED / CLEANED, no PENDING_P3 / MANUAL / DRIFT)
-- and the conversion channel is formally closed, so the queue no longer serves any runtime purpose.
-- The write-side guard that read it (FieldAdminRepository.HasPendingMigrationFilterAsync) is removed
-- in the same change set; archive of the full log is kept at logs/fields-chooser-migration/migration-log-archive.csv.
-- Drift guard: refuse to drop while any pending-rebuild row still exists.

SET NOCOUNT ON;

DECLARE @GUARD NVARCHAR(400) = N'本脚本只能在 EOS.ERP 数据库内执行，当前库为 ' + DB_NAME() + N'。';
IF DB_NAME() <> N'EOS.ERP'
    THROW 50000, @GUARD, 1;

BEGIN TRANSACTION;

IF EXISTS (SELECT 1 FROM dbo.CHOOSER_FILTER_MIGRATION_LOG WITH (NOLOCK) WHERE STATUS IN (N'PENDING_P3', N'MANUAL', N'DRIFT'))
    THROW 50000, N'迁移日志仍存在待重建行（PENDING_P3/MANUAL/DRIFT），禁止删除 CHOOSER_FILTER_MIGRATION_LOG。', 1;

IF OBJECT_ID(N'dbo.CHOOSER_FILTER_MIGRATION_LOG', N'U') IS NOT NULL
    DROP TABLE dbo.CHOOSER_FILTER_MIGRATION_LOG;

COMMIT TRANSACTION;

PRINT N'[041] CHOOSER_FILTER_MIGRATION_LOG 已删除；全量归档见 logs/fields-chooser-migration/migration-log-archive.csv。';
