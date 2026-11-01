-- ============================================================================
-- EOS.ERP migration 469：型号身份并进"用途" —— 同一型号可同时用于对话与判官
-- ----------------------------------------------------------------------------
--  背景（ADR-044 §8）：判官的用途是**把一个模型钉住**，使"换对话模型 ≠ 换判官"。
--  而 `UQ_ASSISTANT_MODEL_CODE` 只认 (PROVIDER_ID, MODEL_CODE)，同一型号在库里只能有一行，
--  于是"同一个型号既当对话、又当判官"根本存不进去——那恰恰是判官最主要的用法。
--
--  这次把用途并进唯一键：(供应商, 型号, 用途)。每用途至多一条当前模型仍由
--  `UX_ASSISTANT_MODEL_ACTIVE_KIND`（KIND WHERE IS_ACTIVE = 1）保证，两条规则互不重叠。
--  存量数据天然满足新约束（旧约束更严），因此不需要回填。
-- ============================================================================
SET NOCOUNT ON;
SET XACT_ABORT ON;
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;

IF DB_NAME() <> N'EOS.ERP'
    THROW 51980, N'本脚本只允许在 EOS.ERP 数据库上执行。', 1;

IF OBJECT_ID(N'dbo.ASSISTANT_MODEL', N'U') IS NULL
    THROW 51981, N'ASSISTANT_MODEL 不存在，迁移终止。', 1;

/* ---------- 唯一键：(供应商, 型号) → (供应商, 型号, 用途)（幂等） ---------- */
DECLARE @indexId int =
    (SELECT unique_index_id FROM sys.key_constraints
     WHERE parent_object_id = OBJECT_ID(N'dbo.ASSISTANT_MODEL')
       AND name = N'UQ_ASSISTANT_MODEL_CODE');

IF @indexId IS NULL
BEGIN
    ALTER TABLE dbo.ASSISTANT_MODEL
        ADD CONSTRAINT UQ_ASSISTANT_MODEL_CODE UNIQUE (PROVIDER_ID, MODEL_CODE, KIND);
    PRINT N'== UQ_ASSISTANT_MODEL_CODE 已创建（含 KIND） ==';
END
ELSE IF EXISTS (
    SELECT 1
    FROM sys.index_columns ic
    INNER JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
    WHERE ic.object_id = OBJECT_ID(N'dbo.ASSISTANT_MODEL')
      AND ic.index_id = @indexId
      AND c.name = N'KIND')
BEGIN
    PRINT N'== UQ_ASSISTANT_MODEL_CODE 已含 KIND，跳过 ==';
END
ELSE
BEGIN
    ALTER TABLE dbo.ASSISTANT_MODEL DROP CONSTRAINT UQ_ASSISTANT_MODEL_CODE;
    ALTER TABLE dbo.ASSISTANT_MODEL
        ADD CONSTRAINT UQ_ASSISTANT_MODEL_CODE UNIQUE (PROVIDER_ID, MODEL_CODE, KIND);
    PRINT N'== UQ_ASSISTANT_MODEL_CODE 已重建（新增 KIND） ==';
END
