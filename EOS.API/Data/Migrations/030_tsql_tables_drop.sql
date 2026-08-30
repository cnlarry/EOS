-- ============================================================================
-- EOS.ERP 迁移 030：ADR-009 §4/P3 处置——物理删除"用户存 SQL"四表
-- ----------------------------------------------------------------------------
-- 背景（2026-08-30，用户拍板）：ADR-009 §3.3 三张同构"用户存 SQL"表
--   SYSQD / SYSQL / LISTREPORT（PK 均含 USER_ID+T_ID，列均为 T_SQL/T_CONDITION）
--   与 LISTREPORT 的 WHERE 语法树子表 LISTREPORT_CONDITION 一并物理删除。
--   审计（logs/ADR-009-P3-tsql-tables-audit.md）：现代系统（EOS.API/EOS.Web）
--   零消费；旧系统消费页（QueryDetail/QueryAnalyser/RptList）在现代重构中均未移植，
--   ADR §4 决策"不再新增用户运行时存 SQL"，处置方向 = 物理删除。
--
-- 删除范围：
--   SYSQD / SYSQL / LISTREPORT / LISTREPORT_CONDITION
--
-- 保留（活表，工作台查询/列配置在用，不得删除）：
--   SYSQD_CONDITION / SYSQL_FIELDS / SYSQL_CONDITION / SYSQL_COND_DFT / SYSQL_DEFAULT
--   （FieldAdminRepository 字段删除清理/引用计数依赖这些表）
--
-- 遗留登记：
--   旧系统存储过程 xp_user_listrpt_fields 引用 LISTREPORT_CONDITION（现代零调用，
--   旧库小写命名遗留对象暂不处理，见技术债登记；删除后该 SP 成为悬空引用，
--   执行即报错，现代系统不调用，不影响运行）。
--
-- 幂等性：全程 IF OBJECT_ID 守卫——表已不存在则跳过，DbUp 重复执行无副作用。
-- ============================================================================

SET NOCOUNT ON;
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;

DECLARE @GUARD_MESSAGE NVARCHAR(400) = N'本脚本只能在 EOS.ERP 数据库内执行，当前库为 ' + DB_NAME() + N'。';
IF DB_NAME() <> N'EOS.ERP'
    THROW 50000, @GUARD_MESSAGE, 1;

BEGIN TRANSACTION;

-- 1. 物理删除四表（存在才删）
IF OBJECT_ID('dbo.LISTREPORT_CONDITION') IS NOT NULL DROP TABLE dbo.LISTREPORT_CONDITION;
IF OBJECT_ID('dbo.LISTREPORT') IS NOT NULL DROP TABLE dbo.LISTREPORT;
IF OBJECT_ID('dbo.SYSQL') IS NOT NULL DROP TABLE dbo.SYSQL;
IF OBJECT_ID('dbo.SYSQD') IS NOT NULL DROP TABLE dbo.SYSQD;

-- 2. 审计计数
SELECT 'LISTREPORT_CONDITION' AS OBJECT_NAME, COUNT(*) AS REMAINING
FROM sys.tables WHERE name = 'LISTREPORT_CONDITION';
SELECT 'LISTREPORT' AS OBJECT_NAME, COUNT(*) AS REMAINING
FROM sys.tables WHERE name = 'LISTREPORT';
SELECT 'SYSQL' AS OBJECT_NAME, COUNT(*) AS REMAINING
FROM sys.tables WHERE name = 'SYSQL';
SELECT 'SYSQD' AS OBJECT_NAME, COUNT(*) AS REMAINING
FROM sys.tables WHERE name = 'SYSQD';

COMMIT TRANSACTION;
