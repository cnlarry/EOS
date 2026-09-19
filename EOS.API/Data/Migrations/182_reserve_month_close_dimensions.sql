-- ============================================================================
-- EOS.ERP migration 183: 月结明细预留位置 / 批次列
-- ----------------------------------------------------------------------------
-- 月结统计要支持"按批次细分 / 按库位细分"两种粒度，由部署级参数决定；本迁移只
-- 预留承载列，不改变任何月结逻辑（月结功能本身尚未实现）。
--
-- 两列可空：未启用对应维度时保持 NULL。**暂不进主键**——主键是否包含这两维，
-- 取决于月结立项时确定的粒度口径；若届时进主键，必须改为 NOT NULL + 哨兵值
-- （主键列不接受 NULL，且唯一性比较把 NULL 视为相等）。
--
-- 幂等：列已存在即跳过。
-- 回滚：DROP COLUMN（此时表内尚无依赖）。
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
    THROW 51200, @GUARD_MESSAGE, 1;

IF OBJECT_ID(N'dbo.INV_PRO_MONTH_D', N'U') IS NULL
    THROW 51201, N'dbo.INV_PRO_MONTH_D 不存在，迁移中止。', 1;

DECLARE @rowsBefore INT = (SELECT COUNT(*) FROM dbo.INV_PRO_MONTH_D);

IF COL_LENGTH(N'dbo.INV_PRO_MONTH_D', N'BATCH_NO') IS NULL
BEGIN
    ALTER TABLE dbo.INV_PRO_MONTH_D ADD BATCH_NO NCHAR(30) NULL;
    PRINT N'== 已加列 INV_PRO_MONTH_D.BATCH_NO ==';
END

IF COL_LENGTH(N'dbo.INV_PRO_MONTH_D', N'LOCATION_NO') IS NULL
BEGIN
    ALTER TABLE dbo.INV_PRO_MONTH_D ADD LOCATION_NO NVARCHAR(30) NULL;
    PRINT N'== 已加列 INV_PRO_MONTH_D.LOCATION_NO ==';
END

/* ---------- 列说明 ---------- */
DECLARE @descriptions TABLE (COLUMN_NAME SYSNAME, DESCRIPTION NVARCHAR(400));
INSERT INTO @descriptions (COLUMN_NAME, DESCRIPTION) VALUES
    (N'BATCH_NO',    N'月结统计的批号维度（未启用按批次细分时为空）'),
    (N'LOCATION_NO', N'月结统计的库位维度（未启用按库位细分时为空）');

DECLARE @columnName SYSNAME, @description NVARCHAR(400);
DECLARE description_cursor CURSOR LOCAL FAST_FORWARD FOR SELECT COLUMN_NAME, DESCRIPTION FROM @descriptions;
OPEN description_cursor;
FETCH NEXT FROM description_cursor INTO @columnName, @description;
WHILE @@FETCH_STATUS = 0
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM sys.extended_properties ep
        WHERE ep.major_id = OBJECT_ID(N'dbo.INV_PRO_MONTH_D')
          AND ep.minor_id = COLUMNPROPERTY(OBJECT_ID(N'dbo.INV_PRO_MONTH_D'), @columnName, 'ColumnId')
          AND ep.name = N'MS_Description')
    BEGIN
        EXEC sp_addextendedproperty
            @name = N'MS_Description', @value = @description,
            @level0type = N'SCHEMA', @level0name = N'dbo',
            @level1type = N'TABLE',  @level1name = N'INV_PRO_MONTH_D',
            @level2type = N'COLUMN', @level2name = @columnName;
    END
    FETCH NEXT FROM description_cursor INTO @columnName, @description;
END
CLOSE description_cursor;
DEALLOCATE description_cursor;

/* ---------- 收口断言 ---------- */
IF COL_LENGTH(N'dbo.INV_PRO_MONTH_D', N'BATCH_NO') IS NULL
    THROW 51202, N'INV_PRO_MONTH_D.BATCH_NO 未建立，迁移中止。', 1;

IF COL_LENGTH(N'dbo.INV_PRO_MONTH_D', N'LOCATION_NO') IS NULL
    THROW 51203, N'INV_PRO_MONTH_D.LOCATION_NO 未建立，迁移中止。', 1;

DECLARE @rowsAfter INT = (SELECT COUNT(*) FROM dbo.INV_PRO_MONTH_D);
DECLARE @rowsBeforeText NVARCHAR(20) = CONVERT(NVARCHAR(20), @rowsBefore);

PRINT N'== 收口：INV_PRO_MONTH_D 已预留 BATCH_NO / LOCATION_NO；行数 ' + CONVERT(NVARCHAR(20), @rowsAfter) + N'（迁移前 ' + @rowsBeforeText + N'）==';
