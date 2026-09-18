-- ============================================================================
-- EOS.ERP migration 167: 客户订单入校验目录并退役 C#（cop-order：1405）
--                        新增校验模板 custom-validation（注册代码的定制校验）
-- ----------------------------------------------------------------------------
-- 旧过程 `P_COP_ORDER_After_Save` 调用的 `P_COP_ORDER_CHECK` / C# `CopDomainRules.CopOrderAfterSaveAsync`
-- 是一组**表达式级跨表判据**，现有六个校验模板都表达不了（都是"字段×字段"/"日期差"/"跨表上限"形态）：
--   ① 客户交易天数：`SYSSS.CLIENT_DAYS < DATEDIFF(day, CLIENT.LAST_TRADE_DATE, 订单日期)` ⇒ 拒；
--   ② 最低订单金额：`客户最低订单额 × 客户币别汇率 > 订单价税合计 × 订单币别汇率` ⇒ 拒（回报"金额+币别"）；
--   ③ 客户信用余额：`信用额度 × 客户币别汇率 < 订单价税合计 × 订单币别汇率` ⇒ 拒（回报"差额+币别"）；
--   ④ 产品交易天数：明细产品的 `PRODUCT_DAYS < DATEDIFF(day, 产品最后交易日, 订单日期)` ⇒ 拒（回报产品号）；
--   ⑤ 产品计价有效期：客户计价明细的 `IN_EFFECT_DATE < 订单日期` ⇒ 拒（回报产品号）；
--   ⑥ 最小生产数量：`明细数量 < 产品最小生产量` ⇒ 拒（回报产品号）；
--   ⑦ 客户订单号重复（排除本单、空号不算）⇒ 拒；
--   ⑧ 预交日期早于订单日期 ⇒ 拒（回报明细序号）。
-- 承载方式：**新增校验模板 `custom-validation`**（ADR-012 §14.2 目录里本就列有该模板）——
-- 参数为 `{"handler":"<已注册校验键>","check":{…实现自己的参数…}}`，handler 走**闭集注册**（未注册即配置错）；
-- 执行器把 `check` 交给注册实现，命中即抛校验异常阻断保存（文案由实现渲染，与旧实现逐字一致）。
-- 本次注册的实现是 `cop-order-check`（八张表与各列名分组闭合声明，全部校验为物理列）。
-- 本族仍是保存期校验，故走**校验目录**而非效果链；1405 因此登记进 `CatalogAfterSaveMap`。
-- 序号：既有的引用校验占 SEQ=1，本迁移的新规则排在其后（SEQ=2）。
-- 幂等：规则按 模块+SAVE+VALIDATION_KEY+SEQ 合并；快照族名仅当仍含 `cop-order` 时改写。
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

DECLARE @ModuleId INT = 1405;
DECLARE @Family NVARCHAR(40) = N'cop-order';

IF NOT EXISTS (SELECT 1 FROM dbo.MODULES m WHERE m.M_IDX = @ModuleId
                 AND LTRIM(RTRIM(ISNULL(m.MASTER_TABLE, ''))) = N'COP_ORDER_M'
                 AND LTRIM(RTRIM(ISNULL(m.DETAIL_TABLE, ''))) = N'COP_ORDER_D')
    THROW 50001, N'模块 1405 形态不符（应为 COP_ORDER_M / COP_ORDER_D），迁移中止。', 1;

IF NOT EXISTS (SELECT 1 FROM dbo.MODULE_VALIDATION_RULE
               WHERE MODULE_ID = @ModuleId AND STAGE = N'SAVE' AND VALIDATION_KEY = N'reference-exists'
                 AND SEQ = 1 AND ENABLED = 1)
    THROW 50002, N'模块 1405 缺少既有的 SAVE 期引用校验（SEQ=1），迁移中止（先核对配置）。', 1;

DECLARE @Check NVARCHAR(MAX) =
    N'{"master":{"typeField":"ORDER_TYPE","noField":"ORDER_NO","dateField":"ORDER_DATE",'
    + N'"amountTaxField":"AMOUNT_TAX","clientField":"CLIENT_ID","currencyRateField":"CURR_RATE",'
    + N'"clientOrderNoField":"CLIENT_ORDER_NO"},'
    + N'"detail":{"table":"COP_ORDER_D","productField":"PRO_NO","qtyField":"QTY",'
    + N'"preSendDateField":"PRE_SEND_DATE","serialField":"SERIAL_NO"},'
    + N'"client":{"table":"CLIENT","keyField":"CLIENT_ID","lastTradeDateField":"LAST_TRADE_DATE",'
    + N'"creditLimitField":"CREDIT_LIMIT_NUM","minOrderAmountField":"MIN_ORDER_AMOUNT","currencyField":"CURR_ID"},'
    + N'"currency":{"table":"CURR","keyField":"CURR_ID","rateField":"CURR_RATE"},'
    + N'"product":{"table":"PRODUCT","keyField":"PRO_NO","lastTradeDateField":"LAST_TRADE_DATE",'
    + N'"minProduceQtyField":"MIN_PRODUCE_QTY"},'
    + N'"clientPrice":{"table":"CLIENT_PRICE_D","clientField":"CLIENT_ID","productField":"PRO_NO",'
    + N'"inEffectDateField":"IN_EFFECT_DATE"},'
    + N'"settings":{"table":"SYSSS","clientDaysField":"CLIENT_DAYS","productDaysField":"PRODUCT_DAYS"},'
    + N'"messages":{"tradeDays":"已超过客户交易天数","minOrder":"总金额小于客户最低订单额:",'
    + N'"credit":"客户信用余额不足：","productDays":"以下产品编号已超出产品交易天数限制\r\n",'
    + N'"priceExpired":"以下产品计价已过有效期\r\n","minProduce":"以下产品编号订单量低于最小生产要求数量\r\n",'
    + N'"duplicateOrderNo":"客户订单号重复。","preSend":"以下序号项预交日期小于订单日期 \r\n"}}';

DECLARE @Param NVARCHAR(MAX) =
    N'{"handler":"cop-order-check","check":' + @Check + N'}';

MERGE dbo.MODULE_VALIDATION_RULE AS T
USING (SELECT @ModuleId AS MODULE_ID) AS S
   ON T.MODULE_ID = S.MODULE_ID AND T.STAGE = N'SAVE'
  AND T.VALIDATION_KEY = N'custom-validation' AND T.SEQ = 2
WHEN MATCHED THEN
    UPDATE SET T.PARAM_STRUCT = @Param, T.MESSAGE = NULL, T.ENABLED = 1,
               T.REMARK = N'客户订单保存期八条判据（交易天数/最低订单额/信用余额/产品交易天数/计价有效期/最小生产量/订单号重复/预交日期；原 C# 判据的忠实移植）',
               T.LAST_UPDATE_BY = N'DbUp', T.LAST_UPDATE_DATE = SYSDATETIME()
WHEN NOT MATCHED THEN
    INSERT (MODULE_ID, STAGE, SEQ, VALIDATION_KEY, ENABLED, PARAM_STRUCT, MESSAGE, REMARK, SOURCE_REF,
            CREATE_PERSON, CREATE_DATE, LAST_UPDATE_BY, LAST_UPDATE_DATE)
    VALUES (S.MODULE_ID, N'SAVE', 2, N'custom-validation', 1, @Param, NULL,
            N'客户订单保存期八条判据（交易天数/最低订单额/信用余额/产品交易天数/计价有效期/最小生产量/订单号重复/预交日期；原 C# 判据的忠实移植）',
            @Family, N'DbUp', SYSDATETIME(), N'DbUp', SYSDATETIME());

UPDATE S
   SET S.DEFINITION_JSON = REPLACE(S.DEFINITION_JSON, N'"DomainRule":"' + @Family + N'"', N'"DomainRule":null')
 FROM dbo.WORKBENCH_DEFINITION_SNAPSHOT S
WHERE S.MODULE_ID = @ModuleId AND S.IS_CURRENT = 1
  AND S.DEFINITION_JSON LIKE N'%"DomainRule":"' + @Family + N'"%';

IF EXISTS (SELECT 1 FROM dbo.WORKBENCH_DEFINITION_SNAPSHOT S
           WHERE S.MODULE_ID = @ModuleId AND S.IS_CURRENT = 1
             AND S.DEFINITION_JSON LIKE N'%"DomainRule":"' + @Family + N'"%')
    THROW 50003, N'当前快照仍残留已退役的族名，迁移中止。', 1;

IF NOT EXISTS (SELECT 1 FROM dbo.MODULE_VALIDATION_RULE
               WHERE MODULE_ID = @ModuleId AND STAGE = N'SAVE' AND ENABLED = 1
                 AND VALIDATION_KEY = N'custom-validation' AND SEQ = 2
                 AND PARAM_STRUCT LIKE N'%"handler":"cop-order-check"%')
    THROW 50004, N'退役 C# 后缺少启用的 SAVE 期 custom-validation（cop-order-check）规则，迁移中止。', 1;

PRINT N'== cop-order 入校验目录并退役 C# 完成（1405，custom-validation → cop-order-check）==';
