-- 退役 `REPORT.R_M_IDX` / `REPORT.Q_M_IDX`：报表归属只剩 `REPORT.M_IDX` 一列。

--
-- 依据（归属模型）：
--   报表的归属锚点已由"承载页模块"搬成"归属业务模块"（267 回填、272 收口），
--   这两列在运行期只剩三处"过渡期破并列"的排序用法，而那三处已按**当前并列结果落成数据**
--   （迁移 273：每个模块至多一张 `IS_DEFAULT=1`），排序不再需要它们。
--
-- 顺序（与代码改动同一批）：
--   1. 代码不再读写两列（定义/排序上下文、打印面板预选、单据打印默认页头页脚、模块号级联、
--      新增报表 INSERT）——先冻结读路径，再删列，避免"删了才发现还有人在读"；
--   2. 清掉指向这两列的元数据（`FIELDS` 的 REPORT.R_M_IDX/Q_M_IDX/Q_M_DESC 三行、
--      `TABLES` 里 REPORT 的查询关系文本）——元数据留着指向不存在的列，
--      泛型工作台（2201/229801 打开 REPORT 表）会在运行期报"列不存在"；
--   3. 删索引、删列。
--
-- 留底：`logs/report-host-columns-retire.csv`（删前的列定义与索引定义、以及受影响行数）。
--
-- **不要顺手去改 `db/bootstrap`**：引导数据代表的是"到日志基线（266）为止"的状态，
-- 267 的回填正是按 `REPORT.R_M_IDX` 做的，bootstrap 里那些按两列播种的报表行与权限行也都依赖它。
-- 全新库的路径是"bootstrap（266 时点）→ 回放 267…274"，本脚本排在链尾，
-- 因此两列在更早的脚本里必须仍然存在，清理只在这里做一次。

SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @GUARD_MESSAGE NVARCHAR(400) = N'本脚本只能在 EOS.ERP 数据库内执行，当前库为 ' + DB_NAME() + N'。';
IF DB_NAME() <> N'EOS.ERP'
    THROW 50000, @GUARD_MESSAGE, 1;

BEGIN TRANSACTION;

-- ---------------------------------------------------------------- ① 前置：没有库内对象依赖这两列
IF EXISTS (
    SELECT 1 FROM sys.sql_modules WITH (NOLOCK)
    WHERE definition LIKE N'%REPORT.R_M_IDX%' OR definition LIKE N'%REPORT.Q_M_IDX%')
    THROW 55800, N'仍有视图/过程/函数依赖 REPORT.R_M_IDX 或 Q_M_IDX，先处理它们，中止。', 1;

-- 前置：两列必须存在（否则说明已退役过，不该重跑）
IF COL_LENGTH('dbo.REPORT', 'R_M_IDX') IS NULL OR COL_LENGTH('dbo.REPORT', 'Q_M_IDX') IS NULL
    THROW 55801, N'REPORT.R_M_IDX / Q_M_IDX 不存在，脚本可能被重复执行，中止。', 1;

-- ---------------------------------------------------------------- ② 元数据清理（指向不存在列的元数据）
DECLARE @FieldRows INT = 0;
DELETE FROM dbo.FIELDS
WHERE T_ID = N'REPORT' AND F_ID IN (N'R_M_IDX', N'Q_M_IDX', N'Q_M_DESC');
SET @FieldRows = @@ROWCOUNT;

-- REPORT 的查询关系：去掉按承载页/权限列连 MODULES 的两段，改按归属列连（M_DESC 仍能取值）
DECLARE @RelationSql NVARCHAR(MAX) = (
    SELECT TOP 1 QUERY_RELATION FROM dbo.TABLES WITH (NOLOCK) WHERE T_ID = N'REPORT');
DECLARE @Rewritten NVARCHAR(MAX) = @RelationSql;
IF @RelationSql IS NOT NULL
BEGIN
    SET @Rewritten = REPLACE(@RelationSql,
        N'LEFT JOIN MODULES WITH (NOLOCK) ON REPORT.R_M_IDX=MODULES.M_IDX',
        N'LEFT JOIN MODULES WITH (NOLOCK) ON REPORT.M_IDX=MODULES.M_IDX');
    SET @Rewritten = REPLACE(@Rewritten,
        N'LEFT JOIN MODULES M2 WITH (NOLOCK) ON REPORT.Q_M_IDX=M2.M_IDX',
        N'');
    IF @Rewritten <> @RelationSql
        UPDATE dbo.TABLES SET QUERY_RELATION = @Rewritten WHERE T_ID = N'REPORT';
END

-- 前置：重写后不该再提到两列（报错前把线索留下）
IF @Rewritten LIKE N'%R_M_IDX%' OR @Rewritten LIKE N'%Q_M_IDX%'
    THROW 55802, N'REPORT 的查询关系仍提到 R_M_IDX/Q_M_IDX，请人工核对后重试，中止。', 1;

-- ---------------------------------------------------------------- ③ 删索引与列
IF EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID('dbo.REPORT') AND name = 'IX_REPORT')
    DROP INDEX IX_REPORT ON dbo.REPORT;
IF EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID('dbo.REPORT') AND name = 'IX_REPORT_1')
    DROP INDEX IX_REPORT_1 ON dbo.REPORT;

-- 列上的默认约束会挡住 DROP COLUMN（对象依赖列），先摘掉
DECLARE @DropDefaults NVARCHAR(MAX) = N'';
SELECT @DropDefaults = @DropDefaults
       + N'ALTER TABLE dbo.REPORT DROP CONSTRAINT ' + QUOTENAME(dc.name) + N';'
FROM sys.default_constraints dc
INNER JOIN sys.columns c
        ON c.object_id = dc.parent_object_id AND c.column_id = dc.parent_column_id
WHERE dc.parent_object_id = OBJECT_ID('dbo.REPORT')
  AND c.name IN (N'R_M_IDX', N'Q_M_IDX');
IF @DropDefaults <> N''
    EXEC sp_executesql @DropDefaults;

ALTER TABLE dbo.REPORT DROP COLUMN R_M_IDX;
ALTER TABLE dbo.REPORT DROP COLUMN Q_M_IDX;

-- ---------------------------------------------------------------- ④ 不变量
IF COL_LENGTH('dbo.REPORT', 'R_M_IDX') IS NOT NULL OR COL_LENGTH('dbo.REPORT', 'Q_M_IDX') IS NOT NULL
    THROW 55803, N'两列未被删除，中止。', 1;

IF EXISTS (SELECT 1 FROM dbo.FIELDS WITH (NOLOCK)
           WHERE T_ID = N'REPORT' AND F_ID IN (N'R_M_IDX', N'Q_M_IDX', N'Q_M_DESC'))
    THROW 55804, N'仍存在指向已删列的字段元数据，中止。', 1;

PRINT CONCAT(N'== 退役 REPORT.R_M_IDX / Q_M_IDX：删字段元数据 ', @FieldRows, N' 行，删索引 2 个，删列 2 个 ==');

COMMIT TRANSACTION;
