-- Report right model: SYSDD_REPORT / SYSDH_REPORT demoted to override tables, with
-- favorite and last-run columns added. Semantic change (code side, see LegacyRightsRepository.GetReportAsync):
--   SYSDD.REPORT_TAG is the single source of truth for module-level report visibility;
--   SYSDD_REPORT / SYSDH_REPORT become override tables:
--   no row = follow module REPORT_TAG (preview/print/export all open);
--   row present = override to tighten (preview/print/export override, DATA_FILTER intersection with module filter).
-- New columns for the report center (P5 favorites / recent use); DATA_FILTER retained (override semantics).
-- Cleanup: remove materialized "default-open" rows (PREVIEW=PRINT=EXPORT=1 and DATA_FILTER empty),
-- which are equivalent to "follow module REPORT_TAG open" and constituted ~100K rows (200 users × 500 reports).

SET NOCOUNT ON;

DECLARE @GUARD_MESSAGE NVARCHAR(400) = N'本脚本只能在 EOS.ERP 数据库内执行，当前库为 ' + DB_NAME() + N'。';
IF DB_NAME() <> N'EOS.ERP'
    THROW 50000, @GUARD_MESSAGE, 1;

BEGIN TRANSACTION;

-- SYSDD_REPORT 加列
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.SYSDD_REPORT') AND name = 'FAVORITE_TAG')
    ALTER TABLE dbo.SYSDD_REPORT ADD
        [FAVORITE_TAG] BIT NOT NULL CONSTRAINT [DF_SYSDD_REPORT_FAVORITE] DEFAULT (0),
        [SORT_IDX] INT NULL,
        [LAST_RUN_AT] DATETIME2 NULL;

-- SYSDH_REPORT 加列
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.SYSDH_REPORT') AND name = 'FAVORITE_TAG')
    ALTER TABLE dbo.SYSDH_REPORT ADD
        [FAVORITE_TAG] BIT NOT NULL CONSTRAINT [DF_SYSDH_REPORT_FAVORITE] DEFAULT (0),
        [SORT_IDX] INT NULL,
        [LAST_RUN_AT] DATETIME2 NULL;

-- 清理物化冗余行：三标记全 1 且 DATA_FILTER 空 = 与"默认全开"行为等价。
-- 保留有收紧语义的行（任一标记为 0 或 DATA_FILTER 非空）。
DELETE FROM dbo.SYSDD_REPORT
WHERE ISNULL(PREVIEW_TAG,0)=1 AND ISNULL(PRINT_TAG,0)=1 AND ISNULL(EXPORT_TAG,0)=1
  AND ISNULL(LTRIM(RTRIM(DATA_FILTER)),'')='';

DELETE FROM dbo.SYSDH_REPORT
WHERE ISNULL(PREVIEW_TAG,0)=1 AND ISNULL(PRINT_TAG,0)=1 AND ISNULL(EXPORT_TAG,0)=1
  AND ISNULL(LTRIM(RTRIM(DATA_FILTER)),'')='';

-- 扩展属性
IF NOT EXISTS (SELECT 1 FROM sys.extended_properties WHERE major_id = OBJECT_ID('dbo.SYSDD_REPORT') AND minor_id = (SELECT column_id FROM sys.columns WHERE object_id = OBJECT_ID('dbo.SYSDD_REPORT') AND name = 'FAVORITE_TAG'))
    EXEC sp_addextendedproperty @name = N'MS_Description', @value = N'收藏标记', @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'SYSDD_REPORT', @level2type = N'COLUMN', @level2name = N'FAVORITE_TAG';

IF NOT EXISTS (SELECT 1 FROM sys.extended_properties WHERE major_id = OBJECT_ID('dbo.SYSDD_REPORT') AND minor_id = (SELECT column_id FROM sys.columns WHERE object_id = OBJECT_ID('dbo.SYSDD_REPORT') AND name = 'SORT_IDX'))
    EXEC sp_addextendedproperty @name = N'MS_Description', @value = N'收藏排序', @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'SYSDD_REPORT', @level2type = N'COLUMN', @level2name = N'SORT_IDX';

IF NOT EXISTS (SELECT 1 FROM sys.extended_properties WHERE major_id = OBJECT_ID('dbo.SYSDD_REPORT') AND minor_id = (SELECT column_id FROM sys.columns WHERE object_id = OBJECT_ID('dbo.SYSDD_REPORT') AND name = 'LAST_RUN_AT'))
    EXEC sp_addextendedproperty @name = N'MS_Description', @value = N'最近使用时间', @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'SYSDD_REPORT', @level2type = N'COLUMN', @level2name = N'LAST_RUN_AT';

IF NOT EXISTS (SELECT 1 FROM sys.extended_properties WHERE major_id = OBJECT_ID('dbo.SYSDH_REPORT') AND minor_id = (SELECT column_id FROM sys.columns WHERE object_id = OBJECT_ID('dbo.SYSDH_REPORT') AND name = 'FAVORITE_TAG'))
    EXEC sp_addextendedproperty @name = N'MS_Description', @value = N'收藏标记', @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'SYSDH_REPORT', @level2type = N'COLUMN', @level2name = N'FAVORITE_TAG';

IF NOT EXISTS (SELECT 1 FROM sys.extended_properties WHERE major_id = OBJECT_ID('dbo.SYSDH_REPORT') AND minor_id = (SELECT column_id FROM sys.columns WHERE object_id = OBJECT_ID('dbo.SYSDH_REPORT') AND name = 'SORT_IDX'))
    EXEC sp_addextendedproperty @name = N'MS_Description', @value = N'收藏排序', @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'SYSDH_REPORT', @level2type = N'COLUMN', @level2name = N'SORT_IDX';

IF NOT EXISTS (SELECT 1 FROM sys.extended_properties WHERE major_id = OBJECT_ID('dbo.SYSDH_REPORT') AND minor_id = (SELECT column_id FROM sys.columns WHERE object_id = OBJECT_ID('dbo.SYSDH_REPORT') AND name = 'LAST_RUN_AT'))
    EXEC sp_addextendedproperty @name = N'MS_Description', @value = N'最近使用时间', @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'SYSDH_REPORT', @level2type = N'COLUMN', @level2name = N'LAST_RUN_AT';

COMMIT TRANSACTION;