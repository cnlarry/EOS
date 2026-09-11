-- ============================================================================
-- EOS.ERP migration 069: 下线模具族已收口模块批核旧存储过程（效果引擎已接管）
-- ----------------------------------------------------------------------------
-- 模具族已含副作用表真双路收口的 2 个模块（2903 开模申请单 / 2906 量产模具
-- 开模完工单）批核/解批已由效果引擎（APPROVE_EFFECT/DEAPPROVE）接管并通过
-- 影子对拍（2026-09-11 收口）：
--   - P_WF_MOU_APPLY：2903 开模申请单批核（link-stamp 评估单盖章）
--   - P_WF_MOU_BATCH：2906 量产模具（开模完工）单批核（状态机 + 数量/次数回写）
-- 从库中下线。源码保留于 EOS.Database SSDT 项目（dbo/Stored Procedures/
-- P_WF_MOU_APPLY.sql / P_WF_MOU_BATCH.sql），移出 Build 组供考古。
-- 保存侧 AFTERSAVE_SP 与共享助手仍被使用，不在本迁移范围。
-- 注意：模具族其它 SP（P_WF_MOU_ACCEPT/P_WF_MOU_GET/P_WF_MOU_GET2/P_WF_MOU_BATCHTOP/
-- P_WF_MOU_BATCHIN/P_WF_MOU_IN/P_WF_MOU_OUT/P_WF_MOU_SCRAP）为 2904/2907/2912/2913/
-- 2915/2916/2908/2909/2910 等未完成解批对拍的模块使用，不在此列。
-- 幂等：OBJECT_ID 守卫；动态守卫：任何非本批对象若 exec 引用即中止（保守）。
-- ============================================================================

SET NOCOUNT ON;

DECLARE @GUARD_MESSAGE NVARCHAR(400) = N'本脚本只能在 EOS.ERP 数据库内执行，当前库为 ' + DB_NAME() + N'。';
IF DB_NAME() <> N'EOS.ERP'
    THROW 50000, @GUARD_MESSAGE, 1;

/* 动态守卫：本批名单之外的对象若 exec 引用本批过程，中止（防漏删仍被调用者）。
   使用精确过程名匹配，避免 P_WF_MOU_BATCHTOP/P_WF_MOU_BATCHIN 与 P_WF_MOU_BATCH
   前缀误伤。 */
IF EXISTS (
    SELECT 1
    FROM sys.sql_modules m
    JOIN sys.objects o ON o.object_id = m.object_id
    WHERE o.type IN (N'P', N'FN', N'IF', N'TF', N'TR', N'V')
      AND o.name NOT IN (N'P_WF_MOU_APPLY', N'P_WF_MOU_BATCH')
      AND (m.definition LIKE N'%exec P_WF_MOU_APPLY%' OR m.definition LIKE N'%EXEC P_WF_MOU_APPLY%'
        OR m.definition LIKE N'%exec P_WF_MOU_BATCH[^_]%' OR m.definition LIKE N'%EXEC P_WF_MOU_BATCH[^_]%'
        OR m.definition LIKE N'%exec dbo.P_WF_MOU_APPLY%' OR m.definition LIKE N'%exec dbo.P_WF_MOU_BATCH[^_]%')
)
    THROW 50001, N'仍有本批名单外对象引用本批旧模具批核过程，迁移中止（先核实引用方）。', 1;

IF OBJECT_ID(N'dbo.P_WF_MOU_APPLY', N'P') IS NOT NULL DROP PROCEDURE dbo.P_WF_MOU_APPLY;
IF OBJECT_ID(N'dbo.P_WF_MOU_BATCH', N'P') IS NOT NULL DROP PROCEDURE dbo.P_WF_MOU_BATCH;

PRINT N'== 模具族已收口模块批核旧存储过程（2 个）下线完成 ==';
