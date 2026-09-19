-- ============================================================================
-- EOS.ERP migration 178: 库存余额表扩为四键（料号 × 库别 × 位置 × 批次）
-- ----------------------------------------------------------------------------
-- 余额键由 (PRO_NO, DEPOT_ID) 扩为 (PRO_NO, DEPOT_ID, LOCATION_NO, BATCH_NO)：
--   · LOCATION_NO 用哨兵 N'-' 表示"未指定位置"，每库别预置一条哨兵位置行；
--   · BATCH_NO 用空串表示"非批号管理料件"（与既有的批号归零约定一致）。
-- 两列均 NOT NULL + DEFAULT：唯一索引 / 主键把 NULL 当作相等值，多行 NULL 会
-- 直接判为重复键冲突，因此哨兵值必须是真实值而不是 NULL。
--
-- 存量数据全部落在哨兵组合上，因此**数量层面与扩键前完全等价**：
--   每个 (PRO_NO, DEPOT_ID) 的 SUM(QTY) 不变、表行数不变。
--
-- 前置数据处置：余额表中存在 DEPOT_ID = N'YL' 的 68 行，而该库别已不在 DEPOT
-- 主档中（其余额为全零，历史流水仍完整保留在 INV_DEPOT_LOG）。余额表要建指向
-- DEPOT_LOCATION 的外键，而 DEPOT_LOCATION 又指向 DEPOT，故这些行无法满足
-- 外键约束 —— 本迁移先把它们**整表归档**到 INV_PRO_DEPOT_ORPHAN_DEPOT_BACKUP
-- 再删除；需要时可由该备份表原样还原。
--
-- 尾部空格：LOCATION_NO 是变长列，尾随空格会被保留且参与 LIKE / 拼接比较，
-- 故加 CHECK 拒绝带尾随空格的位置号。BATCH_NO 是定长 NCHAR，写入时由存储引擎
-- 统一填充到定长，空串与纯空格串在存储层本就是同一个值，因此**无法**用 CHECK
-- 区分，也不存在"写入 '  ' 会新建一行"的风险（它们必然落在同一行）。
--
-- 执行方式：SQL Server 按批编译，新增列在同一批内对后续语句不可见，故本脚本
-- 以 GO 分段：加列 → 重建主键与约束 → 收口断言。
--
-- 幂等：列已存在时只做断言，不重复扩键。
-- 回滚：还原 PK 为两键 + 删列 + 删索引 + 删外键；被删除的孤立行从备份表取回。
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
    THROW 50700, @GUARD_MESSAGE, 1;

IF OBJECT_ID(N'dbo.DEPOT_LOCATION', N'U') IS NULL
    THROW 50701, N'dbo.DEPOT_LOCATION 不存在，请先执行建表与哨兵行迁移，迁移中止。', 1;

/* ---------- 前置（一）：哨兵行必须覆盖全部库别（外键前提） ---------- */
DECLARE @sentinelCount INT = (SELECT COUNT(*) FROM dbo.DEPOT_LOCATION WHERE LOCATION_NO = N'-');
DECLARE @depotCount INT = (SELECT COUNT(*) FROM dbo.DEPOT);

IF @sentinelCount <> @depotCount
    THROW 50702, N'哨兵行数与库别数不一致，请先执行哨兵行迁移，迁移中止。', 1;

/* ---------- 前置（二）：待归档的孤立库别行必须全为零余额 ---------- */
IF EXISTS (
    SELECT 1 FROM dbo.INV_PRO_DEPOT d
    WHERE NOT EXISTS (SELECT 1 FROM dbo.DEPOT p WHERE p.DEPOT_ID = d.DEPOT_ID)
      AND (ISNULL(d.QTY, 0) <> 0 OR ISNULL(d.INIT_QTY, 0) <> 0
           OR ISNULL(d.COST_AMOUNT, 0) <> 0 OR ISNULL(d.COST_PRICE, 0) <> 0))
    THROW 50703, N'存在留有非零余额的孤立库别行，须先人工确认归属，迁移中止。', 1;

/* ---------- ① 归档并移除孤立库别行 ---------- */
IF OBJECT_ID(N'dbo.INV_PRO_DEPOT_ORPHAN_DEPOT_BACKUP', N'U') IS NULL
BEGIN
    SELECT * INTO dbo.INV_PRO_DEPOT_ORPHAN_DEPOT_BACKUP
    FROM dbo.INV_PRO_DEPOT d
    WHERE NOT EXISTS (SELECT 1 FROM dbo.DEPOT p WHERE p.DEPOT_ID = d.DEPOT_ID);

    PRINT N'== 已归档孤立库别余额行 ' + CONVERT(NVARCHAR(10), @@ROWCOUNT) + N' 条 ==';

    /* SELECT * INTO 不复制默认值约束，而建立组 / 状态位按结构口径必须是 NOT NULL + DEFAULT */
    ALTER TABLE dbo.INV_PRO_DEPOT_ORPHAN_DEPOT_BACKUP ADD
        CONSTRAINT DF_INV_PRO_DEPOT_ORPHAN_CREATE_PERSON DEFAULT (N'')      FOR CREATE_PERSON,
        CONSTRAINT DF_INV_PRO_DEPOT_ORPHAN_CREATE_DATE   DEFAULT (GETDATE()) FOR CREATE_DATE,
        CONSTRAINT DF_INV_PRO_DEPOT_ORPHAN_CONFIRM_TAG   DEFAULT ((0))       FOR CONFIRM_TAG;
END
ELSE
BEGIN
    PRINT N'== 孤立库别余额备份表已存在（幂等跳过归档）==';
END

DELETE d FROM dbo.INV_PRO_DEPOT d
WHERE NOT EXISTS (SELECT 1 FROM dbo.DEPOT p WHERE p.DEPOT_ID = d.DEPOT_ID);

PRINT N'== 已移除孤立库别余额行 ' + CONVERT(NVARCHAR(10), @@ROWCOUNT) + N' 条（历史流水未动）==';

/* ---------- ② 加两列（存量行由默认值填充为哨兵） ---------- */
IF COL_LENGTH(N'dbo.INV_PRO_DEPOT', N'LOCATION_NO') IS NULL
BEGIN
    ALTER TABLE dbo.INV_PRO_DEPOT ADD
        LOCATION_NO NVARCHAR (30) NOT NULL
            CONSTRAINT DF_INV_PRO_DEPOT_LOCATION_NO DEFAULT (N'-');

    PRINT N'== 已加列 INV_PRO_DEPOT.LOCATION_NO ==';
END

IF COL_LENGTH(N'dbo.INV_PRO_DEPOT', N'BATCH_NO') IS NULL
BEGIN
    ALTER TABLE dbo.INV_PRO_DEPOT ADD
        BATCH_NO NCHAR (30) NOT NULL
            CONSTRAINT DF_INV_PRO_DEPOT_BATCH_NO DEFAULT (N'');

    PRINT N'== 已加列 INV_PRO_DEPOT.BATCH_NO ==';
END
GO

/* ---------- ③ 主键扩为四键 ---------- */
DECLARE @pkColsNow INT = (
    SELECT COUNT(*) FROM sys.index_columns ic
    JOIN sys.indexes i ON i.object_id = ic.object_id AND i.index_id = ic.index_id
    WHERE i.object_id = OBJECT_ID(N'dbo.INV_PRO_DEPOT') AND i.is_primary_key = 1 AND ic.is_included_column = 0);

IF @pkColsNow <> 4
BEGIN
    ALTER TABLE dbo.INV_PRO_DEPOT DROP CONSTRAINT PK_INV_PRO_DEPOT;

    ALTER TABLE dbo.INV_PRO_DEPOT ADD CONSTRAINT PK_INV_PRO_DEPOT
        PRIMARY KEY CLUSTERED (PRO_NO ASC, DEPOT_ID ASC, LOCATION_NO ASC, BATCH_NO ASC);

    PRINT N'== INV_PRO_DEPOT 主键已扩为四键 ==';
END
ELSE
BEGIN
    PRINT N'== INV_PRO_DEPOT 主键已是四键（幂等跳过）==';
END

/* ---------- ④ 按库别方向的查询不是最左前缀，另加索引 ---------- */
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.INV_PRO_DEPOT') AND name = N'IX_INV_PRO_DEPOT_DEPOT')
BEGIN
    CREATE NONCLUSTERED INDEX IX_INV_PRO_DEPOT_DEPOT
        ON dbo.INV_PRO_DEPOT (DEPOT_ID ASC, PRO_NO ASC)
        INCLUDE (QTY);

    PRINT N'== 已建立 IX_INV_PRO_DEPOT_DEPOT ==';
END

/* ---------- ⑤ 位置合法性由余额表的外键兜底 ---------- */
IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE parent_object_id = OBJECT_ID(N'dbo.INV_PRO_DEPOT') AND name = N'FK_INV_PRO_DEPOT_LOCATION')
BEGIN
    ALTER TABLE dbo.INV_PRO_DEPOT ADD CONSTRAINT FK_INV_PRO_DEPOT_LOCATION
        FOREIGN KEY (DEPOT_ID, LOCATION_NO) REFERENCES dbo.DEPOT_LOCATION (DEPOT_ID, LOCATION_NO);

    PRINT N'== 已建立 FK_INV_PRO_DEPOT_LOCATION ==';
END

/* ---------- ⑥ 拒绝带尾随空格的位置号 ---------- */
IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE parent_object_id = OBJECT_ID(N'dbo.INV_PRO_DEPOT') AND name = N'CK_INV_PRO_DEPOT_LOCATION_NO_TRIMMED')
BEGIN
    ALTER TABLE dbo.INV_PRO_DEPOT ADD CONSTRAINT CK_INV_PRO_DEPOT_LOCATION_NO_TRIMMED
        CHECK (LOCATION_NO NOT LIKE N'% ');

    PRINT N'== 已建立 CK_INV_PRO_DEPOT_LOCATION_NO_TRIMMED ==';
END
GO

/* ---------- 收口断言 ---------- */
DECLARE @pkCols INT = (
    SELECT COUNT(*) FROM sys.index_columns ic
    JOIN sys.indexes i ON i.object_id = ic.object_id AND i.index_id = ic.index_id
    WHERE i.object_id = OBJECT_ID(N'dbo.INV_PRO_DEPOT') AND i.is_primary_key = 1 AND ic.is_included_column = 0);

IF @pkCols <> 4
    THROW 50704, N'INV_PRO_DEPOT 主键列数不是 4，迁移中止。', 1;

DECLARE @pkOrder NVARCHAR(200) = (
    SELECT STUFF((SELECT N',' + c.name FROM sys.index_columns ic
                  JOIN sys.indexes i ON i.object_id = ic.object_id AND i.index_id = ic.index_id
                  JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
                  WHERE i.object_id = OBJECT_ID(N'dbo.INV_PRO_DEPOT') AND i.is_primary_key = 1 AND ic.is_included_column = 0
                  ORDER BY ic.key_ordinal FOR XML PATH(''), TYPE).value('.', N'NVARCHAR(200)'), 1, 1, N''));

IF @pkOrder <> N'PRO_NO,DEPOT_ID,LOCATION_NO,BATCH_NO'
    THROW 50705, N'INV_PRO_DEPOT 主键列顺序不是 PRO_NO,DEPOT_ID,LOCATION_NO,BATCH_NO，迁移中止。', 1;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.INV_PRO_DEPOT') AND name = N'IX_INV_PRO_DEPOT_DEPOT')
    THROW 50706, N'IX_INV_PRO_DEPOT_DEPOT 未建立，迁移中止。', 1;

IF (SELECT COUNT(*) FROM sys.foreign_keys WHERE parent_object_id = OBJECT_ID(N'dbo.INV_PRO_DEPOT')) <> 1
    THROW 50707, N'INV_PRO_DEPOT 的外键数不是 1，迁移中止。', 1;

IF EXISTS (SELECT 1 FROM dbo.INV_PRO_DEPOT WHERE LOCATION_NO IS NULL OR BATCH_NO IS NULL)
    THROW 50708, N'INV_PRO_DEPOT 的位置 / 批次列存在 NULL，迁移中止。', 1;

/* 扩键后每键可能多行：库别级三字段在同键内仍必须一致 */
IF EXISTS (
    SELECT PRO_NO, DEPOT_ID FROM dbo.INV_PRO_DEPOT
    GROUP BY PRO_NO, DEPOT_ID
    HAVING COUNT(DISTINCT ISNULL(COST_PRICE, -1)) > 1
        OR COUNT(DISTINCT ISNULL(INIT_QTY, -1)) > 1
        OR COUNT(DISTINCT ISNULL(COST_AMOUNT, -1)) > 1)
    THROW 50709, N'库别级字段在同键内不一致，迁移中止。', 1;

DECLARE @finalRows INT = (SELECT COUNT(*) FROM dbo.INV_PRO_DEPOT);

PRINT N'== 收口：INV_PRO_DEPOT 主键 ' + @pkOrder + N'；行数 ' + CONVERT(NVARCHAR(10), @finalRows) + N' ==';
