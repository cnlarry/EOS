-- ============================================================================
-- EOS.ERP 迁移 007：180218 员工批量发卡归类特殊页（清空统一表单路由）
-- ----------------------------------------------------------------------------
-- 库：EOS.ERP（全新系统的唯一业务数据库）
--
-- 背景（2026-08-26 用户决策，决策清单 #31）：
--   180218 员工批量发卡在系统内以批量作业页交付（/jobs/card-batch + JobPage），
--   但 MODULES.MODI_URL 仍指向 /document-workbench/{moduleId}/edit（统一表单）。
--   180218 不在统一表单白名单（UnifiedFormEditor.EnabledModuleIds），一旦经工作台
--   列表触发编辑/双击即落入未移植的统一表单（404/不可用）。用户拍板：归类特殊页，
--   清空 MODI_URL（及 NEW_URL），防止误入未移植统一表单；单卡维护 180208 已走
--   employee-card 领域规则并在白名单内，不受影响。
--
-- 处理范围：
--   1. MODULES 180218：MODI_URL、NEW_URL 清空，REMARK 登记特殊页结论；
--   2. WORKBENCH_MODULE_DIRTY / WORKBENCH_DEFINITION_SNAPSHOT 按 180218 清理
--      （防御性，180218 从未入白名单，通常无记录）。
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

DECLARE @MIG NVARCHAR(20) = N'EOS-20';
DECLARE @NOW DATETIME = GETDATE();

-- 1. MODULES 180218 归类特殊页：清空统一表单路由，登记结论
UPDATE dbo.MODULES
SET MODI_URL = NULL,
    NEW_URL = NULL,
    REMARK = N'已归类特殊页（2026-08-26，EOS-20）：员工批量发卡以批量作业页交付（/jobs/card-batch + JobPage），清空统一表单 MODI_URL/NEW_URL 防误入未移植表单；单卡维护 180208 走 employee-card 领域规则',
    LAST_UPDATE_BY = @MIG,
    LAST_UPDATE_DATE = @NOW
WHERE M_IDX = 180218;
PRINT N'[EOS-20] MODULES 180218 特殊页化（清空 MODI_URL/NEW_URL）：' + CAST(@@ROWCOUNT AS NVARCHAR(10)) + N' 行';

-- 2. 防御性清理工作台脏标记 / Definition 快照（180218 从未入白名单，通常无记录）
IF OBJECT_ID('dbo.WORKBENCH_MODULE_DIRTY','U') IS NOT NULL
BEGIN
    DELETE FROM dbo.WORKBENCH_MODULE_DIRTY WHERE MODULE_ID = 180218;
    PRINT N'[EOS-20] WORKBENCH_MODULE_DIRTY 180218 清理：' + CAST(@@ROWCOUNT AS NVARCHAR(10)) + N' 行';
END

IF OBJECT_ID('dbo.WORKBENCH_DEFINITION_SNAPSHOT','U') IS NOT NULL
BEGIN
    DELETE FROM dbo.WORKBENCH_DEFINITION_SNAPSHOT WHERE MODULE_ID = 180218;
    PRINT N'[EOS-20] WORKBENCH_DEFINITION_SNAPSHOT 180218 清理：' + CAST(@@ROWCOUNT AS NVARCHAR(10)) + N' 行';
END

COMMIT TRANSACTION;
PRINT N'[EOS-20] 180218 员工批量发卡归类特殊页完成（清空 MODI_URL/NEW_URL）。';