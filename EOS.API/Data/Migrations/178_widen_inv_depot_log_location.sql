-- ============================================================================
-- EOS.ERP migration 179: 库存流水加位置列（进主键）与位置路径快照
-- ----------------------------------------------------------------------------
-- LOCATION_NO 进主键：流水与余额共用同一组维度，出库才能定位到具体位置。
-- 主键由七键扩为八键：品号 / 日期 / 方向 / 单别 / 单号 / 序号 / 库别 / 位置。
--
-- LOCATION_PATH 是**冗余快照**，记录写入当时的位置物化路径。位置可以被移动
-- （改 PARENT_NO），也可以被托盘号复用，因此"当时货在哪"不能靠当前路径回溯，
-- 必须落在流水上。按库区取历史归属时读这一列，不读位置主档的当前值。
--
-- 行列式说明：LOCATION_NO 为 NOT NULL + DEFAULT N'-'（哨兵 = 未指定位置）。
-- 主键列不允许 NULL，且唯一性比较把 NULL 视为相等，故哨兵必须是真实值。
--
-- 本表不建指向 DEPOT_LOCATION 的外键：流水是既成事实，历史库别（如已从主档
-- 移除的库别）仍需保留其流水；位置合法性由余额表的外键与过账路径校验兜底。
--
-- 反向流水沿用"解批记 SYSDATETIME()"的既有约定以避开主键重复，本次不改变。
--
-- 执行方式：SQL Server 按批编译，新增列在同一批内对后续语句不可见，故以 GO 分段。
-- 幂等：列已存在时只做断言，不重复加列与重建主键。
-- 回滚：还原七键主键 + 删列 + 删索引。
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
    THROW 50800, @GUARD_MESSAGE, 1;

IF OBJECT_ID(N'dbo.INV_DEPOT_LOG', N'U') IS NULL
    THROW 50801, N'dbo.INV_DEPOT_LOG 不存在，迁移中止。', 1;

/* ---------- ① 加列 ---------- */
IF COL_LENGTH(N'dbo.INV_DEPOT_LOG', N'LOCATION_NO') IS NULL
BEGIN
    ALTER TABLE dbo.INV_DEPOT_LOG ADD
        LOCATION_NO NVARCHAR (30) NOT NULL
            CONSTRAINT DF_INV_DEPOT_LOG_LOCATION_NO DEFAULT (N'-');

    PRINT N'== 已加列 INV_DEPOT_LOG.LOCATION_NO ==';
END

IF COL_LENGTH(N'dbo.INV_DEPOT_LOG', N'LOCATION_PATH') IS NULL
BEGIN
    ALTER TABLE dbo.INV_DEPOT_LOG ADD
        LOCATION_PATH NVARCHAR (300) NULL;

    PRINT N'== 已加列 INV_DEPOT_LOG.LOCATION_PATH（快照，可空）==';
END
GO

/* ---------- ② 主键扩为八键 ---------- */
DECLARE @pkColsNow INT = (
    SELECT COUNT(*) FROM sys.index_columns ic
    JOIN sys.indexes i ON i.object_id = ic.object_id AND i.index_id = ic.index_id
    WHERE i.object_id = OBJECT_ID(N'dbo.INV_DEPOT_LOG') AND i.is_primary_key = 1 AND ic.is_included_column = 0);

IF @pkColsNow <> 8
BEGIN
    ALTER TABLE dbo.INV_DEPOT_LOG DROP CONSTRAINT PK_INVSD;

    ALTER TABLE dbo.INV_DEPOT_LOG ADD CONSTRAINT PK_INVSD
        PRIMARY KEY CLUSTERED (PRO_NO ASC, MUTUALITY_DATE ASC, IN_OUT ASC, MUTUALITY_TYPE ASC,
                               MUTUALITY_NO ASC, MUTUALITY_SERIAL_NO ASC, DEPOT_ID ASC, LOCATION_NO ASC);

    PRINT N'== INV_DEPOT_LOG 主键已扩为八键（末列为 LOCATION_NO）==';
END
ELSE
BEGIN
    PRINT N'== INV_DEPOT_LOG 主键已是八键（幂等跳过）==';
END
GO

/* ---------- ③ 按库别 / 位置的流水查询不是最左前缀，另加索引 ---------- */
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.INV_DEPOT_LOG') AND name = N'IX_INV_DEPOT_LOG_DEPOT_LOCATION')
BEGIN
    CREATE NONCLUSTERED INDEX IX_INV_DEPOT_LOG_DEPOT_LOCATION
        ON dbo.INV_DEPOT_LOG (DEPOT_ID ASC, LOCATION_NO ASC, MUTUALITY_DATE ASC);

    PRINT N'== 已建立 IX_INV_DEPOT_LOG_DEPOT_LOCATION ==';
END
GO

/* ---------- 收口断言 ---------- */
DECLARE @pkCols INT = (
    SELECT COUNT(*) FROM sys.index_columns ic
    JOIN sys.indexes i ON i.object_id = ic.object_id AND i.index_id = ic.index_id
    WHERE i.object_id = OBJECT_ID(N'dbo.INV_DEPOT_LOG') AND i.is_primary_key = 1 AND ic.is_included_column = 0);

IF @pkCols <> 8
    THROW 50802, N'INV_DEPOT_LOG 主键列数不是 8，迁移中止。', 1;

DECLARE @pkOrder NVARCHAR(400) = (
    SELECT STUFF((SELECT N',' + c.name FROM sys.index_columns ic
                  JOIN sys.indexes i ON i.object_id = ic.object_id AND i.index_id = ic.index_id
                  JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
                  WHERE i.object_id = OBJECT_ID(N'dbo.INV_DEPOT_LOG') AND i.is_primary_key = 1 AND ic.is_included_column = 0
                  ORDER BY ic.key_ordinal FOR XML PATH(''), TYPE).value('.', N'NVARCHAR(400)'), 1, 1, N''));

IF @pkOrder <> N'PRO_NO,MUTUALITY_DATE,IN_OUT,MUTUALITY_TYPE,MUTUALITY_NO,MUTUALITY_SERIAL_NO,DEPOT_ID,LOCATION_NO'
    THROW 50803, N'INV_DEPOT_LOG 主键列顺序不符合预期，迁移中止。', 1;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.INV_DEPOT_LOG') AND name = N'IX_INV_DEPOT_LOG_DEPOT_LOCATION')
    THROW 50804, N'IX_INV_DEPOT_LOG_DEPOT_LOCATION 未建立，迁移中止。', 1;

IF EXISTS (SELECT 1 FROM dbo.INV_DEPOT_LOG WHERE LOCATION_NO IS NULL)
    THROW 50805, N'INV_DEPOT_LOG.LOCATION_NO 存在 NULL，迁移中止。', 1;

DECLARE @rowsAfter INT = (SELECT COUNT(*) FROM dbo.INV_DEPOT_LOG);

PRINT N'== 收口：INV_DEPOT_LOG 主键 ' + @pkOrder + N'；行数 ' + CONVERT(NVARCHAR(20), @rowsAfter) + N' ==';
