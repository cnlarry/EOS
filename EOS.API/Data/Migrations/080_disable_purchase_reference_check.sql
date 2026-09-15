-- ============================================================================
-- EOS.ERP migration 081: 停用 1606 采购单的 SAVE 引用存在性校验（参数待重新推导）
-- ----------------------------------------------------------------------------
-- 背景：该规则行自建立后从未真正执行过（保存路径只在遗留 AFTERSAVE_SP 分支调用效果管线，
-- 带 C# 领域规则的模块根本不走；引擎开关亦为 0）。目录校验接线修复后它开始真跑，
-- 立刻暴露出参数与真实数据不符：
--   · 明细"申购单存在"断言：8729 张采购单中 6746 张被判不通过（其中 678 张明细申购单号为空，
--     其余是历史单号在 PUR_APPLY_M 中查无对应）——断言写成了强制关联，而配的 allowEmpty
--     只对 refKey 形态生效、对 join 形态无效；
--   · 明细"产品存在"断言：146 张单据判不通过；
--   · 对照 C# 域规则 PurPurchaseAfterSaveAsync，其引用类判据只有"厂商存在且未停止交易"
--     （其余是计价有效期、预交日期），并未要求申购单/产品存在。
-- 故本条断言属翻译期臆造，直接执行会拦下绝大多数正常采购保存。
--
-- 处置：先停用（ENABLED=0）止血，参数重新推导后按 B3（引用存在性批）重新落地；
-- 停用随模块重新发布进入快照后生效。
-- 幂等：仅在仍启用时更新。
-- ============================================================================

SET NOCOUNT ON;

DECLARE @GUARD_MESSAGE NVARCHAR(400) = N'本脚本只能在 EOS.ERP 数据库内执行，当前库为 ' + DB_NAME() + N'。';
IF DB_NAME() <> N'EOS.ERP'
    THROW 50000, @GUARD_MESSAGE, 1;

PRINT N'== 停用 1606 SAVE 引用存在性校验 ==';

UPDATE dbo.MODULE_VALIDATION_RULE
SET ENABLED = 0,
    REMARK = N'停用：断言与真实数据不符（6746/8729 采购单被误判），参数待重新推导后按 B3 落地',
    LAST_UPDATE_BY = N'migration-081', LAST_UPDATE_DATE = SYSDATETIME()
WHERE MODULE_ID = 1606 AND STAGE = N'SAVE' AND VALIDATION_KEY = N'reference-exists' AND ENABLED = 1;

SELECT CONCAT(N'  module=', MODULE_ID, N' key=', VALIDATION_KEY, N' enabled=', ENABLED) AS SUMMARY
FROM dbo.MODULE_VALIDATION_RULE WHERE MODULE_ID = 1606;

PRINT N'== 1606 SAVE 引用存在性校验已停用 ==';
