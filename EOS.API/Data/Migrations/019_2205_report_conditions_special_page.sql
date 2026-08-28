-- ============================================================================
-- EOS.ERP 迁移 019：2205 报表过滤条件设置归类纯定制页（移除工作台承载）
-- ----------------------------------------------------------------------------
-- 库：EOS.ERP（全新系统的唯一业务数据库）
--
-- 背景（2026-08-28 用户拍板）：
--   2205（旧 RPT/SysqrDft.aspx）维护报表查看器条件面板的默认条件定义
--   （SYSQR_DA 主档 + SYSQR_DEFAULT 条件行），按模块维护、条件语句/数据源
--   为专用 DSL（F_TYPE 1-5），统一表单无法提供类型化编辑与写侧校验，
--   归类定制页 /admin/report-conditions（与 2201 报表排序汇总设置同族同形态）。
--   已从 UnifiedFormEditor.EnabledModuleIds 白名单剔除（随本次代码提交生效）。
--
-- 处理范围：
--   1. MODULES 2205：M_URL 指向定制页，清空 NEW_URL/MODI_URL，REMARK 登记决策；
--   2. WORKBENCH_MODULE_DIRTY / WORKBENCH_DEFINITION_SNAPSHOT 防御性清理。
--
-- 幂等：全程 WHERE 守卫，重复执行无副作用。
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
SET M_URL = N'/admin/report-conditions',
    NEW_URL = NULL,
    MODI_URL = NULL,
    REMARK = N'已归入纯定制页（2026-08-28 用户拍板）：报表过滤条件设置由 /admin/report-conditions 承载（SYSQR_DA + SYSQR_DEFAULT 按模块维护），已从统一表单白名单剔除并清空 NEW_URL/MODI_URL，不与工作台关联',
    LAST_UPDATE_BY = @MIG,
    LAST_UPDATE_DATE = @NOW
WHERE M_IDX = 2205;
PRINT N'[EOS-23] MODULES 2205 纯定制页化（M_URL=/admin/report-conditions，清空 NEW_URL/MODI_URL）：' + CAST(@@ROWCOUNT AS NVARCHAR(10)) + N' 行';

IF OBJECT_ID('dbo.WORKBENCH_MODULE_DIRTY','U') IS NOT NULL
BEGIN
    DELETE FROM dbo.WORKBENCH_MODULE_DIRTY WHERE MODULE_ID = 2205;
    PRINT N'[EOS-23] WORKBENCH_MODULE_DIRTY 2205 清理：' + CAST(@@ROWCOUNT AS NVARCHAR(10)) + N' 行';
END

IF OBJECT_ID('dbo.WORKBENCH_DEFINITION_SNAPSHOT','U') IS NOT NULL
BEGIN
    DELETE FROM dbo.WORKBENCH_DEFINITION_SNAPSHOT WHERE MODULE_ID = 2205;
    PRINT N'[EOS-23] WORKBENCH_DEFINITION_SNAPSHOT 2205 清理：' + CAST(@@ROWCOUNT AS NVARCHAR(10)) + N' 行';
END

COMMIT TRANSACTION;
PRINT N'[EOS-23] 2205 报表过滤条件设置归类纯定制页完成（页面/端点/路由代码随本次提交落地）。';
