-- ============================================================================
-- EOS.ERP migration 071: 下线请购/预收预付/HR 已收口模块批核旧存储过程（效果引擎已接管）
-- ----------------------------------------------------------------------------
-- 请购/预收预付/HR 族已含副作用表真双路收口的模块批核/解批已由效果引擎
-- （APPROVE_EFFECT/DEAPPROVE）接管并通过影子对拍（2026-09-11 收口）：
--   - P_WF_PUR_APPLY：1615 成品请购单 / 1616 备料单批核（计划/订单回写与结案）
--   - P_WF_COP_PREPAY：170103 预收帐款单批核（银行/客户信用/订单行累加）
--   - P_WF_PUR_PREPAY：170203 预付帐款单批核（银行/供应商信用/采购行累加）
--   - P_WF_HR_CONTRACT：180106 合同签订批核（合同同步）
--   - P_WF_HR_WAGE_LZ：180310 / 1803101 离职工资批核（员工离职状态/日期）
--   - P_WF_COP_BACK：1423 退料单（生产不良）批核（订单冲减/不良转计划/库存移动）
--   - P_WF_PUR_CALLBACK：1610 收料核价单批核（回执重算）
-- 从库中下线。源码保留于 EOS.Database SSDT 项目（dbo/Stored Procedures/ 对应 .sql），
-- 移出 Build 组供考古。
-- 保存侧 AFTERSAVE_SP（P_PUR_APPLY_After_Save / P_COP_PREPAY_After_Save 等）与共享助手
-- （P_PUR_PREPAY_CHECK / P_UPDATE_PRO_DEPOT 等）仍被使用，不在本迁移范围。
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
      AND o.name NOT IN (N'P_WF_PUR_APPLY', N'P_WF_COP_PREPAY', N'P_WF_PUR_PREPAY',
                         N'P_WF_HR_CONTRACT', N'P_WF_HR_WAGE_LZ', N'P_WF_COP_BACK',
                         N'P_WF_PUR_CALLBACK')
      AND (m.definition LIKE N'%exec P_WF_PUR_APPLY%' OR m.definition LIKE N'%EXEC P_WF_PUR_APPLY%'
        OR m.definition LIKE N'%exec P_WF_COP_PREPAY%' OR m.definition LIKE N'%EXEC P_WF_COP_PREPAY%'
        OR m.definition LIKE N'%exec P_WF_PUR_PREPAY%' OR m.definition LIKE N'%EXEC P_WF_PUR_PREPAY%'
        OR m.definition LIKE N'%exec P_WF_HR_CONTRACT%' OR m.definition LIKE N'%EXEC P_WF_HR_CONTRACT%'
        OR m.definition LIKE N'%exec P_WF_HR_WAGE_LZ%' OR m.definition LIKE N'%EXEC P_WF_HR_WAGE_LZ%'
        OR m.definition LIKE N'%exec P_WF_COP_BACK%' OR m.definition LIKE N'%EXEC P_WF_COP_BACK%'
        OR m.definition LIKE N'%exec P_WF_PUR_CALLBACK%' OR m.definition LIKE N'%EXEC P_WF_PUR_CALLBACK%'
        OR m.definition LIKE N'%exec dbo.P_WF_PUR_APPLY%' OR m.definition LIKE N'%exec dbo.P_WF_COP_PREPAY%'
        OR m.definition LIKE N'%exec dbo.P_WF_PUR_PREPAY%' OR m.definition LIKE N'%exec dbo.P_WF_HR_CONTRACT%'
        OR m.definition LIKE N'%exec dbo.P_WF_HR_WAGE_LZ%' OR m.definition LIKE N'%exec dbo.P_WF_COP_BACK%'
        OR m.definition LIKE N'%exec dbo.P_WF_PUR_CALLBACK%')
)
    THROW 50001, N'仍有本批名单外对象引用本批旧请购/预收预付/HR 批核过程，迁移中止（先核实引用方）。', 1;

IF OBJECT_ID(N'dbo.P_WF_PUR_APPLY', N'P') IS NOT NULL DROP PROCEDURE dbo.P_WF_PUR_APPLY;
IF OBJECT_ID(N'dbo.P_WF_COP_PREPAY', N'P') IS NOT NULL DROP PROCEDURE dbo.P_WF_COP_PREPAY;
IF OBJECT_ID(N'dbo.P_WF_PUR_PREPAY', N'P') IS NOT NULL DROP PROCEDURE dbo.P_WF_PUR_PREPAY;
IF OBJECT_ID(N'dbo.P_WF_HR_CONTRACT', N'P') IS NOT NULL DROP PROCEDURE dbo.P_WF_HR_CONTRACT;
IF OBJECT_ID(N'dbo.P_WF_HR_WAGE_LZ', N'P') IS NOT NULL DROP PROCEDURE dbo.P_WF_HR_WAGE_LZ;
IF OBJECT_ID(N'dbo.P_WF_COP_BACK', N'P') IS NOT NULL DROP PROCEDURE dbo.P_WF_COP_BACK;
IF OBJECT_ID(N'dbo.P_WF_PUR_CALLBACK', N'P') IS NOT NULL DROP PROCEDURE dbo.P_WF_PUR_CALLBACK;

PRINT N'== 请购/预收预付/HR 批核旧存储过程（7 个）下线完成 ==';
