-- ============================================================================
-- EOS.ERP migration 158: 海关对帐单入效果目录并退役 C#（cus-account：3014）
-- ----------------------------------------------------------------------------
-- C# 侧（`CusDomainRules.CusAccountAfterSaveAsync`）分两段，来源是旧过程 `P_CUS_ACCOUNT_After_Save`
-- （受门控段调 `P_CUS_ACCOUNT_CHECK`）：
--   ① **门控段**（`MODULES.ERROR_NO_SAVE=1` 时）两条"对帐不超送/退货单数量"：
--        送货：`ISNULL(COP_SEND_D.FINISHED_QTY,0) + SUM(本单明细 QTY) > ISNULL(COP_SEND_D.QTY,0)`
--              （本单明细按 `(S_R_TYPE, S_R_NO, S_R_SERIAL_NO)` 分组求和后与送货行对齐）；
--        退货：同形，换 `COP_RETURN_D`（`RETURN_TYPE/RETURN_NO/SERIAL_NO`）。
--      诊断四列 = 单号 + 单据数量 + 已对帐数量 + 本单数量，列间四空格、行间 CRLF，表头两行。
--   ② **写段**（无条件）：明细单重/毛重取产品档案并换算对帐数量/毛重数量 → 对帐数量为 0 的明细补值
--      → 主表按明细汇总（金额/税额/价税合计/数量合计/对帐数量/毛重数量，另含税总额与加工金额）。
-- 承载方式：
--   · 门控段 = 一条 SAVE 期 `qty-not-exceed`（`thisQty.agg=SUM` 分组形态 + `switch.gates` 模块门控 +
--     四列诊断 + 每列四空格），与 2707 同形；
--   · 写段 = 新增服务处理器 **`cus-account-sync`**（三张表与各列名分组声明，全部校验为物理列）；
--   · 目录里原有两条 SAVE 期 `reference-exists`（客户存在、送/退货单存在）此前因模块未接管
--     （`EFFECT_ENGINE_TAG=0`）而**从未生效**，本迁移一并把模块接进引擎使其生效——这正是旧过程的两条前置校验。
-- 忠实口径：求和结果与"其它费用/加工单价"的运算不做空值兜底（任一侧为空则整体为空），与旧过程一致。
-- 幂等：规则按 模块+SAVE+VALIDATION_KEY+SEQ 合并；动作按 模块+SAVE+SEQ 合并；接管开关仅当为 0 时置 1；
--       快照族名仅当仍含 `cus-account` 时改写。
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

DECLARE @ModuleId INT = 3014;
DECLARE @Family NVARCHAR(40) = N'cus-account';

IF NOT EXISTS (SELECT 1 FROM dbo.MODULES m WHERE m.M_IDX = @ModuleId
                 AND LTRIM(RTRIM(ISNULL(m.MASTER_TABLE, ''))) = N'CUS_ACCOUNT_M'
                 AND LTRIM(RTRIM(ISNULL(m.DETAIL_TABLE, ''))) = N'CUS_ACCOUNT_D')
    THROW 50001, N'模块 3014 形态不符（应为 CUS_ACCOUNT_M / CUS_ACCOUNT_D），迁移中止。', 1;

IF (SELECT COUNT(*) FROM dbo.MODULE_VALIDATION_RULE
     WHERE MODULE_ID = @ModuleId AND STAGE = N'SAVE' AND VALIDATION_KEY = N'reference-exists' AND ENABLED = 1) <> 2
    THROW 50002, N'模块 3014 缺少既有的两条 SAVE 期引用校验（客户存在 / 送退货单存在），迁移中止（先核对配置）。', 1;

IF EXISTS (SELECT 1 FROM dbo.MODULE_BUSINESS_ACTION WHERE MODULE_ID = @ModuleId AND EVENT_CODE = N'SAVE')
    THROW 50003, N'模块 3014 已有 SAVE 期业务动作配置，迁移中止（先核对配置）。', 1;

IF EXISTS (SELECT 1 FROM dbo.MODULE_VALIDATION_RULE
            WHERE MODULE_ID = @ModuleId AND STAGE = N'SAVE' AND VALIDATION_KEY = N'qty-not-exceed')
    THROW 50004, N'模块 3014 已有 SAVE 期不超量规则，迁移中止（先核对配置）。', 1;

/* ① 门控段：对帐不超送/退货单数量（数量按本单明细同引用键分组求和） */
DECLARE @CheckMessage NVARCHAR(400) =
    N'以下对帐已超出送货单数量' + CHAR(13) + CHAR(10)
    + N' 送货单号  送货数量  已对帐数量  单据数量'
    + CHAR(13) + CHAR(10) + N'{ROWS}';
DECLARE @CheckParam NVARCHAR(MAX) =
    N'{"mode":"usage-not-exceed","checks":['
    + N'{"targetTable":"COP_SEND_D",'
    + N'"match":[{"target":"SEND_TYPE","source":{"scope":"DETAIL","field":"S_R_TYPE"}},'
    + N'{"target":"SEND_NO","source":{"scope":"DETAIL","field":"S_R_NO"}},'
    + N'{"target":"SERIAL_NO","source":{"scope":"DETAIL","field":"S_R_SERIAL_NO"}}],'
    + N'"thisQty":{"scope":"DETAIL","agg":"SUM","terms":[{"field":"QTY","coef":1}]},'
    + N'"usage":{"scope":"TARGET","fields":["FINISHED_QTY"]},"limit":{"scope":"TARGET","fields":["QTY"]},'
    + N'"switch":{"gates":[{"scope":"MODULE","key":"ERROR_NO_SAVE","expect":1}]},'
    + N'"diagnosticFields":[{"scope":"TARGET","field":"SEND_NO"},{"scope":"TARGET","field":"QTY"},'
    + N'{"scope":"TARGET","field":"FINISHED_QTY"},{"scope":"THIS"}],'
    + N'"diagnosticCellSeparator":"    ","maxRows":100,'
    + N'"message":"以下对帐已超出送货单数量\r\n 送货单号  送货数量  已对帐数量  单据数量\r\n{ROWS}"},'
    + N'{"targetTable":"COP_RETURN_D",'
    + N'"match":[{"target":"RETURN_TYPE","source":{"scope":"DETAIL","field":"S_R_TYPE"}},'
    + N'{"target":"RETURN_NO","source":{"scope":"DETAIL","field":"S_R_NO"}},'
    + N'{"target":"SERIAL_NO","source":{"scope":"DETAIL","field":"S_R_SERIAL_NO"}}],'
    + N'"thisQty":{"scope":"DETAIL","agg":"SUM","terms":[{"field":"QTY","coef":1}]},'
    + N'"usage":{"scope":"TARGET","fields":["FINISHED_QTY"]},"limit":{"scope":"TARGET","fields":["QTY"]},'
    + N'"switch":{"gates":[{"scope":"MODULE","key":"ERROR_NO_SAVE","expect":1}]},'
    + N'"diagnosticFields":[{"scope":"TARGET","field":"RETURN_NO"},{"scope":"TARGET","field":"QTY"},'
    + N'{"scope":"TARGET","field":"FINISHED_QTY"},{"scope":"THIS"}],'
    + N'"diagnosticCellSeparator":"    ","maxRows":100,'
    + N'"message":"以下对帐已超出退货单数量\r\n 退货单号  退货数量  已对帐数量  单据数量\r\n{ROWS}"}]}';

MERGE dbo.MODULE_VALIDATION_RULE AS T
USING (SELECT @ModuleId AS MODULE_ID) AS S
   ON T.MODULE_ID = S.MODULE_ID AND T.STAGE = N'SAVE'
  AND T.VALIDATION_KEY = N'qty-not-exceed' AND T.SEQ = 3
WHEN MATCHED THEN
    UPDATE SET T.PARAM_STRUCT = @CheckParam, T.MESSAGE = @CheckMessage, T.ENABLED = 1,
               T.REMARK = N'对帐不超送/退货单数量（受模块门控；原 C# 海关对帐单判据的忠实移植）',
               T.LAST_UPDATE_BY = N'DbUp', T.LAST_UPDATE_DATE = SYSDATETIME()
WHEN NOT MATCHED THEN
    INSERT (MODULE_ID, STAGE, SEQ, VALIDATION_KEY, ENABLED, PARAM_STRUCT, MESSAGE, REMARK, SOURCE_REF,
            CREATE_PERSON, CREATE_DATE, LAST_UPDATE_BY, LAST_UPDATE_DATE)
    VALUES (S.MODULE_ID, N'SAVE', 3, N'qty-not-exceed', 1, @CheckParam, @CheckMessage,
            N'对帐不超送/退货单数量（受模块门控；原 C# 海关对帐单判据的忠实移植）', @Family,
            N'DbUp', SYSDATETIME(), N'DbUp', SYSDATETIME());

/* ② 写段：明细单重/毛重与主表汇总 */
DECLARE @EffectParam NVARCHAR(MAX) =
    N'{"master":{"table":"CUS_ACCOUNT_M","typeField":"ACCOUNT_TYPE","noField":"ACCOUNT_NO",'
    + N'"amountField":"AMOUNT","taxSumField":"TAX_SUM","amountTaxField":"AMOUNT_TAX","sumAmountField":"SUM_AMOUNT",'
    + N'"qtyTotalField":"QTY_TOTAL","cusQtyField":"CUS_QTY","cusGrossQtyField":"CUS_GROSS_QTY",'
    + N'"processAmountField":"PROCESS_AMOUNT","otherPriceField":"OTHER_PRICE","processPriceField":"PROCESS_PRICE"},'
    + N'"detail":{"table":"CUS_ACCOUNT_D","productField":"PRO_NO","qtyField":"QTY","suttleField":"SUTTLE",'
    + N'"cusQtyField":"CUS_QTY","grossWeightField":"GROSS_WEIGHT","cusGrossQtyField":"CUS_GROSS_QTY",'
    + N'"accountQtyField":"ACCOUNT_QTY","amountField":"AMOUNT","taxSumField":"TAX_SUM","amountTaxField":"AMOUNT_TAX"},'
    + N'"product":{"table":"PRODUCT","productField":"PRO_NO","suttleField":"SUTTLE","grossWeightField":"GROSS_WEIGHT"},'
    + N'"roundDigits":2}';

MERGE dbo.MODULE_BUSINESS_ACTION AS T
USING (SELECT @ModuleId AS MODULE_ID) AS S
   ON T.MODULE_ID = S.MODULE_ID AND T.EVENT_CODE = N'SAVE' AND T.SEQ = 1
WHEN MATCHED THEN
    UPDATE SET T.EFFECT_KEY = N'cus-account-sync', T.EFFECT_NAME = N'对帐单明细单重与主表汇总（保存期）',
               T.ENABLED = 1, T.FAIL_MODE = N'BLOCK', T.PARAM_STRUCT = @EffectParam,
               T.REVERSE_STRUCT = N'{"kind":"none"}',
               T.REMARK = N'原 C# 海关对帐单保存后动作的忠实移植（明细单重/毛重与数量换算、对帐数量补零、主表汇总）',
               T.LAST_UPDATE_BY = N'DbUp', T.LAST_UPDATE_DATE = SYSDATETIME()
WHEN NOT MATCHED THEN
    INSERT (MODULE_ID, EVENT_CODE, SEQ, EFFECT_KEY, EFFECT_NAME, ENABLED, FAIL_MODE, CONDITION_STRUCT,
            PARAM_STRUCT, REVERSE_STRUCT, REMARK, SOURCE_REF, CREATE_PERSON, CREATE_DATE, LAST_UPDATE_BY, LAST_UPDATE_DATE)
    VALUES (S.MODULE_ID, N'SAVE', 1, N'cus-account-sync', N'对帐单明细单重与主表汇总（保存期）', 1, N'BLOCK', NULL,
            @EffectParam, N'{"kind":"none"}',
            N'原 C# 海关对帐单保存后动作的忠实移植（明细单重/毛重与数量换算、对帐数量补零、主表汇总）', @Family,
            N'DbUp', SYSDATETIME(), N'DbUp', SYSDATETIME());

/* ③ 接进效果引擎（同时激活既有的两条引用校验规则） */
UPDATE dbo.MODULES SET EFFECT_ENGINE_TAG = 1, LAST_UPDATE_BY = N'DbUp', LAST_UPDATE_DATE = SYSDATETIME()
WHERE M_IDX = @ModuleId AND ISNULL(EFFECT_ENGINE_TAG, 0) = 0;

/* ④ 置空已发布快照里的族名 */
UPDATE S
   SET S.DEFINITION_JSON = REPLACE(S.DEFINITION_JSON, N'"DomainRule":"' + @Family + N'"', N'"DomainRule":null')
 FROM dbo.WORKBENCH_DEFINITION_SNAPSHOT S
WHERE S.MODULE_ID = @ModuleId AND S.IS_CURRENT = 1
  AND S.DEFINITION_JSON LIKE N'%"DomainRule":"' + @Family + N'"%';

/* ⑤ 收口断言 */
IF EXISTS (SELECT 1 FROM dbo.WORKBENCH_DEFINITION_SNAPSHOT S
           WHERE S.MODULE_ID = @ModuleId AND S.IS_CURRENT = 1
             AND S.DEFINITION_JSON LIKE N'%"DomainRule":"' + @Family + N'"%')
    THROW 50005, N'当前快照仍残留已退役的族名，迁移中止。', 1;

IF NOT EXISTS (SELECT 1 FROM dbo.MODULE_VALIDATION_RULE
               WHERE MODULE_ID = @ModuleId AND STAGE = N'SAVE' AND ENABLED = 1
                 AND VALIDATION_KEY = N'qty-not-exceed' AND SEQ = 3)
    THROW 50006, N'退役 C# 后缺少启用的 SAVE 期 qty-not-exceed 规则，迁移中止。', 1;

IF NOT EXISTS (SELECT 1 FROM dbo.MODULE_BUSINESS_ACTION
               WHERE MODULE_ID = @ModuleId AND EVENT_CODE = N'SAVE' AND SEQ = 1
                 AND EFFECT_KEY = N'cus-account-sync' AND ENABLED = 1)
    THROW 50007, N'退役 C# 后缺少启用的 SAVE 期 cus-account-sync 动作，迁移中止。', 1;

IF NOT EXISTS (SELECT 1 FROM dbo.MODULES WHERE M_IDX = @ModuleId AND ISNULL(EFFECT_ENGINE_TAG, 0) = 1)
    THROW 50008, N'模块 3014 未接管进效果引擎（EFFECT_ENGINE_TAG 仍为 0），迁移中止。', 1;

PRINT N'== cus-account 入效果目录并退役 C# 完成（3014，SAVE 期 cus-account-sync + 门控不超量规则；快照族名已置空）==';
