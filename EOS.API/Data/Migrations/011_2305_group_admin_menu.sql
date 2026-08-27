-- ============================================================================
-- EOS.ERP 迁移 011：2305 用户组管理菜单回归专用定制页 /admin/groups
-- ----------------------------------------------------------------------------
-- 库：EOS.ERP（全新系统的唯一业务数据库）
--
-- 背景（2026-08-27 定制页逐页验收，EOS-23）：
--   M92（2026-08-16）决策——2305 用户组管理菜单从工作台（SYSDG 统一表单 CRUD
--   保留）切换到专用页面 /admin/groups（组权限/报表权限/成员），页面内提供
--   「主档」入口延续组主档 CRUD；ModuleRouteValidator 精确路径白名单已含
--   /admin/groups。开发库未吃到该数据变更（M_URL 仍为 /workbench），定制页
--   /admin/groups 无菜单入口，与本清单（docs/plans/定制页面清单.md）不符。
--
-- 处理范围：
--   1. MODULES 2305：M_URL 收敛到 /admin/groups，REMARK 登记决策结论；
--   2. MODI_URL 保留 /workbench/{moduleId}/edit（主档工作台 /workbench/2305
--      编辑仍可用）；NEW_URL 保持为空（定制页无新增按钮，主档工作台提供）。
--
-- 幂等：以 WHERE 守卫（仅当 M_URL 非目标值时更新），重复执行无副作用。
-- 命名约定（AGENTS.md 强制）：对象名全大写。
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

UPDATE dbo.MODULES
SET M_URL = N'/admin/groups',
    REMARK = N'已归入定制页（M92/2026-08-16 决策，EOS-23/2026-08-27 迁移落库）：用户组管理菜单指向专用页 /admin/groups（组权限/报表权限/成员），组主档 CRUD 保留统一表单（页面内「主档」入口 /workbench/2305，MODI_URL 保留编辑模板）',
    LAST_UPDATE_BY = @MIG,
    LAST_UPDATE_DATE = @NOW
WHERE M_IDX = 2305
  AND LTRIM(RTRIM(ISNULL(M_URL, ''))) <> N'/admin/groups';
PRINT N'[EOS-23] MODULES 2305 M_URL → /admin/groups：' + CAST(@@ROWCOUNT AS NVARCHAR(10)) + N' 行';

COMMIT TRANSACTION;
PRINT N'[EOS-23] 2305 用户组管理菜单回归定制页完成。';
