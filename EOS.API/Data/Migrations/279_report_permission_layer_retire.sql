-- 退役报表权限例外层的结构：`SYSDH_REPORT` 整表 + `SYSDD_REPORT` 的四列。

--
-- 依据（D16）：报表权限已收敛为单层——唯一真源是归属模块的 `REPORT_TAG`
-- （个人 `SYSDD` 优先，否则组 `SYSDH` 取或）。例外层的运行时读取点、写入点、管理界面与
-- HTTP 端点均已退场，异常行也已收尾（276：登记 27 行、清理 228 行）。本脚本把结构一并退掉，
-- 使"第二处真源"在代码与库里都不再留有痕。
--
-- `SYSDD_REPORT` **表保留**：它今后只承载报表中心的用户状态（`FAVORITE_TAG`/`SORT_IDX`/`LAST_RUN_AT`），
-- 本次只删它那四列权限列。
--
-- 守卫（任一不成立即中止，不做"差不多就删"）：
--   ① 表必须存在且**为空**——又有数据说明有人把例外层用回来了，要先看是谁写的；
--   ② 不得有外键指向该表；
--   ③ 库内不得有模块/过程/视图提到这两处结构（元数据表与字段表除外，它们由本脚本清理）。
-- 留底：`logs/report-permission-structure-retire.md`（删前结构定义与元数据行数）。

SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @GUARD_MESSAGE NVARCHAR(400) = N'本脚本只能在 EOS.ERP 数据库内执行，当前库为 ' + DB_NAME() + N'。';
IF DB_NAME() <> N'EOS.ERP'
    THROW 50000, @GUARD_MESSAGE, 1;

BEGIN TRANSACTION;

-- ---------------------------------------------------------------- ① 前置
IF OBJECT_ID('dbo.SYSDH_REPORT', 'U') IS NULL
    THROW 56100, N'SYSDH_REPORT 已不存在——脚本可能被重复执行，中止。', 1;

DECLARE @SdhRows INT = (SELECT COUNT(*) FROM dbo.SYSDH_REPORT WITH (NOLOCK));
IF @SdhRows <> 0
BEGIN
    DECLARE @NotEmpty NVARCHAR(300) = N'SYSDH_REPORT 里出现了 ' + CAST(@SdhRows AS NVARCHAR(10))
        + N' 行数据——例外层疑似被重新启用，先查清写入来源，中止。';
    THROW 56101, @NotEmpty, 1;
END

IF EXISTS (SELECT 1 FROM sys.foreign_keys WHERE referenced_object_id = OBJECT_ID('dbo.SYSDH_REPORT'))
    THROW 56102, N'仍有外键指向 SYSDH_REPORT，先处理外键，中止。', 1;

IF COL_LENGTH('dbo.SYSDD_REPORT', 'PREVIEW_TAG') IS NULL
    THROW 56103, N'SYSDD_REPORT.PREVIEW_TAG 不存在——可能已退役，脚本不该重跑，中止。', 1;

-- 库内对象（视图/过程/函数）不得再提到这两处结构
IF EXISTS (SELECT 1 FROM sys.sql_modules WITH (NOLOCK)
           WHERE definition LIKE N'%SYSDH_REPORT%'
              OR (definition LIKE N'%SYSDD_REPORT%'
                  AND (definition LIKE N'%PREVIEW_TAG%' OR definition LIKE N'%PRINT_TAG%'
                       OR definition LIKE N'%EXPORT_TAG%')))
    THROW 56104, N'仍有视图/过程/函数依赖这两处结构，先处理它们，中止。', 1;

-- 元数据表里指向 SYSDH_REPORT 的关联列（TABLES.FK_T_ID_1..5）必须先解引用，否则删表后它们悬空
DECLARE @RelLinks INT = (
    SELECT COUNT(*) FROM dbo.TABLES WITH (NOLOCK)
    WHERE FK_T_ID_1 = N'SYSDH_REPORT' OR FK_T_ID_2 = N'SYSDH_REPORT' OR FK_T_ID_3 = N'SYSDH_REPORT'
       OR FK_T_ID_4 = N'SYSDH_REPORT' OR FK_T_ID_5 = N'SYSDH_REPORT');

-- 查询关系（QUERY_RELATION）里不得再提到 SYSDH_REPORT：那是泛型工作台真正执行的 SQL 文本，
-- 提到一张即将被删的表 = 那个界面日后必然报"对象不存在"。这份文本要人工改写，故只守不改。
-- 排除"被删的那一行自己"：SYSDH_REPORT 的查询关系随它的元数据行一起消失，不需要改写。
IF EXISTS (SELECT 1 FROM dbo.TABLES WITH (NOLOCK)
           WHERE QUERY_RELATION LIKE N'%SYSDH_REPORT%' AND T_ID <> N'SYSDH_REPORT')
    THROW 56105, N'有查询关系文本仍在 JOIN SYSDH_REPORT，需人工改写后再退役，中止。', 1;

-- ---------------------------------------------------------------- ② 元数据清理
DECLARE @SdhFieldRows INT = 0, @SddPermFieldRows INT = 0, @TableRows INT = 0;

DELETE FROM dbo.FIELDS WHERE T_ID = N'SYSDH_REPORT';
SET @SdhFieldRows = @@ROWCOUNT;

DELETE FROM dbo.FIELDS
WHERE T_ID = N'SYSDD_REPORT' AND F_ID IN (N'PREVIEW_TAG', N'PRINT_TAG', N'EXPORT_TAG', N'DATA_FILTER');
SET @SddPermFieldRows = @@ROWCOUNT;

UPDATE dbo.TABLES SET FK_T_ID_1 = NULL WHERE FK_T_ID_1 = N'SYSDH_REPORT';
UPDATE dbo.TABLES SET FK_T_ID_2 = NULL WHERE FK_T_ID_2 = N'SYSDH_REPORT';
UPDATE dbo.TABLES SET FK_T_ID_3 = NULL WHERE FK_T_ID_3 = N'SYSDH_REPORT';
UPDATE dbo.TABLES SET FK_T_ID_4 = NULL WHERE FK_T_ID_4 = N'SYSDH_REPORT';
UPDATE dbo.TABLES SET FK_T_ID_5 = NULL WHERE FK_T_ID_5 = N'SYSDH_REPORT';

DELETE FROM dbo.TABLES WHERE T_ID = N'SYSDH_REPORT';
SET @TableRows = @@ROWCOUNT;

-- ---------------------------------------------------------------- ③ 删表
DROP TABLE dbo.SYSDH_REPORT;

-- ---------------------------------------------------------------- ④ 删四列（先摘默认约束）
DECLARE @DropDefaults NVARCHAR(MAX) = N'';
SELECT @DropDefaults = @DropDefaults
       + N'ALTER TABLE dbo.SYSDD_REPORT DROP CONSTRAINT ' + QUOTENAME(dc.name) + N';'
FROM sys.default_constraints dc
INNER JOIN sys.columns c
        ON c.object_id = dc.parent_object_id AND c.column_id = dc.parent_column_id
WHERE dc.parent_object_id = OBJECT_ID('dbo.SYSDD_REPORT')
  AND c.name IN (N'PREVIEW_TAG', N'PRINT_TAG', N'EXPORT_TAG', N'DATA_FILTER');
IF @DropDefaults <> N''
    EXEC sp_executesql @DropDefaults;

ALTER TABLE dbo.SYSDD_REPORT DROP COLUMN PREVIEW_TAG;
ALTER TABLE dbo.SYSDD_REPORT DROP COLUMN PRINT_TAG;
ALTER TABLE dbo.SYSDD_REPORT DROP COLUMN EXPORT_TAG;
ALTER TABLE dbo.SYSDD_REPORT DROP COLUMN DATA_FILTER;

-- ---------------------------------------------------------------- ⑤ 不变量
IF OBJECT_ID('dbo.SYSDH_REPORT', 'U') IS NOT NULL
    THROW 56106, N'SYSDH_REPORT 未被删除，中止。', 1;

IF COL_LENGTH('dbo.SYSDD_REPORT', 'PREVIEW_TAG') IS NOT NULL
    OR COL_LENGTH('dbo.SYSDD_REPORT', 'PRINT_TAG') IS NOT NULL
    OR COL_LENGTH('dbo.SYSDD_REPORT', 'EXPORT_TAG') IS NOT NULL
    OR COL_LENGTH('dbo.SYSDD_REPORT', 'DATA_FILTER') IS NOT NULL
    THROW 56107, N'SYSDD_REPORT 的四列未被删除，中止。', 1;

IF EXISTS (SELECT 1 FROM dbo.TABLES WITH (NOLOCK) WHERE T_ID = N'SYSDH_REPORT')
    THROW 56108, N'表元数据未清理干净，中止。', 1;

-- 用户状态列必须还在（收藏/排序/最近使用是用户数据）
IF COL_LENGTH('dbo.SYSDD_REPORT', 'FAVORITE_TAG') IS NULL
    OR COL_LENGTH('dbo.SYSDD_REPORT', 'SORT_IDX') IS NULL
    OR COL_LENGTH('dbo.SYSDD_REPORT', 'LAST_RUN_AT') IS NULL
    THROW 56109, N'SYSDD_REPORT 的用户状态列缺失，中止。', 1;

DECLARE @Done NVARCHAR(400) = N'== 报表权限例外层结构退役：删字段元数据 '
    + CAST(@SdhFieldRows AS NVARCHAR(10)) + N' + ' + CAST(@SddPermFieldRows AS NVARCHAR(10))
    + N' 行、解关联 ' + CAST(@RelLinks AS NVARCHAR(10)) + N' 处、删表元数据 ' + CAST(@TableRows AS NVARCHAR(10))
    + N' 行、删表 1 张、删列 4 个 ==';
PRINT @Done;

COMMIT TRANSACTION;
