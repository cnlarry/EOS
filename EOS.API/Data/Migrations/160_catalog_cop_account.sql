-- ============================================================================
-- EOS.ERP migration 161: 应收对帐单入效果目录并退役 C#（cop-account：170101）
-- ----------------------------------------------------------------------------
-- 旧过程 `P_COP_ACCOUNT_After_Save`（受门控段调 `P_COP_ACCOUNT_CHECK`）与
-- C# `CopDomainRules.CopAccountAfterSaveAsync`：
--   ① **门控段**（`MODULES.ERROR_NO_SAVE=1` 时）两条"对帐不超送/退货单数量"：
--        送货：`ISNULL(COP_SEND_D.FINISHED_QTY,0) + SUM(本单明细 QTY) > ISNULL(COP_SEND_D.QTY,0)`
--              （本单明细按 `(S_R_TYPE, S_R_NO, S_R_SERIAL_NO)` 分组求和后与送货行对齐）；
--        退货：同形，换 `COP_RETURN_D`（`RETURN_TYPE/RETURN_NO/SERIAL_NO`）。
--      诊断四列 = 单号 + 单据数量 + 已对帐数量 + 本单数量，列间四空格、行间 CRLF，表头两行。
--   ② **写段**（无条件）：主表 金额/税额/价税合计/数量合计 按明细聚合回写（舍入 2 位），
--      含税总额 ＝ 价税合计 + 其它费用；明细为空时不回写（既有实现为内连接形态）。
-- 承载方式：
--   · 门控段 = SAVE 期 `qty-not-exceed`（`thisQty.agg=SUM` 分组形态 + `switch.gates` 模块门控 + 四列诊断），
--     与 3014 海关对帐单同形（两边是同一段既有逻辑的两份拷贝，只是明细表不同）；
--   · 写段 = 新增服务处理器 **`cop-account-rollup`**；
--   · 目录里原有两条 SAVE 期 `reference-exists`（客户存在且未停用、送/退货单存在）保持不动。
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

DECLARE @ModuleId INT = 170101;
DECLARE @Family NVARCHAR(40) = N'cop-account';

IF NOT EXISTS (SELECT 1 FROM dbo.MODULES m WHERE m.M_IDX = @ModuleId
                 AND LTRIM(RTRIM(ISNULL(m.MASTER_TABLE, ''))) = N'COP_ACCOUNT_M'
                 AND LTRIM(RTRIM(ISNULL(m.DETAIL_TABLE, ''))) = N'COP_ACCOUNT_D')
    THROW 50001, N'模块 170101 形态不符（应为 COP_ACCOUNT_M / COP_ACCOUNT_D），迁移中止。', 1;

IF NOT EXISTS (SELECT 1 FROM dbo.MODULE_VALIDATION_RULE
               WHERE MODULE_ID = @ModuleId AND STAGE = N'SAVE' AND VALIDATION_KEY = N'reference-exists' AND ENABLED = 1)
    THROW 50002, N'模块 170101 缺少既有的 SAVE 期引用校验，迁移中止（先核对配置）。', 1;

IF EXISTS (SELECT 1 FROM dbo.MODULE_BUSINESS_ACTION WHERE MODULE_ID = @ModuleId AND EVENT_CODE = N'SAVE')
    THROW 50003, N'模块 170101 已有 SAVE 期业务动作配置，迁移中止（先核对配置）。', 1;

/* ① 门控段：对帐不超送/退货单数量 */
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
    UPDATE SET T.PARAM_STRUCT = @CheckParam, T.MESSAGE = NULL, T.ENABLED = 1,
               T.REMARK = N'对帐不超送/退货单数量（受模块门控；原 C# 应收对帐单判据的忠实移植）',
               T.LAST_UPDATE_BY = N'DbUp', T.LAST_UPDATE_DATE = SYSDATETIME()
WHEN NOT MATCHED THEN
    INSERT (MODULE_ID, STAGE, SEQ, VALIDATION_KEY, ENABLED, PARAM_STRUCT, MESSAGE, REMARK, SOURCE_REF,
            CREATE_PERSON, CREATE_DATE, LAST_UPDATE_BY, LAST_UPDATE_DATE)
    VALUES (S.MODULE_ID, N'SAVE', 3, N'qty-not-exceed', 1, @CheckParam, NULL,
            N'对帐不超送/退货单数量（受模块门控；原 C# 应收对帐单判据的忠实移植）', @Family,
            N'DbUp', SYSDATETIME(), N'DbUp', SYSDATETIME());

/* ② 写段：主表金额汇总 */
DECLARE @EffectParam NVARCHAR(MAX) =
    N'{"master":{"typeField":"ACCOUNT_TYPE","noField":"ACCOUNT_NO","amountField":"AMOUNT","taxSumField":"TAX_SUM",'
    + N'"amountTaxField":"AMOUNT_TAX","sumAmountField":"SUM_AMOUNT","otherPriceField":"OTHER_PRICE",'
    + N'"qtyTotalField":"QTY_TOTAL"},'
    + N'"detail":{"table":"COP_ACCOUNT_D","qtyField":"QTY","amountField":"AMOUNT","taxSumField":"TAX_SUM",'
    + N'"amountTaxField":"AMOUNT_TAX"},"roundDigits":2}';

MERGE dbo.MODULE_BUSINESS_ACTION AS T
USING (SELECT @ModuleId AS MODULE_ID) AS S
   ON T.MODULE_ID = S.MODULE_ID AND T.EVENT_CODE = N'SAVE' AND T.SEQ = 1
WHEN MATCHED THEN
    UPDATE SET T.EFFECT_KEY = N'cop-account-rollup', T.EFFECT_NAME = N'应收对帐单主表金额汇总（保存期）',
               T.ENABLED = 1, T.FAIL_MODE = N'BLOCK', T.PARAM_STRUCT = @EffectParam,
               T.REVERSE_STRUCT = N'{"kind":"none"}',
               T.REMARK = N'原 C# 应收对帐单保存后动作的忠实移植（明细金额/税额/价税合计/数量合计回写主表）',
               T.LAST_UPDATE_BY = N'DbUp', T.LAST_UPDATE_DATE = SYSDATETIME()
WHEN NOT MATCHED THEN
    INSERT (MODULE_ID, EVENT_CODE, SEQ, EFFECT_KEY, EFFECT_NAME, ENABLED, FAIL_MODE, CONDITION_STRUCT,
            PARAM_STRUCT, REVERSE_STRUCT, REMARK, SOURCE_REF, CREATE_PERSON, CREATE_DATE, LAST_UPDATE_BY, LAST_UPDATE_DATE)
    VALUES (S.MODULE_ID, N'SAVE', 1, N'cop-account-rollup', N'应收对帐单主表金额汇总（保存期）', 1, N'BLOCK', NULL,
            @EffectParam, N'{"kind":"none"}',
            N'原 C# 应收对帐单保存后动作的忠实移植（明细金额/税额/价税合计/数量合计回写主表）', @Family,
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
                 AND VALIDATION_KEY = N'qty-not-exceed' AND SEQ = 3)
    THROW 50005, N'退役 C# 后缺少启用的 SAVE 期 qty-not-exceed 规则，迁移中止。', 1;

IF NOT EXISTS (SELECT 1 FROM dbo.MODULE_BUSINESS_ACTION
               WHERE MODULE_ID = @ModuleId AND EVENT_CODE = N'SAVE' AND SEQ = 1
                 AND EFFECT_KEY = N'cop-account-rollup' AND ENABLED = 1)
    THROW 50006, N'退役 C# 后缺少启用的 SAVE 期 cop-account-rollup 动作，迁移中止。', 1;

PRINT N'== cop-account 入效果目录并退役 C# 完成（170101，SAVE 期 cop-account-rollup + 门控不超量规则）==';
