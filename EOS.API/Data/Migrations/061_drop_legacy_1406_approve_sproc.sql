-- ============================================================================
-- EOS.ERP migration 062: 下线 1406 批核旧存储过程（效果引擎已接管）
-- ----------------------------------------------------------------------------
-- 1406 送货单批核/解批已由效果引擎（APPROVE_EFFECT/DEAPPROVE）接管并通过
-- 三态影子对拍（报告 logs/adr012-acceptance/t3/adr012-1406-*.json）；旧批核入口
-- P_WF_COP_SEND 从此下线。源码保留于 EOS.Database SSDT 项目
-- （dbo/Stored Procedures/P_WF_COP_SEND.sql），移出 Build 组供考古。
-- 保存侧与共享助手（P_COP_SEND_CHECK / P_COP_ORDER_FINISHED /
-- P_COP_SHIPMENT_FINISHED / P_COP_FITOUT_FINISHED / P_UPDATE_PRO_DEPOT）
-- 仍被其它模块或保存链路使用，不在本迁移范围。
-- 幂等：OBJECT_ID 守卫。
-- ============================================================================

SET NOCOUNT ON;

DECLARE @GUARD_MESSAGE NVARCHAR(400) = N'本脚本只能在 EOS.ERP 数据库内执行，当前库为 ' + DB_NAME() + N'。';
IF DB_NAME() <> N'EOS.ERP'
    THROW 50000, @GUARD_MESSAGE, 1;

IF OBJECT_ID(N'dbo.P_WF_COP_SEND', N'P') IS NOT NULL
    DROP PROCEDURE dbo.P_WF_COP_SEND;

PRINT N'== 1406 批核旧存储过程下线完成 ==';
