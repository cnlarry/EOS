-- ============================================================================
-- EOS.ERP 迁移 015：2306 用户权限设定归类纯定制页（移除工作台承载）
-- ----------------------------------------------------------------------------
-- 库：EOS.ERP（全新系统的唯一业务数据库）
--
-- 背景（2026-08-27 定制页逐页验收，EOS-23）：
--   用户拍板：2306 是定制页，不应与工作台关联。M_URL 已为 /admin/users；
--   本迁移清空 NEW_URL/MODI_URL（MODI_URL 原为 /admin/users 指向列表自身，冗余），
--   清理 Definition 快照与脏标记（2306 已从 UnifiedFormEditor.EnabledModuleIds
--   白名单剔除），并在 REMARK 登记决策。
--
-- 处理范围：
--   1. MODULES 2306：NEW_URL/MODI_URL 清空，REMARK 登记决策；
--   2. WORKBENCH_MODULE_DIRTY / WORKBENCH_DEFINITION_SNAPSHOT 按 2306 清理。
--
-- 幂等：全程以 WHERE 守卫，重复执行无副作用。
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
SET NEW_URL = NULL,
    MODI_URL = NULL,
    REMARK = N'已归入纯定制页（EOS-23/2026-08-27 用户拍板）：用户权限设定由 /admin/users 承载，用户权限/报表权限为完整子页面，所属组为弹窗；已从统一表单白名单剔除并清空 NEW_URL/MODI_URL，不与工作台关联',
    LAST_UPDATE_BY = @MIG,
    LAST_UPDATE_DATE = @NOW
WHERE M_IDX = 2306;
PRINT N'[EOS-23] MODULES 2306 纯定制页化（清空 NEW_URL/MODI_URL）：' + CAST(@@ROWCOUNT AS NVARCHAR(10)) + N' 行';

IF OBJECT_ID('dbo.WORKBENCH_MODULE_DIRTY','U') IS NOT NULL
BEGIN
    DELETE FROM dbo.WORKBENCH_MODULE_DIRTY WHERE MODULE_ID = 2306;
    PRINT N'[EOS-23] WORKBENCH_MODULE_DIRTY 2306 清理：' + CAST(@@ROWCOUNT AS NVARCHAR(10)) + N' 行';
END

IF OBJECT_ID('dbo.WORKBENCH_DEFINITION_SNAPSHOT','U') IS NOT NULL
BEGIN
    DELETE FROM dbo.WORKBENCH_DEFINITION_SNAPSHOT WHERE MODULE_ID = 2306;
    PRINT N'[EOS-23] WORKBENCH_DEFINITION_SNAPSHOT 2306 清理：' + CAST(@@ROWCOUNT AS NVARCHAR(10)) + N' 行';
END

COMMIT TRANSACTION;
PRINT N'[EOS-23] 2306 用户权限设定归类纯定制页完成。';
