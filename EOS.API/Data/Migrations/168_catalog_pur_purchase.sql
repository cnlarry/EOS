-- ============================================================================
-- EOS.ERP migration 169: 采购单入校验/效果目录并退役 C#（pur-purchase：1606）
-- ----------------------------------------------------------------------------
-- 最后一个静态登记族。C# `PurDomainRules.PurPurchaseAfterSaveAsync`（来源旧过程
-- `P_PUR_PURCHASE_After_Save`）分两段：
--   ① **校验段**：产品计价有效期（所引用的厂商计价明细 `IN_EFFECT_DATE` 早于采购日期 ⇒ 拒，回报产品号）；
--      预交日期不得早于采购单日期 ⇒ 拒（回报明细序号）。两条都是"明细/主表日期 vs 另一张表日期"，
--      模板表达不了 ⇒ 走校验目录的 `custom-validation`（注册实现 `pur-purchase-check`）。
--   ② **写段**（`PUR_PURCHASE_MORE` 汇总同步整链）：
--      a 待购表尚未成行的产品按 `SUM(REQUIRE_QTY)` 补明细行（序号递增、仓库/数量/单位取产品档案与待购合计）；
--      b 主表 币别/汇率/税种/税率/税别 带到本单全部明细；
--      c 厂商计价（同厂商+产品+单位+币别+税别+税种）回填 单价/税率/汇率/折扣；
--      d 明细 应购数量 清零后按产品汇总回填；
--      e 明细 金额/价税合计/税额 按税种 I/O/N 公式重算；
--      f 主表 金额/价税合计/税额 ＝ 明细按汇率折算求和 ÷ 主表汇率（ROUND 2）；
--      g 数量分配：待购表 QTY 清零后按 `(产品, 序号)` 顺序把明细 QTY 逐行分给待购行；
--      h 主表 采购订单号/生产单号 ＝ 待购表去重非空值按序串联（全空不回写；无待购行时只做这一步）。
--      ② = 新增服务处理器 **`pur-purchase-sync`**（五张表 + 各列名分组闭合声明，全部校验为物理列）。
-- 校验与效果的先后：SAVE 期校验目录先跑、效果后跑 —— 与 C# 的"先校验后写"一致。
-- 幂等：规则按 模块+SAVE+VALIDATION_KEY+SEQ 合并；动作按 模块+SAVE+SEQ 合并；快照族名仅当仍含族名时改写。
-- 注意：PARAM_STRUCT 里的换行必须是转义的 `\r\n`（JSON 不允许裸控制字符），MESSAGE 列才用真实 CRLF。
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

DECLARE @ModuleId INT = 1606;
DECLARE @Family NVARCHAR(40) = N'pur-purchase';

IF NOT EXISTS (SELECT 1 FROM dbo.MODULES m WHERE m.M_IDX = @ModuleId
                 AND LTRIM(RTRIM(ISNULL(m.MASTER_TABLE, ''))) = N'PUR_PURCHASE_M'
                 AND LTRIM(RTRIM(ISNULL(m.DETAIL_TABLE, ''))) = N'PUR_PURCHASE_D')
    THROW 50001, N'模块 1606 形态不符（应为 PUR_PURCHASE_M / PUR_PURCHASE_D），迁移中止。', 1;

IF EXISTS (SELECT 1 FROM dbo.MODULE_VALIDATION_RULE
            WHERE MODULE_ID = @ModuleId AND STAGE = N'SAVE' AND VALIDATION_KEY = N'custom-validation')
    THROW 50002, N'模块 1606 已有 custom-validation 规则，迁移中止（先核对配置）。', 1;

IF EXISTS (SELECT 1 FROM dbo.MODULE_BUSINESS_ACTION WHERE MODULE_ID = @ModuleId AND EVENT_CODE = N'SAVE')
    THROW 50003, N'模块 1606 已有 SAVE 期业务动作配置，迁移中止（先核对配置）。', 1;

/* ① 校验段（custom-validation → pur-purchase-check） */
DECLARE @Check NVARCHAR(MAX) =
    N'{"master":{"typeField":"PURCHASE_TYPE","noField":"PURCHASE_NO","dateField":"PURCHASE_DATE",'
    + N'"supplierField":"SUPPLIER_ID"},'
    + N'"detail":{"table":"PUR_PURCHASE_D","productField":"PRO_NO","serialField":"SERIAL_NO",'
    + N'"planDeliveryDateField":"PLAN_DELIVERY_DATE","currencyField":"CURR_ID","taxIdField":"TAX_ID",'
    + N'"taxTypeField":"TAX_TYPE"},'
    + N'"supplierPrice":{"table":"SUPPLIER_PRICE_D","supplierField":"SUPPLIER_ID","productField":"PRO_NO",'
    + N'"currencyField":"CURR_ID","taxIdField":"TAX_ID","taxTypeField":"TAX_TYPE","inEffectDateField":"IN_EFFECT_DATE"},'
    + N'"messages":{"priceExpired":"以下产品计价已过有效期\r\n","deliveryDate":"以下序号项预交日期小于采购单日期 \r\n"}}';

DECLARE @Param NVARCHAR(MAX) = N'{"handler":"pur-purchase-check","check":' + @Check + N'}';

MERGE dbo.MODULE_VALIDATION_RULE AS T
USING (SELECT @ModuleId AS MODULE_ID) AS S
   ON T.MODULE_ID = S.MODULE_ID AND T.STAGE = N'SAVE'
  AND T.VALIDATION_KEY = N'custom-validation' AND T.SEQ = 3
WHEN MATCHED THEN
    UPDATE SET T.PARAM_STRUCT = @Param, T.MESSAGE = NULL, T.ENABLED = 1,
               T.REMARK = N'采购单保存期两条判据（产品计价有效期、预交日期不早于采购日期；原 C# 判据的忠实移植）',
               T.LAST_UPDATE_BY = N'DbUp', T.LAST_UPDATE_DATE = SYSDATETIME()
WHEN NOT MATCHED THEN
    INSERT (MODULE_ID, STAGE, SEQ, VALIDATION_KEY, ENABLED, PARAM_STRUCT, MESSAGE, REMARK, SOURCE_REF,
            CREATE_PERSON, CREATE_DATE, LAST_UPDATE_BY, LAST_UPDATE_DATE)
    VALUES (S.MODULE_ID, N'SAVE', 3, N'custom-validation', 1, @Param, NULL,
            N'采购单保存期两条判据（产品计价有效期、预交日期不早于采购日期；原 C# 判据的忠实移植）',
            @Family, N'DbUp', SYSDATETIME(), N'DbUp', SYSDATETIME());

/* ② 写段（pur-purchase-sync） */
DECLARE @EffectParam NVARCHAR(MAX) =
    N'{"master":{"typeField":"PURCHASE_TYPE","noField":"PURCHASE_NO","currencyField":"CURR_ID",'
    + N'"currencyRateField":"CURR_RATE","taxTypeField":"TAX_TYPE","taxRateField":"TAX_RATE","taxIdField":"TAX_ID",'
    + N'"supplierField":"SUPPLIER_ID","amountField":"AMOUNT","amountTaxField":"AMOUNT_TAX","taxSumField":"TAX_SUM",'
    + N'"orderNoField":"ORDER_NO","produceNoField":"PRODUCE_NO"},'
    + N'"detail":{"table":"PUR_PURCHASE_D","productField":"PRO_NO","serialField":"SERIAL_NO","qtyField":"QTY",'
    + N'"receiveQtyField":"RECEIVE_QTY","unitField":"UNIT_ID","currencyField":"CURR_ID",'
    + N'"currencyRateField":"CURR_RATE","taxTypeField":"TAX_TYPE","taxRateField":"TAX_RATE","taxIdField":"TAX_ID",'
    + N'"priceField":"PRICE","rebateField":"REBATE","requireQtyField":"REQUIRE_QTY","amountField":"AMOUNT",'
    + N'"amountTaxField":"AMOUNT_TAX","taxSumField":"TAX_SUM"},'
    + N'"more":{"table":"PUR_PURCHASE_MORE","qtyField":"QTY","requireQtyField":"REQUIRE_QTY",'
    + N'"orderNoField":"ORDER_NO","produceNoField":"PRODUCE_NO"},'
    + N'"product":{"table":"PRODUCT","productField":"PRO_NO","depotField":"DEPOT_ID","unitField":"UNIT_ID"},'
    + N'"supplierPrice":{"table":"SUPPLIER_PRICE_D","supplierField":"SUPPLIER_ID","productField":"PRO_NO",'
    + N'"unitField":"UNIT_ID","currencyField":"CURR_ID","taxIdField":"TAX_ID","taxTypeField":"TAX_TYPE",'
    + N'"priceField":"PRICE","taxRateField":"TAX_RATE","currencyRateField":"CURR_RATE","rebateField":"REBATE"}}';

MERGE dbo.MODULE_BUSINESS_ACTION AS T
USING (SELECT @ModuleId AS MODULE_ID) AS S
   ON T.MODULE_ID = S.MODULE_ID AND T.EVENT_CODE = N'SAVE' AND T.SEQ = 1
WHEN MATCHED THEN
    UPDATE SET T.EFFECT_KEY = N'pur-purchase-sync', T.EFFECT_NAME = N'采购单待购表汇总同步（保存期）',
               T.ENABLED = 1, T.FAIL_MODE = N'BLOCK', T.PARAM_STRUCT = @EffectParam,
               T.REVERSE_STRUCT = N'{"kind":"none"}',
               T.REMARK = N'原 C# 采购单保存后动作的忠实移植（补明细、币别税率带出、厂商计价回填、应购回填、金额重算、主表汇总、数量分配、单号串联）',
               T.LAST_UPDATE_BY = N'DbUp', T.LAST_UPDATE_DATE = SYSDATETIME()
WHEN NOT MATCHED THEN
    INSERT (MODULE_ID, EVENT_CODE, SEQ, EFFECT_KEY, EFFECT_NAME, ENABLED, FAIL_MODE, CONDITION_STRUCT,
            PARAM_STRUCT, REVERSE_STRUCT, REMARK, SOURCE_REF, CREATE_PERSON, CREATE_DATE, LAST_UPDATE_BY, LAST_UPDATE_DATE)
    VALUES (S.MODULE_ID, N'SAVE', 1, N'pur-purchase-sync', N'采购单待购表汇总同步（保存期）', 1, N'BLOCK', NULL,
            @EffectParam, N'{"kind":"none"}',
            N'原 C# 采购单保存后动作的忠实移植（补明细、币别税率带出、厂商计价回填、应购回填、金额重算、主表汇总、数量分配、单号串联）',
            @Family, N'DbUp', SYSDATETIME(), N'DbUp', SYSDATETIME());

/* ③ 置空已发布快照里的族名 */
UPDATE S
   SET S.DEFINITION_JSON = REPLACE(S.DEFINITION_JSON, N'"DomainRule":"' + @Family + N'"', N'"DomainRule":null')
 FROM dbo.WORKBENCH_DEFINITION_SNAPSHOT S
WHERE S.MODULE_ID = @ModuleId AND S.IS_CURRENT = 1
  AND S.DEFINITION_JSON LIKE N'%"DomainRule":"' + @Family + N'"%';

/* ④ 收口断言 */
IF EXISTS (SELECT 1 FROM dbo.WORKBENCH_DEFINITION_SNAPSHOT S
           WHERE S.MODULE_ID = @ModuleId AND S.IS_CURRENT = 1
             AND S.DEFINITION_JSON LIKE N'%"DomainRule":"' + @Family + N'"%')
    THROW 50004, N'当前快照仍残留已退役的族名，迁移中止。', 1;

IF NOT EXISTS (SELECT 1 FROM dbo.MODULE_VALIDATION_RULE
               WHERE MODULE_ID = @ModuleId AND STAGE = N'SAVE' AND ENABLED = 1
                 AND VALIDATION_KEY = N'custom-validation' AND SEQ = 3
                 AND PARAM_STRUCT LIKE N'%"handler":"pur-purchase-check"%')
    THROW 50005, N'退役 C# 后缺少启用的 SAVE 期 custom-validation（pur-purchase-check）规则，迁移中止。', 1;

IF NOT EXISTS (SELECT 1 FROM dbo.MODULE_BUSINESS_ACTION
               WHERE MODULE_ID = @ModuleId AND EVENT_CODE = N'SAVE' AND SEQ = 1
                 AND EFFECT_KEY = N'pur-purchase-sync' AND ENABLED = 1)
    THROW 50006, N'退役 C# 后缺少启用的 SAVE 期 pur-purchase-sync 动作，迁移中止。', 1;

PRINT N'== pur-purchase 入目录并退役 C# 完成（1606，custom-validation → pur-purchase-check + pur-purchase-sync）==';
