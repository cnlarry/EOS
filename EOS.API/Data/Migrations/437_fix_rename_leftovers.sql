-- ============================================================================
-- EOS.ERP migration 437: finish the framework-table rename
-- ----------------------------------------------------------------------------
-- Database: EOS.ERP (the single business database of the system)
--
-- Migration 436 renamed the seventeen framework tables and carried the rename
-- through TABLES / FIELDS / FIELD_DATASOURCE / MODULES / QUERY_RELATION /
-- VIRTUAL_EXP / PARAM_STRUCT / GROUP_FIELDS / FILTER_TEMPLATE, and rebuilt the
-- twelve views and functions that read them. Six places that also hold table
-- names were missed; every one of them leaves metadata pointing at a name that
-- no longer exists:
--
--   MODULE_FORM_LAYOUT.T_ID            17 rows still say SYSDN
--   TABLES.FK_T_ID_1                    2 rows say SYSDD / SYSDH
--   SYS_REPORT_PARAMETER.F_ID           2 rows say SYSDN.DEPT_ID / SYSDN.EMP_ID
--   FIELD_DATASOURCE.SOURCE_DESC        1 row says SYSQR_TYPE
--   SYS_DEFAULT_COLUMN                  6 rows point at SYSDD_REPORT / SYSDH_REPORT
--   WORKBENCH_DEFINITION_SNAPSHOT      already marked dirty by 436; republication
--                                      regenerates both JSON documents
--
-- SYSDD_REPORT / SYSDH_REPORT were the report-rights override tables retired
-- when REPORT_USER_STATE took over the report centre state; their column
-- configuration rows point at tables that do not exist and are removed here.
--
-- Deliberately untouched:
--   REPORT.REPORT_ID / REPORT_SORT.REPORT_ID / SYS_USER_REPORT_PRINT.REPORT_ID
--     reportId is a primary key and immutable by contract (AGENTS.md 报表域);
--     ids such as SYS_Sysdl carry history and are not table references.
--   TABLES.T_ID = FIELDS.T_ID = 'V_SYSDL_SYSDN'
--     that is a view name, not a table. Renaming views is a separate concern;
--     the reference stays valid because the view itself was not renamed.
-- ============================================================================

SET NOCOUNT ON;
GO

DECLARE @guard nvarchar(400) = N'本脚本只能在 EOS.ERP 数据库内执行，当前库为 ' + DB_NAME() + N'。';
IF DB_NAME() <> N'EOS.ERP'
    THROW 50000, @guard, 1;
GO

-- ------------------------------------------------------- 1. 版式与关联登记
UPDATE dbo.MODULE_FORM_LAYOUT
   SET T_ID = N'SYS_EMPLOYEE'
 WHERE T_ID = N'SYSDN';
GO

UPDATE dbo.TABLES
   SET FK_T_ID_1 = N'SYS_USER_MODULE_PERMISSION'
 WHERE FK_T_ID_1 = N'SYSDD';

UPDATE dbo.TABLES
   SET FK_T_ID_1 = N'SYS_GROUP_MODULE_PERMISSION'
 WHERE FK_T_ID_1 = N'SYSDH';
GO

-- ----------------------------------------------------------- 2. 条件与描述
UPDATE dbo.SYS_REPORT_PARAMETER
   SET F_ID = N'SYS_EMPLOYEE' + SUBSTRING(F_ID, LEN(N'SYSDN') + 1, 400)
 WHERE F_ID LIKE N'SYSDN.%';
GO

UPDATE dbo.FIELD_DATASOURCE
   SET SOURCE_DESC = N'SYS_REPORT_PARAMETER_TYPE'
 WHERE SOURCE_DESC = N'SYSQR_TYPE';
GO

-- ------------------------------------------- 3. 指向已退役报表权限表的列配置
DELETE FROM dbo.SYS_DEFAULT_COLUMN
 WHERE T_ID IN (N'SYSDD_REPORT', N'SYSDH_REPORT')
    OR T_ID_R IN (N'SYSDD_REPORT', N'SYSDH_REPORT');

DELETE FROM dbo.SYS_USER_COLUMN
 WHERE T_ID IN (N'SYSDD_REPORT', N'SYSDH_REPORT')
    OR T_ID_R IN (N'SYSDD_REPORT', N'SYSDH_REPORT');
GO

-- ------------------------------------------------------------ 4. 收口断言
DECLARE @left nvarchar(1000);

SELECT @left = STRING_AGG(CONVERT(nvarchar(max), v.loc), N'; ')
  FROM (
        SELECT 'MODULE_FORM_LAYOUT.T_ID' AS loc
          FROM dbo.MODULE_FORM_LAYOUT
         WHERE T_ID IN (N'SYSDN', N'SYSDL', N'SYSDD', N'SYSDH', N'SYSDG', N'SYSSS', N'SYSDG_USER')
        UNION ALL
        SELECT 'TABLES.FK_T_ID_1'
          FROM dbo.TABLES
         WHERE FK_T_ID_1 IN (N'SYSDN', N'SYSDL', N'SYSDD', N'SYSDH', N'SYSDG', N'SYSSS')
        UNION ALL
        SELECT 'SYS_REPORT_PARAMETER.F_ID'
          FROM dbo.SYS_REPORT_PARAMETER
         WHERE F_ID LIKE N'SYSDN.%' OR F_ID LIKE N'SYSDL.%'
        UNION ALL
        SELECT 'FIELD_DATASOURCE.SOURCE_DESC'
          FROM dbo.FIELD_DATASOURCE
         WHERE SOURCE_DESC IN (N'SYSQR_TYPE', N'SYSQR_DEFAULT', N'SYSQR_USER', N'SYSQR')
        UNION ALL
        SELECT 'SYS_DEFAULT_COLUMN(dead)'
          FROM dbo.SYS_DEFAULT_COLUMN
         WHERE T_ID IN (N'SYSDD_REPORT', N'SYSDH_REPORT')
            OR T_ID_R IN (N'SYSDD_REPORT', N'SYSDH_REPORT')
       ) v;

IF @left IS NOT NULL
    THROW 60061, @left, 1;
GO
