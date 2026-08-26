-- ============================================================================
-- EOS.ERP 迁移 008：工作台浏览器路由前缀 /document-workbench → /workbench
-- ----------------------------------------------------------------------------
-- 库：EOS.ERP（全新系统的唯一业务数据库）
--
-- 背景（2026-08-26 用户拍板，A 档 URL 重构，计划 docs/plans/工作台URL重构.md）：
--   浏览器路由前缀由 /document-workbench 收敛为 /workbench（更短、贴近单据心智），
--   记录键同时从 query 迁入路径段（/workbench/{moduleId}/view/{主键段...}）。
--   本迁移只改 MODULES 元数据（M_URL 承载页、NEW_URL/MODI_URL 动作模板），
--   使菜单导航/表单路由与新前端路由一致；API 路径 /api/v1/document-workbench 不变
--   （非用户可见，控制器路由与前端 apiClient 调用保持原样）。
--
-- 处理范围：
--   1. MODULES.M_URL：'/document-workbench' → '/workbench'（282 行，工作台承载模块）；
--   2. MODULES.MODI_URL：模板 '/document-workbench/{moduleId}/edit' → '/workbench/...'（264 行）；
--   3. MODULES.NEW_URL / HELP_URL：同样 REPLACE（当前无命中，幂等兜底）。
--
-- 幂等：REPLACE 天然幂等；LAST_UPDATE_BY/DATE 记录执行时间。
-- 命名约定（AGENTS.md 强制）：对象名全大写。
-- ============================================================================

SET NOCOUNT ON;
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;

DECLARE @GUARD_MESSAGE NVARCHAR(400) = N'本脚本只能在 EOS.ERP 数据库内执行，当前库为 ' + DB_NAME() + N'。';
IF DB_NAME() <> N'EOS.ERP'
    THROW 50000, @GUARD_MESSAGE, 1;

BEGIN TRANSACTION;

DECLARE @MIG NVARCHAR(20) = N'EOS-21';
DECLARE @NOW DATETIME = GETDATE();

UPDATE dbo.MODULES
SET M_URL = REPLACE(M_URL, '/document-workbench', '/workbench'),
    LAST_UPDATE_BY = @MIG,
    LAST_UPDATE_DATE = @NOW
WHERE ISNULL(M_URL, '') LIKE '%/document-workbench%';
PRINT N'[EOS-21] MODULES.M_URL /document-workbench → /workbench：' + CAST(@@ROWCOUNT AS NVARCHAR(10)) + N' 行';

UPDATE dbo.MODULES
SET MODI_URL = REPLACE(MODI_URL, '/document-workbench', '/workbench'),
    LAST_UPDATE_BY = @MIG,
    LAST_UPDATE_DATE = @NOW
WHERE ISNULL(MODI_URL, '') LIKE '%/document-workbench%';
PRINT N'[EOS-21] MODULES.MODI_URL /document-workbench → /workbench：' + CAST(@@ROWCOUNT AS NVARCHAR(10)) + N' 行';

UPDATE dbo.MODULES
SET NEW_URL = REPLACE(NEW_URL, '/document-workbench', '/workbench'),
    LAST_UPDATE_BY = @MIG,
    LAST_UPDATE_DATE = @NOW
WHERE ISNULL(NEW_URL, '') LIKE '%/document-workbench%';
PRINT N'[EOS-21] MODULES.NEW_URL /document-workbench → /workbench：' + CAST(@@ROWCOUNT AS NVARCHAR(10)) + N' 行';

UPDATE dbo.MODULES
SET HELP_URL = REPLACE(HELP_URL, '/document-workbench', '/workbench'),
    LAST_UPDATE_BY = @MIG,
    LAST_UPDATE_DATE = @NOW
WHERE ISNULL(HELP_URL, '') LIKE '%/document-workbench%';
PRINT N'[EOS-21] MODULES.HELP_URL /document-workbench → /workbench：' + CAST(@@ROWCOUNT AS NVARCHAR(10)) + N' 行';

-- 2. Definition 快照 JSON 同步刷新（ADR-005 运行时快照缓存了 NewUrl/ModiUrl 等派生自
--    旧 M_URL 元数据的路由；不刷新则统一表单「新增/编辑」会导航到不存在的旧浏览器路径）。
IF OBJECT_ID('dbo.WORKBENCH_DEFINITION_SNAPSHOT', 'U') IS NOT NULL
BEGIN
    UPDATE dbo.WORKBENCH_DEFINITION_SNAPSHOT
    SET DEFINITION_JSON = REPLACE(CAST(DEFINITION_JSON AS NVARCHAR(MAX)), '/document-workbench', '/workbench')
    WHERE CAST(DEFINITION_JSON AS NVARCHAR(MAX)) LIKE '%/document-workbench%';
    PRINT N'[EOS-21] WORKBENCH_DEFINITION_SNAPSHOT 快照 JSON 刷新：' + CAST(@@ROWCOUNT AS NVARCHAR(10)) + N' 行';
END

COMMIT TRANSACTION;
PRINT N'[EOS-21] 工作台路由前缀迁移完成（M_URL/MODI_URL/NEW_URL/HELP_URL + Definition 快照）。';