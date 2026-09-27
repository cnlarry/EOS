-- ============================================================================
-- EOS.ERP migration 065: 下线旧工作流框架存储过程（现代 WorkflowEngine 已接管）
-- ----------------------------------------------------------------------------
-- 既有实现工作流由一组 P_WF_* 框架过程驱动（审批主流程/子流程查询/步骤流转/
-- 撤回/批核信息落库/自动批核入口）。现代系统的工作流由 WorkflowEngine
-- （2101 流程设计器 / 2103 流程监控）承载，MODULES.UPDATE_SP 对这批过程
-- 零引用、EOS.API 代码零调用、库内依赖仅框架过程内部互相调用闭包。
-- 逐一人工核实（2026-09-09）：
--   - P_WF_SAMPLE_PRO 仅在注释中提及 P_WF_RUN（历史说明），非执行引用；
--   - 无任何业务/模块存储过程 exec 这批框架过程。
-- 源码保留于 EOS.Database SSDT 项目（dbo/Stored Procedures/*.sql），
-- 移出 Build 组供考古（与 061-064 同处理）。
-- 幂等：OBJECT_ID 守卫；动态守卫：任何非本批对象若 exec 引用即中止（保守）。
-- ============================================================================

SET NOCOUNT ON;

DECLARE @GUARD_MESSAGE NVARCHAR(400) = N'本脚本只能在 EOS.ERP 数据库内执行，当前库为 ' + DB_NAME() + N'。';
IF DB_NAME() <> N'EOS.ERP'
    THROW 50000, @GUARD_MESSAGE, 1;

/* 动态守卫：本批名单之外的对象若 exec 引用本批过程，中止（防漏删仍被调用者）。 */
IF EXISTS (
    SELECT 1
    FROM sys.sql_modules m
    JOIN sys.objects o ON o.object_id = m.object_id
    WHERE o.type IN (N'P', N'FN', N'IF', N'TF', N'TR', N'V')
      AND o.name NOT IN (
          N'P_WF_SAMPLE_PRO', -- 仅注释提及 P_WF_RUN（历史说明），非执行引用，已人工核实豁免
          N'P_WF_APPROVE', N'P_WF_APPROVE_NOFLOW', N'P_WF_APPROVE_STATE',
          N'P_WF_GET_ALL_NEXTSUBFLOW', N'P_WF_GET_ALL_PRESUBFLOW', N'P_WF_GET_BillStateInMytask',
          N'P_WF_GET_CUR_SUBFLOW', N'P_WF_GET_NEXT_SUBFLOW', N'P_WF_GET_NOBACK_STATE',
          N'P_WF_GET_PRE_SUBFLOW', N'P_WF_GET_SUBFLOW_APPROVER', N'P_WF_GET_SUBFLOW_APPROVER_1',
          N'P_WF_GETALL_APPROVER', N'P_WF_RUN', N'P_WF_RUN_AUTO', N'P_WF_RUN_ONE',
          N'P_WF_SAVE_APPROVE_INFO', N'P_WF_STEP_FLOW_ATAPPROVE', N'P_WF_STEP_FLOW_BACKWARDS_JUMP',
          N'P_WF_STEP_FLOW_BEFORE_JUMP', N'P_WF_STEP_FLOW_UNAPPROVE', N'P_WF_STEP_SUBFLOW', N'P_WF_UNDO')
      AND (
          m.definition LIKE N'%exec P_WF_APPROVE%' OR m.definition LIKE N'%EXEC P_WF_APPROVE%'
          OR m.definition LIKE N'%exec P_WF_GET%' OR m.definition LIKE N'%EXEC P_WF_GET%'
          OR m.definition LIKE N'%exec P_WF_STEP%' OR m.definition LIKE N'%EXEC P_WF_STEP%'
          OR m.definition LIKE N'%exec P_WF_RUN%' OR m.definition LIKE N'%EXEC P_WF_RUN%'
          OR m.definition LIKE N'%exec P_WF_UNDO%' OR m.definition LIKE N'%EXEC P_WF_UNDO%'
          OR m.definition LIKE N'%exec P_WF_SAVE_APPROVE_INFO%' OR m.definition LIKE N'%EXEC P_WF_SAVE_APPROVE_INFO%')
)
    THROW 50001, N'仍有框架外对象引用本批旧工作流过程，迁移中止（先核实引用方）。', 1;

IF OBJECT_ID(N'dbo.P_WF_APPROVE', N'P') IS NOT NULL DROP PROCEDURE dbo.P_WF_APPROVE;
IF OBJECT_ID(N'dbo.P_WF_APPROVE_NOFLOW', N'P') IS NOT NULL DROP PROCEDURE dbo.P_WF_APPROVE_NOFLOW;
IF OBJECT_ID(N'dbo.P_WF_APPROVE_STATE', N'P') IS NOT NULL DROP PROCEDURE dbo.P_WF_APPROVE_STATE;
IF OBJECT_ID(N'dbo.P_WF_GET_ALL_NEXTSUBFLOW', N'P') IS NOT NULL DROP PROCEDURE dbo.P_WF_GET_ALL_NEXTSUBFLOW;
IF OBJECT_ID(N'dbo.P_WF_GET_ALL_PRESUBFLOW', N'P') IS NOT NULL DROP PROCEDURE dbo.P_WF_GET_ALL_PRESUBFLOW;
IF OBJECT_ID(N'dbo.P_WF_GET_BillStateInMytask', N'P') IS NOT NULL DROP PROCEDURE dbo.P_WF_GET_BillStateInMytask;
IF OBJECT_ID(N'dbo.P_WF_GET_CUR_SUBFLOW', N'P') IS NOT NULL DROP PROCEDURE dbo.P_WF_GET_CUR_SUBFLOW;
IF OBJECT_ID(N'dbo.P_WF_GET_NEXT_SUBFLOW', N'P') IS NOT NULL DROP PROCEDURE dbo.P_WF_GET_NEXT_SUBFLOW;
IF OBJECT_ID(N'dbo.P_WF_GET_NOBACK_STATE', N'P') IS NOT NULL DROP PROCEDURE dbo.P_WF_GET_NOBACK_STATE;
IF OBJECT_ID(N'dbo.P_WF_GET_PRE_SUBFLOW', N'P') IS NOT NULL DROP PROCEDURE dbo.P_WF_GET_PRE_SUBFLOW;
IF OBJECT_ID(N'dbo.P_WF_GET_SUBFLOW_APPROVER', N'P') IS NOT NULL DROP PROCEDURE dbo.P_WF_GET_SUBFLOW_APPROVER;
IF OBJECT_ID(N'dbo.P_WF_GET_SUBFLOW_APPROVER_1', N'P') IS NOT NULL DROP PROCEDURE dbo.P_WF_GET_SUBFLOW_APPROVER_1;
IF OBJECT_ID(N'dbo.P_WF_GETALL_APPROVER', N'P') IS NOT NULL DROP PROCEDURE dbo.P_WF_GETALL_APPROVER;
IF OBJECT_ID(N'dbo.P_WF_RUN', N'P') IS NOT NULL DROP PROCEDURE dbo.P_WF_RUN;
IF OBJECT_ID(N'dbo.P_WF_RUN_AUTO', N'P') IS NOT NULL DROP PROCEDURE dbo.P_WF_RUN_AUTO;
IF OBJECT_ID(N'dbo.P_WF_RUN_ONE', N'P') IS NOT NULL DROP PROCEDURE dbo.P_WF_RUN_ONE;
IF OBJECT_ID(N'dbo.P_WF_SAVE_APPROVE_INFO', N'P') IS NOT NULL DROP PROCEDURE dbo.P_WF_SAVE_APPROVE_INFO;
IF OBJECT_ID(N'dbo.P_WF_STEP_FLOW_ATAPPROVE', N'P') IS NOT NULL DROP PROCEDURE dbo.P_WF_STEP_FLOW_ATAPPROVE;
IF OBJECT_ID(N'dbo.P_WF_STEP_FLOW_BACKWARDS_JUMP', N'P') IS NOT NULL DROP PROCEDURE dbo.P_WF_STEP_FLOW_BACKWARDS_JUMP;
IF OBJECT_ID(N'dbo.P_WF_STEP_FLOW_BEFORE_JUMP', N'P') IS NOT NULL DROP PROCEDURE dbo.P_WF_STEP_FLOW_BEFORE_JUMP;
IF OBJECT_ID(N'dbo.P_WF_STEP_FLOW_UNAPPROVE', N'P') IS NOT NULL DROP PROCEDURE dbo.P_WF_STEP_FLOW_UNAPPROVE;
IF OBJECT_ID(N'dbo.P_WF_STEP_SUBFLOW', N'P') IS NOT NULL DROP PROCEDURE dbo.P_WF_STEP_SUBFLOW;
IF OBJECT_ID(N'dbo.P_WF_UNDO', N'P') IS NOT NULL DROP PROCEDURE dbo.P_WF_UNDO;

PRINT N'== 旧工作流框架存储过程（23 个）下线完成 ==';
