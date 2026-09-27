-- ============================================================================
-- EOS.ERP migration 088: B3 空值口径收紧第一批（状态位 + 建立组，精炼口径）
-- ----------------------------------------------------------------------------
--  精炼口径（2026-09-16）：
--   · CONFIRM_TAG / FINISHED_TAG：NOT NULL DEFAULT 0（存量 NULL 补 0）；
--   · CREATE_PERSON / CREATE_DATE：NOT NULL（新建由服务端必写；存量 best-effort
--     回填：人填空串表未知，日期回溯同表 LAST_UPDATE_DATE、再无则以迁移时刻为准，
--     属开发数据整理，不伪装精确史）；
--   · LAST_UPDATE_* / CONFIRM_PERSON/DATE / FINISHED_PERSON/DATE：永久可空，
--     本迁移不碰（NULL = 事件未发生：未修改/未批核/未结案）。
-- DEFAULT 约束同步建立（位→0、人→空串、日期→GETDATE），保护绕过管线的历史写入；
-- 约束名全大写 DF_<表>_<列>。幂等：逐表检查可空性/默认值存在性，未达标才改；
-- 计算列与 text/ntext/image/timestamp 类型跳过并 PRINT。可逆：DROP 约束 + 改回 NULL。
-- ============================================================================

SET NOCOUNT ON;
-- 过滤索引的 DROP/CREATE 要求 QUOTED_IDENTIFIER/ANSI_NULLS 为 ON（与执行入口无关，脚本自带）。
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;

DECLARE @GUARD_MESSAGE NVARCHAR(400) = N'本脚本只能在 EOS.ERP 数据库内执行，当前库为 ' + DB_NAME() + N'。';
IF DB_NAME() <> N'EOS.ERP'
    THROW 50000, @GUARD_MESSAGE, 1;

PRINT N'== B3 空值口径收紧开始 ==';

DECLARE @Work TABLE (
    SEQ INT IDENTITY(1,1) PRIMARY KEY,
    TABLE_NAME SYSNAME NOT NULL,
    COLUMN_NAME SYSNAME NOT NULL,
    ROLE NCHAR(10) NOT NULL -- BIT / PERSON / DATE
);

INSERT INTO @Work (TABLE_NAME, COLUMN_NAME, ROLE)
SELECT s.name + N'.' + o.name, c.name,
       CASE WHEN c.name IN (N'CONFIRM_TAG', N'FINISHED_TAG') THEN N'BIT'
            WHEN c.name = N'CREATE_PERSON' THEN N'PERSON'
            ELSE N'DATE' END
FROM sys.columns c
JOIN sys.objects o ON c.object_id = o.object_id AND o.type = N'U'
JOIN sys.schemas s ON o.schema_id = s.schema_id
WHERE s.name = N'dbo'
  AND c.name IN (N'CONFIRM_TAG', N'FINISHED_TAG', N'CREATE_PERSON', N'CREATE_DATE')
  AND c.is_computed = 0
ORDER BY o.name, c.name;

DECLARE @Total INT = (SELECT COUNT(*) FROM @Work);
DECLARE @FixedNull INT = 0, @FixedDefault INT = 0, @FixedNotNull INT = 0, @Skipped INT = 0;
DECLARE @Seq INT = 1;

WHILE @Seq <= @Total
BEGIN
    DECLARE @FullName NVARCHAR(300), @TableName SYSNAME, @ColumnName SYSNAME, @Role NCHAR(10);
    SELECT @FullName = TABLE_NAME, @ColumnName = COLUMN_NAME, @Role = ROLE FROM @Work WHERE SEQ = @Seq;
    SET @TableName = PARSENAME(@FullName, 1);

    DECLARE @TypeName SYSNAME, @MaxLen SMALLINT, @Prec TINYINT, @Scale TINYINT, @Nullable BIT;
    SELECT @TypeName = TYPE_NAME(c.user_type_id), @MaxLen = c.max_length,
           @Prec = c.precision, @Scale = c.scale, @Nullable = c.is_nullable
    FROM sys.columns c
    JOIN sys.objects o ON c.object_id = o.object_id
    JOIN sys.schemas s ON o.schema_id = s.schema_id
    WHERE s.name = N'dbo' AND o.name = @TableName AND o.type = N'U' AND c.name = @ColumnName;

    -- text/ntext/image/timestamp 不可 ALTER COLUMN，跳过
    IF @TypeName IN (N'text', N'ntext', N'image', N'timestamp')
    BEGIN
        PRINT CONCAT(N'  SKIP 不可变更类型 ', @FullName, N'.', @ColumnName, N' (', @TypeName, N')');
        SET @Skipped += 1; SET @Seq += 1; CONTINUE;
    END

    DECLARE @QuotedTable NVARCHAR(300) = N'dbo.' + QUOTENAME(@TableName);
    DECLARE @QuotedColumn NVARCHAR(300) = QUOTENAME(@ColumnName);
    DECLARE @ConstraintName SYSNAME = N'DF_' + UPPER(@TableName) + N'_' + @ColumnName;
    DECLARE @Sql NVARCHAR(MAX);

    -- 1) 回填存量 NULL
    IF @Role = N'BIT'
        SET @Sql = N'UPDATE ' + @QuotedTable + N' SET ' + @QuotedColumn + N' = 0 WHERE ' + @QuotedColumn + N' IS NULL;';
    ELSE IF @Role = N'PERSON'
        SET @Sql = N'UPDATE ' + @QuotedTable + N' SET ' + @QuotedColumn + N' = N'''' WHERE ' + @QuotedColumn + N' IS NULL;';
    ELSE
    BEGIN
        DECLARE @HasLastUpdate BIT = 0;
        SELECT @HasLastUpdate = 1 FROM sys.columns c
        JOIN sys.objects o ON c.object_id = o.object_id
        JOIN sys.schemas s ON o.schema_id = s.schema_id
        WHERE s.name = N'dbo' AND o.name = @TableName AND o.type = N'U' AND c.name = N'LAST_UPDATE_DATE';
        IF @HasLastUpdate = 1
            SET @Sql = N'UPDATE ' + @QuotedTable + N' SET ' + @QuotedColumn + N' = COALESCE(LAST_UPDATE_DATE, SYSDATETIME()) WHERE ' + @QuotedColumn + N' IS NULL;';
        ELSE
            SET @Sql = N'UPDATE ' + @QuotedTable + N' SET ' + @QuotedColumn + N' = SYSDATETIME() WHERE ' + @QuotedColumn + N' IS NULL;';
    END
    EXEC sp_executesql @Sql;
    IF @@ROWCOUNT > 0 SET @FixedNull += 1;

    -- 2) 默认值约束（不存在才加）
    DECLARE @HasDefault BIT = 0;
    SELECT @HasDefault = 1 FROM sys.default_constraints d
    JOIN sys.columns c ON d.parent_object_id = c.object_id AND d.parent_column_id = c.column_id
    JOIN sys.objects o ON c.object_id = o.object_id
    JOIN sys.schemas s ON o.schema_id = s.schema_id
    WHERE s.name = N'dbo' AND o.name = @TableName AND o.type = N'U' AND c.name = @ColumnName;
    IF @HasDefault = 0
    BEGIN
        DECLARE @DefaultExpr NVARCHAR(50) = CASE @Role WHEN N'BIT' THEN N'0' WHEN N'PERSON' THEN N'''''' ELSE N'GETDATE()' END;
        SET @Sql = N'ALTER TABLE ' + @QuotedTable + N' ADD CONSTRAINT ' + QUOTENAME(@ConstraintName)
                 + N' DEFAULT ' + @DefaultExpr + N' FOR ' + @QuotedColumn + N';';
        EXEC sp_executesql @Sql;
        SET @FixedDefault += 1;
    END

    -- 3) 收紧 NOT NULL（仍可空才改；类型按原样重建）。
    -- 索引依赖：ALTER COLUMN 被含该列的索引阻塞，先按目录重建定义 DROP，
    -- 列收紧后再原样重建（键序/降序/INCLUDE/过滤/唯一性保留；填充因子保留非零值，
    -- 其余物理选项取默认）。聚集索引与唯一约束后备索引不碰，该列跳过并 PRINT。
    IF @Nullable = 1
    BEGIN
        DECLARE @BlockingClustered INT = 0;
        SELECT @BlockingClustered = COUNT(*) FROM sys.indexes i
        JOIN sys.index_columns ic ON i.object_id = ic.object_id AND i.index_id = ic.index_id
        JOIN sys.columns c ON ic.object_id = c.object_id AND ic.column_id = c.column_id
        JOIN sys.objects o ON c.object_id = o.object_id
        JOIN sys.schemas s ON o.schema_id = s.schema_id
        WHERE s.name = N'dbo' AND o.name = @TableName AND o.type = N'U' AND c.name = @ColumnName
          AND (i.type = 1 OR i.is_unique_constraint = 1 OR i.is_primary_key = 1);
        IF @BlockingClustered > 0
        BEGIN
            PRINT CONCAT(N'  SKIP 聚集/唯一索引依赖 ', @FullName, N'.', @ColumnName);
            SET @Skipped += 1;
        END
        ELSE
        BEGIN
            DECLARE @RecreateSql NVARCHAR(MAX) = N'';
            SELECT @RecreateSql += N'CREATE ' + CASE WHEN i.is_unique = 1 THEN N'UNIQUE ' ELSE N'' END
                + N'NONCLUSTERED INDEX ' + QUOTENAME(i.name) + N' ON dbo.' + QUOTENAME(@TableName) + N' ('
                + keycols.Keys + N')'
                + CASE WHEN inc.Incs IS NULL THEN N'' ELSE N' INCLUDE (' + inc.Incs + N')' END
                + CASE WHEN i.filter_definition IS NULL THEN N'' ELSE N' WHERE ' + i.filter_definition END
                + CASE WHEN i.fill_factor <> 0 THEN N' WITH (FILLFACTOR = ' + CAST(i.fill_factor AS NVARCHAR(10)) + N')' ELSE N'' END
                + N';'
            FROM sys.indexes i
            CROSS APPLY (
                SELECT (SELECT QUOTENAME(c2.name) + CASE WHEN ic2.is_descending_key = 1 THEN N' DESC' ELSE N'' END + N','
                        FROM sys.index_columns ic2
                        JOIN sys.columns c2 ON ic2.object_id = c2.object_id AND ic2.column_id = c2.column_id
                        WHERE ic2.object_id = i.object_id AND ic2.index_id = i.index_id AND ic2.is_included_column = 0
                        ORDER BY ic2.key_ordinal
                        FOR XML PATH(''), TYPE).value('.', 'NVARCHAR(MAX)') AS Keys
            ) keycols
            OUTER APPLY (
                SELECT (SELECT QUOTENAME(c3.name) + N','
                        FROM sys.index_columns ic3
                        JOIN sys.columns c3 ON ic3.object_id = c3.object_id AND ic3.column_id = c3.column_id
                        WHERE ic3.object_id = i.object_id AND ic3.index_id = i.index_id AND ic3.is_included_column = 1
                        ORDER BY ic3.index_column_id
                        FOR XML PATH(''), TYPE).value('.', 'NVARCHAR(MAX)') AS Incs
            ) inc
            WHERE i.object_id = OBJECT_ID(N'dbo.' + @TableName)
              AND i.type = 2 AND i.is_primary_key = 0 AND i.is_unique_constraint = 0
              AND EXISTS (SELECT 1 FROM sys.index_columns ic
                          JOIN sys.columns c ON ic.object_id = c.object_id AND ic.column_id = c.column_id
                          WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id AND c.name = @ColumnName);
            -- 去掉 CROSS APPLY 拼接的尾随逗号
            SET @RecreateSql = REPLACE(REPLACE(@RecreateSql, N',)', N')'), N'INCLUDE (,', N'INCLUDE (');

            DECLARE @DropSql NVARCHAR(MAX) = N'';
            SELECT @DropSql += N'DROP INDEX ' + QUOTENAME(i.name) + N' ON dbo.' + QUOTENAME(@TableName) + N';'
            FROM sys.indexes i
            WHERE i.object_id = OBJECT_ID(N'dbo.' + @TableName)
              AND i.type = 2 AND i.is_primary_key = 0 AND i.is_unique_constraint = 0
              AND EXISTS (SELECT 1 FROM sys.index_columns ic
                          JOIN sys.columns c ON ic.object_id = c.object_id AND ic.column_id = c.column_id
                          WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id AND c.name = @ColumnName);
            IF @DropSql <> N'' EXEC sp_executesql @DropSql;

            DECLARE @TypeSpec NVARCHAR(100) =
                CASE WHEN @TypeName IN (N'char', N'varchar', N'nchar', N'nvarchar')
                          THEN @TypeName + CASE WHEN @MaxLen = -1 THEN N'(MAX)'
                                                ELSE N'(' + CAST(CASE WHEN @TypeName LIKE N'n%' THEN @MaxLen / 2 ELSE @MaxLen END AS NVARCHAR(20)) + N')' END
                     WHEN @TypeName IN (N'decimal', N'numeric')
                          THEN @TypeName + N'(' + CAST(@Prec AS NVARCHAR(10)) + N',' + CAST(@Scale AS NVARCHAR(10)) + N')'
                     ELSE @TypeName END;
            SET @Sql = N'ALTER TABLE ' + @QuotedTable + N' ALTER COLUMN ' + @QuotedColumn + N' ' + @TypeSpec + N' NOT NULL;';
            EXEC sp_executesql @Sql;

            IF @RecreateSql <> N'' EXEC sp_executesql @RecreateSql;
            SET @FixedNotNull += 1;
        END
    END

    SET @Seq += 1;
END

PRINT N'-- 收紧汇总 --';
SELECT CONCAT(N'  参与表列=', @Total, N' 回填=', @FixedNull, N' 补默认值=', @FixedDefault,
              N' 收紧NOT NULL=', @FixedNotNull, N' 跳过=', @Skipped) AS SUMMARY;

PRINT N'-- 残留校验（应全零） --';
SELECT CONCAT(N'  仍可空的目标列数=', COUNT(*)) AS SUMMARY
FROM sys.columns c
JOIN sys.objects o ON c.object_id = o.object_id AND o.type = N'U'
JOIN sys.schemas s ON o.schema_id = s.schema_id
WHERE s.name = N'dbo' AND c.is_nullable = 1
  AND c.name IN (N'CONFIRM_TAG', N'FINISHED_TAG', N'CREATE_PERSON', N'CREATE_DATE');

PRINT N'== B3 空值口径收紧完成 ==';
