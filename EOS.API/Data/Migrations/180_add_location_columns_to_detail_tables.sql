-- ============================================================================
-- EOS.ERP migration 181: 单据明细表补位置列（24 张 / 30 列）
-- ----------------------------------------------------------------------------
-- 库存移动的效果链从明细行取值，明细必须能承载位置，过账才能写入四键余额。
-- 库别列与位置列一一对应：DEPOT_ID→LOCATION_NO、IN_DEPOT_ID→IN_LOCATION_NO、
-- OUT_DEPOT_ID→OUT_LOCATION_NO、BAD_DEPOT_ID→BAD_LOCATION_NO。
--
-- 列一律**可空**且无默认值：单据是草稿态，位置允许"先填后建"，且必须能区分
-- "用户没填"（NULL）与"显式选了未指定位置"（哨兵 N'-'）。哨兵只允许由过账路径
-- 在归一化时产生，选择器默认过滤哨兵行。
--
-- 明细表不建指向 DEPOT_LOCATION 的外键：草稿态位置可能先填后建，24 个外键的
-- 写入校验与删除守卫复杂度不划算；位置合法性由过账路径统一校验。
--
-- 附带：INV_CHECK_STOCK_D 补 BATCH_NO（盘点明细原先连批号列都没有，按批次盘出的
-- 差异无法传递，会在库别层面被抹平）。
--
-- 幂等：逐列判断存在性，已存在即跳过。
-- 回滚：逐列 DROP COLUMN。
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
    THROW 51000, @GUARD_MESSAGE, 1;

/* ---------- 映射表：明细表 → 库别列 → 位置列 ---------- */
DECLARE @map TABLE (TBL SYSNAME NOT NULL, DEPOT_COL SYSNAME NOT NULL, LOCATION_COL SYSNAME NOT NULL);

INSERT INTO @map (TBL, DEPOT_COL, LOCATION_COL) VALUES
    (N'INV_OCCUR_INIT_D',     N'DEPOT_ID',     N'LOCATION_NO'),
    (N'INV_OCCUR_IN_D',       N'DEPOT_ID',     N'LOCATION_NO'),
    (N'INV_OCCUR_OUT_D',      N'DEPOT_ID',     N'LOCATION_NO'),
    (N'INV_OCCUR_TRANSFER_D', N'DEPOT_ID',     N'LOCATION_NO'),
    (N'INV_OCCUR_TRANSFER_D', N'IN_DEPOT_ID',  N'IN_LOCATION_NO'),
    (N'INV_OCCUR_SCRAP_D',    N'DEPOT_ID',     N'LOCATION_NO'),
    (N'INV_OCCUR_SCRAP_D',    N'IN_DEPOT_ID',  N'IN_LOCATION_NO'),
    (N'INV_OCCUR_ADJUST_D',   N'DEPOT_ID',     N'LOCATION_NO'),
    (N'INV_LOAN_D',           N'DEPOT_ID',     N'LOCATION_NO'),
    (N'INV_LOAN_D',           N'IN_DEPOT_ID',  N'IN_LOCATION_NO'),
    (N'INV_RETURN_D',         N'DEPOT_ID',     N'LOCATION_NO'),
    (N'INV_RETURN_D',         N'OUT_DEPOT_ID', N'OUT_LOCATION_NO'),
    (N'INV_CHECK_STOCK_D',    N'DEPOT_ID',     N'LOCATION_NO'),
    (N'COP_SEND_D',           N'DEPOT_ID',     N'LOCATION_NO'),
    (N'COP_RETURN_D',         N'DEPOT_ID',     N'LOCATION_NO'),
    (N'COP_RETURN_D',         N'BAD_DEPOT_ID', N'BAD_LOCATION_NO'),
    (N'COP_FITOUT_D',         N'DEPOT_ID',     N'LOCATION_NO'),
    (N'COP_FITIN_D',          N'DEPOT_ID',     N'LOCATION_NO'),
    (N'COP_BACK_D',           N'DEPOT_ID',     N'LOCATION_NO'),
    (N'MOC_GET_D',            N'DEPOT_ID',     N'LOCATION_NO'),
    (N'MOC_BACK_D',           N'DEPOT_ID',     N'LOCATION_NO'),
    (N'MOC_PRODUCT_IN_D',     N'DEPOT_ID',     N'LOCATION_NO'),
    (N'MOC_PRODUCT_OUT_D',    N'DEPOT_ID',     N'LOCATION_NO'),
    (N'PUR_RECEIVE_D',        N'DEPOT_ID',     N'LOCATION_NO'),
    (N'PUR_CANCEL_D',         N'DEPOT_ID',     N'LOCATION_NO'),
    (N'MOC_OUT_PRODUCT_IN_D', N'DEPOT_ID',     N'LOCATION_NO'),
    (N'MOC_OUT_PRODUCT_OUT_D',N'DEPOT_ID',     N'LOCATION_NO'),
    (N'MOU_GET_D',            N'DEPOT_ID',     N'LOCATION_NO'),
    (N'MOU_GET_D',            N'IN_DEPOT_ID',  N'IN_LOCATION_NO'),
    (N'MOU_GET2_D',           N'DEPOT_ID',     N'LOCATION_NO');

/* ---------- 前置：映射里的表与库别列都必须真实存在（fail-closed） ---------- */
IF EXISTS (
    SELECT 1 FROM @map m
    WHERE OBJECT_ID(N'dbo.' + m.TBL, N'U') IS NULL
       OR COL_LENGTH(N'dbo.' + m.TBL, m.DEPOT_COL) IS NULL)
    THROW 51001, N'映射表中存在不存在的明细表或库别列，迁移中止。', 1;

/* ---------- ① 逐列补位置列（可空、无默认值） ---------- */
DECLARE @tbl SYSNAME, @locCol SYSNAME, @sql NVARCHAR(400);
DECLARE @added INT = 0;

DECLARE add_cursor CURSOR LOCAL FAST_FORWARD FOR
    SELECT TBL, LOCATION_COL FROM @map ORDER BY TBL, LOCATION_COL;
OPEN add_cursor;
FETCH NEXT FROM add_cursor INTO @tbl, @locCol;
WHILE @@FETCH_STATUS = 0
BEGIN
    IF COL_LENGTH(N'dbo.' + @tbl, @locCol) IS NULL
    BEGIN
        SET @sql = N'ALTER TABLE dbo.' + QUOTENAME(@tbl) + N' ADD ' + QUOTENAME(@locCol) + N' NVARCHAR(30) NULL;';
        EXEC sp_executesql @sql;
        SET @added = @added + 1;
    END
    FETCH NEXT FROM add_cursor INTO @tbl, @locCol;
END
CLOSE add_cursor;
DEALLOCATE add_cursor;

DECLARE @mapCount INT = (SELECT COUNT(*) FROM @map);
DECLARE @mapCountText NVARCHAR(10) = CONVERT(NVARCHAR(10), @mapCount);

PRINT N'== 已补位置列 ' + CONVERT(NVARCHAR(10), @added) + N' 列（映射共 ' + @mapCountText + N' 列）==';

/* ---------- ② 盘点明细补批号列 ---------- */
IF COL_LENGTH(N'dbo.INV_CHECK_STOCK_D', N'BATCH_NO') IS NULL
BEGIN
    ALTER TABLE dbo.INV_CHECK_STOCK_D ADD BATCH_NO NCHAR(30) NULL;
    PRINT N'== 已补列 INV_CHECK_STOCK_D.BATCH_NO ==';
END
ELSE
BEGIN
    PRINT N'== INV_CHECK_STOCK_D.BATCH_NO 已存在（幂等跳过）==';
END

/* ---------- 收口断言 ---------- */
IF EXISTS (
    SELECT 1 FROM @map m
    WHERE COL_LENGTH(N'dbo.' + m.TBL, m.LOCATION_COL) IS NULL)
    THROW 51002, N'仍有位置列未建立，迁移中止。', 1;

IF NOT EXISTS (
    SELECT 1 FROM sys.columns c
    WHERE c.object_id = OBJECT_ID(N'dbo.INV_CHECK_STOCK_D') AND c.name = N'BATCH_NO'
      AND TYPE_NAME(c.user_type_id) = N'nchar' AND c.max_length = 60)
    THROW 51003, N'INV_CHECK_STOCK_D.BATCH_NO 未按 NCHAR(30) 建立，迁移中止。', 1;

/* 位置列一律可空，且不带默认值 */
IF EXISTS (
    SELECT 1 FROM @map m
    JOIN sys.columns c ON c.object_id = OBJECT_ID(N'dbo.' + m.TBL) AND c.name = m.LOCATION_COL
    WHERE c.is_nullable = 0 OR c.default_object_id <> 0)
    THROW 51004, N'位置列必须可空且无默认值，迁移中止。', 1;

DECLARE @detailTables INT = (SELECT COUNT(DISTINCT TBL) FROM @map);
DECLARE @locationCols INT = (SELECT COUNT(*) FROM @map);
DECLARE @locationColsText NVARCHAR(10) = CONVERT(NVARCHAR(10), @locationCols);
DECLARE @detailTablesText NVARCHAR(10) = CONVERT(NVARCHAR(10), @detailTables);

PRINT N'== 收口：' + @detailTablesText + N' 张明细表 / ' + @locationColsText + N' 个位置列全部就位（可空、无默认值）==';
