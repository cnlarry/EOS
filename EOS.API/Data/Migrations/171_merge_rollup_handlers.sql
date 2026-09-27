-- EOS.ERP migration 172: 合并四个同形"主表 ← 明细汇总"键为一个通用键（detail-rollup）
-- ----------------------------------------------------------------------------
-- 合并对象（四个键形状相同：明细按单据键聚合后回写主表，明细为空则不回写）：
--   170101 cop-account-rollup  应收对帐单：金额/税额/价税合计/数量合计 + 含税总额＝价税合计＋其它费用
--   170201 purchase-due-rollup 应付对帐单：同上（明细表 PUR_DUE_D）
--   170103 cop-prepay-rollup   预收帐款单：仅金额（舍入 3 位）
--   170203 pur-prepay-rollup   预付帐款单：仅金额（舍入 3 位）
-- 通用键 `detail-rollup` 的参数形态（闭合、fail-closed 校验物理列）：
--   {"detailTable":"<明细表>","roundDigits":<0..6>,"assignments":[
--       {"target":"<主表列>","sum":"<明细列>"[,"plusMaster":"<主表列>"]}, …]}
--   语义：主表 <target> ＝ ROUND(SUM(明细 <sum>), roundDigits)［＋主表 <plusMaster>］；
--   同一明细列只聚合一次、多目标共用别名（与既有实现复用 AMOUNT_TAX_SUM 一致）。
-- 单据键列由处理器取自**单据计划**（快照里的 MasterPkOrder），故本迁移同时断言其与旧配置声明的
-- (typeField,noField) 一致——否则合并后过滤条件会变。
--
-- 注意：已发布快照里的 businessActions 仍是旧键，需在重启后立刻重发布这 4 个模块（迁移只改库内配置）。
-- 幂等：按"模块 + SAVE + 旧键"更新；已是新键的模块跳过（守卫允许两种状态）。
-- ============================================================================

SET NOCOUNT ON;

SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET ANSI_PADDING ON;
SET ANSI_WARNINGS ON;
SET CONCAT_NULL_YIELDS_NULL ON;
SET ARITHABORT ON;

DECLARE @GUARD_MESSAGE NVARCHAR(400) = N'本脚本只能在 EOS.ERP 数据库内执行，当前库为 ' + DB_NAME() + N'。';
IF DB_NAME() <> N'EOS.ERP'
    THROW 50000, @GUARD_MESSAGE, 1;

DECLARE @Rows TABLE (
    MODULE_ID INT PRIMARY KEY,
    OLD_KEY NVARCHAR(40),
    NEW_PARAM NVARCHAR(MAX),
    EXPECT_TYPE NVARCHAR(50),
    EXPECT_NO NVARCHAR(50));

INSERT INTO @Rows (MODULE_ID, OLD_KEY, NEW_PARAM, EXPECT_TYPE, EXPECT_NO) VALUES
(170101, N'cop-account-rollup',
 N'{"detailTable":"COP_ACCOUNT_D","roundDigits":2,"assignments":['
 + N'{"target":"AMOUNT","sum":"AMOUNT"},{"target":"TAX_SUM","sum":"TAX_SUM"},'
 + N'{"target":"AMOUNT_TAX","sum":"AMOUNT_TAX"},{"target":"QTY_TOTAL","sum":"QTY"},'
 + N'{"target":"SUM_AMOUNT","sum":"AMOUNT_TAX","plusMaster":"OTHER_PRICE"}]}',
 N'ACCOUNT_TYPE', N'ACCOUNT_NO'),
(170201, N'purchase-due-rollup',
 N'{"detailTable":"PUR_DUE_D","roundDigits":2,"assignments":['
 + N'{"target":"AMOUNT","sum":"AMOUNT"},{"target":"TAX_SUM","sum":"TAX_SUM"},'
 + N'{"target":"AMOUNT_TAX","sum":"AMOUNT_TAX"},{"target":"QTY_TOTAL","sum":"QTY"},'
 + N'{"target":"SUM_AMOUNT","sum":"AMOUNT_TAX","plusMaster":"OTHER_PRICE"}]}',
 N'DUE_TYPE', N'DUE_NO'),
(170103, N'cop-prepay-rollup',
 N'{"detailTable":"COP_PREPAY_D","roundDigits":3,"assignments":[{"target":"AMOUNT","sum":"AMOUNT"}]}',
 N'PREPAY_TYPE', N'PREPAY_NO'),
(170203, N'pur-prepay-rollup',
 N'{"detailTable":"PUR_PREPAY_D","roundDigits":3,"assignments":[{"target":"AMOUNT","sum":"AMOUNT"}]}',
 N'PREPAY_TYPE', N'PREPAY_NO');

/* ① 前置守卫：四个模块的 SAVE 动作必须"仍是旧键"或"已是新键"（幂等），且键列与快照主键序一致 */
IF EXISTS (SELECT 1 FROM @Rows r WHERE NOT EXISTS (
        SELECT 1 FROM dbo.MODULE_BUSINESS_ACTION a
        WHERE a.MODULE_ID = r.MODULE_ID AND a.EVENT_CODE = N'SAVE'
          AND a.EFFECT_KEY IN (r.OLD_KEY, N'detail-rollup')))
    THROW 50001, N'待合并的四个 SAVE 动作不全（既非旧键也非新键），迁移中止（先核对配置）。', 1;

IF EXISTS (
    SELECT 1 FROM @Rows r
    JOIN dbo.WORKBENCH_DEFINITION_SNAPSHOT s ON s.MODULE_ID = r.MODULE_ID AND s.IS_CURRENT = 1
    WHERE ISNULL(JSON_VALUE(s.DEFINITION_JSON, N'$.MasterPkOrder[0]'), N'') <> r.EXPECT_TYPE
       OR ISNULL(JSON_VALUE(s.DEFINITION_JSON, N'$.MasterPkOrder[1]'), N'') <> r.EXPECT_NO)
    THROW 50002, N'快照里的单据主键序与旧配置声明的键列不一致（合并后过滤条件会变），迁移中止。', 1;

/* ② 换成通用键与新参数 */
UPDATE a
   SET a.EFFECT_KEY = N'detail-rollup',
       a.EFFECT_NAME = N'主表 ← 明细汇总（通用）',
       a.PARAM_STRUCT = r.NEW_PARAM,
       a.LAST_UPDATE_BY = N'DbUp',
       a.LAST_UPDATE_DATE = SYSDATETIME()
FROM dbo.MODULE_BUSINESS_ACTION a JOIN @Rows r ON r.MODULE_ID = a.MODULE_ID
WHERE a.EVENT_CODE = N'SAVE' AND a.EFFECT_KEY = r.OLD_KEY;

/* ③ 收口断言：旧键清零、新键恰好四条、参数合法且明细表已声明 */
IF EXISTS (SELECT 1 FROM dbo.MODULE_BUSINESS_ACTION
           WHERE EFFECT_KEY IN (N'cop-account-rollup', N'cop-prepay-rollup',
                                N'purchase-due-rollup', N'pur-prepay-rollup'))
    THROW 50003, N'仍存在被合并的旧效果键，迁移中止。', 1;

IF (SELECT COUNT(*) FROM dbo.MODULE_BUSINESS_ACTION WHERE EFFECT_KEY = N'detail-rollup' AND ENABLED = 1) <> 4
    THROW 50004, N'合并后的 detail-rollup 动作数不是 4 条，迁移中止。', 1;

IF EXISTS (SELECT 1 FROM dbo.MODULE_BUSINESS_ACTION
           WHERE EFFECT_KEY = N'detail-rollup'
             AND (ISJSON(PARAM_STRUCT) <> 1
                  OR JSON_VALUE(PARAM_STRUCT, N'$.detailTable') IS NULL
                  OR JSON_QUERY(PARAM_STRUCT, N'$.assignments') IS NULL))
    THROW 50005, N'合并后的 detail-rollup 参数不合法（缺少 detailTable 或 assignments），迁移中止。', 1;

PRINT N'== 四个同形汇总键合并为 detail-rollup 完成（170101/170201/170103/170203）==';
