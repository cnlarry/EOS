-- ============================================================================
-- EOS.ERP migration 089: CI 行公司启用（服务端持有 + 存量关系回填 + 收紧）
-- ----------------------------------------------------------------------------
-- 决议：CI=行公司，取当前用户所属公司（SYSDL→SYSDN.CI），用户不可改、
-- 不可选填；新建由服务端覆盖回填，更新保持创建归属（见 WorkbenchCommandHandler）。
-- 本迁移只处理"有 OWNER 列的业务表"（254 张）：
--   · 存量空 CI 按归属关系回填（OWNER→账号→员工→公司），无归属/链路缺失 fallback DEMO；
--   · C1 类孤儿公司值按关系原样保留（数据问题，不在本迁移内改写）；
--   · 加 DEFAULT N'DEMO'（保护绕行的历史写入）+ NOT NULL。
-- 不碰：SYSDD/SYSDH/SYSDF（无 OWNER 列，其 CI 为另一语义域的权限标记，保持可空，
-- 不回填不收紧）；LAST_UPDATE/CONFIRM/FINISHED 人日期（永久可空，见 088 头注）。
-- 约束名全大写 DF_<表>_CI。幂等：逐表检查，未达标才改。可逆：DROP 约束 + 改回 NULL。
-- ============================================================================

SET NOCOUNT ON;
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;

DECLARE @GUARD_MESSAGE NVARCHAR(400) = N'本脚本只能在 EOS.ERP 数据库内执行，当前库为 ' + DB_NAME() + N'。';
IF DB_NAME() <> N'EOS.ERP'
    THROW 50000, @GUARD_MESSAGE, 1;

PRINT N'== CI 行公司启用开始 ==';

DECLARE @Work TABLE (SEQ INT IDENTITY(1,1) PRIMARY KEY, TABLE_NAME SYSNAME NOT NULL);
INSERT INTO @Work (TABLE_NAME)
SELECT o.name
FROM sys.columns c
JOIN sys.objects o ON c.object_id = o.object_id AND o.type = N'U'
JOIN sys.schemas s ON o.schema_id = s.schema_id
WHERE s.name = N'dbo' AND c.name = N'CI'
  AND EXISTS (SELECT 1 FROM sys.columns c2 WHERE c2.object_id = o.object_id AND c2.name = N'OWNER')
ORDER BY o.name;

DECLARE @Total INT = (SELECT COUNT(*) FROM @Work);
DECLARE @FixedRows INT = 0, @FixedDefault INT = 0, @FixedNotNull INT = 0;
DECLARE @Seq INT = 1;

WHILE @Seq <= @Total
BEGIN
    DECLARE @TableName SYSNAME;
    SELECT @TableName = TABLE_NAME FROM @Work WHERE SEQ = @Seq;
    DECLARE @QuotedTable NVARCHAR(300) = N'dbo.' + QUOTENAME(@TableName);
    DECLARE @Sql NVARCHAR(MAX);

    -- 1) 关系回填：OWNER→账号→员工→公司；链路缺失 fallback DEMO；非空行不动
    SET @Sql = N'UPDATE ' + @QuotedTable
        + N' SET CI = COALESCE((SELECT TOP 1 NULLIF(LTRIM(RTRIM(n.CI)), N'''')'
        + N' FROM dbo.SYSDL l WITH (NOLOCK) INNER JOIN dbo.SYSDN n WITH (NOLOCK) ON n.EMP_ID = l.EMP_ID'
        + N' WHERE LTRIM(RTRIM(l.USER_ID)) = LTRIM(RTRIM(' + @QuotedTable + N'.OWNER))), N''DEMO'')'
        + N' WHERE NULLIF(LTRIM(RTRIM(CI)), N'''') IS NULL;';
    EXEC sp_executesql @Sql;
    -- 注意：IF 语句本身会把 @@ROWCOUNT 置 0，必须先落袋再判断（直写 IF @@ROWCOUNT 会恒得 0）。
    DECLARE @HitRows INT = @@ROWCOUNT;
    IF @HitRows > 0 SET @FixedRows += @HitRows;

    -- 2) 默认值约束（不存在才加）
    DECLARE @HasDefault BIT = 0;
    SELECT @HasDefault = 1 FROM sys.default_constraints d
    JOIN sys.columns c ON d.parent_object_id = c.object_id AND d.parent_column_id = c.column_id
    JOIN sys.objects o ON c.object_id = o.object_id
    JOIN sys.schemas s ON o.schema_id = s.schema_id
    WHERE s.name = N'dbo' AND o.name = @TableName AND o.type = N'U' AND c.name = N'CI';
    IF @HasDefault = 0
    BEGIN
        SET @Sql = N'ALTER TABLE ' + @QuotedTable + N' ADD CONSTRAINT ' + QUOTENAME(N'DF_' + UPPER(@TableName) + N'_CI')
                 + N' DEFAULT N''DEMO'' FOR ' + QUOTENAME(N'CI') + N';';
        EXEC sp_executesql @Sql;
        SET @FixedDefault += 1;
    END

    -- 3) 收紧 NOT NULL（CI 无索引依赖，见干跑前盘点；仍可空才改）
    DECLARE @Nullable BIT = 1, @TypeName SYSNAME = N'nchar', @MaxLen SMALLINT = 80;
    SELECT @Nullable = c.is_nullable, @TypeName = TYPE_NAME(c.user_type_id), @MaxLen = c.max_length
    FROM sys.columns c
    JOIN sys.objects o ON c.object_id = o.object_id
    JOIN sys.schemas s ON o.schema_id = s.schema_id
    WHERE s.name = N'dbo' AND o.name = @TableName AND o.type = N'U' AND c.name = N'CI';
    IF @Nullable = 1 AND @TypeName NOT IN (N'text', N'ntext', N'image', N'timestamp')
    BEGIN
        DECLARE @TypeSpec NVARCHAR(100) =
            CASE WHEN @TypeName IN (N'char', N'varchar', N'nchar', N'nvarchar')
                      THEN @TypeName + CASE WHEN @MaxLen = -1 THEN N'(MAX)'
                                            ELSE N'(' + CAST(CASE WHEN @TypeName LIKE N'n%' THEN @MaxLen / 2 ELSE @MaxLen END AS NVARCHAR(20)) + N')' END
                 ELSE @TypeName END;
        SET @Sql = N'ALTER TABLE ' + @QuotedTable + N' ALTER COLUMN ' + QUOTENAME(N'CI') + N' ' + @TypeSpec + N' NOT NULL;';
        EXEC sp_executesql @Sql;
        SET @FixedNotNull += 1;
    END

    SET @Seq += 1;
END

PRINT N'-- 回填与收紧汇总 --';
SELECT CONCAT(N'  参与表=', @Total, N' 回填行=', @FixedRows, N' 补默认值=', @FixedDefault,
              N' 收紧NOT NULL=', @FixedNotNull) AS SUMMARY;

PRINT N'-- 残留校验（有OWNER表的空CI行数，应为 0；SYSDD/SYSDH/SYSDF 不在处理范围） --';
DECLARE @CheckSql NVARCHAR(MAX) = N'SELECT NULL AS K, 0 AS N WHERE 1=0';
SELECT @CheckSql += N' UNION ALL SELECT N''' + o.name + N''', COUNT(*) FROM dbo.' + QUOTENAME(o.name)
    + N' WITH (NOLOCK) WHERE NULLIF(LTRIM(RTRIM(CI)), N'''') IS NULL HAVING COUNT(*) > 0'
FROM sys.columns c
JOIN sys.objects o ON c.object_id = o.object_id AND o.type = N'U'
JOIN sys.schemas s ON o.schema_id = s.schema_id
WHERE s.name = N'dbo' AND c.name = N'CI'
  AND EXISTS (SELECT 1 FROM sys.columns c2 WHERE c2.object_id = o.object_id AND c2.name = N'OWNER');
DECLARE @Residual TABLE (T SYSNAME NULL, N INT NOT NULL);
INSERT INTO @Residual EXEC sp_executesql @CheckSql;
SELECT CONCAT(N'  有OWNER表的空CI行数=', ISNULL(SUM(N), 0)) AS SUMMARY FROM @Residual;

PRINT N'== CI 行公司启用完成 ==';
