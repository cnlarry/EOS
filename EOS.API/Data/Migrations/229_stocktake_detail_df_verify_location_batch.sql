-- ============================================================================
-- EOS.ERP migration 230: 盘点单明细的重复校验键扩到四键（补库位与批次）
-- ----------------------------------------------------------------------------
-- 背景：`TABLES.DF_VERIFY` 是"同一单据内明细不得重复"的判据（保存时按这些列的取值分组，
-- 出现重复即 `DF_VERIFY_DUPLICATE`）。盘点单明细原先只按 `DEPOT_ID;PRO_NO` 判重——
-- 那是四键化之前的口径：库位管理启用后，同一料号在同一库别分布在多个库位 / 批次是**正常**的
-- （按库区生成明细的 `stocktake-scope-generate` 恰恰会写出这样的多行），两键判重会把正常数据
-- 判成重复，盘点单直接存不下去。其余库存明细表的该键均为空，只有盘点单是这一处例外。
--
-- 幂等：仅在该键仍为旧值时改写。
-- 回滚：改回 `DEPOT_ID;PRO_NO`。
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
    THROW 51900, @GUARD_MESSAGE, 1;

DECLARE @table SYSNAME = N'INV_CHECK_STOCK_D';
DECLARE @newKey NVARCHAR(200) = N'DEPOT_ID;PRO_NO;LOCATION_NO;BATCH_NO';

IF NOT EXISTS (SELECT 1 FROM dbo.TABLES WHERE T_ID = @table)
    THROW 51901, N'盘点单明细表不存在，迁移中止。', 1;

/* 判重键引用的列必须都真实存在且已登记字段元数据（否则校验形同虚设） */
DECLARE @missing INT = (
    SELECT COUNT(*) FROM (VALUES (N'DEPOT_ID'), (N'PRO_NO'), (N'LOCATION_NO'), (N'BATCH_NO')) v(F_ID)
    WHERE NOT EXISTS (
        SELECT 1 FROM dbo.FIELDS f WHERE f.T_ID = @table AND LTRIM(RTRIM(f.F_ID)) = v.F_ID));

IF @missing > 0
    THROW 51902, N'判重键引用了未登记字段元数据的列，迁移中止。', 1;

UPDATE dbo.TABLES
SET DF_VERIFY = @newKey
WHERE T_ID = @table
  AND LTRIM(RTRIM(ISNULL(DF_VERIFY, N''))) IN (N'DEPOT_ID;PRO_NO', N'', N'DEPOT_ID;PRO_NO;LOCATION_NO');

PRINT N'== 盘点单明细判重键已改写 ' + CONVERT(NVARCHAR(10), @@ROWCOUNT) + N' 行 ==';

/* ---------- 收口断言 ---------- */
IF NOT EXISTS (
    SELECT 1 FROM dbo.TABLES
    WHERE T_ID = @table AND LTRIM(RTRIM(ISNULL(DF_VERIFY, N''))) = @newKey)
    THROW 51903, N'盘点单明细判重键未改写成功，迁移中止。', 1;

IF EXISTS (
    SELECT 1 FROM dbo.TABLES
    WHERE T_ID = @table
      AND (DF_VERIFY NOT LIKE N'%LOCATION_NO%' OR DF_VERIFY NOT LIKE N'%BATCH_NO%'))
    THROW 51904, N'盘点单明细判重键仍缺少库位或批次，迁移中止。', 1;

PRINT N'== 收口：盘点单明细按 库别/料号/库位/批次 四键判重 ==';
