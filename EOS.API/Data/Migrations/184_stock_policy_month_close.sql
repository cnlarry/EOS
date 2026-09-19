-- ============================================================================
-- EOS.ERP migration 185: 策略表补月结维度参数（仅部署级可配）
-- ----------------------------------------------------------------------------
-- 月结统计要支持"按批次细分 / 按库位细分"两种粒度。**粒度必须全局一致**：若 A 仓按
-- 批次结、B 仓不按批次结，同一个月的月结数据粒度就不一致，跨仓汇总要么重复计数、
-- 要么漏计，且事后无法补救（月结是历史快照）。因此这两个参数是本策略表中**唯一不接受
-- 库别覆盖**的维度，只在部署级行（DEPOT_ID = N'*'）生效。
--
-- 落到同一张表而不是另建表：它们与位置/批次档位同属"库存管理深度"的决策，且作用域键
-- 完全相同；求值入口在读取时对这两列**一律取部署级行的值**，因此即使有人直连改库把
-- 库别行也改了，运行时口径也不会分裂。
--
-- 默认值取"批次细分开、库位细分关"：批次是分析价值高的维度（批次成本、批次周转），
-- 库位是作业维度（分析价值低、行数放大明显）。
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
    THROW 51400, @GUARD_MESSAGE, 1;

IF OBJECT_ID(N'dbo.DEPOT_STOCK_POLICY', N'U') IS NULL
    THROW 51401, N'dbo.DEPOT_STOCK_POLICY 不存在，请先执行建表迁移，迁移中止。', 1;

IF COL_LENGTH(N'dbo.DEPOT_STOCK_POLICY', N'MONTH_CLOSE_BY_BATCH') IS NULL
BEGIN
    ALTER TABLE dbo.DEPOT_STOCK_POLICY ADD
        MONTH_CLOSE_BY_BATCH BIT NOT NULL CONSTRAINT DF_DSP_MONTH_CLOSE_BY_BATCH DEFAULT (1);

    PRINT N'== 已加列 MONTH_CLOSE_BY_BATCH ==';
END

IF COL_LENGTH(N'dbo.DEPOT_STOCK_POLICY', N'MONTH_CLOSE_BY_LOCATION') IS NULL
BEGIN
    ALTER TABLE dbo.DEPOT_STOCK_POLICY ADD
        MONTH_CLOSE_BY_LOCATION BIT NOT NULL CONSTRAINT DF_DSP_MONTH_CLOSE_BY_LOCATION DEFAULT (0);

    PRINT N'== 已加列 MONTH_CLOSE_BY_LOCATION ==';
END
GO

/* ---------- 收敛：库别行不得携带与部署级不同的月结粒度 ---------- */
UPDATE p
   SET p.MONTH_CLOSE_BY_BATCH = d.MONTH_CLOSE_BY_BATCH,
       p.MONTH_CLOSE_BY_LOCATION = d.MONTH_CLOSE_BY_LOCATION
  FROM dbo.DEPOT_STOCK_POLICY p
  CROSS JOIN (SELECT MONTH_CLOSE_BY_BATCH, MONTH_CLOSE_BY_LOCATION
                FROM dbo.DEPOT_STOCK_POLICY WHERE DEPOT_ID = N'*') d
 WHERE p.DEPOT_ID <> N'*'
   AND (p.MONTH_CLOSE_BY_BATCH <> d.MONTH_CLOSE_BY_BATCH
     OR p.MONTH_CLOSE_BY_LOCATION <> d.MONTH_CLOSE_BY_LOCATION);

PRINT N'== 已把库别行的月结维度收敛为部署级取值（不应存在库别覆盖）==';

/* ---------- 列说明 ---------- */
DECLARE @descriptions TABLE (COLUMN_NAME SYSNAME, DESCRIPTION NVARCHAR(400));
INSERT INTO @descriptions (COLUMN_NAME, DESCRIPTION) VALUES
    (N'MONTH_CLOSE_BY_BATCH',    N'月结是否按批次细分（仅部署级生效，库别不可覆盖）'),
    (N'MONTH_CLOSE_BY_LOCATION', N'月结是否按库位细分（仅部署级生效，库别不可覆盖）');

DECLARE @columnName SYSNAME, @description NVARCHAR(400);
DECLARE description_cursor CURSOR LOCAL FAST_FORWARD FOR SELECT COLUMN_NAME, DESCRIPTION FROM @descriptions;
OPEN description_cursor;
FETCH NEXT FROM description_cursor INTO @columnName, @description;
WHILE @@FETCH_STATUS = 0
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM sys.extended_properties ep
        WHERE ep.major_id = OBJECT_ID(N'dbo.DEPOT_STOCK_POLICY')
          AND ep.minor_id = COLUMNPROPERTY(OBJECT_ID(N'dbo.DEPOT_STOCK_POLICY'), @columnName, 'ColumnId')
          AND ep.name = N'MS_Description')
    BEGIN
        EXEC sp_addextendedproperty
            @name = N'MS_Description', @value = @description,
            @level0type = N'SCHEMA', @level0name = N'dbo',
            @level1type = N'TABLE',  @level1name = N'DEPOT_STOCK_POLICY',
            @level2type = N'COLUMN', @level2name = @columnName;
    END
    FETCH NEXT FROM description_cursor INTO @columnName, @description;
END
CLOSE description_cursor;
DEALLOCATE description_cursor;

/* ---------- 收口断言 ---------- */
IF COL_LENGTH(N'dbo.DEPOT_STOCK_POLICY', N'MONTH_CLOSE_BY_BATCH') IS NULL
   OR COL_LENGTH(N'dbo.DEPOT_STOCK_POLICY', N'MONTH_CLOSE_BY_LOCATION') IS NULL
    THROW 51402, N'月结维度参数列未建立，迁移中止。', 1;

IF EXISTS (
    SELECT 1 FROM dbo.DEPOT_STOCK_POLICY p
    CROSS JOIN (SELECT MONTH_CLOSE_BY_BATCH, MONTH_CLOSE_BY_LOCATION
                  FROM dbo.DEPOT_STOCK_POLICY WHERE DEPOT_ID = N'*') d
    WHERE p.DEPOT_ID <> N'*'
      AND (p.MONTH_CLOSE_BY_BATCH <> d.MONTH_CLOSE_BY_BATCH
        OR p.MONTH_CLOSE_BY_LOCATION <> d.MONTH_CLOSE_BY_LOCATION))
    THROW 51403, N'仍存在与部署级不一致的库别月结维度，迁移中止。', 1;

IF NOT EXISTS (SELECT 1 FROM dbo.DEPOT_STOCK_POLICY WHERE DEPOT_ID = N'*')
    THROW 51404, N'部署级默认行缺失，迁移中止。', 1;

PRINT N'== 收口：月结维度参数已就位（部署级 BATCH=1 / LOCATION=0），库别行与部署级一致 ==';
