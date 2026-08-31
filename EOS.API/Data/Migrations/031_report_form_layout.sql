-- ============================================================================
-- EOS.ERP 迁移 031：ADR-010 可视化版式设计器——客户定制布局存储 + 设计权限位
-- ----------------------------------------------------------------------------
-- 背景（2026-08-31，ADR-010 决策 3/4/5，用户拍板）：
--   内置版式为开发态 Git 资产（ReportFormats 格式包，只读，进 Git + code review）；
--   客户定制（copy-on-write）存库，沿用 EOS"配置在库"模式：
--     REPORT_FORM_LAYOUT  = 定制布局本体（复制内置版式后编辑，LAYOUT_VERSION 追踪版本）；
--     REPORT_FORM_BINDING = 绑定表（FORM_TYPE × CLIENT_ID 级配置，空 CLIENT_ID = 单据类型默认；
--                          绑定键沿用 ADR-009 §9.5.3：收货方不作键）。
--   权限位（决策 4/5 三档分级）：角色② 实施顾问 = FORM_DESIGN_TAG（完整设计器）；
--   角色③ 客户维护人员 = FORM_ADJUST_TAG（微调模式）；角色① 开发人员不进运行时权限系统。
--
-- 设计：
--   REPORT_FORM_LAYOUT（LAYOUT_ID INT IDENTITY, BASE_FORMAT_ID, OWNER_ID,
--                        LAYOUT_JSON NVARCHAR(MAX), LAYOUT_VERSION, KIND, 审计列）
--   REPORT_FORM_BINDING（FORM_TYPE, CLIENT_ID, LAYOUT_ID, HEADER_ID, TAIL_ID,
--                        PRINT_PRICE, 审计列；PK = (FORM_TYPE, CLIENT_ID)）
--   SYSDD / SYSDH 各加 FORM_DESIGN_TAG / FORM_ADJUST_TAG（默认 0，全大写命名）
--
-- 幂等性：全程 IF OBJECT_ID / sys.columns 守卫，DbUp 重复执行无副作用。
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

-- 3. SYSDD / SYSDH 设计权限位（ADR-010 决策 4/5：角色② CanDesign / 角色③ CanAdjust）
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
        @value = N'客户定制版式本体（copy-on-write，ADR-010 决策 3）：复制内置格式后编辑，LAYOUT_VERSION 追踪版本',
        @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'REPORT_FORM_LAYOUT';

IF NOT EXISTS (SELECT 1 FROM sys.extended_properties
               WHERE major_id = OBJECT_ID('dbo.REPORT_FORM_BINDING') AND minor_id = 0)
    EXEC sp_addextendedproperty @name = N'MS_Description',
        @value = N'单据版式绑定（ADR-009 §9.5.3 / ADR-010 决策 3）：FORM_TYPE × CLIENT_ID，空 CLIENT_ID = 单据类型默认，收货方不作键',
        @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'REPORT_FORM_BINDING';

IF NOT EXISTS (SELECT 1 FROM sys.extended_properties WHERE major_id = OBJECT_ID('dbo.SYSDD')
               AND minor_id = (SELECT column_id FROM sys.columns WHERE object_id = OBJECT_ID('dbo.SYSDD') AND name = 'FORM_DESIGN_TAG'))
    EXEC sp_addextendedproperty @name = N'MS_Description', @value = N'版式完整设计权限（角色② 实施顾问，ADR-010 决策 4）',
        @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'SYSDD',
        @level2type = N'COLUMN', @level2name = N'FORM_DESIGN_TAG';

IF NOT EXISTS (SELECT 1 FROM sys.extended_properties WHERE major_id = OBJECT_ID('dbo.SYSDD')
               AND minor_id = (SELECT column_id FROM sys.columns WHERE object_id = OBJECT_ID('dbo.SYSDD') AND name = 'FORM_ADJUST_TAG'))
    EXEC sp_addextendedproperty @name = N'MS_Description', @value = N'版式微调权限（角色③ 客户维护人员，ADR-010 决策 4）',
        @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'SYSDD',
        @level2type = N'COLUMN', @level2name = N'FORM_ADJUST_TAG';

IF NOT EXISTS (SELECT 1 FROM sys.extended_properties WHERE major_id = OBJECT_ID('dbo.SYSDH')
               AND minor_id = (SELECT column_id FROM sys.columns WHERE object_id = OBJECT_ID('dbo.SYSDH') AND name = 'FORM_DESIGN_TAG'))
    EXEC sp_addextendedproperty @name = N'MS_Description', @value = N'版式完整设计权限（角色② 实施顾问，ADR-010 决策 4）',
        @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'SYSDH',
        @level2type = N'COLUMN', @level2name = N'FORM_DESIGN_TAG';

IF NOT EXISTS (SELECT 1 FROM sys.extended_properties WHERE major_id = OBJECT_ID('dbo.SYSDH')
               AND minor_id = (SELECT column_id FROM sys.columns WHERE object_id = OBJECT_ID('dbo.SYSDH') AND name = 'FORM_ADJUST_TAG'))
    EXEC sp_addextendedproperty @name = N'MS_Description', @value = N'版式微调权限（角色③ 客户维护人员，ADR-010 决策 4）',
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
