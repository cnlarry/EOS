-- ============================================================================
-- EOS.ERP migration 025: 2205 report conditions management page offline
-- ----------------------------------------------------------------------------
-- Database: EOS.ERP (the single business database of the new system)
--
-- Context: 2205 (formerly RPT/SysqrDft.aspx) maintained default condition definitions
-- for the report viewer's condition panel. The five report management pages (2201-2205)
-- are all taken offline: report definitions, layouts and conditions move to developer
-- assets (under Git). Starting from P4, condition storage uses structured FILTER_TEMPLATE
-- (migration 024); runtime reading still consumes SYSQR_DEFAULT, and user values
-- SYSQR_USER remain as runtime state.
--
-- Scope (contrast with 016 where data tables were deleted; here data tables are kept):
--   1. MODULES 2205: M_TAG=0 to hide the menu node, clear M_URL/NEW_URL/MODI_URL;
--   2. WORKBENCH_MODULE_DIRTY / WORKBENCH_DEFINITION_SNAPSHOT: defensive cleanup;
--   3. SYSDD/SYSDH/SYSQR_DA/SYSQR_DEFAULT/SYSQR_USER: preserved (runtime + permission units);
--   4. Audit/history left intact.
-- Idempotent: guarded by EXISTS/IF. Naming convention: all uppercase.
-- ============================================================================

SET NOCOUNT ON;

DECLARE @GUARD_MESSAGE NVARCHAR(400) = N'本脚本只能在 EOS.ERP 数据库内执行，当前库为 ' + DB_NAME() + N'。';
IF DB_NAME() <> N'EOS.ERP'
    THROW 50000, @GUARD_MESSAGE, 1;

BEGIN TRANSACTION;

-- 1. MODULES 2205：隐藏菜单节点 + 清空页面路由（M_URL 指向已删除的 /admin/report-conditions）
UPDATE dbo.MODULES
SET M_TAG = 0,
    M_URL = NULL,
    NEW_URL = NULL,
    MODI_URL = NULL,
    REMARK = LTRIM(RTRIM(ISNULL(REMARK,''))) + N'；ADR-009 §11 下线（2026-08-30）：报表过滤条件设置管理页退役，条件定义转开发态资产（FILTER_TEMPLATE，迁移 024），SYSQR_DA/SYSQR_DEFAULT 保留供运行时读取'
WHERE M_IDX = 2205;

-- 2. WORKBENCH 快照/脏标记防御性清理（2205 已无工作台承载）
DELETE FROM dbo.WORKBENCH_MODULE_DIRTY WHERE MODULE_ID = 2205;
DELETE FROM dbo.WORKBENCH_DEFINITION_SNAPSHOT WHERE MODULE_ID = 2205;

-- 3. 审计计数留档
SELECT '2205 下线' AS KIND, COUNT(*) AS CNT FROM dbo.MODULES WHERE M_IDX = 2205 AND ISNULL(M_TAG,0) = 0;

COMMIT TRANSACTION;