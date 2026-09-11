-- ============================================================================
-- EOS.ERP migration 070: 下线库存/领料族已收口模块批核旧存储过程（效果引擎已接管）
-- ----------------------------------------------------------------------------
-- 库存/领料族已含副作用表真双路收口的模块（领料族 1503/1514/1517/2805/2806、
-- 其它出入库 130103/130104/130110/3901、盘点 130101、调拨 130105）批核/解批已由
-- 效果引擎（APPROVE_EFFECT/DEAPPROVE）接管并通过影子对拍（2026-09-10/11 收口）：
--   - P_WF_MOC_GET：1503/1514/1517/2805/2806 领料族批核（库存/用料/开工码回写）
--   - P_WF_INV_OCCUR_IN：130103/2817 其它入库批核
--   - P_WF_INV_OCCUR_OUT：130104/130110/2818/3901 其它出库批核
--   - P_WF_INV_CHECK_STOCK：130101 库存盘点批核（LAST_CHECK_DATE max 合并）
--   - P_WF_INV_OCCUR_TRANSFER：130105 仓库调拨批核（双腿库存与流水）
-- 从库中下线。源码保留于 EOS.Database SSDT 项目（dbo/Stored Procedures/
-- P_WF_MOC_GET.sql / P_WF_INV_OCCUR_IN.sql / P_WF_INV_OCCUR_OUT.sql /
-- P_WF_INV_CHECK_STOCK.sql / P_WF_INV_OCCUR_TRANSFER.sql），移出 Build 组供考古。
-- 保存侧 AFTERSAVE_SP 与共享助手（P_UPDATE_PRO_DEPOT / P_INV_OCCUR_IN_After_Save 等）
-- 仍被使用，不在本迁移范围。
-- 注意：P_WF_INV_OCCUR_INIT（130102 期初开帐单）等未完成解批对拍的模块 SP 不在此列。
-- 幂等：OBJECT_ID 守卫；动态守卫：任何非本批对象若 exec 引用即中止（保守）。
-- ============================================================================

SET NOCOUNT ON;

DECLARE @GUARD_MESSAGE NVARCHAR(400) = N'本脚本只能在 EOS.ERP 数据库内执行，当前库为 ' + DB_NAME() + N'。';
IF DB_NAME() <> N'EOS.ERP'
    THROW 50000, @GUARD_MESSAGE, 1;

/* 动态守卫：本批名单之外的对象若 exec 引用本批过程，中止（防漏删仍被调用者）。
   使用精确过程名匹配，避免 P_WF_INV_OCCUR_INIT 与 P_WF_INV_OCCUR_IN 前缀误伤。 */
IF EXISTS (
    SELECT 1
    FROM sys.sql_modules m
    JOIN sys.objects o ON o.object_id = m.object_id
    WHERE o.type IN (N'P', N'FN', N'IF', N'TF', N'TR', N'V')
      AND o.name NOT IN (N'P_WF_MOC_GET', N'P_WF_INV_OCCUR_IN', N'P_WF_INV_OCCUR_OUT',
                         N'P_WF_INV_CHECK_STOCK', N'P_WF_INV_OCCUR_TRANSFER')
      AND (m.definition LIKE N'%exec P_WF_MOC_GET%' OR m.definition LIKE N'%EXEC P_WF_MOC_GET%'
        OR m.definition LIKE N'%exec P_WF_INV_OCCUR_IN[^_I]%' OR m.definition LIKE N'%EXEC P_WF_INV_OCCUR_IN[^_I]%'
        OR m.definition LIKE N'%exec P_WF_INV_OCCUR_OUT%' OR m.definition LIKE N'%EXEC P_WF_INV_OCCUR_OUT%'
        OR m.definition LIKE N'%exec P_WF_INV_CHECK_STOCK%' OR m.definition LIKE N'%EXEC P_WF_INV_CHECK_STOCK%'
        OR m.definition LIKE N'%exec P_WF_INV_OCCUR_TRANSFER%' OR m.definition LIKE N'%EXEC P_WF_INV_OCCUR_TRANSFER%'
        OR m.definition LIKE N'%exec dbo.P_WF_MOC_GET%' OR m.definition LIKE N'%exec dbo.P_WF_INV_OCCUR_IN[^_I]%'
        OR m.definition LIKE N'%exec dbo.P_WF_INV_OCCUR_OUT%' OR m.definition LIKE N'%exec dbo.P_WF_INV_CHECK_STOCK%'
        OR m.definition LIKE N'%exec dbo.P_WF_INV_OCCUR_TRANSFER%')
)
    THROW 50001, N'仍有本批名单外对象引用本批旧库存/领料批核过程，迁移中止（先核实引用方）。', 1;

IF OBJECT_ID(N'dbo.P_WF_MOC_GET', N'P') IS NOT NULL DROP PROCEDURE dbo.P_WF_MOC_GET;
IF OBJECT_ID(N'dbo.P_WF_INV_OCCUR_IN', N'P') IS NOT NULL DROP PROCEDURE dbo.P_WF_INV_OCCUR_IN;
IF OBJECT_ID(N'dbo.P_WF_INV_OCCUR_OUT', N'P') IS NOT NULL DROP PROCEDURE dbo.P_WF_INV_OCCUR_OUT;
IF OBJECT_ID(N'dbo.P_WF_INV_CHECK_STOCK', N'P') IS NOT NULL DROP PROCEDURE dbo.P_WF_INV_CHECK_STOCK;
IF OBJECT_ID(N'dbo.P_WF_INV_OCCUR_TRANSFER', N'P') IS NOT NULL DROP PROCEDURE dbo.P_WF_INV_OCCUR_TRANSFER;

PRINT N'== 库存/领料族批核旧存储过程（5 个）下线完成 ==';
