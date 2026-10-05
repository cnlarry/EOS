-- ============================================================================
-- EOS.ERP migration 316: retire broken legacy functions
-- ----------------------------------------------------------------------------
-- Database: EOS.ERP (the single business database of the new system)
--
-- Context: three scalar functions inherited from the legacy database reference
-- columns that have since been renamed, and they fail the moment they run.
--
--   f_get_form_desc      reads WFFORM.FORM_IDX and TABLES.T_ID; WFFORM no
--                        longer has FORM_IDX (its columns are WF_M_IDX /
--                        FLOW_NAME), so calling it raises
--                        "列名 'T_ID' 无效".
--   f_get_user_desc      reads a renamed column EMP_NAME; calling it raises
--                        "列名 'EMP_NAME' 无效".
--   f_get_user_listdesc  calls f_get_user_desc (at offset 589 of its body), so
--                        it is broken through that dependency even though its
--                        own body holds no bad column reference.
--
-- Why they still exist: SQL Server resolves object names lazily, so a module
-- that is created while the referenced table does not yet exist is accepted
-- as-is; it only fails when executed. These were created before WFFORM was
-- reshaped, and have been dead ever since.
--
-- Verified before writing:
--   1. sp_refreshsqlmodule over every view / function / procedure in the
--      database reports exactly these two as failing (f_get_form_desc,
--      f_get_user_desc); f_get_user_listdesc is added by dependency;
--   2. no other module references f_get_form_desc or f_get_user_desc
--      (sys.sql_expression_dependencies), and nothing references
--      f_get_user_listdesc;
--   3. no application code references any of the three (repository-wide
--      search: hits are only the bootstrap seed and the export script's own
--      exclusion comment);
--   4. a scan of every text column in every user table finds no metadata row
--      mentioning them.
--
-- Scope:
--   1. drop the three functions, dependent first;
--   2. assert they are gone.
--
-- Removed rather than repaired: they were written for the legacy schema, the
-- features they served (legacy form description, legacy user-name list) are not
-- part of the current model, and nothing calls them.
-- ============================================================================

SET NOCOUNT ON;
GO

-- ---------------------------------------------------------------- 1. 退役
-- 顺序有意义：f_get_user_listdesc 依赖 f_get_user_desc，先删依赖方。
IF OBJECT_ID(N'dbo.f_get_user_listdesc', N'FN') IS NOT NULL
    DROP FUNCTION dbo.f_get_user_listdesc;
GO

IF OBJECT_ID(N'dbo.f_get_user_desc', N'FN') IS NOT NULL
    DROP FUNCTION dbo.f_get_user_desc;
GO

IF OBJECT_ID(N'dbo.f_get_form_desc', N'FN') IS NOT NULL
    DROP FUNCTION dbo.f_get_form_desc;
GO

-- ---------------------------------------------------------------- 2. 收口断言
IF OBJECT_ID(N'dbo.f_get_form_desc', N'FN') IS NOT NULL
    THROW 60011, '316: f_get_form_desc 退役未完成。', 1;
GO

IF OBJECT_ID(N'dbo.f_get_user_desc', N'FN') IS NOT NULL
    THROW 60012, '316: f_get_user_desc 退役未完成。', 1;
GO

IF OBJECT_ID(N'dbo.f_get_user_listdesc', N'FN') IS NOT NULL
    THROW 60013, '316: f_get_user_listdesc 退役未完成。', 1;
GO

-- 退役后整库不该再有坏死的模块。
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
        SET @msg = N'316: 退役后仍有坏死模块 ' + @bad + N' —— ' + ERROR_MESSAGE();
        THROW 60014, @msg, 1;
    END CATCH
    FETCH NEXT FROM cur INTO @bad;
END
CLOSE cur; DEALLOCATE cur;
GO
