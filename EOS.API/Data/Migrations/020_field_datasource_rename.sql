-- Rename FIELD_DATASOURCE table: FIELDS_CHOOSER → FIELD_DATASOURCE.
-- Reason: "CHOOSER" is control-semantic; "FIELD_DATASOURCE" better expresses the "field data source" concept.
-- Impact: table name, PK, unique constraint, FK, default constraint, index names all renamed.
-- The stored procedure P_Change_M_IDX is auto-updated by sp_rename.
-- CHOOSER_FILTER_MIGRATION_LOG (migration audit queue) keeps its original name.

SET NOCOUNT ON;

DECLARE @GUARD_MESSAGE NVARCHAR(400) = N'本脚本只能在 EOS.ERP 数据库内执行，当前库为 ' + DB_NAME() + N'。';
IF DB_NAME() <> N'EOS.ERP'
    THROW 50000, @GUARD_MESSAGE, 1;

IF OBJECT_ID(N'dbo.FIELDS_CHOOSER', N'U') IS NOT NULL
   AND OBJECT_ID(N'dbo.FIELD_DATASOURCE', N'U') IS NULL
BEGIN
    EXEC sys.sp_rename N'dbo.FIELDS_CHOOSER', N'FIELD_DATASOURCE';
    EXEC sys.sp_rename N'dbo.PK_FIELDS_CHOOSER', N'PK_FIELD_DATASOURCE', N'OBJECT';
    EXEC sys.sp_rename N'dbo.UQ_FIELDS_CHOOSER', N'UQ_FIELD_DATASOURCE', N'OBJECT';
    EXEC sys.sp_rename N'dbo.FK_FIELDS_CHOOSER_FIELDS', N'FK_FIELD_DATASOURCE_FIELDS', N'OBJECT';
    EXEC sys.sp_rename N'dbo.DF_FIELDS_CHOOSER_ACTIVE_TAG', N'DF_FIELD_DATASOURCE_ACTIVE_TAG', N'OBJECT';
    EXEC sys.sp_rename N'dbo.FIELD_DATASOURCE.IX_FIELDS_CHOOSER_SOURCE_M_IDX', N'IX_FIELD_DATASOURCE_SOURCE_M_IDX', N'INDEX';
END

DECLARE @DATASOURCE_COUNT INT = (SELECT COUNT(*) FROM dbo.FIELD_DATASOURCE);
PRINT N'[] FIELD_DATASOURCE 改名完成：' + CAST(@DATASOURCE_COUNT AS NVARCHAR(10)) + N' 行数据源。';
