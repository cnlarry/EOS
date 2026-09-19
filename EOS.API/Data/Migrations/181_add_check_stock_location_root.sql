-- ============================================================================
-- EOS.ERP migration 182: 盘点单头补盘点范围起点列
-- ----------------------------------------------------------------------------
-- 按库区盘点时，单头需要记录"盘的是哪个区"。否则重打印或复核时无从得知当时的
-- 盘点范围，明细行也无法判断是否漏盘。取值是库位主档里的库区位置号，可空
-- （不按区盘点时为 NULL）。
--
-- 不建外键：范围只用于生成明细与复核展示，位置可能先填后建。
--
-- 幂等：列已存在即跳过。
-- 回滚：DROP COLUMN。
-- ============================================================================

SET NOCOUNT ON;

SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET ANSI_PADDING ON;
SET ANSI_WARNINGS ON;
SET CONCAT_NULL_YIELDS_NULL ON;
SET ARITHABORT ON;

DECLARE @GUARD_MESSAGE NVARCHAR(400) = N'本脚本只能在 EOS.ERP 数据库内执行，当前库为 ' + DB_NAME() + N'。';
IF DB_NAME() <> N'EOS.ERP'
    THROW 51100, @GUARD_MESSAGE, 1;

IF OBJECT_ID(N'dbo.INV_CHECK_STOCK_M', N'U') IS NULL
    THROW 51101, N'dbo.INV_CHECK_STOCK_M 不存在，迁移中止。', 1;

IF COL_LENGTH(N'dbo.INV_CHECK_STOCK_M', N'LOCATION_ROOT_NO') IS NULL
BEGIN
    ALTER TABLE dbo.INV_CHECK_STOCK_M ADD LOCATION_ROOT_NO NVARCHAR(30) NULL;

    PRINT N'== 已加列 INV_CHECK_STOCK_M.LOCATION_ROOT_NO ==';
END
ELSE
BEGIN
    PRINT N'== INV_CHECK_STOCK_M.LOCATION_ROOT_NO 已存在（幂等跳过）==';
END

IF NOT EXISTS (
    SELECT 1 FROM sys.extended_properties ep
    WHERE ep.major_id = OBJECT_ID(N'dbo.INV_CHECK_STOCK_M')
      AND ep.minor_id = COLUMNPROPERTY(OBJECT_ID(N'dbo.INV_CHECK_STOCK_M'), N'LOCATION_ROOT_NO', 'ColumnId')
      AND ep.name = N'MS_Description')
BEGIN
    EXEC sp_addextendedproperty
        @name = N'MS_Description', @value = N'盘点范围起点（库区位置号）；空表示不按区盘点',
        @level0type = N'SCHEMA', @level0name = N'dbo',
        @level1type = N'TABLE',  @level1name = N'INV_CHECK_STOCK_M',
        @level2type = N'COLUMN', @level2name = N'LOCATION_ROOT_NO';
END

/* ---------- 收口断言 ---------- */
IF NOT EXISTS (
    SELECT 1 FROM sys.columns c
    WHERE c.object_id = OBJECT_ID(N'dbo.INV_CHECK_STOCK_M') AND c.name = N'LOCATION_ROOT_NO'
      AND TYPE_NAME(c.user_type_id) = N'nvarchar' AND c.max_length = 60 AND c.is_nullable = 1)
    THROW 51102, N'INV_CHECK_STOCK_M.LOCATION_ROOT_NO 未按 NVARCHAR(30) NULL 建立，迁移中止。', 1;

PRINT N'== 收口：INV_CHECK_STOCK_M.LOCATION_ROOT_NO = NVARCHAR(30) NULL ==';
