-- ============================================================================
-- EOS.ERP migration 231: 月结快照明细的维度键定死（唯一索引扩到七列 + 哨兵值）
-- ----------------------------------------------------------------------------
-- 月结明细（INV_PRO_MONTH_D）原先只按 (单别, 单号, 库别, 料号) 唯一。快照一旦按批次 / 库位 /
-- 制程细分，同一 (料号, 库别) 就会对应多行，撞唯一约束直接存不下去。
--
-- 为什么在这里一次扩到位：唯一索引是**结构**，不能随运行期参数（MONTH_CLOSE_BY_BATCH /
-- MONTH_CLOSE_BY_LOCATION）变动——参数只决定"生成快照时展开哪些维度"。结构恒定、粒度可变，
-- 两者分工明确；反过来的做法（按参数建键）在参数一改就会出现"存不下"。
--
-- 三个维度列一律 NOT NULL + 哨兵默认值：唯一索引把 NULL 视为彼此相等，多行 NULL 同样冲突，
-- 无法表达"该维度未启用"。半成品账（HALF_PRO_DEPOT）按制程分账，其行落在 PROCEDURE_TYPE_ID，
-- 主账行一律落哨兵 ''——与 LOCATION_NO / BATCH_NO 未启用时的处理是同一手法；
-- 哪本账由 MONTH_TYPE 区分。列追加在末位，为将来的维度扩展留出位置。
--
-- 尾随空格：唯一索引所用的 = 比较**忽略**尾随空格（N'- ' 会静默并入 N'-' 哨兵行），
-- 这是 = 表达不了的一条约束，故镜像 INV_PRO_DEPOT 的 CK_INV_PRO_DEPOT_LOCATION_NO_TRIMMED
-- （用 LIKE 表达——LIKE 不忽略尾随空格）。BATCH_NO 是定长 NCHAR，本身以空格补齐，不适用该约束。
--
-- 幂等：列 / 默认值 / 检查约束 / 索引均按存在性判断；索引仅在内核列不是目标七列时重建；
-- 哨兵回填只在确有 NULL 时发生。
-- 回滚：DROP INDEX IX_INV_PRO_MONTH_D → CREATE UNIQUE NONCLUSTERED INDEX IX_INV_PRO_MONTH_D
--       ON dbo.INV_PRO_MONTH_D (MONTH_TYPE, MONTH_NO, DEPOT_ID, PRO_NO)；再 DROP CONSTRAINT
--       CK_INV_PRO_MONTH_D_LOCATION_NO_TRIMMED / DF_INV_PRO_MONTH_D_BATCH_NO /
--       DF_INV_PRO_MONTH_D_LOCATION_NO / DF_INV_PRO_MONTH_D_PROCEDURE_TYPE_ID 并
--       DROP COLUMN PROCEDURE_TYPE_ID。
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
    THROW 52100, @GUARD_MESSAGE, 1;

IF OBJECT_ID(N'dbo.INV_PRO_MONTH_D', N'U') IS NULL
    THROW 52101, N'dbo.INV_PRO_MONTH_D 不存在，迁移中止。', 1;

/* 行数基线：本迁移只改结构，不改行数 */
IF OBJECT_ID(N'tempdb..#month_detail_rows') IS NULL
    CREATE TABLE #month_detail_rows (RowsBefore INT NOT NULL);

INSERT INTO #month_detail_rows (RowsBefore) SELECT COUNT(*) FROM dbo.INV_PRO_MONTH_D;

DECLARE @RowsBefore INT = (SELECT RowsBefore FROM #month_detail_rows);

/* ---------- ① 制程维度列（半成品账按制程分账的承载列；主账行落哨兵 ''） ---------- */
IF COL_LENGTH(N'dbo.INV_PRO_MONTH_D', N'PROCEDURE_TYPE_ID') IS NULL
BEGIN
    ALTER TABLE dbo.INV_PRO_MONTH_D
        ADD PROCEDURE_TYPE_ID NCHAR(20) NOT NULL
            CONSTRAINT DF_INV_PRO_MONTH_D_PROCEDURE_TYPE_ID DEFAULT (N'');
    PRINT N'== 已加列 INV_PRO_MONTH_D.PROCEDURE_TYPE_ID（NOT NULL，默认哨兵空串）==';
END

/* ---------- ② 批号 / 库位两列转 NOT NULL + 哨兵默认值 ---------- */
/* 先回填哨兵（本表当前 0 行，回填为空操作；保留是为了脚本在任何数据状态下都可重复执行），
   再转 NOT NULL——顺序不能反：列上有 NULL 时 ALTER COLUMN 会直接被库拒绝。 */
UPDATE dbo.INV_PRO_MONTH_D SET BATCH_NO = N'' WHERE BATCH_NO IS NULL;
UPDATE dbo.INV_PRO_MONTH_D SET LOCATION_NO = N'-' WHERE LOCATION_NO IS NULL;

IF EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.INV_PRO_MONTH_D')
             AND name = N'BATCH_NO' AND is_nullable = 1)
BEGIN
    ALTER TABLE dbo.INV_PRO_MONTH_D ALTER COLUMN BATCH_NO NCHAR(60) NOT NULL;
    PRINT N'== INV_PRO_MONTH_D.BATCH_NO 已转为 NOT NULL ==';
END

IF EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.INV_PRO_MONTH_D')
             AND name = N'LOCATION_NO' AND is_nullable = 1)
BEGIN
    ALTER TABLE dbo.INV_PRO_MONTH_D ALTER COLUMN LOCATION_NO NVARCHAR(60) NOT NULL;
    PRINT N'== INV_PRO_MONTH_D.LOCATION_NO 已转为 NOT NULL ==';
END

IF NOT EXISTS (SELECT 1 FROM sys.default_constraints
                WHERE parent_object_id = OBJECT_ID(N'dbo.INV_PRO_MONTH_D')
                  AND name = N'DF_INV_PRO_MONTH_D_BATCH_NO')
    ALTER TABLE dbo.INV_PRO_MONTH_D
        ADD CONSTRAINT DF_INV_PRO_MONTH_D_BATCH_NO DEFAULT (N'') FOR BATCH_NO;

IF NOT EXISTS (SELECT 1 FROM sys.default_constraints
                WHERE parent_object_id = OBJECT_ID(N'dbo.INV_PRO_MONTH_D')
                  AND name = N'DF_INV_PRO_MONTH_D_LOCATION_NO')
    ALTER TABLE dbo.INV_PRO_MONTH_D
        ADD CONSTRAINT DF_INV_PRO_MONTH_D_LOCATION_NO DEFAULT (N'-') FOR LOCATION_NO;

/* ---------- ③ 库位哨兵不得带尾随空格（镜像余额表） ---------- */
IF NOT EXISTS (SELECT 1 FROM sys.check_constraints
                WHERE parent_object_id = OBJECT_ID(N'dbo.INV_PRO_MONTH_D')
                  AND name = N'CK_INV_PRO_MONTH_D_LOCATION_NO_TRIMMED')
    ALTER TABLE dbo.INV_PRO_MONTH_D WITH CHECK
        ADD CONSTRAINT CK_INV_PRO_MONTH_D_LOCATION_NO_TRIMMED CHECK (NOT [LOCATION_NO] LIKE N'% ');

/* ---------- ④ 列说明 ---------- */
DECLARE @descriptions TABLE (COLUMN_NAME SYSNAME, DESCRIPTION NVARCHAR(400));
INSERT INTO @descriptions (COLUMN_NAME, DESCRIPTION) VALUES
    (N'PROCEDURE_TYPE_ID', N'制程维度：半成品账按制程分账时的制程代号；主账行固定为空串哨兵'),
    (N'BATCH_NO',          N'批号维度：未启用按批次细分时为空串哨兵'),
    (N'LOCATION_NO',       N'库位维度：未启用按库位细分时为 ''-'' 哨兵，且不得带尾随空格');

DECLARE @columnName SYSNAME, @description NVARCHAR(400);
DECLARE description_cursor CURSOR LOCAL FAST_FORWARD FOR SELECT COLUMN_NAME, DESCRIPTION FROM @descriptions;
OPEN description_cursor;
FETCH NEXT FROM description_cursor INTO @columnName, @description;
WHILE @@FETCH_STATUS = 0
BEGIN
    IF EXISTS (
        SELECT 1 FROM sys.extended_properties ep
        WHERE ep.major_id = OBJECT_ID(N'dbo.INV_PRO_MONTH_D')
          AND ep.minor_id = COLUMNPROPERTY(OBJECT_ID(N'dbo.INV_PRO_MONTH_D'), @columnName, 'ColumnId')
          AND ep.name = N'MS_Description')
    BEGIN
        EXEC sp_updateextendedproperty
            @name = N'MS_Description', @value = @description,
            @level0type = N'SCHEMA', @level0name = N'dbo',
            @level1type = N'TABLE',  @level1name = N'INV_PRO_MONTH_D',
            @level2type = N'COLUMN', @level2name = @columnName;
    END
    ELSE
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

GO

/* ============================================================================
   第二批次：唯一索引扩到七列。
   与上一批分 GO 是必须的——SQL Server 编译整批早于执行，"同批里加列 + 引用该列"
   会报"列名无效"（本仓已有先例）。
   ============================================================================ */

SET NOCOUNT ON;
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET ANSI_PADDING ON;
SET ANSI_WARNINGS ON;
SET CONCAT_NULL_YIELDS_NULL ON;
SET ARITHABORT ON;

DECLARE @tableId INT = OBJECT_ID(N'dbo.INV_PRO_MONTH_D');
DECLARE @indexId INT = (SELECT index_id FROM sys.indexes WHERE object_id = @tableId AND name = N'IX_INV_PRO_MONTH_D');
DECLARE @keyCount INT = 0;
DECLARE @isUnique BIT = 0;

IF @indexId IS NOT NULL
BEGIN
    SELECT @isUnique = is_unique FROM sys.indexes WHERE object_id = @tableId AND index_id = @indexId;
    SELECT @keyCount = COUNT(*) FROM sys.index_columns
     WHERE object_id = @tableId AND index_id = @indexId AND is_included_column = 0;
END

/* 目标形态：唯一 + 恰好七列键且七列齐全 */
DECLARE @sevenComplete INT = 0;
IF @indexId IS NOT NULL
BEGIN
    SELECT @sevenComplete = COUNT(*) FROM sys.index_columns ic
     JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
     WHERE ic.object_id = @tableId AND ic.index_id = @indexId AND ic.is_included_column = 0
       AND c.name IN (N'MONTH_TYPE', N'MONTH_NO', N'DEPOT_ID', N'PRO_NO',
                      N'LOCATION_NO', N'BATCH_NO', N'PROCEDURE_TYPE_ID');
END

IF @indexId IS NULL OR @isUnique = 0 OR @keyCount <> 7 OR @sevenComplete <> 7
BEGIN
    /* 该索引是**唯一约束**的承载索引（`sys.objects.type = 'UQ'`），不能用 DROP INDEX 删
       （库会拒绝："该索引正用于 UNIQUE KEY 约束的强制执行"）；必须走 DROP CONSTRAINT。
       重建时同样建成 UNIQUE 约束，名字与列序保持可追溯。 */
    IF EXISTS (SELECT 1 FROM sys.objects
                WHERE parent_object_id = @tableId AND name = N'IX_INV_PRO_MONTH_D'
                  AND type IN ('UQ', 'PK'))
        ALTER TABLE dbo.INV_PRO_MONTH_D DROP CONSTRAINT IX_INV_PRO_MONTH_D;
    ELSE IF @indexId IS NOT NULL
        DROP INDEX IX_INV_PRO_MONTH_D ON dbo.INV_PRO_MONTH_D;

    ALTER TABLE dbo.INV_PRO_MONTH_D
        ADD CONSTRAINT IX_INV_PRO_MONTH_D UNIQUE NONCLUSTERED
            (MONTH_TYPE, MONTH_NO, DEPOT_ID, PRO_NO, LOCATION_NO, BATCH_NO, PROCEDURE_TYPE_ID);
    PRINT N'== 已重建唯一约束 IX_INV_PRO_MONTH_D（七列）==';
END
ELSE
BEGIN
    PRINT N'== 唯一索引 IX_INV_PRO_MONTH_D 已是七列形态，跳过 ==';
END

/* ---------- 核对断言 ---------- */
DECLARE @indexIdAfter INT = (SELECT index_id FROM sys.indexes WHERE object_id = @tableId AND name = N'IX_INV_PRO_MONTH_D');
IF @indexIdAfter IS NULL
    THROW 52102, N'唯一索引 IX_INV_PRO_MONTH_D 不存在，迁移中止。', 1;

IF (SELECT is_unique FROM sys.indexes WHERE object_id = @tableId AND index_id = @indexIdAfter) <> 1
    THROW 52103, N'IX_INV_PRO_MONTH_D 不是唯一索引，迁移中止。', 1;

IF (SELECT COUNT(*) FROM sys.index_columns WHERE object_id = @tableId AND index_id = @indexIdAfter AND is_included_column = 0) <> 7
    THROW 52104, N'IX_INV_PRO_MONTH_D 的键列数不是七列，迁移中止。', 1;

/* 七列一个都不能少：按列名逐个核对，避免"列数对但换了一列"这类假通过 */
IF EXISTS (
    SELECT 1 FROM (VALUES (N'MONTH_TYPE'), (N'MONTH_NO'), (N'DEPOT_ID'), (N'PRO_NO'),
                         (N'LOCATION_NO'), (N'BATCH_NO'), (N'PROCEDURE_TYPE_ID')) AS expected(F_ID)
    WHERE NOT EXISTS (
        SELECT 1 FROM sys.index_columns ic
        JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
        WHERE ic.object_id = @tableId AND ic.index_id = @indexIdAfter
          AND ic.is_included_column = 0 AND c.name = expected.F_ID))
    THROW 52105, N'IX_INV_PRO_MONTH_D 缺少维度键列，迁移中止。', 1;

/* 三个维度列必须 NOT NULL：唯一索引把 NULL 视为相等，可空即无法表达"该维度未启用" */
IF EXISTS (
    SELECT 1 FROM sys.columns
    WHERE object_id = @tableId AND is_nullable = 1
      AND name IN (N'LOCATION_NO', N'BATCH_NO', N'PROCEDURE_TYPE_ID'))
    THROW 52106, N'维度列仍可空，迁移中止。', 1;

IF NOT EXISTS (SELECT 1 FROM sys.objects
                WHERE parent_object_id = @tableId AND name = N'IX_INV_PRO_MONTH_D' AND type = 'UQ')
    THROW 52109, N'IX_INV_PRO_MONTH_D 不再是唯一约束形态，迁移中止。', 1;

IF NOT EXISTS (SELECT 1 FROM sys.check_constraints
                WHERE parent_object_id = @tableId AND name = N'CK_INV_PRO_MONTH_D_LOCATION_NO_TRIMMED')
    THROW 52107, N'库位哨兵尾随空格约束缺失，迁移中止。', 1;

DECLARE @RowsAfter INT = (SELECT COUNT(*) FROM dbo.INV_PRO_MONTH_D);
IF @RowsAfter <> (SELECT RowsBefore FROM #month_detail_rows)
    THROW 52108, N'明细行数发生变化，迁移中止。', 1;

PRINT N'== 就位：月结明细按 (单别, 单号, 库别, 料号, 库位, 批号, 制程) 七列唯一；行数 '
    + CONVERT(NVARCHAR(10), @RowsAfter) + N' ==';
