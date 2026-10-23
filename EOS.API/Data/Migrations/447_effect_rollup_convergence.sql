-- 效果链里的主表汇总收敛：移除已被框架覆盖的赋值。
--
-- 背景：主表金额现在由 MasterAmountRollup 按元数据发现（主表列与明细列同名 + 同数据类型 +
-- 双方 FIELDS.NUMERIC_ROLE='AMOUNT'）统一回写，且接在三个收尾点（保存、单据动作、批核/解批效果后）。
-- 配置型效果 `detail-rollup` 写的是**本单**主表，与框架重复；框架在效果链之后执行，因此这些
-- 赋值既不会生效、又会让配置者以为自己在控制金额口径。
--
-- 处置分两种：
--   ① 170103 预收帐款单 / 170203 预付帐款单：`detail-rollup` 的唯一赋值就是 AMOUNT ← SUM(AMOUNT)，
--      完全被框架覆盖，整条动作移除。
--   ② 170101 应收货款单 / 170201 应付货款单：同一动作里还带两条框架管不到的赋值——
--      QTY_TOTAL ← SUM(QTY)（主从列名不同）与 SUM_AMOUNT ← SUM(AMOUNT_TAX) + 主表 OTHER_PRICE
--      （带 plusMaster 项），故保留动作、只移除被覆盖的 AMOUNT / AMOUNT_TAX / TAX_SUM 三条。
--
-- 跨单据回写的两处（`order/purchase-change-apply`、`callback-reprice`）不在本迁移范围：
-- 它们写的是**别的**单据，框架按本单键定位，替不了它们。
--
-- 配置型效果进已发布快照，故四个模块一并标脏；生效需重新发布该模块（模块管理保存即发布）。

SET NOCOUNT ON;

DELETE FROM dbo.MODULE_BUSINESS_ACTION
WHERE EFFECT_KEY = N'detail-rollup'
  AND M_IDX IN (170103, 170203);

UPDATE dbo.MODULE_BUSINESS_ACTION
SET PARAM_STRUCT = N'{"detailTable":"COP_ACCOUNT_D","roundDigits":2,"assignments":[{"target":"QTY_TOTAL","sum":"QTY"},{"target":"SUM_AMOUNT","sum":"AMOUNT_TAX","plusMaster":"OTHER_PRICE"}]}'
WHERE EFFECT_KEY = N'detail-rollup' AND M_IDX = 170101;

UPDATE dbo.MODULE_BUSINESS_ACTION
SET PARAM_STRUCT = N'{"detailTable":"PUR_DUE_D","roundDigits":2,"assignments":[{"target":"QTY_TOTAL","sum":"QTY"},{"target":"SUM_AMOUNT","sum":"AMOUNT_TAX","plusMaster":"OTHER_PRICE"}]}'
WHERE EFFECT_KEY = N'detail-rollup' AND M_IDX = 170201;

-- 标脏：四个模块的定义快照已落后于配置，等重新发布。
INSERT INTO dbo.WORKBENCH_MODULE_DIRTY (M_IDX, DIRTY_TAG, LAST_MODIFIED_BY, LAST_MODIFIED_AT)
SELECT v.M_IDX, 1, N'DbUp', SYSDATETIME()
FROM (VALUES (170101), (170103), (170201), (170203)) v(M_IDX)
WHERE NOT EXISTS (SELECT 1 FROM dbo.WORKBENCH_MODULE_DIRTY d WHERE d.M_IDX = v.M_IDX);

-- 断言：170103 / 170203 的 detail-rollup 已不存在；170101 / 170201 的赋值里不再出现被覆盖的三列。
IF EXISTS (SELECT 1 FROM dbo.MODULE_BUSINESS_ACTION
            WHERE EFFECT_KEY = N'detail-rollup' AND M_IDX IN (170103, 170203))
    THROW 51000, '170103 / 170203 的 detail-rollup 未被移除。', 1;

IF EXISTS (SELECT 1 FROM dbo.MODULE_BUSINESS_ACTION
            WHERE EFFECT_KEY = N'detail-rollup' AND M_IDX IN (170101, 170201)
              AND (PARAM_STRUCT LIKE N'%"target":"AMOUNT"%'
                OR PARAM_STRUCT LIKE N'%"target":"AMOUNT_TAX"%'
                OR PARAM_STRUCT LIKE N'%"target":"TAX_SUM"%'))
    THROW 51001, '170101 / 170201 仍保留被框架覆盖的金额赋值。', 1;
