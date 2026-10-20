-- ============================================================================
-- EOS.ERP migration 435: drop retired query / attachment tables
-- ----------------------------------------------------------------------------
-- Database: EOS.ERP (the single business database of the system)
--
-- Context: four tables remain from features this system does not have.
--
--   SYSQD_CONDITION   QueryDetail 条件行         49 rows
--   SYSQL_CONDITION   QueryAnalyser 条件行      376 rows
--   SYSQL_COND_DFT    QueryAnalyser 默认条件     28 rows
--   SYS_FILE          FileUpload 附件索引        51 rows (2009-06 ~ 2017-09)
--
-- Migration 030 dropped the four "user-stored SQL" tables (SYSQD / SYSQL /
-- LISTREPORT / LISTREPORT_CONDITION) under ADR-009 and kept the three
-- condition child tables on the stated grounds that FieldAdminRepository
-- cleans them up when a field is deleted. That reason is circular: the tables
-- survive because code deletes from them, and the code exists because the
-- tables survive. No read path has ever touched them, so the cleanup protects
-- nothing.
--
-- SYS_FILE's function is carried by ATTACHMENT (file bytes on disk, SHA-256,
-- authorized download). The 51 legacy rows index files that no screen can
-- reach any more.
--
-- Scope:
--   1. assert no metadata still points at the four tables;
--   2. delete their column configuration, field metadata and table metadata;
--   3. drop the tables;
--   4. assert they are gone.
-- ============================================================================

SET NOCOUNT ON;
GO

DECLARE @guard nvarchar(400) = N'本脚本只能在 EOS.ERP 数据库内执行，当前库为 ' + DB_NAME() + N'。';
IF DB_NAME() <> N'EOS.ERP'
    THROW 50000, @guard, 1;
GO

-- ------------------------------------------------------- 1. 残留引用检查
-- 报表条件模板、数据源与筛选结构若仍指向这四张表，说明还有配置在用它们。
DECLARE @dangling nvarchar(2000);

SELECT @dangling = STRING_AGG(CONVERT(nvarchar(max), v.loc), N'; ')
  FROM (
        SELECT 'FIELD_DATASOURCE.SOURCE_T_ID' AS loc
          FROM dbo.FIELD_DATASOURCE
         WHERE SOURCE_T_ID IN (N'SYS_FILE', N'SYSQD_CONDITION', N'SYSQL_CONDITION', N'SYSQL_COND_DFT')
        UNION ALL
        SELECT 'FIELD_DATASOURCE.FILTER_STRUCT'
          FROM dbo.FIELD_DATASOURCE
         WHERE FILTER_STRUCT LIKE N'%SYS_FILE%'
            OR FILTER_STRUCT LIKE N'%SYSQD_CONDITION%'
            OR FILTER_STRUCT LIKE N'%SYSQL_CONDITION%'
            OR FILTER_STRUCT LIKE N'%SYSQL_COND_DFT%'
        UNION ALL
        SELECT 'SYSQR_DEFAULT.FILTER_TEMPLATE'
          FROM dbo.SYSQR_DEFAULT
         WHERE FILTER_TEMPLATE LIKE N'%SYS_FILE%'
            OR FILTER_TEMPLATE LIKE N'%SYSQD_CONDITION%'
            OR FILTER_TEMPLATE LIKE N'%SYSQL_CONDITION%'
            OR FILTER_TEMPLATE LIKE N'%SYSQL_COND_DFT%'
        UNION ALL
        SELECT 'MODULES.REMARK'
          FROM dbo.MODULES
         WHERE REMARK LIKE N'%SYS_FILE%'
            OR REMARK LIKE N'%SYSQD_CONDITION%'
            OR REMARK LIKE N'%SYSQL_CONDITION%'
            OR REMARK LIKE N'%SYSQL_COND_DFT%'
       ) v;

IF @dangling IS NOT NULL
BEGIN
    DECLARE @msg nvarchar(400) = N'435: 仍有配置引用待删表 —— ' + @dangling;
    THROW 60041, @msg, 1;
END
GO

-- ------------------------------------------------------------- 2. 清元数据
-- 默认列配置先删：它们指向的条件表随后一起消失。
DELETE FROM dbo.SYSQL_DEFAULT
 WHERE T_ID IN (N'SYS_FILE', N'SYSQD_CONDITION', N'SYSQL_CONDITION', N'SYSQL_COND_DFT')
    OR T_ID_R IN (N'SYS_FILE', N'SYSQD_CONDITION', N'SYSQL_CONDITION', N'SYSQL_COND_DFT');

DELETE FROM dbo.SYSQL_FIELDS
 WHERE T_ID IN (N'SYS_FILE', N'SYSQD_CONDITION', N'SYSQL_CONDITION', N'SYSQL_COND_DFT')
    OR T_ID_R IN (N'SYS_FILE', N'SYSQD_CONDITION', N'SYSQL_CONDITION', N'SYSQL_COND_DFT');

-- FIELD_DATASOURCE 经 (T_ID, F_ID) 级联删除，这里显式清一遍以免依赖级联方向。
DELETE FROM dbo.FIELD_DATASOURCE
 WHERE T_ID IN (N'SYS_FILE', N'SYSQD_CONDITION', N'SYSQL_CONDITION', N'SYSQL_COND_DFT');

DELETE FROM dbo.FIELDS
 WHERE T_ID IN (N'SYS_FILE', N'SYSQD_CONDITION', N'SYSQL_CONDITION', N'SYSQL_COND_DFT');

DELETE FROM dbo.TABLES
 WHERE T_ID IN (N'SYS_FILE', N'SYSQD_CONDITION', N'SYSQL_CONDITION', N'SYSQL_COND_DFT');
GO

-- --------------------------------------------------------------- 3. 删表
IF OBJECT_ID(N'dbo.SYSQD_CONDITION', N'U') IS NOT NULL DROP TABLE dbo.SYSQD_CONDITION;
IF OBJECT_ID(N'dbo.SYSQL_CONDITION', N'U') IS NOT NULL DROP TABLE dbo.SYSQL_CONDITION;
IF OBJECT_ID(N'dbo.SYSQL_COND_DFT', N'U') IS NOT NULL DROP TABLE dbo.SYSQL_COND_DFT;
IF OBJECT_ID(N'dbo.SYS_FILE', N'U') IS NOT NULL DROP TABLE dbo.SYS_FILE;
GO

-- ------------------------------------------------------------ 4. 收口断言
DECLARE @left nvarchar(500);

SELECT @left = STRING_AGG(CONVERT(nvarchar(max), QUOTENAME(v.[name])), N', ')
  FROM (VALUES (N'SYSQD_CONDITION'), (N'SYSQL_CONDITION'), (N'SYSQL_COND_DFT'), (N'SYS_FILE')) v ([name])
 WHERE OBJECT_ID(N'dbo.' + v.[name], N'U') IS NOT NULL;

IF @left IS NOT NULL
    THROW 60042, @left, 1;

DECLARE @meta_left nvarchar(500);

SELECT @meta_left = STRING_AGG(CONVERT(nvarchar(max), QUOTENAME(v.[name])), N', ')
  FROM (VALUES (N'SYSQD_CONDITION'), (N'SYSQL_CONDITION'), (N'SYSQL_COND_DFT'), (N'SYS_FILE')) v ([name])
 WHERE EXISTS (SELECT 1 FROM dbo.TABLES t WHERE t.T_ID = v.[name])
    OR EXISTS (SELECT 1 FROM dbo.FIELDS f WHERE f.T_ID = v.[name]);

IF @meta_left IS NOT NULL
BEGIN
    DECLARE @meta_msg nvarchar(400) = N'435: 元数据仍有残留 —— ' + @meta_left;
    THROW 60043, @meta_msg, 1;
END
GO
