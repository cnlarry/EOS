-- ============================================================================
-- EOS.ERP 迁移 036：ADR-010 版式版本历史（P0 版本历史+回滚）
-- ----------------------------------------------------------------------------
-- 背景（2026-08-31，ADR-010 决策 3 / 设计器 P0）：
--   REPORT_FORM_LAYOUT 单行存最新版（LAYOUT_VERSION 递增但旧版被覆盖），
--   客户定制需版本历史 + 对比 + 回滚 → 新建历史快照表：
--     REPORT_FORM_LAYOUT_VERSION（LAYOUT_ID + VERSION + LAYOUT_JSON + 审计列）
--   保存/回滚时写入快照，内置版式升级不覆盖客户定制（决策 3 语义不变）。
--
-- 幂等性：IF OBJECT_ID 守卫，DbUp 重复执行无副作用。
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
