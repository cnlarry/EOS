-- ============================================================================
-- EOS.ERP 迁移 011：流程设计器（2101）/流程监控（2103）现代路由落地
-- ----------------------------------------------------------------------------
-- 库：EOS.ERP（全新系统的唯一业务数据库）
--
-- 背景（2026-08-27 工作流商用级收口）：
--   2101 表单流程设计、2103 流程监控此前 M_URL 为旧 Web Forms 路径
--   （~/WorkFlow/WF_Design.aspx 等，非 '/' 开头），现代侧回退 /legacy/modules/* 占位页。
--   本次新增现代页面 /workflow/design（流程设计器）与 /workflow/monitor（流程监控），
--   把 2101/2103 的 M_URL 指向现代路由（2102 已于 update.sql 现代化为 /my-tasks）。
--
-- 幂等：仅当 M_URL 为空或非现代（不以 '/' 开头）时更新，重复执行无副作用；
--   管理员后续在菜单管理自行改动的值不被覆盖。
-- 命名约定（AGENTS.md 强制）：对象名全大写。
-- ============================================================================

SET NOCOUNT ON;
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;

DECLARE @GUARD_MESSAGE NVARCHAR(400) = N'本脚本只能在 EOS.ERP 数据库内执行，当前库为 ' + DB_NAME() + N'。';
IF DB_NAME() <> N'EOS.ERP'
    THROW 50000, @GUARD_MESSAGE, 1;

BEGIN TRANSACTION;

-- 1. 2101 表单流程设计 → /workflow/design（值不同才更新，幂等）
UPDATE dbo.MODULES
SET M_URL = N'/workflow/design',
    REMARK = N'流程设计器（现代页面 /workflow/design）：维护各单据模块的审批流程定义',
    LAST_UPDATE_BY = N'EOS-WF-ADMIN',
    LAST_UPDATE_DATE = GETDATE()
WHERE M_IDX = 2101
  AND LTRIM(RTRIM(ISNULL(M_URL, ''))) <> N'/workflow/design';
PRINT N'[EOS-WF-ADMIN] 2101 M_URL 更新：' + CAST(@@ROWCOUNT AS NVARCHAR(10)) + N' 行';

-- 2. 2103 流程监控 → /workflow/monitor（值不同才更新，幂等）
UPDATE dbo.MODULES
SET M_URL = N'/workflow/monitor',
    REMARK = N'流程监控（现代页面 /workflow/monitor）：在途/已完成/已撤回流程实例全局视图',
    LAST_UPDATE_BY = N'EOS-WF-ADMIN',
    LAST_UPDATE_DATE = GETDATE()
WHERE M_IDX = 2103
  AND LTRIM(RTRIM(ISNULL(M_URL, ''))) <> N'/workflow/monitor';
PRINT N'[EOS-WF-ADMIN] 2103 M_URL 更新：' + CAST(@@ROWCOUNT AS NVARCHAR(10)) + N' 行';

COMMIT TRANSACTION;
PRINT N'[EOS-WF-ADMIN] 流程设计器/流程监控路由配置完成。';
