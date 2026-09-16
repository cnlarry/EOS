-- ============================================================================
-- EOS.ERP migration 097: 默认公司改为哨兵 DEFAULT（不再指向真实客户 DEMO）
-- ----------------------------------------------------------------------------
-- CI=行公司。此前归属兜底值是 DEMO（一家真实客户公司），无法归属的行会被静默
-- 算到该客户名下。现改为哨兵公司 DEFAULT（COMPANY.COMPANY_ID='DEFAULT'）：
--   1) 默认值约束：DF_<表>_CI 的 N'DEMO' 改为 N'DEFAULT'（SYSDD/SYSDF/SYSDH 的
--      '_ccorp' 属另一语义域，不在本迁移范围内，且其默认值本就不是 DEMO）；
--   2) 存量数据：把归属回填阶段写入的 DEMO 改为 DEFAULT（SYSDD/SYSDF/SYSDH 同样除外）。
-- 归属事实并未丢失：行上仍保留 OWNER（建单账号），补正员工所属公司后可按关系重新归属。
-- 前置守卫：COMPANY 必须已有 DEFAULT 记录，否则中止。
-- 幂等：约束已是 DEFAULT 即跳过；存量行按当前值判定，重复执行为零行。
-- ============================================================================

SET NOCOUNT ON;
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;

DECLARE @GUARD_MESSAGE NVARCHAR(400) = N'本脚本只能在 EOS.ERP 数据库内执行，当前库为 ' + DB_NAME() + N'。';
IF DB_NAME() <> N'EOS.ERP'
    THROW 50000, @GUARD_MESSAGE, 1;

IF NOT EXISTS (SELECT 1 FROM dbo.COMPANY WITH (NOLOCK) WHERE LTRIM(RTRIM(COMPANY_ID)) = N'DEFAULT')
    THROW 50001, N'COMPANY 缺少 DEFAULT 哨兵公司记录，中止切换。', 1;

PRINT N'== 默认公司切换为 DEFAULT 开始 ==';

-- 1) 默认值约束：N'DEMO' → N'DEFAULT'（保留原约束名）
DECLARE @Constraints TABLE (SEQ INT IDENTITY(1,1) PRIMARY KEY, TABLE_NAME SYSNAME NOT NULL, DF_NAME SYSNAME NOT NULL);
INSERT INTO @Constraints (TABLE_NAME, DF_NAME)
SELECT o.name, dc.name
FROM sys.default_constraints dc
JOIN sys.columns c ON dc.parent_object_id = c.object_id AND dc.parent_column_id = c.column_id
JOIN sys.objects o ON c.object_id = o.object_id
JOIN sys.schemas s ON o.schema_id = s.schema_id
WHERE s.name = N'dbo' AND o.type = N'U' AND c.name = N'CI'
  AND CHARINDEX(N'DEMO', dc.definition) > 0
ORDER BY o.name;

DECLARE @ConstraintTotal INT = (SELECT COUNT(*) FROM @Constraints);
DECLARE @ConstraintSeq INT = 1;
DECLARE @SwappedDefaults INT = 0;
WHILE @ConstraintSeq <= @ConstraintTotal
BEGIN
    DECLARE @C_TABLE SYSNAME, @C_DF SYSNAME;
    SELECT @C_TABLE = TABLE_NAME, @C_DF = DF_NAME FROM @Constraints WHERE SEQ = @ConstraintSeq;
    DECLARE @C_SQL NVARCHAR(MAX) =
        N'ALTER TABLE dbo.' + QUOTENAME(@C_TABLE) + N' DROP CONSTRAINT ' + QUOTENAME(@C_DF) + N';' +
        N'ALTER TABLE dbo.' + QUOTENAME(@C_TABLE) + N' ADD CONSTRAINT ' + QUOTENAME(@C_DF) +
        N' DEFAULT N''DEFAULT'' FOR ' + QUOTENAME(N'CI') + N';';
    EXEC sp_executesql @C_SQL;
    SET @SwappedDefaults += 1;
    SET @ConstraintSeq += 1;
END
SELECT CONCAT(N'  默认值约束改为 DEFAULT：', @SwappedDefaults, N' / CI 默认值约束总数 ', @ConstraintTotal) AS SUMMARY;

-- 2) 存量兜底值：DEMO → DEFAULT（SYSDD/SYSDF/SYSDH 的 CI 为权限域标记，不参与）
DECLARE @Tables TABLE (SEQ INT IDENTITY(1,1) PRIMARY KEY, TABLE_NAME SYSNAME NOT NULL);
INSERT INTO @Tables (TABLE_NAME)
SELECT o.name
FROM sys.columns c
JOIN sys.objects o ON c.object_id = o.object_id
JOIN sys.schemas s ON o.schema_id = s.schema_id
WHERE s.name = N'dbo' AND o.type = N'U' AND c.name = N'CI'
  AND o.name NOT IN (N'SYSDD', N'SYSDF', N'SYSDH')
ORDER BY o.name;

DECLARE @TableTotal INT = (SELECT COUNT(*) FROM @Tables);
DECLARE @TableSeq INT = 1;
DECLARE @Remapped BIGINT = 0;
WHILE @TableSeq <= @TableTotal
BEGIN
    DECLARE @T_NAME SYSNAME;
    SELECT @T_NAME = TABLE_NAME FROM @Tables WHERE SEQ = @TableSeq;
    DECLARE @Rows INT = 0;
    DECLARE @T_SQL NVARCHAR(MAX) =
        N'UPDATE dbo.' + QUOTENAME(@T_NAME) +
        N' SET CI = N''DEFAULT'' WHERE LTRIM(RTRIM(ISNULL(CI, N''''))) = N''DEMO'';' +
        N'SET @n = @@ROWCOUNT;';
    EXEC sp_executesql @T_SQL, N'@n INT OUTPUT', @n = @Rows OUTPUT;
    SET @Remapped += @Rows;
    SET @TableSeq += 1;
END
SELECT CONCAT(N'  存量 CI=DEMO 改为 DEFAULT：', @Remapped, N' 行（参与表 ', @TableTotal, N'）') AS SUMMARY;

PRINT N'== 默认公司切换为 DEFAULT 完成 ==';
