-- ============================================================================
-- EOS.ERP migration 468: 账号 / 用户组的批核三列退役（2306 用户权限设定、2305 用户组管理）
-- ----------------------------------------------------------------------------
-- 背景：这两个模块是定制页（M_URL=/admin/users、/admin/groups），功能早已定型，
-- 不存在批核流程；但表上仍留着旧系统整表初始化时批量填的"批核状态 / 批核人 / 批核时间"。
--
-- 真库复核（2026-11-01）：
--   · SYS_USER_ACCOUNT 40 行、SYS_USER_GROUP 19 行；已批核行的批核人**全部是 'SYSTEM'**，
--     时间散在 2013–2018（整表初始化的痕迹，无真人批核）；
--   · 这两列上的默认约束名还是旧列名（DF__SYSDL__CONFIRM_T__… / DF__SYSDG__CONFIRM_T__…）；
--   · 代码零写入零读取（RightsAdmin / UserAdmin / Authentication 三个仓储都不碰这三列）；
--   · 配置面零引用：FIELD_DATASOURCE 条件 0 条、SYS_REPORT_PARAMETER 0 条、REPORT_FILTER 0 条；
--   · 版式 0 行（定制页无版式）、SYS_DEFAULT_COLUMN 0 行；只 SYS_USER_COLUMN 有 9 行个人列配置挂着；
--   · 依赖这两个表的 7 个 DB 对象（5 个 f_* 函数、V_USERS、V_SYSDL_SYSDN）都是显式列清单，
--     不引用 APPROVED*，无 SELECT *，故删列不影响它们。
--
-- 为什么删列而不是"保留列恒置 1"或"给定制页补批核"：
--   引用数据的批核门槛（decision-ledger #174）按"源表有没有 APPROVED 列"判定，
--   列在 = 这个概念存在。这两张表根本没有批核概念，于是：
--     · 补批核 = 给不需要审批的管理动作加强制状态位，且新建账号会因未批核而在选择器里消失；
--     · 保留列恒置 1 = 留一个装饰性字段，任何新写入路径漏置就把数据从选择器里静默藏掉。
--   删列是"如实声明这个概念不存在"，与 ADR-043"生命周期列只留在真有生命周期的表上"同向。
--
-- 幂等：列/元数据可能已不存在（重复执行或结构已被手工调整），一律按存在性判断后再动。
-- ============================================================================

SET NOCOUNT ON;
SET XACT_ABORT ON;

IF DB_NAME() <> N'EOS.ERP'
    THROW 50000, N'本脚本只能在 EOS.ERP 数据库内执行。', 1;

PRINT N'== 账号/用户组批核三列退役开始 ==';

BEGIN TRANSACTION;

DECLARE @TargetFields TABLE (F_ID SYSNAME PRIMARY KEY);
INSERT INTO @TargetFields (F_ID) VALUES (N'APPROVED'), (N'APPROVED_BY'), (N'APPROVED_TIME');

DECLARE @TargetTables TABLE (T_ID SYSNAME PRIMARY KEY);
INSERT INTO @TargetTables (T_ID) VALUES (N'SYS_USER_ACCOUNT'), (N'SYS_USER_GROUP');

-- ① 前置自证：这两张表必须有主键列可用（USER_ID / G_IDX），否则说明库结构不是本脚本预期的起点。
IF COL_LENGTH(N'dbo.SYS_USER_ACCOUNT', N'USER_ID') IS NULL
   OR COL_LENGTH(N'dbo.SYS_USER_GROUP', N'G_IDX') IS NULL
    THROW 54680, N'前置不成立：SYS_USER_ACCOUNT.USER_ID 或 SYS_USER_GROUP.G_IDX 不存在。', 1;

-- ② 先摘默认约束：不摘直接 DROP COLUMN 会被 SQL Server 拒绝（约束仍依赖该列）
DECLARE @DropConstraint TABLE (SEQ INT IDENTITY(1,1) PRIMARY KEY, STMT NVARCHAR(MAX) NOT NULL);
INSERT INTO @DropConstraint (STMT)
SELECT N'ALTER TABLE dbo.' + QUOTENAME(o.name) + N' DROP CONSTRAINT ' + QUOTENAME(dc.name) + N';'
FROM sys.default_constraints dc
JOIN sys.objects o ON o.object_id = dc.parent_object_id
JOIN sys.columns c ON c.object_id = dc.parent_object_id AND c.column_id = dc.parent_column_id
JOIN @TargetTables tt ON tt.T_ID = o.name
JOIN @TargetFields tf ON tf.F_ID = c.name;

DECLARE @seq INT = 1, @stmt NVARCHAR(MAX), @maxSeq INT = (SELECT ISNULL(MAX(SEQ), 0) FROM @DropConstraint);
WHILE @seq <= @maxSeq
BEGIN
    SELECT @stmt = STMT FROM @DropConstraint WHERE SEQ = @seq;
    EXEC sp_executesql @stmt;
    SET @seq += 1;
END

-- ③ 删列（每张表一条 ALTER，列出仍存在的目标列）
DECLARE @DropColumn TABLE (SEQ INT IDENTITY(1,1) PRIMARY KEY, STMT NVARCHAR(MAX) NOT NULL);
INSERT INTO @DropColumn (STMT)
SELECT N'ALTER TABLE dbo.' + QUOTENAME(o.name) + N' DROP COLUMN '
       + STRING_AGG(CONVERT(NVARCHAR(300), QUOTENAME(c.name)) COLLATE DATABASE_DEFAULT, N', ')
             WITHIN GROUP (ORDER BY c.name) + N';'
FROM sys.columns c
JOIN sys.objects o ON o.object_id = c.object_id AND o.type = 'U'
JOIN @TargetTables tt ON tt.T_ID = o.name
JOIN @TargetFields tf ON tf.F_ID = c.name
GROUP BY o.name;

SET @seq = 1;
SET @maxSeq = (SELECT ISNULL(MAX(SEQ), 0) FROM @DropColumn);
WHILE @seq <= @maxSeq
BEGIN
    SELECT @stmt = STMT FROM @DropColumn WHERE SEQ = @seq;
    EXEC sp_executesql @stmt;
    PRINT N'  已删列：' + @stmt;
    SET @seq += 1;
END

-- ④ 清元数据残留：字段登记、个人列配置、默认列、版式行、脏标记
DELETE uc FROM dbo.SYS_USER_COLUMN uc
JOIN @TargetTables tt ON tt.T_ID = uc.T_ID
JOIN @TargetFields tf ON tf.F_ID = uc.F_ID;
PRINT N'  SYS_USER_COLUMN 清理 ' + CAST(@@ROWCOUNT AS NVARCHAR(10)) + N' 行';

DELETE dc FROM dbo.SYS_DEFAULT_COLUMN dc
JOIN @TargetTables tt ON tt.T_ID = dc.T_ID
JOIN @TargetFields tf ON tf.F_ID = dc.F_ID;
PRINT N'  SYS_DEFAULT_COLUMN 清理 ' + CAST(@@ROWCOUNT AS NVARCHAR(10)) + N' 行';

-- 版式按 (M_IDX, F_ID) 存；只清这两个定制页自己的行（别的模块同名 F_ID 不在此列）
DELETE l FROM dbo.MODULE_FORM_LAYOUT l
JOIN @TargetFields tf ON tf.F_ID = l.F_ID
WHERE l.M_IDX IN (2305, 2306);
PRINT N'  MODULE_FORM_LAYOUT 清理 ' + CAST(@@ROWCOUNT AS NVARCHAR(10)) + N' 行';

DELETE f FROM dbo.FIELDS f
JOIN @TargetTables tt ON tt.T_ID = f.T_ID
JOIN @TargetFields tf ON tf.F_ID = f.F_ID;
PRINT N'  FIELDS 清理 ' + CAST(@@ROWCOUNT AS NVARCHAR(10)) + N' 行';

-- 这两个模块是定制页、不走发布；若历史上留下脏标记，一并清掉（指向不可能被发布的条目）
DELETE d FROM dbo.WORKBENCH_MODULE_DIRTY d WHERE d.M_IDX IN (2305, 2306);
PRINT N'  WORKBENCH_MODULE_DIRTY 清理 ' + CAST(@@ROWCOUNT AS NVARCHAR(10)) + N' 行';

-- ⑤ 自证：列与字段登记都必须消失
IF COL_LENGTH(N'dbo.SYS_USER_ACCOUNT', N'APPROVED') IS NOT NULL
   OR COL_LENGTH(N'dbo.SYS_USER_ACCOUNT', N'APPROVED_BY') IS NOT NULL
   OR COL_LENGTH(N'dbo.SYS_USER_ACCOUNT', N'APPROVED_TIME') IS NOT NULL
   OR COL_LENGTH(N'dbo.SYS_USER_GROUP', N'APPROVED') IS NOT NULL
   OR COL_LENGTH(N'dbo.SYS_USER_GROUP', N'APPROVED_BY') IS NOT NULL
   OR COL_LENGTH(N'dbo.SYS_USER_GROUP', N'APPROVED_TIME') IS NOT NULL
    THROW 54681, N'自证失败：批核三列仍存在。', 1;

IF EXISTS (SELECT 1 FROM dbo.FIELDS f
           JOIN @TargetTables tt ON tt.T_ID = f.T_ID
           JOIN @TargetFields tf ON tf.F_ID = f.F_ID)
    THROW 54682, N'自证失败：FIELDS 仍登记着这两张表的批核列。', 1;

IF EXISTS (SELECT 1 FROM dbo.SYS_USER_COLUMN uc
           JOIN @TargetTables tt ON tt.T_ID = uc.T_ID
           JOIN @TargetFields tf ON tf.F_ID = uc.F_ID)
    THROW 54683, N'自证失败：SYS_USER_COLUMN 仍挂着这两张表的批核列。', 1;

COMMIT TRANSACTION;

PRINT N'== 账号/用户组批核三列退役完成（批核门槛此后不再作用于这两张表）==';
