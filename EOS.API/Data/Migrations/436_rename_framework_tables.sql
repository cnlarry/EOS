-- ============================================================================
-- EOS.ERP migration 436: rename framework tables to readable names
-- ----------------------------------------------------------------------------
-- Database: EOS.ERP (the single business database of the system)
--
-- Context: the framework tables carried two-letter codes that no one can read
-- without the code: SYSDD / SYSDH (whose permission?), SYSDG / SYSDL / SYSDN
-- (group? login? employee?), SYSQD / SYSQL / SYSQQ / SYSQR (which query?).
-- The prefixes are a two-letter code space with five meanings, exhausted long
-- before 22 tables were named, so the names carry no information.
--
-- Renamed (old -> new):
--   SYSDD            -> SYS_USER_MODULE_PERMISSION
--   SYSDH            -> SYS_GROUP_MODULE_PERMISSION
--   SYSDD_BUTTON     -> SYS_USER_BUTTON_PERMISSION
--   SYSDH_BUTTON     -> SYS_GROUP_BUTTON_PERMISSION
--   SYSDD_EXEC_TYPE  -> SYS_PERMISSION_ACTION
--   SYSDG            -> SYS_USER_GROUP
--   SYSDG_USER       -> SYS_USER_GROUP_MEMBER
--   SYSDL            -> SYS_USER_ACCOUNT
--   SYSDN            -> SYS_EMPLOYEE
--   SYSSS            -> SYS_PARAMETER
--   SYSQL_FIELDS     -> SYS_USER_COLUMN
--   SYSQL_DEFAULT    -> SYS_DEFAULT_COLUMN
--   SYSQR            -> SYS_USER_REPORT_PRINT
--   SYSQR_DEFAULT    -> SYS_REPORT_PARAMETER
--   SYSQR_TYPE       -> SYS_REPORT_PARAMETER_TYPE
--   SYSQR_USER       -> SYS_USER_REPORT_PARAMETER
--   SYSQQ            -> SYS_USER_CHOOSER_MEMORY
--
-- SYS_USER_CHOOSER_MEMORY keeps its rows: the legacy choosers stored, per user
-- and target table, the filter field and keyword the user last searched with
-- (PersonChooser.aspx restored droFieldList + txtFieldValue from SYSQQ). Those
-- rows are the initial state of the new chooser memory.
--
-- What the rename has to carry along (the physical rename alone leaves the
-- metadata pointing at names that no longer exist):
--   1. FIELD_DATASOURCE references FIELDS(T_ID, F_ID) with ON UPDATE NO ACTION,
--      so the foreign key is disabled while FIELDS.T_ID changes;
--   2. TABLES.T_ID / FIELDS.T_ID / FIELD_DATASOURCE / SYS_USER_COLUMN /
--      SYS_DEFAULT_COLUMN hold table names as values;
--   3. MODULES.MASTER_TABLE / DETAIL_TABLE hold table names as values;
--   4. TABLES.QUERY_RELATION, FIELDS.VIRTUAL_EXP, MODULE_VALIDATION_RULE
--      .PARAM_STRUCT, REPORT_SORT.GROUP_FIELDS, SYS_REPORT_PARAMETER
--      .FILTER_TEMPLATE, FIELD_DATASOURCE.FILTER_STRUCT hold SQL fragments;
--   5. twelve views and functions read those tables by name and would go dead
--      under lazy name resolution (they are rebuilt from sys.sql_modules with
--      the same mapping);
--   6. snapshots that mention an old name are marked dirty for republication.
--
-- Index and constraint names (PK_SYSDD, DF_SYSDD_*) are left untouched: they
-- are metadata-only artefacts with no runtime effect. Renaming them is a
-- separate concern from making the schema readable.
-- ============================================================================

SET NOCOUNT ON;
GO

DECLARE @guard nvarchar(400) = N'本脚本只能在 EOS.ERP 数据库内执行，当前库为 ' + DB_NAME() + N'。';
IF DB_NAME() <> N'EOS.ERP'
    THROW 50000, @guard, 1;
GO

-- ------------------------------------------------------------ 0. 映射表
IF OBJECT_ID(N'tempdb..#rename_map') IS NOT NULL DROP TABLE #rename_map;

CREATE TABLE #rename_map (
    old_name sysname COLLATE DATABASE_DEFAULT NOT NULL PRIMARY KEY,
    new_name sysname COLLATE DATABASE_DEFAULT NOT NULL UNIQUE
);

INSERT INTO #rename_map (old_name, new_name) VALUES
    (N'SYSDD_EXEC_TYPE', N'SYS_PERMISSION_ACTION'),
    (N'SYSQR_DEFAULT',   N'SYS_REPORT_PARAMETER'),
    (N'SYSQR_TYPE',      N'SYS_REPORT_PARAMETER_TYPE'),
    (N'SYSQR_USER',      N'SYS_USER_REPORT_PARAMETER'),
    (N'SYSQL_DEFAULT',   N'SYS_DEFAULT_COLUMN'),
    (N'SYSQL_FIELDS',    N'SYS_USER_COLUMN'),
    (N'SYSDD_BUTTON',    N'SYS_USER_BUTTON_PERMISSION'),
    (N'SYSDH_BUTTON',    N'SYS_GROUP_BUTTON_PERMISSION'),
    (N'SYSDG_USER',      N'SYS_USER_GROUP_MEMBER'),
    (N'SYSDD',           N'SYS_USER_MODULE_PERMISSION'),
    (N'SYSDH',           N'SYS_GROUP_MODULE_PERMISSION'),
    (N'SYSDG',           N'SYS_USER_GROUP'),
    (N'SYSDL',           N'SYS_USER_ACCOUNT'),
    (N'SYSDN',           N'SYS_EMPLOYEE'),
    (N'SYSSS',           N'SYS_PARAMETER'),
    (N'SYSQR',           N'SYS_USER_REPORT_PRINT'),
    (N'SYSQQ',           N'SYS_USER_CHOOSER_MEMORY');
GO

-- ------------------------------------------------- 1. 物理改名 + 值列同步
ALTER TABLE dbo.FIELD_DATASOURCE NOCHECK CONSTRAINT FK_FIELD_DATASOURCE_FIELDS;

DECLARE @old sysname, @new sysname, @full sysname;

DECLARE ren CURSOR LOCAL FAST_FORWARD FOR
    SELECT old_name, new_name FROM #rename_map ORDER BY LEN(old_name) DESC;
OPEN ren;
FETCH NEXT FROM ren INTO @old, @new;
WHILE @@FETCH_STATUS = 0
BEGIN
    SET @full = N'dbo.' + @old;
    IF OBJECT_ID(@full, N'U') IS NOT NULL
        EXEC sp_rename @objname = @full, @newname = @new;

    FETCH NEXT FROM ren INTO @old, @new;
END
CLOSE ren; DEALLOCATE ren;

UPDATE f SET f.T_ID = m.new_name
  FROM dbo.FIELDS f JOIN #rename_map m ON f.T_ID = m.old_name;

UPDATE d SET d.T_ID = m.new_name
  FROM dbo.FIELD_DATASOURCE d JOIN #rename_map m ON d.T_ID = m.old_name;

UPDATE d SET d.SOURCE_T_ID = m.new_name
  FROM dbo.FIELD_DATASOURCE d JOIN #rename_map m ON d.SOURCE_T_ID = m.old_name;

UPDATE t SET t.T_ID = m.new_name
  FROM dbo.TABLES t JOIN #rename_map m ON t.T_ID = m.old_name;

UPDATE m SET m.MASTER_TABLE = r.new_name
  FROM dbo.MODULES m JOIN #rename_map r ON m.MASTER_TABLE = r.old_name;

UPDATE m SET m.DETAIL_TABLE = r.new_name
  FROM dbo.MODULES m JOIN #rename_map r ON m.DETAIL_TABLE = r.old_name;

-- 这两张表本身也在改名清单里，改名后必须用新名引用。
UPDATE c SET c.T_ID = m.new_name
  FROM dbo.SYS_USER_COLUMN c JOIN #rename_map m ON c.T_ID = m.old_name;

UPDATE c SET c.T_ID_R = m.new_name
  FROM dbo.SYS_USER_COLUMN c JOIN #rename_map m ON c.T_ID_R = m.old_name;

UPDATE c SET c.T_ID = m.new_name
  FROM dbo.SYS_DEFAULT_COLUMN c JOIN #rename_map m ON c.T_ID = m.old_name;

UPDATE c SET c.T_ID_R = m.new_name
  FROM dbo.SYS_DEFAULT_COLUMN c JOIN #rename_map m ON c.T_ID_R = m.old_name;

ALTER TABLE dbo.FIELD_DATASOURCE WITH CHECK CHECK CONSTRAINT FK_FIELD_DATASOURCE_FIELDS;
GO

-- ------------------------------------------- 2. SQL 片段列（含视图/函数体）
DECLARE @targets TABLE ([tbl] sysname, [col] sysname);
INSERT INTO @targets ([tbl], [col]) VALUES
    (N'TABLES',                N'QUERY_RELATION'),
    (N'TABLES',                N'T_REMARK'),
    (N'TABLES',                N'T_DESC'),
    (N'FIELDS',                N'VIRTUAL_EXP'),
    (N'FIELDS',                N'F_REMARK'),
    (N'MODULES',               N'REMARK'),
    (N'MODULE_VALIDATION_RULE', N'PARAM_STRUCT'),
    (N'MODULE_VALIDATION_RULE', N'REMARK'),
    (N'REPORT_SORT',           N'GROUP_FIELDS'),
    (N'SYS_REPORT_PARAMETER',  N'FILTER_TEMPLATE'),
    (N'FIELD_DATASOURCE',      N'FILTER_STRUCT');

DECLARE @t sysname, @c sysname, @expr nvarchar(max), @sql nvarchar(max);

DECLARE frag CURSOR LOCAL FAST_FORWARD FOR SELECT [tbl], [col] FROM @targets;
OPEN frag;
FETCH NEXT FROM frag INTO @t, @c;
WHILE @@FETCH_STATUS = 0
BEGIN
    SET @expr = N'{C}';

    -- 长名先替换：外层是短名，内层是长名，替换从内向外执行。
    DECLARE @m_old sysname, @m_new sysname;
    DECLARE ren2 CURSOR LOCAL FAST_FORWARD FOR
        SELECT old_name, new_name FROM #rename_map ORDER BY LEN(old_name) DESC;
    OPEN ren2;
    FETCH NEXT FROM ren2 INTO @m_old, @m_new;
    WHILE @@FETCH_STATUS = 0
    BEGIN
        SET @expr = REPLACE(@expr, N'{C}',
            N'REPLACE({C}, N''' + @m_old + N''', N''' + @m_new + N''')');
        FETCH NEXT FROM ren2 INTO @m_old, @m_new;
    END
    CLOSE ren2; DEALLOCATE ren2;

    SET @expr = REPLACE(@expr, N'{C}', QUOTENAME(@c));

    SET @sql = N'UPDATE dbo.' + QUOTENAME(@t)
             + N' SET ' + QUOTENAME(@c) + N' = ' + @expr
             + N' WHERE ' + QUOTENAME(@c) + N' IS NOT NULL;';
    EXEC sp_executesql @sql;

    FETCH NEXT FROM frag INTO @t, @c;
END
CLOSE frag; DEALLOCATE frag;
GO

-- --------------------------------------- 3. 重建读取这些表的视图与函数
DECLARE @obj nvarchar(300), @def nvarchar(max), @body nvarchar(max), @pos int;
DECLARE @short sysname, @token nvarchar(60);

DECLARE mods CURSOR LOCAL FAST_FORWARD FOR
    SELECT QUOTENAME(SCHEMA_NAME(o.schema_id)) + N'.' + QUOTENAME(o.name), m.definition
      FROM sys.sql_modules m
      JOIN sys.objects o ON o.object_id = m.object_id
     WHERE o.object_id IN (
            OBJECT_ID(N'dbo.f_get_emp_name_by_id'),
            OBJECT_ID(N'dbo.f_get_empname_by_userid'),
            OBJECT_ID(N'dbo.f_get_user_group_desc'),
            OBJECT_ID(N'dbo.f_split_emp_ids2names'),
            OBJECT_ID(N'dbo.f_sys_param'),
            OBJECT_ID(N'dbo.f_get_sirs'),
            OBJECT_ID(N'dbo.f_get_under_me'),
            OBJECT_ID(N'dbo.f_get_underling'),
            OBJECT_ID(N'dbo.f_get_user_approve'),
            OBJECT_ID(N'dbo.V_COP_ORDER_D_UNDELIVERED'),
            OBJECT_ID(N'dbo.V_SYSDL_SYSDN'),
            OBJECT_ID(N'dbo.V_USERS'));

OPEN mods;
FETCH NEXT FROM mods INTO @obj, @def;
WHILE @@FETCH_STATUS = 0
BEGIN
    -- 对象自身的名字可能含旧表名（V_SYSDL_SYSDN 就是），先保护起来，
    -- 否则 CREATE 行会被一并改名，ALTER 时找不到对象。
    SET @short = PARSENAME(@obj, 1);
    SET @token = N'@@EOS_SELF_' + CONVERT(nvarchar(20), ABS(CHECKSUM(@short))) + N'@@';
    SET @body = REPLACE(@def, @short, @token);

    DECLARE @r_old sysname, @r_new sysname;
    DECLARE ren3 CURSOR LOCAL FAST_FORWARD FOR
        SELECT old_name, new_name FROM #rename_map ORDER BY LEN(old_name) DESC;
    OPEN ren3;
    FETCH NEXT FROM ren3 INTO @r_old, @r_new;
    WHILE @@FETCH_STATUS = 0
    BEGIN
        SET @body = REPLACE(@body, @r_old, @r_new);
        FETCH NEXT FROM ren3 INTO @r_old, @r_new;
    END
    CLOSE ren3; DEALLOCATE ren3;

    SET @body = REPLACE(@body, @token, @short);

    SET @pos = CHARINDEX(N'CREATE', UPPER(@body));
    IF @pos > 0
        SET @body = STUFF(@body, @pos, 6, N'ALTER');

    EXEC sp_executesql @body;

    FETCH NEXT FROM mods INTO @obj, @def;
END
CLOSE mods; DEALLOCATE mods;
GO

-- -------------------------------------------------------- 4. 快照标脏
INSERT INTO dbo.WORKBENCH_MODULE_DIRTY (M_IDX, DIRTY_TAG, LAST_MODIFIED_BY, LAST_MODIFIED_AT)
SELECT DISTINCT s.M_IDX, 1, N'migration-436', SYSUTCDATETIME()
  FROM dbo.WORKBENCH_DEFINITION_SNAPSHOT s
 WHERE s.IS_CURRENT = 1
   AND EXISTS (SELECT 1 FROM #rename_map m WHERE s.DEFINITION_JSON LIKE N'%' + m.old_name + N'%')
   AND NOT EXISTS (SELECT 1 FROM dbo.WORKBENCH_MODULE_DIRTY d WHERE d.M_IDX = s.M_IDX);
GO

-- ------------------------------------------------------------ 5. 收口断言
DECLARE @left_tables nvarchar(1000);

SELECT @left_tables = STRING_AGG(CONVERT(nvarchar(max), QUOTENAME(m.old_name)), N', ')
  FROM #rename_map m
 WHERE OBJECT_ID(N'dbo.' + m.old_name, N'U') IS NOT NULL;

IF @left_tables IS NOT NULL
    THROW 60051, @left_tables, 1;

DECLARE @left_meta nvarchar(1000);

SELECT @left_meta = STRING_AGG(CONVERT(nvarchar(max), QUOTENAME(m.old_name)), N', ')
  FROM #rename_map m
 WHERE EXISTS (SELECT 1 FROM dbo.TABLES t WHERE t.T_ID = m.old_name)
    OR EXISTS (SELECT 1 FROM dbo.FIELDS f WHERE f.T_ID = m.old_name)
    OR EXISTS (SELECT 1 FROM dbo.FIELD_DATASOURCE d WHERE d.T_ID = m.old_name OR d.SOURCE_T_ID = m.old_name)
    OR EXISTS (SELECT 1 FROM dbo.MODULES o WHERE o.MASTER_TABLE = m.old_name OR o.DETAIL_TABLE = m.old_name);

IF @left_meta IS NOT NULL
BEGIN
    DECLARE @meta_msg nvarchar(400) = N'436: 元数据仍有旧表名 —— ' + @left_meta;
    THROW 60052, @meta_msg, 1;
END

DECLARE @missing nvarchar(1000);

SELECT @missing = STRING_AGG(CONVERT(nvarchar(max), QUOTENAME(m.new_name)), N', ')
  FROM #rename_map m
 WHERE OBJECT_ID(N'dbo.' + m.new_name, N'U') IS NULL;

IF @missing IS NOT NULL
BEGIN
    DECLARE @missing_msg nvarchar(400) = N'436: 新表名未落地 —— ' + @missing;
    THROW 60053, @missing_msg, 1;
END
GO

-- 改名后整库不该再有坏死的模块。
DECLARE @bad nvarchar(300), @refresh_msg nvarchar(400);
DECLARE cur CURSOR FOR
    SELECT QUOTENAME(SCHEMA_NAME(schema_id)) + '.' + QUOTENAME(name)
      FROM sys.objects
     WHERE type IN ('V', 'FN', 'IF', 'TF', 'P') AND is_ms_shipped = 0;
OPEN cur;
FETCH NEXT FROM cur INTO @bad;
WHILE @@FETCH_STATUS = 0
BEGIN
    BEGIN TRY
        EXEC sp_refreshsqlmodule @bad;
    END TRY
    BEGIN CATCH
        SET @refresh_msg = N'436: 改名后仍有坏死模块 ' + @bad + N' —— ' + ERROR_MESSAGE();
        THROW 60054, @refresh_msg, 1;
    END CATCH
    FETCH NEXT FROM cur INTO @bad;
END
CLOSE cur; DEALLOCATE cur;
GO

DROP TABLE #rename_map;
GO
