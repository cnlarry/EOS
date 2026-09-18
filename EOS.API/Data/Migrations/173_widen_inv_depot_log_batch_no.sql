-- ============================================================================
-- EOS.ERP migration 174: 统一库存流水批号列长度（INV_DEPOT_LOG.BATCH_NO）
-- ----------------------------------------------------------------------------
-- 现状：INV_DEPOT_LOG.BATCH_NO 为 NCHAR(10)，而 INV_BATCH_M / INV_BATCH_D 以及各
-- 出入库明细表的批号列均为 NCHAR(30)。批号超过 10 个字符时写入流水会被静默截断，
-- 使流水与批次账对不上。本迁移把该列加宽到 NCHAR(30)，与其余表同型。
--
-- 加宽的前置事实（越界行必须为 0，否则说明历史写入已经丢字，须先处置）：
--     SELECT COUNT(*) FROM dbo.INV_DEPOT_LOG WHERE LEN(RTRIM(BATCH_NO)) > 10;
--
-- 非聚集索引 IDX_INVSD_1 (PRO_NO, BATCH_NO) 的键包含该列，SQL Server 不允许直接
-- ALTER COLUMN（Msg 4922）。因此本迁移先删该索引、加宽列、再按原定义重建，并在
-- 前后各取一次索引形态签名比对，确保没有被顺手改动。
--
-- 列可空性保持不变（该列历史上可空，加宽不改变语义）。
-- 幂等：列已是 NCHAR(30) 时只做断言，不重复 DROP / ALTER / CREATE。
-- 回滚：重建 NCHAR(10) 列并重建索引，仅在越界行数仍为 0 时安全。
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
    THROW 50300, @GUARD_MESSAGE, 1;

DECLARE @IndexName SYSNAME = N'IDX_INVSD_1';

/* ---------- 前置（一）：目标列存在 ---------- */
IF COL_LENGTH('dbo.INV_DEPOT_LOG', 'BATCH_NO') IS NULL
    THROW 50301, N'INV_DEPOT_LOG.BATCH_NO 不存在，迁移中止（表结构已变更？）。', 1;

/* ---------- 前置（二）：不存在会被截断的既有数据 ---------- */
IF EXISTS (SELECT 1 FROM dbo.INV_DEPOT_LOG WHERE LEN(RTRIM(BATCH_NO)) > 10)
    THROW 50302, N'存在批号长度超过 10 的流水行，加宽前须先处置被截断的数据，迁移中止。', 1;

/* ---------- 索引键形态签名（键列序 / 唯一性 / 筛选 / 填充因子） ---------- */
DECLARE @signatureBefore NVARCHAR(400) = (
    SELECT STUFF((
        SELECT N';' + i.name
             + N'[u' + CONVERT(NVARCHAR(1), i.is_unique)
             + N'/f' + CONVERT(NVARCHAR(3), ISNULL(i.fill_factor, -1))
             + N'/' + ISNULL(i.filter_definition, N'-') + N']='
             + STUFF((SELECT N',' + c.name + N':' + CONVERT(NVARCHAR(3), ic.key_ordinal)
                      FROM sys.index_columns ic
                      JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
                      WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.is_included_column = 0
                      ORDER BY ic.key_ordinal
                      FOR XML PATH(''), TYPE).value('.', N'NVARCHAR(300)'), 1, 1, N'')
        FROM sys.indexes i
        WHERE i.object_id = OBJECT_ID('dbo.INV_DEPOT_LOG') AND i.is_primary_key = 0 AND i.type = 2
        ORDER BY i.name
        FOR XML PATH(''), TYPE).value('.', N'NVARCHAR(400)'), 1, 1, N''));

DECLARE @currentLength INT = (
    SELECT c.max_length FROM sys.columns c
    WHERE c.object_id = OBJECT_ID('dbo.INV_DEPOT_LOG') AND c.name = N'BATCH_NO');

/* NCHAR(30) 的 max_length 为 60 字节 */
IF @currentLength = 60
BEGIN
    PRINT N'== INV_DEPOT_LOG.BATCH_NO 已是 NCHAR(30)（幂等跳过 DROP / ALTER / CREATE）==';
END
ELSE
BEGIN
    /* ---------- ① 删索引（键含 BATCH_NO，否则 ALTER 会被拒） ---------- */
    IF EXISTS (SELECT 1 FROM sys.indexes
               WHERE object_id = OBJECT_ID('dbo.INV_DEPOT_LOG') AND name = @IndexName)
    BEGIN
        DECLARE @dropSql NVARCHAR(300) = N'DROP INDEX ' + QUOTENAME(@IndexName) + N' ON dbo.INV_DEPOT_LOG;';
        EXEC sp_executesql @dropSql;
    END

    /* ---------- ② 加宽列（保持可空） ---------- */
    ALTER TABLE dbo.INV_DEPOT_LOG ALTER COLUMN BATCH_NO NCHAR(30) NULL;

    /* ---------- ③ 按原定义重建索引 ---------- */
    IF NOT EXISTS (SELECT 1 FROM sys.indexes
                   WHERE object_id = OBJECT_ID('dbo.INV_DEPOT_LOG') AND name = @IndexName)
    BEGIN
        CREATE NONCLUSTERED INDEX IDX_INVSD_1
            ON dbo.INV_DEPOT_LOG (PRO_NO ASC, BATCH_NO ASC)
            WITH (FILLFACTOR = 90);
    END

    PRINT N'== INV_DEPOT_LOG.BATCH_NO 已由 NCHAR(10) 加宽到 NCHAR(30)，索引 IDX_INVSD_1 已重建 ==';
END

/* ---------- 收口断言 ---------- */
IF NOT EXISTS (
    SELECT 1 FROM sys.columns c
    WHERE c.object_id = OBJECT_ID('dbo.INV_DEPOT_LOG')
      AND c.name = N'BATCH_NO'
      AND TYPE_NAME(c.user_type_id) = N'nchar'
      AND c.max_length = 60)
    THROW 50304, N'INV_DEPOT_LOG.BATCH_NO 未达到 NCHAR(30)，迁移中止。', 1;

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE object_id = OBJECT_ID('dbo.INV_DEPOT_LOG') AND name = @IndexName AND type = 2 AND is_primary_key = 0)
    THROW 50305, N'索引 IDX_INVSD_1 未重建，迁移中止。', 1;

DECLARE @signatureAfter NVARCHAR(400) = (
    SELECT STUFF((
        SELECT N';' + i.name
             + N'[u' + CONVERT(NVARCHAR(1), i.is_unique)
             + N'/f' + CONVERT(NVARCHAR(3), ISNULL(i.fill_factor, -1))
             + N'/' + ISNULL(i.filter_definition, N'-') + N']='
             + STUFF((SELECT N',' + c.name + N':' + CONVERT(NVARCHAR(3), ic.key_ordinal)
                      FROM sys.index_columns ic
                      JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
                      WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.is_included_column = 0
                      ORDER BY ic.key_ordinal
                      FOR XML PATH(''), TYPE).value('.', N'NVARCHAR(300)'), 1, 1, N'')
        FROM sys.indexes i
        WHERE i.object_id = OBJECT_ID('dbo.INV_DEPOT_LOG') AND i.is_primary_key = 0 AND i.type = 2
        ORDER BY i.name
        FOR XML PATH(''), TYPE).value('.', N'NVARCHAR(400)'), 1, 1, N''));

IF ISNULL(@signatureAfter, N'') <> ISNULL(@signatureBefore, N'')
    THROW 50306, N'非聚集索引形态在本次迁移前后不一致，迁移中止（请复核索引定义）。', 1;

IF EXISTS (SELECT 1 FROM dbo.INV_DEPOT_LOG WHERE LEN(RTRIM(BATCH_NO)) > 30)
    THROW 50307, N'存在批号长度超过 30 的流水行，迁移中止。', 1;

PRINT N'== 收口：INV_DEPOT_LOG.BATCH_NO = NCHAR(30)；非聚集索引形态 ' + ISNULL(@signatureAfter, N'(空)') + N' ==';
