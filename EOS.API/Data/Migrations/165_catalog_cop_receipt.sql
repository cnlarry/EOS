-- ============================================================================
-- EOS.ERP migration 166: 收款单入效果目录并退役 C#（cop-receipt：170102）；
--                       同时把付款单（170202）的动作参数改名为收付单共用的中性键名
-- ----------------------------------------------------------------------------
-- 收款单与付款单是**同一段旧逻辑的两份拷贝**（`P_COP_RECEIPT_After_Save`/`P_PUR_PAY_After_Save`）：
--   ① 主表 冲抵合计 ＝ 冲抵表合计，结算额 ＝ 价款税合计-现金折扣-冲抵合计，并刷新最后更新日期；
--   ② 结算额为负 ⇒ 拒绝（无门控）："实收金额不能为负数" / "实付金额不能为负数"；
--   ③ 受 `MODULES.ERROR_NO_SAVE=1` 门控：结算额超（价款税合计-折扣-冲抵 [+0.1 容差，仅收款单]）⇒ 拒绝；
--   ④ 受同一门控：被引用对帐单"已结算+本次结算"超应结算额 ⇒ 拒绝，四列诊断（列间 7/10/10 空格）。
-- 承载方式：两族共用处理器内核 `PrepayOffsetRunner`，各自一个薄处理器
-- （`pur-pay-offset` / `cop-receipt-offset`）；参数键名统一为中性名（冲抵合计列 `offsetSumField`、
-- 结算列 `settleSumField`、被引用行结算列 `settledAmountField`），故本迁移同时把 170202 已落库的参数
-- 改名为同一套键名（值不变，仅键名），避免"付款单专用键名"被收款单沿用。
-- 两族均为"先写后校验"的有序链 ⇒ 整链在一个处理器内执行（命中即抛校验异常阻断保存），
-- 不做成"目录校验 + 写动作"两段（SAVE 期目录校验跑在效果之前，会读到尚未刷新的结算列）。
-- 幂等：动作按 模块+SAVE+SEQ 合并；快照族名仅当仍含对应族名时改写。
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

/* ① 收款单（170102）形态与前置配置 */
IF NOT EXISTS (SELECT 1 FROM dbo.MODULES m WHERE m.M_IDX = 170102
                 AND LTRIM(RTRIM(ISNULL(m.MASTER_TABLE, ''))) = N'COP_RECEIPT_M'
                 AND LTRIM(RTRIM(ISNULL(m.DETAIL_TABLE, ''))) = N'COP_RECEIPT_D')
    THROW 50001, N'模块 170102 形态不符（应为 COP_RECEIPT_M / COP_RECEIPT_D），迁移中止。', 1;

IF EXISTS (SELECT 1 FROM dbo.MODULE_VALIDATION_RULE WHERE MODULE_ID = 170102 AND STAGE = N'SAVE')
    THROW 50002, N'模块 170102 已有 SAVE 期校验规则，迁移中止（先核对配置）。', 1;

IF EXISTS (SELECT 1 FROM dbo.MODULE_BUSINESS_ACTION WHERE MODULE_ID = 170102 AND EVENT_CODE = N'SAVE')
    THROW 50003, N'模块 170102 已有 SAVE 期业务动作配置，迁移中止（先核对配置）。', 1;

/* ② 付款单（170202）参数改名：专用键名 → 收付单共用中性键名（值不变） */
UPDATE dbo.MODULE_BUSINESS_ACTION
   SET PARAM_STRUCT = REPLACE(REPLACE(REPLACE(REPLACE(PARAM_STRUCT,
           N'"prepaySumField"', N'"offsetSumField"'),
           N'"payoutSumField"', N'"settleSumField"'),
           N'"payoutAmountField"', N'"settledAmountField"'),
           N'"dueMessage"', N'"dueMessage"'),
       LAST_UPDATE_BY = N'DbUp', LAST_UPDATE_DATE = SYSDATETIME()
 WHERE MODULE_ID = 170202 AND EVENT_CODE = N'SAVE' AND SEQ = 1
   AND EFFECT_KEY = N'pur-pay-offset'
   AND PARAM_STRUCT LIKE N'%"prepaySumField"%';

/* ③ 收款单写动作 */
DECLARE @EffectParam NVARCHAR(MAX) =
    N'{"master":{"typeField":"RECEIPT_TYPE","noField":"RECEIPT_NO","amountTaxField":"AMOUNT_TAX",'
    + N'"rebateSumField":"REBATE_SUM","offsetSumField":"PREPAY_SUM","settleSumField":"RECEIVE_SUM",'
    + N'"lastUpdateField":"LAST_UPDATE_DATE"},'
    + N'"offset":{"table":"COP_RECEIPT_PREPAY","typeField":"RECEIPT_TYPE","noField":"RECEIPT_NO",'
    + N'"amountField":"PREPAY_AMOUNT"},'
    + N'"due":{"table":"COP_ACCOUNT_M","typeField":"ACCOUNT_TYPE","noField":"ACCOUNT_NO",'
    + N'"sumAmountField":"SUM_AMOUNT","settledAmountField":"RECEIVE_AMOUNT"},'
    + N'"detail":{"table":"COP_RECEIPT_D","typeField":"RECEIPT_TYPE","noField":"RECEIPT_NO",'
    + N'"dueTypeField":"ACCOUNT_TYPE","dueNoField":"ACCOUNT_NO","settledAmountField":"RECEIVE_AMOUNT"},'
    + N'"gateFlag":"ERROR_NO_SAVE","exceedOffset":0.1,'
    + N'"negativeMessage":"实收金额不能为负数",'
    + N'"exceedMessage":"实收金额 不能大于 应收金额-现金折扣-预收冲帐",'
    + N'"dueMessage":"以下会出现对帐单已收款大于应收款\r\n对帐单号     应收款       已收款       本次收款\r\n{ROWS}"}';

MERGE dbo.MODULE_BUSINESS_ACTION AS T
USING (SELECT 170102 AS MODULE_ID) AS S
   ON T.MODULE_ID = S.MODULE_ID AND T.EVENT_CODE = N'SAVE' AND T.SEQ = 1
WHEN MATCHED THEN
    UPDATE SET T.EFFECT_KEY = N'cop-receipt-offset', T.EFFECT_NAME = N'收款单预收冲抵汇总与实收校验（保存期）',
               T.ENABLED = 1, T.FAIL_MODE = N'BLOCK', T.PARAM_STRUCT = @EffectParam,
               T.REVERSE_STRUCT = N'{"kind":"none"}',
               T.REMARK = N'原 C# 收款单保存后动作的忠实移植（预收冲抵汇总 + 实收为负/超额/对帐单超收的拒绝）',
               T.LAST_UPDATE_BY = N'DbUp', T.LAST_UPDATE_DATE = SYSDATETIME()
WHEN NOT MATCHED THEN
    INSERT (MODULE_ID, EVENT_CODE, SEQ, EFFECT_KEY, EFFECT_NAME, ENABLED, FAIL_MODE, CONDITION_STRUCT,
            PARAM_STRUCT, REVERSE_STRUCT, REMARK, SOURCE_REF, CREATE_PERSON, CREATE_DATE, LAST_UPDATE_BY, LAST_UPDATE_DATE)
    VALUES (S.MODULE_ID, N'SAVE', 1, N'cop-receipt-offset', N'收款单预收冲抵汇总与实收校验（保存期）', 1, N'BLOCK', NULL,
            @EffectParam, N'{"kind":"none"}',
            N'原 C# 收款单保存后动作的忠实移植（预收冲抵汇总 + 实收为负/超额/对帐单超收的拒绝）',
            N'cop-receipt', N'DbUp', SYSDATETIME(), N'DbUp', SYSDATETIME());

/* ④ 置空两族在已发布快照里的族名 */
UPDATE S
   SET S.DEFINITION_JSON = REPLACE(S.DEFINITION_JSON, N'"DomainRule":"' + F.FAMILY + N'"', N'"DomainRule":null')
 FROM dbo.WORKBENCH_DEFINITION_SNAPSHOT S
 JOIN (SELECT 170102 AS MODULE_ID, N'cop-receipt' AS FAMILY) F ON F.MODULE_ID = S.MODULE_ID
WHERE S.IS_CURRENT = 1
  AND S.DEFINITION_JSON LIKE N'%"DomainRule":"' + F.FAMILY + N'"%';

/* ⑤ 收口断言 */
IF EXISTS (SELECT 1 FROM dbo.WORKBENCH_DEFINITION_SNAPSHOT S
           WHERE S.MODULE_ID = 170102 AND S.IS_CURRENT = 1
             AND S.DEFINITION_JSON LIKE N'%"DomainRule":"cop-receipt"%')
    THROW 50004, N'当前快照仍残留已退役的族名，迁移中止。', 1;

IF NOT EXISTS (SELECT 1 FROM dbo.MODULE_BUSINESS_ACTION
               WHERE MODULE_ID = 170102 AND EVENT_CODE = N'SAVE' AND SEQ = 1
                 AND EFFECT_KEY = N'cop-receipt-offset' AND ENABLED = 1)
    THROW 50005, N'退役 C# 后缺少启用的 SAVE 期 cop-receipt-offset 动作，迁移中止。', 1;

IF EXISTS (SELECT 1 FROM dbo.MODULE_BUSINESS_ACTION
           WHERE MODULE_ID = 170202 AND EVENT_CODE = N'SAVE' AND SEQ = 1
             AND (PARAM_STRUCT LIKE N'%"prepaySumField"%' OR PARAM_STRUCT LIKE N'%"payoutSumField"%'
                  OR PARAM_STRUCT LIKE N'%"payoutAmountField"%'))
    THROW 50006, N'付款单动作参数未完成中性键名改名，迁移中止。', 1;

PRINT N'== cop-receipt 入效果目录并退役 C# 完成（170102，SAVE 期 cop-receipt-offset）；170202 参数键名已统一 ==';
