-- ============================================================================
-- EOS.ERP 迁移 010：1906 油卡充值单试点审批流程（工作流启用试点）
-- ----------------------------------------------------------------------------
-- 库：EOS.ERP（全新系统的唯一业务数据库）
--
-- 背景（2026-08-26 工作流启用试点）：
--   WorkflowEngine v2/v2.1（EOS.API/Data/WorkflowEngine.cs）已完整实现旧系统
--   P_WF_RUN/P_WF_APPROVE/P_WF_RUN_AUTO 语义（动态条件/会签/跳转/自动执行/撤回），
--   但 EOS.ERP 从未配置任何流程定义（WFFORM/WFFORM_FLOW 空），引擎零生产使用。
--   本次以 1906 油卡充值单为试点：主子表 CAR_FEE_M/CAR_FEE_D（主键 FEE_TYPE,FEE_NO）、
--   批核副作用 P_WF_CAR_FEE、已在统一表单白名单、不在任何 E2E 直接批核断言清单，
--   是验证真实审批链的最小合适模块。批核按钮语义随之切换：送审（启动流程）而非直接批核。
--
-- 流程设计（二级审批）：
--   001 一级审批：admin（组 1 对 1906 有 EXEC_TAG=Z；个人权限 Z）
--   002 二级审批：admin（无个人权限，回退组 1 EXEC_TAG=Z，可浏览 1906 单据）
--   末步通过后落主表 CONFIRM_TAG + P_WF_CAR_FEE 副作用 + WF_APPROVE 历史。
--
-- 幂等：先按 REMARK 标记清理存量再插入，重复执行无副作用。
-- 命名约定（AGENTS.md 强制）：对象名全大写。
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
