-- ============================================================================
-- EOS.ERP migration 012: 2305 group admin reclassified as a pure custom page
-- ----------------------------------------------------------------------------
-- Database: EOS.ERP (the single business database of the new system)
--
-- Context: 2305 group management is a custom page with special business rules
-- that the unified form workbench cannot handle. Group master CRUD (add/edit/delete)
-- is via modals on /admin/groups, and group permissions/report permissions/members
-- are full sub-pages. This migration clears NEW_URL/MODI_URL (prevents accidental
-- navigation to the unified form), cleans up Definition snapshots and dirty markers
-- (2305 is already removed from the unified form whitelist).
--
-- Scope:
--   1. MODULES 2305: clear NEW_URL/MODI_URL, record decision in REMARK;
--   2. Clean WORKBENCH_MODULE_DIRTY / WORKBENCH_DEFINITION_SNAPSHOT for 2305.
--
-- Idempotent: guarded by WHERE clauses.
-- Naming convention: all object names uppercase.
-- ============================================================================

SET NOCOUNT ON;
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;

DECLARE @GUARD_MESSAGE NVARCHAR(400) = N'本脚本只能在 EOS.ERP 数据库内执行，当前库为 ' + DB_NAME() + N'。';
IF DB_NAME() <> N'EOS.ERP'
    THROW 50000, @GUARD_MESSAGE, 1;

BEGIN TRANSACTION;

DECLARE @MIG NVARCHAR(20) = N'EOS-23';
DECLARE @NOW DATETIME = GETDATE();

-- 1. MODULES 2305 归类纯定制页：清空统一表单动作路由，登记决策
UPDATE dbo.MODULES
SET NEW_URL = NULL,
    MODI_URL = NULL,
    REMARK = N'已归入纯定制页（EOS-23/2026-08-27 用户拍板）：用户组管理由 /admin/groups 承载，组主档新增/编辑弹窗化、删除带成员守卫（旧系统不清理成员，新系统拒绝删有关联成员的组），组权限/报表权限/成员为完整页面；已从统一表单白名单剔除并清空 NEW_URL/MODI_URL，不再引导主档工作台',
    LAST_UPDATE_BY = @MIG,
    LAST_UPDATE_DATE = @NOW
WHERE M_IDX = 2305;
PRINT N'[EOS-23] MODULES 2305 纯定制页化（清空 NEW_URL/MODI_URL）：' + CAST(@@ROWCOUNT AS NVARCHAR(10)) + N' 行';

-- 2. 清理工作台脏标记 / Definition 快照（2305 已剔除白名单，不再维护快照）
IF OBJECT_ID('dbo.WORKBENCH_MODULE_DIRTY','U') IS NOT NULL
BEGIN
    DELETE FROM dbo.WORKBENCH_MODULE_DIRTY WHERE MODULE_ID = 2305;
    PRINT N'[EOS-23] WORKBENCH_MODULE_DIRTY 2305 清理：' + CAST(@@ROWCOUNT AS NVARCHAR(10)) + N' 行';
END

IF OBJECT_ID('dbo.WORKBENCH_DEFINITION_SNAPSHOT','U') IS NOT NULL
BEGIN
    DELETE FROM dbo.WORKBENCH_DEFINITION_SNAPSHOT WHERE MODULE_ID = 2305;
    PRINT N'[EOS-23] WORKBENCH_DEFINITION_SNAPSHOT 2305 清理：' + CAST(@@ROWCOUNT AS NVARCHAR(10)) + N' 行';
END

COMMIT TRANSACTION;
PRINT N'[EOS-23] 2305 用户组管理归类纯定制页完成。';
