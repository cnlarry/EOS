-- ============================================================================
-- EOS.ERP migration 068: 下线工序族批核旧存储过程（效果引擎已接管）
-- ----------------------------------------------------------------------------
-- 工序族 4 模块（2705 工序工单 / 2706 工序完工单 / 2707 工序发料单 /
-- 2708 工序生产计划）的批核/解批已由效果引擎（APPROVE_EFFECT/DEAPPROVE）接管
-- 并通过影子对拍（含副作用表真双路，2026-09-10/11 收口）：
--   - P_WF_MOC_WORK：2705 工序工单批核
--   - P_WF_MOC_WORK_IN：2706 工序完工单批核
--   - P_WF_MOC_WORK_OUT：2707 工序发料单批核
--   - P_WF_SFC_PLAN：2708 工序生产计划批核
-- 从库中下线。源码保留于 EOS.Database SSDT 项目（dbo/Stored Procedures/
-- P_WF_MOC_WORK*.sql / P_WF_SFC_PLAN.sql），移出 Build 组供考古。
-- 保存侧 AFTERSAVE_SP 与共享助手（P_UPDATE_PRO_DEPOT 等）仍被使用，不在本迁移范围。
-- 注意：P_WF_SFC_PLAN_PROCESS（2709-2711 工序冲裁生产计划）为独立 SP，不在此列。
-- 幂等：OBJECT_ID 守卫；动态守卫：任何非本批对象若 exec 引用即中止（保守）。
-- ============================================================================

SET NOCOUNT ON;

DECLARE @GUARD_MESSAGE NVARCHAR(400) = N'本脚本只能在 EOS.ERP 数据库内执行，当前库为 ' + DB_NAME() + N'。';
IF DB_NAME() <> N'EOS.ERP'
    THROW 50000, @GUARD_MESSAGE, 1;

/* 动态守卫：本批名单之外的对象若 exec 引用本批过程，中止（防漏删仍被调用者）。
   使用精确过程名匹配，避免 P_WF_SFC_PLAN_PROCESS 与 P_WF_SFC_PLAN 前缀误伤。 */
IF EXISTS (
    SELECT 1
    FROM sys.sql_modules m
    JOIN sys.objects o ON o.object_id = m.object_id
    WHERE o.type IN (N'P', N'FN', N'IF', N'TF', N'TR', N'V')
      AND o.name NOT IN (N'P_WF_MOC_WORK', N'P_WF_MOC_WORK_IN', N'P_WF_MOC_WORK_OUT', N'P_WF_SFC_PLAN')
      AND (m.definition LIKE N'%exec P_WF_MOC_WORK[^_]%' OR m.definition LIKE N'%EXEC P_WF_MOC_WORK[^_]%'
        OR m.definition LIKE N'%exec P_WF_MOC_WORK_IN%' OR m.definition LIKE N'%EXEC P_WF_MOC_WORK_IN%'
        OR m.definition LIKE N'%exec P_WF_MOC_WORK_OUT%' OR m.definition LIKE N'%EXEC P_WF_MOC_WORK_OUT%'
        OR m.definition LIKE N'%exec P_WF_SFC_PLAN[^_]%' OR m.definition LIKE N'%EXEC P_WF_SFC_PLAN[^_]%'
        OR m.definition LIKE N'%exec dbo.P_WF_MOC_WORK[^_]%' OR m.definition LIKE N'%exec dbo.P_WF_MOC_WORK_IN%'
        OR m.definition LIKE N'%exec dbo.P_WF_MOC_WORK_OUT%' OR m.definition LIKE N'%exec dbo.P_WF_SFC_PLAN[^_]%')
)
    THROW 50001, N'仍有本批名单外对象引用本批旧工序批核过程，迁移中止（先核实引用方）。', 1;

IF OBJECT_ID(N'dbo.P_WF_MOC_WORK', N'P') IS NOT NULL DROP PROCEDURE dbo.P_WF_MOC_WORK;
IF OBJECT_ID(N'dbo.P_WF_MOC_WORK_IN', N'P') IS NOT NULL DROP PROCEDURE dbo.P_WF_MOC_WORK_IN;
IF OBJECT_ID(N'dbo.P_WF_MOC_WORK_OUT', N'P') IS NOT NULL DROP PROCEDURE dbo.P_WF_MOC_WORK_OUT;
IF OBJECT_ID(N'dbo.P_WF_SFC_PLAN', N'P') IS NOT NULL DROP PROCEDURE dbo.P_WF_SFC_PLAN;

PRINT N'== 工序族批核旧存储过程（4 个）下线完成 ==';
