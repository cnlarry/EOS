-- ============================================================================
-- EOS.ERP migration 438: rename V_SYSDL_SYSDN to a readable name
-- ----------------------------------------------------------------------------
-- Database: EOS.ERP (the single business database of the system)
--
-- V_SYSDL_SYSDN joins SYS_USER_ACCOUNT (formerly SYSDL) with SYS_EMPLOYEE
-- (formerly SYSDN) and exposes USER_ID, EMP_ID, EMP_NAME. Migration 436
-- renamed both source tables but had to protect the view name from the same
-- substitution (the name is built from the two old table names), so the view
-- still carries both codes.
--
-- Renamed: V_SYSDL_SYSDN -> V_SYS_USER_ACCOUNT_EMPLOYEE
--
-- Verified before writing: no application source, script or test names the
-- view; no database module references it; no data source, column configuration,
-- report parameter or validation rule points at it. The only references are the
-- metadata registration rows (one TABLES row, three FIELDS rows), updated here.
--
-- Scope:
--   1. rename the view;
--   2. move the metadata registration to the new name;
--   3. assert the old name is gone.
-- ============================================================================

SET NOCOUNT ON;
GO

DECLARE @guard nvarchar(400) = N'本脚本只能在 EOS.ERP 数据库内执行，当前库为 ' + DB_NAME() + N'。';
IF DB_NAME() <> N'EOS.ERP'
    THROW 50000, @guard, 1;
GO

IF OBJECT_ID(N'dbo.V_SYSDL_SYSDN', N'V') IS NOT NULL
    EXEC sp_rename @objname = N'dbo.V_SYSDL_SYSDN', @newname = N'V_SYS_USER_ACCOUNT_EMPLOYEE';
GO

UPDATE dbo.FIELDS
   SET T_ID = N'V_SYS_USER_ACCOUNT_EMPLOYEE'
 WHERE T_ID = N'V_SYSDL_SYSDN';

UPDATE dbo.TABLES
   SET T_ID = N'V_SYS_USER_ACCOUNT_EMPLOYEE'
 WHERE T_ID = N'V_SYSDL_SYSDN';
GO

DECLARE @left nvarchar(300);

SELECT @left = STRING_AGG(CONVERT(nvarchar(max), v.loc), N'; ')
  FROM (SELECT N'OBJECT' AS loc WHERE OBJECT_ID(N'dbo.V_SYSDL_SYSDN') IS NOT NULL
        UNION ALL
        SELECT N'TABLES' WHERE EXISTS (SELECT 1 FROM dbo.TABLES WHERE T_ID = N'V_SYSDL_SYSDN')
        UNION ALL
        SELECT N'FIELDS' WHERE EXISTS (SELECT 1 FROM dbo.FIELDS WHERE T_ID = N'V_SYSDL_SYSDN')
       ) v;

IF @left IS NOT NULL
    THROW 60071, @left, 1;

IF OBJECT_ID(N'dbo.V_SYS_USER_ACCOUNT_EMPLOYEE', N'V') IS NULL
    THROW 60072, N'438: 新视图名未落地。', 1;
GO
