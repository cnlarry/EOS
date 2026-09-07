-- ============================================================================
-- EOS.ERP migration 061: 下线 1607 批核旧存储过程（效果引擎已接管）
-- ----------------------------------------------------------------------------
-- 1607 收料单批核/解批已由效果引擎（APPROVE_EFFECT/DEAPPROVE）接管并通过
-- 影子对拍；旧批核入口 P_WF_PUR_RECEIVE 及其无引用的考古变体
-- P_WF_PUR_RECEIVE_ZX 从此下线。源码保留于 EOS.Database SSDT 项目
-- （dbo/Stored Procedures/P_WF_PUR_RECEIVE*.sql），移出 Build 组供考古。
-- 保存侧 AFTERSAVE_SP（P_PUR_RECEIVE_After_Save）与共享助手
-- （P_PUR_RECEIVE_CHECK / P_PUR_PURCHASE_FINISHED / P_UPDATE_PRO_DEPOT）
-- 仍被其它模块或保存链路使用，不在本迁移范围。
-- 幂等：OBJECT_ID 守卫。
-- ============================================================================

SET NOCOUNT ON;

DECLARE @GUARD_MESSAGE NVARCHAR(400) = N'本脚本只能在 EOS.ERP 数据库内执行，当前库为 ' + DB_NAME() + N'。';
IF DB_NAME() <> N'EOS.ERP'
    THROW 50000, @GUARD_MESSAGE, 1;

IF OBJECT_ID(N'dbo.P_WF_PUR_RECEIVE', N'P') IS NOT NULL
    DROP PROCEDURE dbo.P_WF_PUR_RECEIVE;

IF OBJECT_ID(N'dbo.P_WF_PUR_RECEIVE_ZX', N'P') IS NOT NULL
    DROP PROCEDURE dbo.P_WF_PUR_RECEIVE_ZX;

PRINT N'== 1607 批核旧存储过程下线完成 ==';
