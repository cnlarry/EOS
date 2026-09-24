-- ============================================================================
-- EOS.ERP migration 228: module configuration capability bit (SYSDD / SYSDH)
-- ----------------------------------------------------------------------------
-- Context: module 2301 (module management) exposes two write surfaces on one page:
--   · basic attributes — menu name / URL / icon / help, enable-disable, ordering,
--     move, delete, default query columns;
--   · configuration surface — business actions (formula steps), validation rules,
--     custom document buttons, plus publishing the workbench definition snapshot.
--
-- SETUP_TAG alone cannot express "maintains menus but must not touch configuration",
-- so a separate capability bit is added next to it:
--   MODULE_CONFIG_TAG = 1 → may edit the module's configuration surface.
-- SETUP_TAG keeps gating the basic attributes; neither bit implies the other.
--
-- Additive: existing rows default to 0. Rows of module 2301 are then backfilled from
-- SETUP_TAG so that whoever configures the system today is not locked out when the
-- gate starts reading the new bit. Only module 2301 is backfilled — the bit is read
-- nowhere else.
--
-- Structure: the ALTER and the statements that reference the new column live in
-- separate batches. SQL Server compiles a batch before running it, so a column added
-- and used within one batch fails with "invalid column name".
--
-- Idempotent: sys.columns guards, safe to re-run.
-- ============================================================================

SET NOCOUNT ON;
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;

DECLARE @GUARD_MESSAGE NVARCHAR(400) = N'本脚本只能在 EOS.ERP 数据库内执行，当前库为 ' + DB_NAME() + N'。';
IF DB_NAME() <> N'EOS.ERP'
    THROW 50000, @GUARD_MESSAGE, 1;

BEGIN TRANSACTION;

-- 1. Add the capability bit next to the existing SETUP_TAG.
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.SYSDD') AND name = 'MODULE_CONFIG_TAG')
    ALTER TABLE dbo.SYSDD ADD
        [MODULE_CONFIG_TAG] BIT NOT NULL CONSTRAINT [DF_SYSDD_MODULE_CONFIG] DEFAULT (0);

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.SYSDH') AND name = 'MODULE_CONFIG_TAG')
    ALTER TABLE dbo.SYSDH ADD
        [MODULE_CONFIG_TAG] BIT NOT NULL CONSTRAINT [DF_SYSDH_MODULE_CONFIG] DEFAULT (0);
GO

-- 2. Backfill module 2301 only: everyone who holds SETUP_TAG today keeps configuring.
UPDATE dbo.SYSDD SET MODULE_CONFIG_TAG = ISNULL(SETUP_TAG, 0) WHERE M_IDX = 2301;
UPDATE dbo.SYSDH SET MODULE_CONFIG_TAG = ISNULL(SETUP_TAG, 0) WHERE M_IDX = 2301;

-- 3. Extended properties (schema review aid).
IF NOT EXISTS (SELECT 1 FROM sys.extended_properties WHERE major_id = OBJECT_ID('dbo.SYSDD')
               AND minor_id = (SELECT column_id FROM sys.columns WHERE object_id = OBJECT_ID('dbo.SYSDD') AND name = 'MODULE_CONFIG_TAG'))
    EXEC sp_addextendedproperty @name = N'MS_Description',
        @value = N'模块配置权：可编辑该模块的行为动作/校验规则/自定义按钮并发布定义快照；与 SETUP_TAG（基础属性）各管一块',
        @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'SYSDD',
        @level2type = N'COLUMN', @level2name = N'MODULE_CONFIG_TAG';

IF NOT EXISTS (SELECT 1 FROM sys.extended_properties WHERE major_id = OBJECT_ID('dbo.SYSDH')
               AND minor_id = (SELECT column_id FROM sys.columns WHERE object_id = OBJECT_ID('dbo.SYSDH') AND name = 'MODULE_CONFIG_TAG'))
    EXEC sp_addextendedproperty @name = N'MS_Description',
        @value = N'模块配置权：可编辑该模块的行为动作/校验规则/自定义按钮并发布定义快照；与 SETUP_TAG（基础属性）各管一块',
        @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'SYSDH',
        @level2type = N'COLUMN', @level2name = N'MODULE_CONFIG_TAG';

-- 4. Self-checks: both columns exist, and every module-2301 holder of SETUP_TAG came
--    across — otherwise the new gate would lock out today's configuration owners.
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.SYSDD') AND name = 'MODULE_CONFIG_TAG')
    THROW 50000, N'断言失败：SYSDD.MODULE_CONFIG_TAG 未创建。', 1;
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.SYSDH') AND name = 'MODULE_CONFIG_TAG')
    THROW 50000, N'断言失败：SYSDH.MODULE_CONFIG_TAG 未创建。', 1;

IF EXISTS (SELECT 1 FROM dbo.SYSDD WHERE M_IDX = 2301 AND ISNULL(SETUP_TAG, 0) = 1 AND MODULE_CONFIG_TAG = 0)
    THROW 50000, N'断言失败：SYSDD 中模块 2301 的配置权未按 SETUP_TAG 回填。', 1;
IF EXISTS (SELECT 1 FROM dbo.SYSDH WHERE M_IDX = 2301 AND ISNULL(SETUP_TAG, 0) = 1 AND MODULE_CONFIG_TAG = 0)
    THROW 50000, N'断言失败：SYSDH 中模块 2301 的配置权未按 SETUP_TAG 回填。', 1;

IF EXISTS (SELECT 1 FROM dbo.SYSDD WHERE M_IDX <> 2301 AND MODULE_CONFIG_TAG = 1)
    THROW 50000, N'断言失败：配置权只应授予模块 2301，其它模块出现了置位行。', 1;

-- 5. Audit counts.
SELECT 'SYSDD_MODULE_CONFIG_COLUMN' AS OBJECT_NAME, COUNT(*) AS CNT FROM sys.columns
WHERE object_id = OBJECT_ID('dbo.SYSDD') AND name = 'MODULE_CONFIG_TAG';
SELECT 'SYSDH_MODULE_CONFIG_COLUMN' AS OBJECT_NAME, COUNT(*) AS CNT FROM sys.columns
WHERE object_id = OBJECT_ID('dbo.SYSDH') AND name = 'MODULE_CONFIG_TAG';
SELECT 'SYSDD_2301_GRANTED' AS OBJECT_NAME, COUNT(*) AS CNT FROM dbo.SYSDD WHERE M_IDX = 2301 AND MODULE_CONFIG_TAG = 1;
SELECT 'SYSDH_2301_GRANTED' AS OBJECT_NAME, COUNT(*) AS CNT FROM dbo.SYSDH WHERE M_IDX = 2301 AND MODULE_CONFIG_TAG = 1;

PRINT N'== SYSDD / SYSDH 已新增 MODULE_CONFIG_TAG，并已按 SETUP_TAG 回填模块 2301 ==';
GO
