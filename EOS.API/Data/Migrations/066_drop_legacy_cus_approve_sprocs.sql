-- ============================================================================
-- EOS.ERP migration 067: 下线报关族批核旧存储过程（效果引擎已接管）
-- ----------------------------------------------------------------------------
-- 报关族 5 模块（300301/300304 出口、300302/300305 进口、3303 品质日分析）的
-- 批核/解批已由效果引擎（APPROVE_EFFECT/DEAPPROVE）接管并通过影子对拍
-- （含副作用表真双路，2026-09-11 收口）：
--   - P_WF_CUS_EXPORT：300301/300304 出口报关批核（link-stamp 盖章对帐单/关封单
--     + 手册 EXP_QTY 累加；解批 clear-refs-unfinish + 反向累加）
--   - P_WF_CUS_IMPORT：300302/300305 进口报关批核（手册 IMP_QTY 累加）
--   - P_WF_QC_ANALYSIS：3303 品质日分析批核（制令品检数量/日期回写）
-- 从库中下线。源码保留于 EOS.Database SSDT 项目（dbo/Stored Procedures/
-- P_WF_CUS_EXPORT.sql / P_WF_CUS_IMPORT.sql / P_WF_QC_ANALYSIS.sql），
-- 移出 Build 组供考古。
-- 保存侧 AFTERSAVE_SP（P_CUS_EXPORT_After_Save / P_CUS_IMPORT_After_Save）与
-- 批核前检查（P_CUS_EXPORT_CHECK / P_CUS_IMPORT_CHECK）仍被使用，不在本迁移范围。
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
      AND o.name NOT IN (N'P_WF_CUS_EXPORT', N'P_WF_CUS_IMPORT', N'P_WF_QC_ANALYSIS')
      AND (m.definition LIKE N'%exec P_WF_CUS_EXPORT%' OR m.definition LIKE N'%EXEC P_WF_CUS_EXPORT%'
        OR m.definition LIKE N'%exec P_WF_CUS_IMPORT%' OR m.definition LIKE N'%EXEC P_WF_CUS_IMPORT%'
        OR m.definition LIKE N'%exec P_WF_QC_ANALYSIS%' OR m.definition LIKE N'%EXEC P_WF_QC_ANALYSIS%')
)
    THROW 50001, N'仍有本批名单外对象引用本批旧报关批核过程，迁移中止（先核实引用方）。', 1;

IF OBJECT_ID(N'dbo.P_WF_CUS_EXPORT', N'P') IS NOT NULL DROP PROCEDURE dbo.P_WF_CUS_EXPORT;
IF OBJECT_ID(N'dbo.P_WF_CUS_IMPORT', N'P') IS NOT NULL DROP PROCEDURE dbo.P_WF_CUS_IMPORT;
IF OBJECT_ID(N'dbo.P_WF_QC_ANALYSIS', N'P') IS NOT NULL DROP PROCEDURE dbo.P_WF_QC_ANALYSIS;

PRINT N'== 报关族批核旧存储过程（3 个）下线完成 ==';
