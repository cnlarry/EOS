-- ============================================================================
-- EOS.ERP migration 066: 下线 P_WFB_* 批量过账遗留存储过程（死代码）
-- ----------------------------------------------------------------------------
-- P_WFB_COPTB/COPTD/COPTF/COPTH/COPTJ 为旧「批量过账」遗留过程，其引用的业务表
-- （COPTB/COPTD/WB_CUST/WB_TOTAL/COPMH/COPME/COPTC/INVMA/SYSDK）在本库均不存在
-- （2026-09-09 sys.objects 核实），MODULES.UPDATE_SP/AFTERSAVE_SP 零引用，
-- EOS.API 代码零调用 —— 死代码，运行必炸，无任何消费者。
-- 源码保留于 EOS.Database SSDT 项目（dbo/Stored Procedures/P_WFB_*.sql），
-- 移出 Build 组供考古（与 061-065 同处理）。
-- 幂等：OBJECT_ID 守卫。
-- ============================================================================

SET NOCOUNT ON;

IF DB_NAME() <> N'EOS.ERP'
    THROW 50000, N'本脚本只能在 EOS.ERP 数据库内执行，当前库为 ' + DB_NAME() + N'。', 1;

IF OBJECT_ID(N'dbo.P_WFB_COPTB', N'P') IS NOT NULL DROP PROCEDURE dbo.P_WFB_COPTB;
IF OBJECT_ID(N'dbo.P_WFB_COPTD', N'P') IS NOT NULL DROP PROCEDURE dbo.P_WFB_COPTD;
IF OBJECT_ID(N'dbo.P_WFB_COPTF', N'P') IS NOT NULL DROP PROCEDURE dbo.P_WFB_COPTF;
IF OBJECT_ID(N'dbo.P_WFB_COPTH', N'P') IS NOT NULL DROP PROCEDURE dbo.P_WFB_COPTH;
IF OBJECT_ID(N'dbo.P_WFB_COPTJ', N'P') IS NOT NULL DROP PROCEDURE dbo.P_WFB_COPTJ;

PRINT N'== P_WFB_* 批量过账遗留存储过程（5 个）下线完成 ==';