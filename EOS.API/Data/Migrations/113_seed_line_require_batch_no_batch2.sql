-- ============================================================================
-- EOS.ERP migration 113: 同形族"批管品必填批号"继续入目录（保存期，第二批）
-- ----------------------------------------------------------------------------
-- 本批 5 个族的 C# 判据与迁移 111 完全同形（单条 `ValidateDetailAsync`，条件与文案逐字一致）：
--   cop-back(1423/COP_BACK_D)、cop-fitin(1412/COP_FITIN_D)、inv-loan(130108/INV_LOAN_D)、
--   inv-return(130109/INV_RETURN_D)、moc-product-in(1505/1519/2816/MOC_PRODUCT_IN_D)
-- SEQ 取 3（1/2 已被 B3a/B4 的 reference-exists 与 qty-not-exceed 占用）。
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
INSERT INTO @Targets (MODULE_ID) VALUES (1423),(1412),(130108),(130109),(1505),(1519),(2816);

MERGE dbo.MODULE_VALIDATION_RULE AS T
USING (SELECT MODULE_ID, N'SAVE' AS STAGE, 3 AS SEQ, N'line-require' AS VALIDATION_KEY FROM @Targets) AS S
   ON T.MODULE_ID = S.MODULE_ID AND T.STAGE = S.STAGE AND T.VALIDATION_KEY = S.VALIDATION_KEY AND T.SEQ = S.SEQ
WHEN MATCHED THEN
    UPDATE SET T.PARAM_STRUCT = @PARAM, T.MESSAGE = @MSG, T.ENABLED = 1,
               T.LAST_UPDATE_BY = N'DbUp', T.LAST_UPDATE_DATE = SYSDATETIME()
WHEN NOT MATCHED THEN
    INSERT (MODULE_ID, STAGE, SEQ, VALIDATION_KEY, ENABLED, PARAM_STRUCT, MESSAGE, REMARK, SOURCE_REF,
            CREATE_PERSON, CREATE_DATE, LAST_UPDATE_BY, LAST_UPDATE_DATE)
    VALUES (S.MODULE_ID, S.STAGE, S.SEQ, S.VALIDATION_KEY, 1, @PARAM, @MSG,
            N'批管品必填批号（保存期；原 C# ValidateDetailAsync 判据的忠实移植）', N'P_WF_*',
            N'DbUp', SYSDATETIME(), N'DbUp', SYSDATETIME());

DECLARE @COUNT INT = (SELECT COUNT(*) FROM dbo.MODULE_VALIDATION_RULE WHERE VALIDATION_KEY = N'line-require' AND STAGE = N'SAVE');
PRINT N'== 第二批批管品批号规则播种完成，当前 SAVE 期 line-require 实例 ' + CAST(@COUNT AS NVARCHAR(10)) + N' 条 ==';
