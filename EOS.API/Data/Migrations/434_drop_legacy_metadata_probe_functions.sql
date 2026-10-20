-- ============================================================================
-- EOS.ERP migration 434: retire legacy metadata-probe functions
-- ----------------------------------------------------------------------------
-- Database: EOS.ERP (the single business database of the system)
--
-- Context: six scalar/table-valued functions inspect the physical schema with
-- the pre-2005 catalog views (sysobjects / syscolumns / systypes /
-- INFORMATION_SCHEMA). They still compile and run, but nothing calls them:
-- every capability they offered is implemented in EOS.API against sys.columns
-- and sys.objects (WorkbenchSql.ColumnExistsAsync, WorkbenchSql column queries,
-- WorkbenchDefinitionBuilder, SysMetricSchemaProbe).
--
--   f_get_field_colid      column ordinal by table/column name   (syscolumns)
--   f_get_field_type       "column;type;length" triple          (syscolumns, systypes)
--   f_get_object_type      object kind label                    (sysobjects)
--   f_get_same_fieldlist   comma list of shared column names    (syscolumns)
--   f_get_user_fields_info queryable field list with descriptions (syscolumns)
--   f_get_key_value_desc   primary-key field names to descriptions (INFORMATION_SCHEMA)
--
-- Verified before writing:
--   1. no application source, test, script or document calls any of them;
--   2. no database module references any of them
--      (sys.sql_expression_dependencies);
--   3. no metadata text column mentions any of them (FIELDS.VIRTUAL_EXP,
--      FIELDS.CONVERT_FUNCTION, FIELD_DATASOURCE.FILTER_STRUCT,
--      WORKBENCH_DEFINITION_SNAPSHOT.DEFINITION_JSON and every other text
--      column that can hold an expression).
--
-- Removed rather than rewritten: rewriting them onto sys.* would preserve code
-- nothing calls, and the two that carry real logic are broken by their own
-- terms. f_get_object_type tests 'P ' (trailing blank) for procedures, which
-- never matches because xtype is right-trimmed first, so every procedure is
-- labelled 'SYSTEM TABLE'. f_get_user_fields_info declares @userId and
-- @table_type and reads neither. f_get_same_fieldlist served only the legacy
-- P_WF_PRODUCT edition snapshot, which ADR-012 ruled does not need porting
-- (SYSSS.PRO_EDITION_TAG is 0; should it ever be enabled, the replacement is a
-- controlled handler, not this function).
--
-- Scope:
--   1. refuse to run if any module still references one of the six;
--   2. drop them;
--   3. assert they are gone and that no module in the database is broken.
-- ============================================================================

SET NOCOUNT ON;
GO

-- ------------------------------------------------------------- 1. 引用检查
DECLARE @targets TABLE ([name] sysname PRIMARY KEY);

INSERT INTO @targets ([name]) VALUES
    (N'f_get_field_colid'),
    (N'f_get_field_type'),
    (N'f_get_object_type'),
    (N'f_get_same_fieldlist'),
    (N'f_get_user_fields_info'),
    (N'f_get_key_value_desc');

DECLARE @blocked nvarchar(400);

SELECT TOP (1)
       @blocked = N'434: 仍有模块引用 '
                + QUOTENAME(r.[name]) + N' -> ' + QUOTENAME(d.referenced_entity_name)
                + N'，不能退役。'
  FROM @targets t
  JOIN sys.sql_expression_dependencies d ON d.referenced_entity_name = t.[name]
  JOIN sys.objects r ON r.[object_id] = d.referencing_id
 WHERE NOT EXISTS (SELECT 1 FROM @targets x WHERE x.[name] = r.[name]);

IF @blocked IS NOT NULL
    THROW 60031, @blocked, 1;
GO

-- ------------------------------------------------------------------ 2. 退役
IF OBJECT_ID(N'dbo.f_get_field_colid') IS NOT NULL
    DROP FUNCTION dbo.f_get_field_colid;
GO

IF OBJECT_ID(N'dbo.f_get_field_type') IS NOT NULL
    DROP FUNCTION dbo.f_get_field_type;
GO

IF OBJECT_ID(N'dbo.f_get_object_type') IS NOT NULL
    DROP FUNCTION dbo.f_get_object_type;
GO

IF OBJECT_ID(N'dbo.f_get_same_fieldlist') IS NOT NULL
    DROP FUNCTION dbo.f_get_same_fieldlist;
GO

IF OBJECT_ID(N'dbo.f_get_user_fields_info') IS NOT NULL
    DROP FUNCTION dbo.f_get_user_fields_info;
GO

IF OBJECT_ID(N'dbo.f_get_key_value_desc') IS NOT NULL
    DROP FUNCTION dbo.f_get_key_value_desc;
GO

-- ------------------------------------------------------------- 3. 收口断言
DECLARE @still_there nvarchar(500);

SELECT @still_there = STRING_AGG(CONVERT(nvarchar(max), QUOTENAME(v.[name])), N', ')
  FROM (VALUES
        (N'f_get_field_colid'), (N'f_get_field_type'), (N'f_get_object_type'),
        (N'f_get_same_fieldlist'), (N'f_get_user_fields_info'), (N'f_get_key_value_desc')
       ) v ([name])
 WHERE OBJECT_ID(N'dbo.' + v.[name]) IS NOT NULL;

IF @still_there IS NOT NULL
    THROW 60032, @still_there, 1;
GO

-- 退役后整库不该再有坏死的模块（与迁移 316、433 同一口径）。
DECLARE @bad nvarchar(300), @msg nvarchar(400);
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
        SET @msg = N'434: 退役后仍有坏死模块 ' + @bad + N' —— ' + ERROR_MESSAGE();
        THROW 60033, @msg, 1;
    END CATCH
    FETCH NEXT FROM cur INTO @bad;
END
CLOSE cur; DEALLOCATE cur;
GO
