-- ============================================================================
-- EOS.ERP migration 031: visual layout designer — customer layout storage + design permission bits
-- ----------------------------------------------------------------------------
-- Context: built-in layouts are developer-owned Git assets (ReportFormats packages,
-- read-only, versioned via Git + code review). Customer customization is copy-on-write
-- stored in the database:
--     REPORT_FORM_LAYOUT  = customized layout body (copy of the built-in format then edit, LAYOUT_VERSION tracks versions);
--     REPORT_FORM_BINDING = binding table (FORM_TYPE × CLIENT_ID; empty CLIENT_ID = document-type default).
-- Permission bits (three tiers): role ② implementer = FORM_DESIGN_TAG (full designer);
-- role ③ customer maintainer = FORM_ADJUST_TAG (tweak mode); role ① developer stays out of the runtime permission system.
--
-- Design:
--   REPORT_FORM_LAYOUT (LAYOUT_ID INT IDENTITY, BASE_FORMAT_ID, OWNER_ID,
--                        LAYOUT_JSON NVARCHAR(MAX), LAYOUT_VERSION, KIND, audit cols)
--   REPORT_FORM_BINDING (FORM_TYPE, CLIENT_ID, LAYOUT_ID, HEADER_ID, TAIL_ID,
--                        PRINT_PRICE, audit cols; PK = (FORM_TYPE, CLIENT_ID))
--   SYSDD / SYSDH each gain FORM_DESIGN_TAG / FORM_ADJUST_TAG (default 0, uppercase)
--
-- Idempotent: IF OBJECT_ID / sys.columns guards, safe to re-run.
-- ============================================================================

SET NOCOUNT ON;
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;

DECLARE @GUARD_MESSAGE NVARCHAR(400) = N'本脚本只能在 EOS.ERP 数据库内执行，当前库为 ' + DB_NAME() + N'。';
IF DB_NAME() <> N'EOS.ERP'
    THROW 50000, @GUARD_MESSAGE, 1;

BEGIN TRANSACTION;

-- 1. REPORT_FORM_LAYOUT：客户定制布局本体（copy-on-write）
IF OBJECT_ID('dbo.REPORT_FORM_LAYOUT') IS NULL
BEGIN
    CREATE TABLE dbo.REPORT_FORM_LAYOUT (
        [LAYOUT_ID] INT IDENTITY(1,1) NOT NULL
            CONSTRAINT [PK_REPORT_FORM_LAYOUT] PRIMARY KEY,
        [BASE_FORMAT_ID] NVARCHAR(50) NOT NULL,
        [OWNER_ID] NVARCHAR(50) NOT NULL CONSTRAINT [DF_REPORT_FORM_LAYOUT_OWNER] DEFAULT (N''),
        [LAYOUT_JSON] NVARCHAR(MAX) NOT NULL,
        [LAYOUT_VERSION] INT NOT NULL CONSTRAINT [DF_REPORT_FORM_LAYOUT_VERSION] DEFAULT (1),
        [KIND] NVARCHAR(20) NOT NULL CONSTRAINT [DF_REPORT_FORM_LAYOUT_KIND] DEFAULT (N'document'),
        [CREATE_PERSON] NCHAR(40) NULL,
        [CREATE_DATE] DATETIME2 NOT NULL CONSTRAINT [DF_REPORT_FORM_LAYOUT_CREATED] DEFAULT (SYSDATETIME()),
        [LAST_UPDATE_BY] NCHAR(40) NULL,
        [LAST_UPDATE_DATE] DATETIME2 NULL
    );
    CREATE INDEX [IX_REPORT_FORM_LAYOUT_BASE_OWNER]
        ON dbo.REPORT_FORM_LAYOUT ([BASE_FORMAT_ID], [OWNER_ID]);
END

-- 2. REPORT_FORM_BINDING：FORM_TYPE × CLIENT_ID 级配置（空 CLIENT_ID = 单据类型默认）
IF OBJECT_ID('dbo.REPORT_FORM_BINDING') IS NULL
BEGIN
    CREATE TABLE dbo.REPORT_FORM_BINDING (
        [FORM_TYPE] NVARCHAR(20) NOT NULL,
        [CLIENT_ID] NVARCHAR(50) NOT NULL CONSTRAINT [DF_REPORT_FORM_BINDING_CLIENT] DEFAULT (N''),
        [LAYOUT_ID] INT NULL,
        [HEADER_ID] NVARCHAR(50) NULL,
        [TAIL_ID] NVARCHAR(50) NULL,
        [PRINT_PRICE] BIT NULL,
        [CREATE_PERSON] NCHAR(40) NULL,
        [CREATE_DATE] DATETIME2 NOT NULL CONSTRAINT [DF_REPORT_FORM_BINDING_CREATED] DEFAULT (SYSDATETIME()),
        [LAST_UPDATE_BY] NCHAR(40) NULL,
        [LAST_UPDATE_DATE] DATETIME2 NULL,
        CONSTRAINT [PK_REPORT_FORM_BINDING] PRIMARY KEY ([FORM_TYPE], [CLIENT_ID]),
        CONSTRAINT [FK_REPORT_FORM_BINDING_LAYOUT] FOREIGN KEY ([LAYOUT_ID])
            REFERENCES dbo.REPORT_FORM_LAYOUT ([LAYOUT_ID]) ON DELETE SET NULL
    );
    CREATE INDEX [IX_REPORT_FORM_BINDING_LAYOUT]
        ON dbo.REPORT_FORM_BINDING ([LAYOUT_ID]);
END

-- 3. SYSDD / SYSDH design permission bits (full designer / tweak mode)
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.SYSDD') AND name = 'FORM_DESIGN_TAG')
    ALTER TABLE dbo.SYSDD ADD
        [FORM_DESIGN_TAG] BIT NOT NULL CONSTRAINT [DF_SYSDD_FORM_DESIGN] DEFAULT (0),
        [FORM_ADJUST_TAG] BIT NOT NULL CONSTRAINT [DF_SYSDD_FORM_ADJUST] DEFAULT (0);

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.SYSDH') AND name = 'FORM_DESIGN_TAG')
    ALTER TABLE dbo.SYSDH ADD
        [FORM_DESIGN_TAG] BIT NOT NULL CONSTRAINT [DF_SYSDH_FORM_DESIGN] DEFAULT (0),
        [FORM_ADJUST_TAG] BIT NOT NULL CONSTRAINT [DF_SYSDH_FORM_ADJUST] DEFAULT (0);

-- 4. 扩展属性（表/列说明，便于 schema 审阅）
IF NOT EXISTS (SELECT 1 FROM sys.extended_properties
               WHERE major_id = OBJECT_ID('dbo.REPORT_FORM_LAYOUT') AND minor_id = 0)
    EXEC sp_addextendedproperty @name = N'MS_Description',
        @value = N'客户定制版式本体（copy-on-write，）：复制内置格式后编辑，LAYOUT_VERSION 追踪版本',
        @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'REPORT_FORM_LAYOUT';

IF NOT EXISTS (SELECT 1 FROM sys.extended_properties
               WHERE major_id = OBJECT_ID('dbo.REPORT_FORM_BINDING') AND minor_id = 0)
    EXEC sp_addextendedproperty @name = N'MS_Description',
        @value = N'单据版式绑定（ / ）：FORM_TYPE × CLIENT_ID，空 CLIENT_ID = 单据类型默认，收货方不作键',
        @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'REPORT_FORM_BINDING';

IF NOT EXISTS (SELECT 1 FROM sys.extended_properties WHERE major_id = OBJECT_ID('dbo.SYSDD')
               AND minor_id = (SELECT column_id FROM sys.columns WHERE object_id = OBJECT_ID('dbo.SYSDD') AND name = 'FORM_DESIGN_TAG'))
    EXEC sp_addextendedproperty @name = N'MS_Description', @value = N'版式完整设计权限（角色② 实施顾问，）',
        @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'SYSDD',
        @level2type = N'COLUMN', @level2name = N'FORM_DESIGN_TAG';

IF NOT EXISTS (SELECT 1 FROM sys.extended_properties WHERE major_id = OBJECT_ID('dbo.SYSDD')
               AND minor_id = (SELECT column_id FROM sys.columns WHERE object_id = OBJECT_ID('dbo.SYSDD') AND name = 'FORM_ADJUST_TAG'))
    EXEC sp_addextendedproperty @name = N'MS_Description', @value = N'版式微调权限（角色③ 客户维护人员，）',
        @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'SYSDD',
        @level2type = N'COLUMN', @level2name = N'FORM_ADJUST_TAG';

IF NOT EXISTS (SELECT 1 FROM sys.extended_properties WHERE major_id = OBJECT_ID('dbo.SYSDH')
               AND minor_id = (SELECT column_id FROM sys.columns WHERE object_id = OBJECT_ID('dbo.SYSDH') AND name = 'FORM_DESIGN_TAG'))
    EXEC sp_addextendedproperty @name = N'MS_Description', @value = N'版式完整设计权限（角色② 实施顾问，）',
        @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'SYSDH',
        @level2type = N'COLUMN', @level2name = N'FORM_DESIGN_TAG';

IF NOT EXISTS (SELECT 1 FROM sys.extended_properties WHERE major_id = OBJECT_ID('dbo.SYSDH')
               AND minor_id = (SELECT column_id FROM sys.columns WHERE object_id = OBJECT_ID('dbo.SYSDH') AND name = 'FORM_ADJUST_TAG'))
    EXEC sp_addextendedproperty @name = N'MS_Description', @value = N'版式微调权限（角色③ 客户维护人员，）',
        @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'SYSDH',
        @level2type = N'COLUMN', @level2name = N'FORM_ADJUST_TAG';

-- 5. 审计计数
SELECT 'REPORT_FORM_LAYOUT' AS OBJECT_NAME, COUNT(*) AS CNT FROM sys.tables WHERE name = 'REPORT_FORM_LAYOUT';
SELECT 'REPORT_FORM_BINDING' AS OBJECT_NAME, COUNT(*) AS CNT FROM sys.tables WHERE name = 'REPORT_FORM_BINDING';
SELECT 'SYSDD_FORM_DESIGN' AS OBJECT_NAME, COUNT(*) AS CNT FROM sys.columns
WHERE object_id = OBJECT_ID('dbo.SYSDD') AND name = 'FORM_DESIGN_TAG';
SELECT 'SYSDH_FORM_DESIGN' AS OBJECT_NAME, COUNT(*) AS CNT FROM sys.columns
WHERE object_id = OBJECT_ID('dbo.SYSDH') AND name = 'FORM_DESIGN_TAG';
SELECT 'SYSDD_FORM_ADJUST' AS OBJECT_NAME, COUNT(*) AS CNT FROM sys.columns
WHERE object_id = OBJECT_ID('dbo.SYSDD') AND name = 'FORM_ADJUST_TAG';
SELECT 'SYSDH_FORM_ADJUST' AS OBJECT_NAME, COUNT(*) AS CNT FROM sys.columns
WHERE object_id = OBJECT_ID('dbo.SYSDH') AND name = 'FORM_ADJUST_TAG';

COMMIT TRANSACTION;
