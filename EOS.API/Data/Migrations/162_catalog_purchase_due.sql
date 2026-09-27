-- ============================================================================
-- EOS.ERP migration 163: 应付对帐单入效果目录并退役 C#（purchase-due：170201）
-- ----------------------------------------------------------------------------
-- 旧过程 `P_PUR_DUE_After_Save`（受门控段调 `P_PUR_DUE_CHECK`）与
-- C# `PurDomainRules.PurchaseDueAfterSaveAsync`：
--   ① **门控段**（`MODULES.ERROR_NO_SAVE=1` 时）两条"对帐不超收料/退料单数量"：
--        收料：`ISNULL(PUR_RECEIVE_D.FINISHED_QTY,0) + SUM(本单明细 QTY) > ISNULL(PUR_RECEIVE_D.QTY,0)`
--              （本单明细按 `(R_C_TYPE, R_C_NO, R_C_SERIAL_NO)` 分组求和后与收料行对齐）；
--        退料：同形，换 `PUR_CANCEL_D`（`CANCEL_TYPE/CANCEL_NO/SERIAL_NO`）。
--      诊断四列 = 单号 + 单据数量 + 已对帐数量 + 本单数量，列间四空格、行间 CRLF，表头两行
--      （注意表头是"收料单号"/"退料单号"，与 170101 应收对帐单同形但措辞不同）。
--   ② **写段**（无条件）：主表 金额/税额/价税合计/数量合计 按明细聚合回写（舍入 2 位），
--      含税总额 ＝ 价税合计 + 其它费用；明细为空时不回写（既有实现为内连接形态）。
-- 承载方式：门控段 = SAVE 期 `qty-not-exceed`（`thisQty.agg=SUM` 分组形态 + 模块门控 + 四列诊断）；
--           写段 = 新增服务处理器 **`purchase-due-rollup`**。该模块此前目录里没有任何 SAVE 期规则。
-- 幂等：规则按 模块+SAVE+VALIDATION_KEY+SEQ 合并；动作按 模块+SAVE+SEQ 合并；快照族名仅当仍含族名时改写。
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

DECLARE @ModuleId INT = 170201;
DECLARE @Family NVARCHAR(40) = N'purchase-due';

IF NOT EXISTS (SELECT 1 FROM dbo.MODULES m WHERE m.M_IDX = @ModuleId
                 AND LTRIM(RTRIM(ISNULL(m.MASTER_TABLE, ''))) = N'PUR_DUE_M'
                 AND LTRIM(RTRIM(ISNULL(m.DETAIL_TABLE, ''))) = N'PUR_DUE_D')
    THROW 50001, N'模块 170201 形态不符（应为 PUR_DUE_M / PUR_DUE_D），迁移中止。', 1;

IF (SELECT COUNT(*) FROM dbo.MODULE_VALIDATION_RULE WHERE MODULE_ID = @ModuleId) <> 0
    THROW 50002, N'模块 170201 已有校验规则，迁移中止（先核对配置）。', 1;

IF EXISTS (SELECT 1 FROM dbo.MODULE_BUSINESS_ACTION WHERE MODULE_ID = @ModuleId AND EVENT_CODE = N'SAVE')
    THROW 50003, N'模块 170201 已有 SAVE 期业务动作配置，迁移中止（先核对配置）。', 1;

/* ① 门控段：对帐不超收料/退料单数量 */
DECLARE @CheckParam NVARCHAR(MAX) =
    N'{"mode":"usage-not-exceed","checks":['
    + N'{"targetTable":"PUR_RECEIVE_D",'
    + N'"match":[{"target":"RECEIVE_TYPE","source":{"scope":"DETAIL","field":"R_C_TYPE"}},'
    + N'{"target":"RECEIVE_NO","source":{"scope":"DETAIL","field":"R_C_NO"}},'
    + N'{"target":"SERIAL_NO","source":{"scope":"DETAIL","field":"R_C_SERIAL_NO"}}],'
    + N'"thisQty":{"scope":"DETAIL","agg":"SUM","terms":[{"field":"QTY","coef":1}]},'
    + N'"usage":{"scope":"TARGET","fields":["FINISHED_QTY"]},"limit":{"scope":"TARGET","fields":["QTY"]},'
    + N'"switch":{"gates":[{"scope":"MODULE","key":"ERROR_NO_SAVE","expect":1}]},'
    + N'"diagnosticFields":[{"scope":"TARGET","field":"RECEIVE_NO"},{"scope":"TARGET","field":"QTY"},'
    + N'{"scope":"TARGET","field":"FINISHED_QTY"},{"scope":"THIS"}],'
    + N'"diagnosticCellSeparator":"    ","maxRows":100,'
    + N'"message":"以下对帐已超出收料单数量\r\n 收料单号  收料数量  已对帐数量  单据数量\r\n{ROWS}"},'
    + N'{"targetTable":"PUR_CANCEL_D",'
    + N'"match":[{"target":"CANCEL_TYPE","source":{"scope":"DETAIL","field":"R_C_TYPE"}},'
    + N'{"target":"CANCEL_NO","source":{"scope":"DETAIL","field":"R_C_NO"}},'
    + N'{"target":"SERIAL_NO","source":{"scope":"DETAIL","field":"R_C_SERIAL_NO"}}],'
    + N'"thisQty":{"scope":"DETAIL","agg":"SUM","terms":[{"field":"QTY","coef":1}]},'
    + N'"usage":{"scope":"TARGET","fields":["FINISHED_QTY"]},"limit":{"scope":"TARGET","fields":["QTY"]},'
    + N'"switch":{"gates":[{"scope":"MODULE","key":"ERROR_NO_SAVE","expect":1}]},'
    + N'"diagnosticFields":[{"scope":"TARGET","field":"CANCEL_NO"},{"scope":"TARGET","field":"QTY"},'
    + N'{"scope":"TARGET","field":"FINISHED_QTY"},{"scope":"THIS"}],'
    + N'"diagnosticCellSeparator":"    ","maxRows":100,'
    + N'"message":"以下对帐已超出退料单数量\r\n 退料单号  退料数量  已对帐数量  单据数量\r\n{ROWS}"}]}';

MERGE dbo.MODULE_VALIDATION_RULE AS T
USING (SELECT @ModuleId AS MODULE_ID) AS S
   ON T.MODULE_ID = S.MODULE_ID AND T.STAGE = N'SAVE'
  AND T.VALIDATION_KEY = N'qty-not-exceed' AND T.SEQ = 1
WHEN MATCHED THEN
    UPDATE SET T.PARAM_STRUCT = @CheckParam, T.MESSAGE = NULL, T.ENABLED = 1,
               T.REMARK = N'对帐不超收料/退料单数量（受模块门控；原 C# 应付对帐单判据的忠实移植）',
               T.LAST_UPDATE_BY = N'DbUp', T.LAST_UPDATE_DATE = SYSDATETIME()
WHEN NOT MATCHED THEN
    INSERT (MODULE_ID, STAGE, SEQ, VALIDATION_KEY, ENABLED, PARAM_STRUCT, MESSAGE, REMARK, SOURCE_REF,
            CREATE_PERSON, CREATE_DATE, LAST_UPDATE_BY, LAST_UPDATE_DATE)
    VALUES (S.MODULE_ID, N'SAVE', 1, N'qty-not-exceed', 1, @CheckParam, NULL,
            N'对帐不超收料/退料单数量（受模块门控；原 C# 应付对帐单判据的忠实移植）', @Family,
            N'DbUp', SYSDATETIME(), N'DbUp', SYSDATETIME());

/* ② 写段：主表金额汇总 */
DECLARE @EffectParam NVARCHAR(MAX) =
    N'{"master":{"typeField":"DUE_TYPE","noField":"DUE_NO","amountField":"AMOUNT","taxSumField":"TAX_SUM",'
    + N'"amountTaxField":"AMOUNT_TAX","sumAmountField":"SUM_AMOUNT","otherPriceField":"OTHER_PRICE",'
    + N'"qtyTotalField":"QTY_TOTAL"},'
    + N'"detail":{"table":"PUR_DUE_D","qtyField":"QTY","amountField":"AMOUNT","taxSumField":"TAX_SUM",'
    + N'"amountTaxField":"AMOUNT_TAX"},"roundDigits":2}';

MERGE dbo.MODULE_BUSINESS_ACTION AS T
USING (SELECT @ModuleId AS MODULE_ID) AS S
   ON T.MODULE_ID = S.MODULE_ID AND T.EVENT_CODE = N'SAVE' AND T.SEQ = 1
WHEN MATCHED THEN
    UPDATE SET T.EFFECT_KEY = N'purchase-due-rollup', T.EFFECT_NAME = N'应付对帐单主表金额汇总（保存期）',
               T.ENABLED = 1, T.FAIL_MODE = N'BLOCK', T.PARAM_STRUCT = @EffectParam,
               T.REVERSE_STRUCT = N'{"kind":"none"}',
               T.REMARK = N'原 C# 应付对帐单保存后动作的忠实移植（明细金额/税额/价税合计/数量合计回写主表）',
               T.LAST_UPDATE_BY = N'DbUp', T.LAST_UPDATE_DATE = SYSDATETIME()
WHEN NOT MATCHED THEN
    INSERT (MODULE_ID, EVENT_CODE, SEQ, EFFECT_KEY, EFFECT_NAME, ENABLED, FAIL_MODE, CONDITION_STRUCT,
            PARAM_STRUCT, REVERSE_STRUCT, REMARK, SOURCE_REF, CREATE_PERSON, CREATE_DATE, LAST_UPDATE_BY, LAST_UPDATE_DATE)
    VALUES (S.MODULE_ID, N'SAVE', 1, N'purchase-due-rollup', N'应付对帐单主表金额汇总（保存期）', 1, N'BLOCK', NULL,
            @EffectParam, N'{"kind":"none"}',
            N'原 C# 应付对帐单保存后动作的忠实移植（明细金额/税额/价税合计/数量合计回写主表）', @Family,
            N'DbUp', SYSDATETIME(), N'DbUp', SYSDATETIME());

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
                 AND VALIDATION_KEY = N'qty-not-exceed' AND SEQ = 1)
    THROW 50005, N'退役 C# 后缺少启用的 SAVE 期 qty-not-exceed 规则，迁移中止。', 1;

IF NOT EXISTS (SELECT 1 FROM dbo.MODULE_BUSINESS_ACTION
               WHERE MODULE_ID = @ModuleId AND EVENT_CODE = N'SAVE' AND SEQ = 1
                 AND EFFECT_KEY = N'purchase-due-rollup' AND ENABLED = 1)
    THROW 50006, N'退役 C# 后缺少启用的 SAVE 期 purchase-due-rollup 动作，迁移中止。', 1;

PRINT N'== purchase-due 入效果目录并退役 C# 完成（170201，SAVE 期 purchase-due-rollup + 门控不超量规则）==';
