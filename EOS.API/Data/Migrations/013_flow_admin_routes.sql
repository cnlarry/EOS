-- ============================================================================
-- EOS.ERP migration 013: flow designer (2101) / flow monitor (2103) modern routes
-- ----------------------------------------------------------------------------
-- Database: EOS.ERP (the single business database of the new system)
--
-- Context: 2101 (flow designer) and 2103 (flow monitor) had old Web Forms paths
-- that fell back to /historic/modules/* placeholder pages. This migration adds-- modern pages /workflow/design and /workflow/monitor, and points M_URL to them.
-- 2102 was already updated to /my-tasks in a previous migration.
--
-- Idempotent: only updates when M_URL is empty or not a modern path (does not start with '/').
-- Naming convention: all object names uppercase.
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
