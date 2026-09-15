-- ============================================================================
-- EOS.ERP migration 076: 清空三个未替代模块的悬空 AFTERSAVE_SP（保存侧审计收尾）
-- ----------------------------------------------------------------------------
-- 背景：现代保存链不执行遗留 AFTERSAVE_SP——保存后处理按「静态规则表 → 领域规则映射 →
-- 否则 fail-closed 拒存（SP_NOT_PORTED）」三级回落。以下三个模块既无领域规则映射，
-- 其保存后逻辑也已由既有拍板确定不再移植，但仍挂着 AFTERSAVE_SP：
--   2205 报表过滤条件设置 —— 管理页随 ADR-009 §11 退役（迁移 025），写入口已删除
--   2306 用户权限设定     —— 决策 A（2026-08-16）：接受显式管理，不注册 sysdl 规则
--   180218 员工批量发卡   —— 决策 #31：特殊页（/jobs/card-batch），不走统一表单
-- 三者均已清空 NEW_URL/MODI_URL 且不在统一表单白名单（DocumentWorkbenchController 对
-- 非白名单模块直接 404），因此该字段触发不到，属悬空配置。
-- 行为等价性：三者当前无任何可到达的保存路径，清空该字段不改变任何可观测行为，
-- 仅使"不移植/已下线"的台账结论与库内配置一致（与 072 清悬空 UPDATE_SP、075 复位
-- 悬空 EFFECT_ENGINE_TAG 同类）。
-- 前提记录：若将来重新把其中任一模块纳入统一表单白名单，必须先实现对应的保存后
-- 逻辑（否则该模块会被 fail-closed 拒绝保存）。
-- 可逆：按需写回原过程名即可。
-- 幂等：仅当 AFTERSAVE_SP 非空时改写。
-- ============================================================================

SET NOCOUNT ON;

DECLARE @GUARD_MESSAGE NVARCHAR(400) = N'本脚本只能在 EOS.ERP 数据库内执行，当前库为 ' + DB_NAME() + N'。';
IF DB_NAME() <> N'EOS.ERP'
    THROW 50000, @GUARD_MESSAGE, 1;

BEGIN TRANSACTION;

UPDATE dbo.MODULES
   SET AFTERSAVE_SP = NULL,
       LAST_UPDATE_BY = N'DbUp',
       LAST_UPDATE_DATE = GETDATE()
 WHERE M_IDX IN (2205, 2306, 180218)
   AND AFTERSAVE_SP IS NOT NULL
   AND LTRIM(RTRIM(AFTERSAVE_SP)) <> '';

COMMIT;
