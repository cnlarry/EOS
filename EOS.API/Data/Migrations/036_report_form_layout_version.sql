-- ============================================================================
-- EOS.ERP migration 036: layout version history (version history + rollback)
-- ----------------------------------------------------------------------------
-- Context: REPORT_FORM_LAYOUT stores only the latest version per layout row
-- (LAYOUT_VERSION increments but old versions were overwritten). Customer-customized
-- layouts need version history + comparison + rollback, so a history snapshot table
-- is introduced: REPORT_FORM_LAYOUT_VERSION (LAYOUT_ID + VERSION + LAYOUT_JSON + audit).
-- Save/rollback writes a snapshot; built-in layout upgrades never overwrite customer
-- customizations.
-- Idempotent: IF OBJECT_ID guards, safe to re-run.
-- ============================================================================

SET NOCOUNT ON;
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;

DECLARE @GUARD_MESSAGE NVARCHAR(400) = N'本脚本只能在 EOS.ERP 数据库内执行，当前库为 ' + DB_NAME() + N'。';
IF DB_NAME() <> N'EOS.ERP'
    THROW 50000, @GUARD_MESSAGE, 1;

BEGIN TRANSACTION;

IF OBJECT_ID('dbo.REPORT_FORM_LAYOUT_VERSION') IS NULL
BEGIN
    CREATE TABLE dbo.REPORT_FORM_LAYOUT_VERSION (
        [LAYOUT_ID] INT NOT NULL,
        [VERSION] INT NOT NULL,
        [LAYOUT_JSON] NVARCHAR(MAX) NOT NULL,
        [CREATE_PERSON] NCHAR(40) NULL,
        [CREATE_DATE] DATETIME2 NOT NULL CONSTRAINT [DF_REPORT_FORM_LAYOUT_VERSION_CREATED] DEFAULT (SYSDATETIME()),
        CONSTRAINT [PK_REPORT_FORM_LAYOUT_VERSION] PRIMARY KEY ([LAYOUT_ID], [VERSION]),
        CONSTRAINT [FK_REPORT_FORM_LAYOUT_VERSION_LAYOUT] FOREIGN KEY ([LAYOUT_ID])
            REFERENCES dbo.REPORT_FORM_LAYOUT ([LAYOUT_ID]) ON DELETE CASCADE
    );
    CREATE INDEX [IX_REPORT_FORM_LAYOUT_VERSION_DATE]
        ON dbo.REPORT_FORM_LAYOUT_VERSION ([LAYOUT_ID], [CREATE_DATE] DESC);
END

IF NOT EXISTS (SELECT 1 FROM sys.extended_properties
               WHERE major_id = OBJECT_ID('dbo.REPORT_FORM_LAYOUT_VERSION') AND minor_id = 0)
    EXEC sp_addextendedproperty @name = N'MS_Description',
        @value = N'客户定制版式历史快照（ADR-010 P0 版本历史/对比/回滚）：保存与回滚时写入，LAYOUT_VERSION 对应主表版本',
        @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'REPORT_FORM_LAYOUT_VERSION';

SELECT 'REPORT_FORM_LAYOUT_VERSION' AS OBJECT_NAME, COUNT(*) AS CNT FROM sys.tables WHERE name = 'REPORT_FORM_LAYOUT_VERSION';

COMMIT TRANSACTION;
