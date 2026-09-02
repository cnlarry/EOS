-- ============================================================================
-- EOS.ERP migration 010: 1906 fuel card top-up approval pilot workflow
-- ----------------------------------------------------------------------------
-- Database: EOS.ERP (the single business database of the new system)
--
-- Context: the WorkflowEngine (WorkflowEngine.cs) supports dynamic conditions,
-- countersign, jump, auto-execute and withdrawal, but the database had no configured
-- workflow definitions (WFFORM/WFFORM_FLOW tables were empty). This migration
-- enables 1906 fuel card top-up as a pilot: the form's approve button switches to
-- "submit" (start a workflow) instead of direct approval.
--
-- Approval design (two-level):
--   001 first level: admin
--   002 second level: admin
-- After the final step passes, CONFIRM_TAG is set, P_WF_CAR_FEE fires, and
-- WF_APPROVE history is written.
--
-- Idempotent: existing definitions are cleaned by REMARK marker before insert.
-- Naming convention: all object names uppercase.
-- ============================================================================

SET NOCOUNT ON;
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;

DECLARE @GUARD_MESSAGE NVARCHAR(400) = N'本脚本只能在 EOS.ERP 数据库内执行，当前库为 ' + DB_NAME() + N'。';
IF DB_NAME() <> N'EOS.ERP'
    THROW 50000, @GUARD_MESSAGE, 1;

BEGIN TRANSACTION;

DECLARE @MIG NVARCHAR(20) = N'EOS-PILOT-1906';
DECLARE @NOW DATETIME = GETDATE();

-- 1. 清理存量（REMARK 标记为试点定义时幂等重建；不触碰其它来源的流程定义）
DELETE FROM dbo.WFFORM_FLOW WHERE WF_M_IDX=1906 AND LTRIM(RTRIM(ISNULL(REMARK,''))) = @MIG;
DELETE FROM dbo.WFFORM WHERE WF_M_IDX=1906 AND LTRIM(RTRIM(ISNULL(REMARK,''))) = @MIG;
PRINT N'[EOS-PILOT-1906] 清理存量试点流程定义：WFFORM_FLOW ' + CAST(@@ROWCOUNT AS NVARCHAR(10)) + N' 行';

-- 2. 流程定义（REMARK 标记试点，便于审计/清理/回滚）
IF NOT EXISTS (SELECT 1 FROM dbo.WFFORM WHERE WF_M_IDX=1906)
BEGIN
    INSERT INTO dbo.WFFORM (WF_M_IDX, FLOW_NAME, REMARK, CREATE_PERSON, CREATE_DATE, CONFIRM_TAG, CONFIRM_PERSON, CONFIRM_DATE)
    VALUES (1906, N'油卡充值单二级审批', @MIG, @MIG, @NOW, 1, @MIG, @NOW);
    PRINT N'[EOS-PILOT-1906] WFFORM 1906 插入（二级审批）';
END
ELSE
    PRINT N'[EOS-PILOT-1906] WFFORM 1906 已存在，保留既有流程定义（不覆盖）。';

IF EXISTS (SELECT 1 FROM dbo.WFFORM WHERE WF_M_IDX=1906 AND LTRIM(RTRIM(ISNULL(REMARK,''))) = @MIG)
   AND NOT EXISTS (SELECT 1 FROM dbo.WFFORM_FLOW WHERE WF_M_IDX=1906)
BEGIN
    INSERT INTO dbo.WFFORM_FLOW (WF_M_IDX, SORT_NO, SUBFLOW_DESC, EXEC_PERSON, REMARK)
    VALUES (1906, '001', N'一级审批', N'admin', @MIG),
           (1906, '002', N'二级审批', N'admin', @MIG);
    PRINT N'[EOS-PILOT-1906] WFFORM_FLOW 1906 插入（001 admin / 002 admin）';
END
ELSE
    PRINT N'[EOS-PILOT-1906] WFFORM_FLOW 1906 已存在步骤或非试点定义，跳过。';

COMMIT TRANSACTION;
PRINT N'[EOS-PILOT-1906] 1906 油卡充值单试点审批流程配置完成。';
