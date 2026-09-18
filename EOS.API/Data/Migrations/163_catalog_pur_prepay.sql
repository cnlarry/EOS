-- ============================================================================
-- EOS.ERP migration 164: 预付帐款单入效果目录并退役 C#（pur-prepay：170203）
-- ----------------------------------------------------------------------------
-- 来源旧过程 `P_PUR_PREPAY_After_Save`（受门控段调 `P_PUR_PREPAY_CHECK`）与
-- C# `PurDomainRules.PurPrepayAfterSaveAsync`：
--   ① 厂商存在且未停用 —— 早已在目录里（SAVE 期 `reference-exists`），本迁移不动；
--   ② **门控段**（`MODULES.ERROR_NO_SAVE=1` 时）"已预付金额不得超出采购行金额"：
--        `SUM(本单明细 AMOUNT) > PUR_PURCHASE_D.AMOUNT - PUR_PURCHASE_D.FINISHED_AMOUNT`
--        （本单明细按 `(PURCHASE_TYPE, PURCHASE_NO, PURCHASE_SERIAL_NO)` 分组求和后与采购行对齐）；
--        诊断四列 = 该组明细的**最大序号** + 采购金额 + 已收金额 + 单据金额（旧实现取 `max(SERIAL_NO)`），
--        列间四空格、行间 CRLF，表头一行。
--   ③ C# 新增的"引用三件套完整性"校验（旧过程没有）：明细填了采购单号，就必须同时填采购单别与采购序号
--      （否则存成半截引用），回报明细序号。本迁移把它一并落地（保持当前线上行为），文案与 C# 逐字一致。
--   ④ 写段（无条件）：主表 `AMOUNT` ＝ `ROUND(SUM(明细 AMOUNT), 3)`；明细为空时不回写。
-- 承载方式：② = SAVE 期 `qty-not-exceed`（分组求和 + 模块门控 + 源列 `MAX` 聚合诊断 + `THIS` 诊断）；
--           ③ = SAVE 期 `line-require`（结构化条件 `blank(negate)` 作触发，两条 check 共用文案）；
--           ④ = 新增服务处理器 **`pur-prepay-rollup`**。
-- 序号：既有的“厂商存在”引用校验占 SEQ=1，本迁移的两条新规则排在其后（SEQ=2/3）。
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

DECLARE @ModuleId INT = 170203;
DECLARE @Family NVARCHAR(40) = N'pur-prepay';

IF NOT EXISTS (SELECT 1 FROM dbo.MODULES m WHERE m.M_IDX = @ModuleId
                 AND LTRIM(RTRIM(ISNULL(m.MASTER_TABLE, ''))) = N'PUR_PREPAY_M'
                 AND LTRIM(RTRIM(ISNULL(m.DETAIL_TABLE, ''))) = N'PUR_PREPAY_D')
    THROW 50001, N'模块 170203 形态不符（应为 PUR_PREPAY_M / PUR_PREPAY_D），迁移中止。', 1;

IF EXISTS (SELECT 1 FROM dbo.MODULE_VALIDATION_RULE
            WHERE MODULE_ID = @ModuleId AND STAGE = N'SAVE' AND VALIDATION_KEY <> N'reference-exists')
    THROW 50002, N'模块 170203 已有非引用类校验规则，迁移中止（先核对配置）。', 1;

IF EXISTS (SELECT 1 FROM dbo.MODULE_BUSINESS_ACTION WHERE MODULE_ID = @ModuleId AND EVENT_CODE = N'SAVE')
    THROW 50003, N'模块 170203 已有 SAVE 期业务动作配置，迁移中止（先核对配置）。', 1;

/* ① 引用三件套完整性（C# 新增判据，旧过程无；保持当前线上行为） */
DECLARE @RefMessage NVARCHAR(400) =
    N'以下序号项已填采购单号，但未填采购单别或采购序号（三者为一体）：'
    + CHAR(13) + CHAR(10) + N'{ROWS}';
DECLARE @RefParam NVARCHAR(MAX) =
    N'{"checks":['
    + N'{"scope":"DETAIL","field":"PURCHASE_TYPE",'
    + N'"condition":{"logic":"AND","items":[{"type":"blank","field":{"scope":"DETAIL","field":"PURCHASE_NO"},"negate":true}]},'
    + N'"message":"以下序号项已填采购单号，但未填采购单别或采购序号（三者为一体）：\r\n{ROWS}",'
    + N'"diagnosticFields":["SERIAL_NO"]},'
    + N'{"scope":"DETAIL","field":"PURCHASE_SERIAL_NO",'
    + N'"condition":{"logic":"AND","items":[{"type":"blank","field":{"scope":"DETAIL","field":"PURCHASE_NO"},"negate":true}]},'
    + N'"message":"以下序号项已填采购单号，但未填采购单别或采购序号（三者为一体）：\r\n{ROWS}",'
    + N'"diagnosticFields":["SERIAL_NO"]}]}';

MERGE dbo.MODULE_VALIDATION_RULE AS T
USING (SELECT @ModuleId AS MODULE_ID) AS S
   ON T.MODULE_ID = S.MODULE_ID AND T.STAGE = N'SAVE'
  AND T.VALIDATION_KEY = N'line-require' AND T.SEQ = 2
WHEN MATCHED THEN
    UPDATE SET T.PARAM_STRUCT = @RefParam, T.MESSAGE = @RefMessage, T.ENABLED = 1,
               T.REMARK = N'采购单引用三件套完整性（填单号即须填单别与序号；原 C# 判据的忠实移植）',
               T.LAST_UPDATE_BY = N'DbUp', T.LAST_UPDATE_DATE = SYSDATETIME()
WHEN NOT MATCHED THEN
    INSERT (MODULE_ID, STAGE, SEQ, VALIDATION_KEY, ENABLED, PARAM_STRUCT, MESSAGE, REMARK, SOURCE_REF,
            CREATE_PERSON, CREATE_DATE, LAST_UPDATE_BY, LAST_UPDATE_DATE)
    VALUES (S.MODULE_ID, N'SAVE', 2, N'line-require', 1, @RefParam, @RefMessage,
            N'采购单引用三件套完整性（填单号即须填单别与序号；原 C# 判据的忠实移植）', @Family,
            N'DbUp', SYSDATETIME(), N'DbUp', SYSDATETIME());

/* ② 门控段：已预付金额不得超出采购行金额 */
DECLARE @ExceedParam NVARCHAR(MAX) =
    N'{"mode":"usage-not-exceed","checks":[{"targetTable":"PUR_PURCHASE_D",'
    + N'"match":[{"target":"PURCHASE_TYPE","source":{"scope":"DETAIL","field":"PURCHASE_TYPE"}},'
    + N'{"target":"PURCHASE_NO","source":{"scope":"DETAIL","field":"PURCHASE_NO"}},'
    + N'{"target":"SERIAL_NO","source":{"scope":"DETAIL","field":"PURCHASE_SERIAL_NO"}}],'
    + N'"thisQty":{"scope":"DETAIL","agg":"SUM","terms":[{"field":"AMOUNT","coef":1}]},'
    + N'"usage":{"scope":"TARGET","fields":["FINISHED_AMOUNT"]},"limit":{"scope":"TARGET","fields":["AMOUNT"]},'
    + N'"switch":{"gates":[{"scope":"MODULE","key":"ERROR_NO_SAVE","expect":1}]},'
    + N'"diagnosticFields":[{"scope":"SOURCE","field":"SERIAL_NO","agg":"MAX"},'
    + N'{"scope":"TARGET","field":"AMOUNT"},{"scope":"TARGET","field":"FINISHED_AMOUNT"},{"scope":"THIS"}],'
    + N'"diagnosticCellSeparator":"    ","maxRows":100,'
    + N'"message":"以下项预付金额超出采购金额\r\n序号  采购金额  已收金额  单据金额\r\n{ROWS}"}]}';

MERGE dbo.MODULE_VALIDATION_RULE AS T
USING (SELECT @ModuleId AS MODULE_ID) AS S
   ON T.MODULE_ID = S.MODULE_ID AND T.STAGE = N'SAVE'
  AND T.VALIDATION_KEY = N'qty-not-exceed' AND T.SEQ = 3
WHEN MATCHED THEN
    UPDATE SET T.PARAM_STRUCT = @ExceedParam, T.MESSAGE = NULL, T.ENABLED = 1,
               T.REMARK = N'预付金额不超采购行未结金额（受模块门控；原 C#/旧过程判据的忠实移植）',
               T.LAST_UPDATE_BY = N'DbUp', T.LAST_UPDATE_DATE = SYSDATETIME()
WHEN NOT MATCHED THEN
    INSERT (MODULE_ID, STAGE, SEQ, VALIDATION_KEY, ENABLED, PARAM_STRUCT, MESSAGE, REMARK, SOURCE_REF,
            CREATE_PERSON, CREATE_DATE, LAST_UPDATE_BY, LAST_UPDATE_DATE)
    VALUES (S.MODULE_ID, N'SAVE', 3, N'qty-not-exceed', 1, @ExceedParam, NULL,
            N'预付金额不超采购行未结金额（受模块门控；原 C#/旧过程判据的忠实移植）', @Family,
            N'DbUp', SYSDATETIME(), N'DbUp', SYSDATETIME());

/* ③ 写段：主表金额汇总 */
DECLARE @EffectParam NVARCHAR(MAX) =
    N'{"master":{"typeField":"PREPAY_TYPE","noField":"PREPAY_NO","amountField":"AMOUNT"},'
    + N'"detail":{"table":"PUR_PREPAY_D","amountField":"AMOUNT"},"roundDigits":3}';

MERGE dbo.MODULE_BUSINESS_ACTION AS T
USING (SELECT @ModuleId AS MODULE_ID) AS S
   ON T.MODULE_ID = S.MODULE_ID AND T.EVENT_CODE = N'SAVE' AND T.SEQ = 1
WHEN MATCHED THEN
    UPDATE SET T.EFFECT_KEY = N'pur-prepay-rollup', T.EFFECT_NAME = N'预付帐款单金额汇总（保存期）',
               T.ENABLED = 1, T.FAIL_MODE = N'BLOCK', T.PARAM_STRUCT = @EffectParam,
               T.REVERSE_STRUCT = N'{"kind":"none"}',
               T.REMARK = N'原 C# 预付帐款单保存后动作的忠实移植（主表金额＝明细金额合计，舍入三位）',
               T.LAST_UPDATE_BY = N'DbUp', T.LAST_UPDATE_DATE = SYSDATETIME()
WHEN NOT MATCHED THEN
    INSERT (MODULE_ID, EVENT_CODE, SEQ, EFFECT_KEY, EFFECT_NAME, ENABLED, FAIL_MODE, CONDITION_STRUCT,
            PARAM_STRUCT, REVERSE_STRUCT, REMARK, SOURCE_REF, CREATE_PERSON, CREATE_DATE, LAST_UPDATE_BY, LAST_UPDATE_DATE)
    VALUES (S.MODULE_ID, N'SAVE', 1, N'pur-prepay-rollup', N'预付帐款单金额汇总（保存期）', 1, N'BLOCK', NULL,
            @EffectParam, N'{"kind":"none"}',
            N'原 C# 预付帐款单保存后动作的忠实移植（主表金额＝明细金额合计，舍入三位）', @Family,
            N'DbUp', SYSDATETIME(), N'DbUp', SYSDATETIME());

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
    THROW 50004, N'当前快照仍残留已退役的族名，迁移中止。', 1;

IF NOT EXISTS (SELECT 1 FROM dbo.MODULE_VALIDATION_RULE
               WHERE MODULE_ID = @ModuleId AND STAGE = N'SAVE' AND ENABLED = 1
                 AND VALIDATION_KEY = N'line-require' AND SEQ = 2)
    THROW 50005, N'退役 C# 后缺少启用的 SAVE 期 line-require 规则，迁移中止。', 1;

IF NOT EXISTS (SELECT 1 FROM dbo.MODULE_VALIDATION_RULE
               WHERE MODULE_ID = @ModuleId AND STAGE = N'SAVE' AND ENABLED = 1
                 AND VALIDATION_KEY = N'qty-not-exceed' AND SEQ = 3)
    THROW 50006, N'退役 C# 后缺少启用的 SAVE 期 qty-not-exceed 规则，迁移中止。', 1;

IF NOT EXISTS (SELECT 1 FROM dbo.MODULE_BUSINESS_ACTION
               WHERE MODULE_ID = @ModuleId AND EVENT_CODE = N'SAVE' AND SEQ = 1
                 AND EFFECT_KEY = N'pur-prepay-rollup' AND ENABLED = 1)
    THROW 50007, N'退役 C# 后缺少启用的 SAVE 期 pur-prepay-rollup 动作，迁移中止。', 1;

PRINT N'== pur-prepay 入效果目录并退役 C# 完成（170203，两条 SAVE 规则 + pur-prepay-rollup）==';
