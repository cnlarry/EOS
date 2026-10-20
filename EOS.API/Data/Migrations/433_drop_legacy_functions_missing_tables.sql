-- ============================================================================
-- EOS.ERP migration 433: retire legacy scalar functions that read tables this
--                         database never had
-- ----------------------------------------------------------------------------
-- Database: EOS.ERP (the single business database of the system)
--
-- Context: nineteen scalar functions read tables of the legacy schema that
-- were never ported into EOS.ERP. Migration 065 already recorded that those
-- tables (INVMA, SYSDK, COPMH, and their siblings) do not exist here; the
-- functions that read them were carried over anyway.
--
-- Why they went unnoticed: SQL Server resolves object names lazily, so a
-- function reading a missing table is accepted at creation and only fails when
-- executed. Nothing executes them, so nothing failed. The whole-database
-- sp_refreshsqlmodule sweep in migration 316 does not catch this case either;
-- sys.sql_expression_dependencies does, because a name that cannot be bound
-- leaves referenced_id NULL.
--
-- Verified before writing:
--   1. every table name these nineteen read is absent from the 497-table
--      schema (db/bootstrap/10_schema.sql);
--   2. no metadata row, application source, test or script names any of them
--      (repository-wide search: the only hit is the bootstrap definition);
--   3. f_get_basic_curr_value is added by dependency: it calls
--      f_get_basic_curr, which reads SYSDK;
--   4. no module outside this list references a member of it.
--
-- Scope:
--   1. refuse to run if a module outside the list still references one of the
--      nineteen;
--   2. drop them, dependent first;
--   3. assert they are gone, that no unresolved reference to the legacy table
--      names remains, and that no module in the database is broken.
--
-- Removed rather than repaired: they serve legacy lookups (currency, colour,
-- customer, product, depot, unit, payment and price condition descriptions)
-- whose tables are not part of the current model. Those descriptions are
-- resolved today through the metadata-driven query pipeline.
-- ============================================================================

SET NOCOUNT ON;
GO

-- ------------------------------------------------------------- 1. 引用检查
-- 清单外的模块若仍引用其中任何一个，说明存在活动调用路径，放弃退役。
DECLARE @targets TABLE ([name] sysname PRIMARY KEY);

INSERT INTO @targets ([name]) VALUES
    (N'f_get_basic_curr_value'),
    (N'f_get_basic_curr'),
    (N'f_get_account_name'),
    (N'f_get_basic_unit_value'),
    (N'f_get_cess_desc'),
    (N'f_get_color_desc'),
    (N'f_get_currency_desc'),
    (N'f_get_currency_rate'),
    (N'f_get_customer_desc'),
    (N'f_get_order_desc'),
    (N'f_get_part_desc'),
    (N'f_get_payment_condi_desc'),
    (N'f_get_price_condi_desc'),
    (N'f_get_pro_name'),
    (N'f_get_pro_unit'),
    (N'f_get_store_desc'),
    (N'f_get_stuff_desc'),
    (N'f_get_type_desc'),
    (N'f_get_unit_desc');

DECLARE @blocked nvarchar(400);

SELECT TOP (1)
       @blocked = N'433: 清单外的模块仍引用 '
                + QUOTENAME(r.[name]) + N' -> ' + QUOTENAME(t.[name])
                + N'，不能退役。'
  FROM @targets t
  JOIN sys.objects o ON o.[object_id] = OBJECT_ID(N'dbo.' + t.[name])
  JOIN sys.sql_expression_dependencies d ON d.referenced_id = o.[object_id]
  JOIN sys.objects r ON r.[object_id] = d.referencing_id
 WHERE NOT EXISTS (SELECT 1 FROM @targets x WHERE x.[name] = r.[name]);

IF @blocked IS NOT NULL
    THROW 60021, @blocked, 1;
GO

-- ------------------------------------------------------------------ 2. 退役
-- 顺序有意义：f_get_basic_curr_value 调用 f_get_basic_curr，先删依赖方。
IF OBJECT_ID(N'dbo.f_get_basic_curr_value') IS NOT NULL
    DROP FUNCTION dbo.f_get_basic_curr_value;
GO

IF OBJECT_ID(N'dbo.f_get_basic_curr') IS NOT NULL
    DROP FUNCTION dbo.f_get_basic_curr;
GO

IF OBJECT_ID(N'dbo.f_get_account_name') IS NOT NULL
    DROP FUNCTION dbo.f_get_account_name;
GO

IF OBJECT_ID(N'dbo.f_get_basic_unit_value') IS NOT NULL
    DROP FUNCTION dbo.f_get_basic_unit_value;
GO

IF OBJECT_ID(N'dbo.f_get_cess_desc') IS NOT NULL
    DROP FUNCTION dbo.f_get_cess_desc;
GO

IF OBJECT_ID(N'dbo.f_get_color_desc') IS NOT NULL
    DROP FUNCTION dbo.f_get_color_desc;
GO

IF OBJECT_ID(N'dbo.f_get_currency_desc') IS NOT NULL
    DROP FUNCTION dbo.f_get_currency_desc;
GO

IF OBJECT_ID(N'dbo.f_get_currency_rate') IS NOT NULL
    DROP FUNCTION dbo.f_get_currency_rate;
GO

IF OBJECT_ID(N'dbo.f_get_customer_desc') IS NOT NULL
    DROP FUNCTION dbo.f_get_customer_desc;
GO

IF OBJECT_ID(N'dbo.f_get_order_desc') IS NOT NULL
    DROP FUNCTION dbo.f_get_order_desc;
GO

IF OBJECT_ID(N'dbo.f_get_part_desc') IS NOT NULL
    DROP FUNCTION dbo.f_get_part_desc;
GO

IF OBJECT_ID(N'dbo.f_get_payment_condi_desc') IS NOT NULL
    DROP FUNCTION dbo.f_get_payment_condi_desc;
GO

IF OBJECT_ID(N'dbo.f_get_price_condi_desc') IS NOT NULL
    DROP FUNCTION dbo.f_get_price_condi_desc;
GO

IF OBJECT_ID(N'dbo.f_get_pro_name') IS NOT NULL
    DROP FUNCTION dbo.f_get_pro_name;
GO

IF OBJECT_ID(N'dbo.f_get_pro_unit') IS NOT NULL
    DROP FUNCTION dbo.f_get_pro_unit;
GO

IF OBJECT_ID(N'dbo.f_get_store_desc') IS NOT NULL
    DROP FUNCTION dbo.f_get_store_desc;
GO

IF OBJECT_ID(N'dbo.f_get_stuff_desc') IS NOT NULL
    DROP FUNCTION dbo.f_get_stuff_desc;
GO

IF OBJECT_ID(N'dbo.f_get_type_desc') IS NOT NULL
    DROP FUNCTION dbo.f_get_type_desc;
GO

IF OBJECT_ID(N'dbo.f_get_unit_desc') IS NOT NULL
    DROP FUNCTION dbo.f_get_unit_desc;
GO

-- ------------------------------------------------------------- 3. 收口断言
-- 表变量不跨批次，这里重新列出，保证断言只用本批次可见的对象。
DECLARE @still_there nvarchar(1000);

SELECT @still_there = STRING_AGG(CONVERT(nvarchar(max), QUOTENAME(v.[name])), N', ')
  FROM (VALUES
        (N'f_get_basic_curr_value'), (N'f_get_basic_curr'),
        (N'f_get_account_name'),     (N'f_get_basic_unit_value'),
        (N'f_get_cess_desc'),        (N'f_get_color_desc'),
        (N'f_get_currency_desc'),    (N'f_get_currency_rate'),
        (N'f_get_customer_desc'),    (N'f_get_order_desc'),
        (N'f_get_part_desc'),        (N'f_get_payment_condi_desc'),
        (N'f_get_price_condi_desc'), (N'f_get_pro_name'),
        (N'f_get_pro_unit'),         (N'f_get_store_desc'),
        (N'f_get_stuff_desc'),       (N'f_get_type_desc'),
        (N'f_get_unit_desc')
       ) v ([name])
 WHERE OBJECT_ID(N'dbo.' + v.[name]) IS NOT NULL;

IF @still_there IS NOT NULL
    THROW 60022, @still_there, 1;
GO

-- 不应再有模块引用这批从未存在的旧表。
DECLARE @legacy_refs nvarchar(2000);

SELECT @legacy_refs = STRING_AGG(
           CONVERT(nvarchar(max), QUOTENAME(r.[name]) + N' -> ' + d.referenced_entity_name),
           N'; ')
  FROM sys.sql_expression_dependencies d
  JOIN sys.objects r ON r.[object_id] = d.referencing_id
 WHERE d.referenced_id IS NULL
   AND UPPER(d.referenced_entity_name) IN (
           N'INVMA', N'INVSA', N'INVSB', N'INVSC', N'INVSF', N'INVSI',
           N'INVSJ', N'INVSK', N'INVSP', N'INVSQ',
           N'SYSDK', N'SYSDM', N'COPMD', N'COPMH',
           N'CAR_PROPERTY', N'COP_ACCOUNT_INFO');

IF @legacy_refs IS NOT NULL
    THROW 60023, @legacy_refs, 1;
GO

-- 退役后整库不该再有坏死的模块（与迁移 316 同一口径）。
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
        SET @msg = N'433: 退役后仍有坏死模块 ' + @bad + N' —— ' + ERROR_MESSAGE();
        THROW 60024, @msg, 1;
    END CATCH
    FETCH NEXT FROM cur INTO @bad;
END
CLOSE cur; DEALLOCATE cur;
GO
