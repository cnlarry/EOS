-- ============================================================================
-- EOS.ERP migration 165: 付款单入效果目录并退役 C#（pur-pay：170202）
-- ----------------------------------------------------------------------------
-- 旧过程 `P_PUR_PAY_After_Save`（受门控段调 `P_PUR_PAY_CHECK`）与 C# `PurDomainRules.PurPayAfterSaveAsync`
-- 是**先写后校验**的有序链：
--   ① 主表 `PREPAY_SUM` ＝ 冲抵表 `PUR_PAY_PREPAY` 合计，
--      `PAYOUT_SUM` ＝ `AMOUNT_TAX - REBATE_SUM - PREPAY_SUM`，并刷新最后更新日期；
--   ② `PAYOUT_SUM < 0` ⇒ 拒绝（"实付金额不能为负数"，**无门控**）；
--   ③ 受 `MODULES.ERROR_NO_SAVE=1` 门控：`PAYOUT_SUM > AMOUNT_TAX-REBATE_SUM-PREPAY_SUM` ⇒ 拒绝；
--   ④ 受同一门控：对帐单"已付款 + 本单本次付款"不得超出应付款，命中回报
--      对帐单号/应付款/已付款/本次付款四列（列间 7/10/10 空格，与既有实现逐字一致）。
-- 承载方式：**整链放在一个服务处理器 `pur-pay-offset`**（顺序与旧过程一致；校验命中即抛
-- `EffectValidationException` 阻断保存）。之所以不做成"校验目录 + 写动作"两段：SAVE 期目录校验跑在效果
-- **之前**，而本族的②③依赖①刚写入的 `PREPAY_SUM`/`PAYOUT_SUM`，拆开会让校验读到旧值。
-- 幂等：动作按 模块+SAVE+SEQ 合并；快照族名仅当仍含 `pur-pay` 时改写。
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

DECLARE @ModuleId INT = 170202;
DECLARE @Family NVARCHAR(40) = N'pur-pay';

IF NOT EXISTS (SELECT 1 FROM dbo.MODULES m WHERE m.M_IDX = @ModuleId
                 AND LTRIM(RTRIM(ISNULL(m.MASTER_TABLE, ''))) = N'PUR_PAY_M'
                 AND LTRIM(RTRIM(ISNULL(m.DETAIL_TABLE, ''))) = N'PUR_PAY_D')
    THROW 50001, N'模块 170202 形态不符（应为 PUR_PAY_M / PUR_PAY_D），迁移中止。', 1;

IF EXISTS (SELECT 1 FROM dbo.MODULE_VALIDATION_RULE WHERE MODULE_ID = @ModuleId AND STAGE = N'SAVE')
    THROW 50002, N'模块 170202 已有 SAVE 期校验规则，迁移中止（先核对配置）。', 1;

IF EXISTS (SELECT 1 FROM dbo.MODULE_BUSINESS_ACTION WHERE MODULE_ID = @ModuleId AND EVENT_CODE = N'SAVE')
    THROW 50003, N'模块 170202 已有 SAVE 期业务动作配置，迁移中止（先核对配置）。', 1;

DECLARE @EffectParam NVARCHAR(MAX) =
    N'{"master":{"typeField":"PAY_TYPE","noField":"PAY_NO","amountTaxField":"AMOUNT_TAX",'
    + N'"rebateSumField":"REBATE_SUM","prepaySumField":"PREPAY_SUM","payoutSumField":"PAYOUT_SUM",'
    + N'"lastUpdateField":"LAST_UPDATE_DATE"},'
    + N'"offset":{"table":"PUR_PAY_PREPAY","typeField":"PAY_TYPE","noField":"PAY_NO","amountField":"PREPAY_AMOUNT"},'
    + N'"due":{"table":"PUR_DUE_M","typeField":"DUE_TYPE","noField":"DUE_NO","sumAmountField":"SUM_AMOUNT",'
    + N'"payoutAmountField":"PAYOUT_AMOUNT"},'
    + N'"detail":{"table":"PUR_PAY_D","typeField":"PAY_TYPE","noField":"PAY_NO","dueTypeField":"DUE_TYPE",'
    + N'"dueNoField":"DUE_NO","payoutAmountField":"PAYOUT_AMOUNT"},'
    + N'"gateFlag":"ERROR_NO_SAVE",'
    + N'"negativeMessage":"实付金额不能为负数",'
    + N'"exceedMessage":"实付金额 不能大于 应付金额-现金折扣-预付冲帐",'
    + N'"dueMessage":"以下会出现对帐单已付款大于应付款\r\n对帐单号     应付款       已付款       本次付款\r\n{ROWS}"}';

MERGE dbo.MODULE_BUSINESS_ACTION AS T
USING (SELECT @ModuleId AS MODULE_ID) AS S
   ON T.MODULE_ID = S.MODULE_ID AND T.EVENT_CODE = N'SAVE' AND T.SEQ = 1
WHEN MATCHED THEN
    UPDATE SET T.EFFECT_KEY = N'pur-pay-offset', T.EFFECT_NAME = N'付款单预付冲抵汇总与实付校验（保存期）',
               T.ENABLED = 1, T.FAIL_MODE = N'BLOCK', T.PARAM_STRUCT = @EffectParam,
               T.REVERSE_STRUCT = N'{"kind":"none"}',
               T.REMARK = N'原 C# 付款单保存后动作的忠实移植（预pay冲抵汇总 + 实付为负/超额的拒绝 + 对帐单已付款超应付的拒绝）',
               T.LAST_UPDATE_BY = N'DbUp', T.LAST_UPDATE_DATE = SYSDATETIME()
WHEN NOT MATCHED THEN
    INSERT (MODULE_ID, EVENT_CODE, SEQ, EFFECT_KEY, EFFECT_NAME, ENABLED, FAIL_MODE, CONDITION_STRUCT,
            PARAM_STRUCT, REVERSE_STRUCT, REMARK, SOURCE_REF, CREATE_PERSON, CREATE_DATE, LAST_UPDATE_BY, LAST_UPDATE_DATE)
    VALUES (S.MODULE_ID, N'SAVE', 1, N'pur-pay-offset', N'付款单预付冲抵汇总与实付校验（保存期）', 1, N'BLOCK', NULL,
            @EffectParam, N'{"kind":"none"}',
            N'原 C# 付款单保存后动作的忠实移植（预pay冲抵汇总 + 实付为负/超额的拒绝 + 对帐单已付款超应付的拒绝）',
            @Family, N'DbUp', SYSDATETIME(), N'DbUp', SYSDATETIME());

UPDATE S
   SET S.DEFINITION_JSON = REPLACE(S.DEFINITION_JSON, N'"DomainRule":"' + @Family + N'"', N'"DomainRule":null')
 FROM dbo.WORKBENCH_DEFINITION_SNAPSHOT S
WHERE S.MODULE_ID = @ModuleId AND S.IS_CURRENT = 1
  AND S.DEFINITION_JSON LIKE N'%"DomainRule":"' + @Family + N'"%';

IF EXISTS (SELECT 1 FROM dbo.WORKBENCH_DEFINITION_SNAPSHOT S
           WHERE S.MODULE_ID = @ModuleId AND S.IS_CURRENT = 1
             AND S.DEFINITION_JSON LIKE N'%"DomainRule":"' + @Family + N'"%')
    THROW 50004, N'当前快照仍残留已退役的族名，迁移中止。', 1;

IF NOT EXISTS (SELECT 1 FROM dbo.MODULE_BUSINESS_ACTION
               WHERE MODULE_ID = @ModuleId AND EVENT_CODE = N'SAVE' AND SEQ = 1
                 AND EFFECT_KEY = N'pur-pay-offset' AND ENABLED = 1)
    THROW 50005, N'退役 C# 后缺少启用的 SAVE 期 pur-pay-offset 动作，迁移中止。', 1;

PRINT N'== pur-pay 入效果目录并退役 C# 完成（170202，SAVE 期 pur-pay-offset 有序链）==';
