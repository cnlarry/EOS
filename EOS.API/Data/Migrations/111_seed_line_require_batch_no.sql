-- ============================================================================
-- EOS.ERP migration 111: 库存异动族"批管品必填批号"入校验目录（保存期）
-- ----------------------------------------------------------------------------
-- 判据逐字对照 C#（InvDomainRules.InvOccurValidateAsync）：
--   明细行满足「产品为批管（PRODUCT.MANAGE_BATCH=1）」且 BATCH_NO 为空 → 拒绝
--   文案：「以下序号项需要输入批号 」+ \r\n + 最多 10 个 SERIAL_NO（每行一个）
-- C# 侧原为保存后钩子（AfterSave），故本批实例一律 STAGE=SAVE。
-- 模板能力扩展（本批落地）：line-require 的 check 支持
--   ① condition：结构化条件（闭式条件编译器；DETAIL 域即明细行别名 S，可表达跨表存在性判据）
--   ② diagnosticFields：命中行按"行间 \r\n"追加到消息后（与既有实现的消息形态一致）
-- 幂等：按 模块+SAVE+VALIDATION_KEY+SEQ 覆盖参数与启用位。
-- ============================================================================

SET NOCOUNT ON;

DECLARE @GUARD_MESSAGE NVARCHAR(400) = N'本脚本只能在 EOS.ERP 数据库内执行，当前库为 ' + DB_NAME() + N'。';
IF DB_NAME() <> N'EOS.ERP'
    THROW 50000, @GUARD_MESSAGE, 1;

DECLARE @MSG NVARCHAR(200) = N'以下序号项需要输入批号 ';
DECLARE @PARAM NVARCHAR(MAX) =
    N'{"checks":[{"scope":"DETAIL","field":"BATCH_NO","message":"' + @MSG + N'",'
    + N'"diagnosticFields":["SERIAL_NO"],'
    + N'"condition":{"logic":"AND","items":['
    + N'{"type":"not-exists","targetTable":"PRODUCT","negate":true,'
    + N'"condition":{"type":"value-eq","field":{"scope":"TARGET","field":"MANAGE_BATCH"},"value":1},'
    + N'"match":[{"target":"PRO_NO","source":{"scope":"DETAIL","field":"PRO_NO"}}]}]}}]}';

DECLARE @Targets TABLE (MODULE_ID INT PRIMARY KEY);
INSERT INTO @Targets (MODULE_ID) VALUES (130102),(130103),(130104),(130105),(130106),(130107),(130110),(2817),(2818),(3901);

MERGE dbo.MODULE_VALIDATION_RULE AS T
USING (SELECT MODULE_ID, N'SAVE' AS STAGE, 2 AS SEQ, N'line-require' AS VALIDATION_KEY FROM @Targets) AS S
   ON T.MODULE_ID = S.MODULE_ID AND T.STAGE = S.STAGE AND T.VALIDATION_KEY = S.VALIDATION_KEY AND T.SEQ = S.SEQ
WHEN MATCHED THEN
    UPDATE SET T.PARAM_STRUCT = @PARAM, T.MESSAGE = @MSG, T.ENABLED = 1,
               T.REMARK = N'批管品必填批号（保存期；原 C# InvOccurValidateAsync 的忠实移植）',
               T.SOURCE_REF = N'P_WF_INV_*',
               T.LAST_UPDATE_BY = N'DbUp', T.LAST_UPDATE_DATE = SYSDATETIME()
WHEN NOT MATCHED THEN
    INSERT (MODULE_ID, STAGE, SEQ, VALIDATION_KEY, ENABLED, PARAM_STRUCT, MESSAGE, REMARK, SOURCE_REF,
            CREATE_PERSON, CREATE_DATE, LAST_UPDATE_BY, LAST_UPDATE_DATE)
    VALUES (S.MODULE_ID, S.STAGE, S.SEQ, S.VALIDATION_KEY, 1, @PARAM, @MSG,
            N'批管品必填批号（保存期；原 C# InvOccurValidateAsync 的忠实移植）', N'P_WF_INV_*',
            N'DbUp', SYSDATETIME(), N'DbUp', SYSDATETIME());

DECLARE @COUNT INT = (SELECT COUNT(*) FROM dbo.MODULE_VALIDATION_RULE WHERE VALIDATION_KEY = N'line-require' AND STAGE = N'SAVE');
PRINT N'== 库存异动族保存期 line-require 播种完成，当前 SAVE 期 line-require 实例 ' + CAST(@COUNT AS NVARCHAR(10)) + N' 条 ==';
