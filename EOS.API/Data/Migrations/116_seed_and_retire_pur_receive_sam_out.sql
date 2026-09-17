-- ============================================================================
-- EOS.ERP migration 116: pur-receive / sam-out 入目录并退役 C#
-- ----------------------------------------------------------------------------
-- ① pur-receive(1607)：C# `PurReceiveAfterSaveAsync` 只有一条判据——「批管品必填批号」，
--    以 SAVE 期 `line-require`（跨表 condition + 序号诊断）忠实移植（SEQ=3，1/2 已被
--    reference-exists 与 qty-not-exceed 占用）。
-- ② sam-out(2404)：C# `SamOutAfterSaveAsync` 为「本单明细数量不得超过 SAMPLE_PRO.QTY」——
--    用 `qty-not-exceed` 的 `this-not-exceed` 模式（该模式不需要 usage，只需 thisQty 与 limit）。
-- 两步同时就地置空已发布快照里的族名（与 105/107/110/112/114/115 同法），
-- 新规则进入运行期需重发布这些模块；幂等 + 退役后守卫。
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

/* ① 1607：批管品必填批号（保存期，SEQ=3） */
DECLARE @BATCH_MSG NVARCHAR(200) = N'以下序号项需要输入批号 ';
DECLARE @BATCH_PARAM NVARCHAR(MAX) =
    N'{"checks":[{"scope":"DETAIL","field":"BATCH_NO","message":"' + @BATCH_MSG + N'",'
    + N'"diagnosticFields":["SERIAL_NO"],'
    + N'"condition":{"logic":"AND","items":['
    + N'{"type":"not-exists","targetTable":"PRODUCT","negate":true,'
    + N'"condition":{"type":"value-eq","field":{"scope":"TARGET","field":"MANAGE_BATCH"},"value":1},'
    + N'"match":[{"target":"PRO_NO","source":{"scope":"DETAIL","field":"PRO_NO"}}]}]}}]}';

MERGE dbo.MODULE_VALIDATION_RULE AS T
USING (SELECT 1607 AS MODULE_ID, N'SAVE' AS STAGE, 3 AS SEQ, N'line-require' AS VALIDATION_KEY) AS S
   ON T.MODULE_ID = S.MODULE_ID AND T.STAGE = S.STAGE AND T.VALIDATION_KEY = S.VALIDATION_KEY AND T.SEQ = S.SEQ
WHEN MATCHED THEN
    UPDATE SET T.PARAM_STRUCT = @BATCH_PARAM, T.MESSAGE = @BATCH_MSG, T.ENABLED = 1,
               T.LAST_UPDATE_BY = N'DbUp', T.LAST_UPDATE_DATE = SYSDATETIME()
WHEN NOT MATCHED THEN
    INSERT (MODULE_ID, STAGE, SEQ, VALIDATION_KEY, ENABLED, PARAM_STRUCT, MESSAGE, REMARK, SOURCE_REF,
            CREATE_PERSON, CREATE_DATE, LAST_UPDATE_BY, LAST_UPDATE_DATE)
    VALUES (S.MODULE_ID, S.STAGE, S.SEQ, S.VALIDATION_KEY, 1, @BATCH_PARAM, @BATCH_MSG,
            N'批管品必填批号（保存期；原 C# PurReceiveAfterSaveAsync 判据的忠实移植）', N'P_PUR_RECEIVE_After_Save',
            N'DbUp', SYSDATETIME(), N'DbUp', SYSDATETIME());

/* ② 2404：样品出库不得超样品库存（保存期，this-not-exceed） */
DECLARE @SAMPLE_MSG NVARCHAR(200) = N'以下序号项样品库存不足 \r\n{ROWS}';
DECLARE @SAMPLE_PARAM NVARCHAR(MAX) =
    N'{"mode":"this-not-exceed","checks":[{'
    + N'"targetTable":"SAMPLE_PRO",'
    + N'"match":[{"target":"PRO_NO","source":{"scope":"DETAIL","field":"PRO_NO"}}],'
    + N'"thisQty":{"scope":"DETAIL","agg":"SUM","terms":[{"field":"QTY","coef":1}]},'
    + N'"limit":{"scope":"TARGET","fields":["QTY"]},'
    + N'"diagnosticFields":[{"scope":"SOURCE","field":"SERIAL_NO"}],'
    + N'"diagnosticCellSeparator":"    ",'
    + N'"message":"' + @SAMPLE_MSG + N'"}]}';

MERGE dbo.MODULE_VALIDATION_RULE AS T
USING (SELECT 2404 AS MODULE_ID, N'SAVE' AS STAGE, 1 AS SEQ, N'qty-not-exceed' AS VALIDATION_KEY) AS S
   ON T.MODULE_ID = S.MODULE_ID AND T.STAGE = S.STAGE AND T.VALIDATION_KEY = S.VALIDATION_KEY AND T.SEQ = S.SEQ
WHEN MATCHED THEN
    UPDATE SET T.PARAM_STRUCT = @SAMPLE_PARAM, T.MESSAGE = @SAMPLE_MSG, T.ENABLED = 1,
               T.LAST_UPDATE_BY = N'DbUp', T.LAST_UPDATE_DATE = SYSDATETIME()
WHEN NOT MATCHED THEN
    INSERT (MODULE_ID, STAGE, SEQ, VALIDATION_KEY, ENABLED, PARAM_STRUCT, MESSAGE, REMARK, SOURCE_REF,
            CREATE_PERSON, CREATE_DATE, LAST_UPDATE_BY, LAST_UPDATE_DATE)
    VALUES (S.MODULE_ID, S.STAGE, S.SEQ, S.VALIDATION_KEY, 1, @SAMPLE_PARAM, @SAMPLE_MSG,
            N'样品出库不超样品库存（保存期；原 C# SamOutAfterSaveAsync 判据的忠实移植）', N'P_WF_SAM_OUT',
            N'DbUp', SYSDATETIME(), N'DbUp', SYSDATETIME());

/* ③ 置空已发布快照里的族名 */
IF OBJECT_ID(N'tempdb..#PortedFamily') IS NOT NULL DROP TABLE #PortedFamily;
CREATE TABLE #PortedFamily (MODULE_ID INT PRIMARY KEY, FAMILY NVARCHAR(40) NOT NULL);
INSERT INTO #PortedFamily (MODULE_ID, FAMILY) VALUES (1607, N'pur-receive'), (2404, N'sam-out');

UPDATE S
   SET S.DEFINITION_JSON = REPLACE(S.DEFINITION_JSON,
                                   N'"DomainRule":"' + F.FAMILY + N'"',
                                   N'"DomainRule":null')
FROM dbo.WORKBENCH_DEFINITION_SNAPSHOT S
JOIN #PortedFamily F ON F.MODULE_ID = S.MODULE_ID
WHERE S.IS_CURRENT = 1
  AND S.DEFINITION_JSON LIKE N'%"DomainRule":"' + F.FAMILY + N'"%';

IF EXISTS (
    SELECT 1 FROM dbo.WORKBENCH_DEFINITION_SNAPSHOT S
    JOIN #PortedFamily F ON F.MODULE_ID = S.MODULE_ID
    WHERE S.IS_CURRENT = 1 AND S.DEFINITION_JSON LIKE N'%"DomainRule":"' + F.FAMILY + N'"%'
)
    THROW 50001, N'当前快照仍残留已退役的族名，迁移中止。', 1;

IF EXISTS (
    SELECT 1 FROM #PortedFamily F
    WHERE NOT EXISTS (
        SELECT 1 FROM dbo.MODULE_VALIDATION_RULE R
        WHERE R.MODULE_ID = F.MODULE_ID AND R.STAGE = N'SAVE' AND R.ENABLED = 1)
)
    THROW 50002, N'有模块在退役 C# 后没有启用的 SAVE 期目录规则，迁移中止。', 1;

IF OBJECT_ID(N'tempdb..#PortedFamily') IS NOT NULL DROP TABLE #PortedFamily;

PRINT N'== pur-receive / sam-out 入目录并退役 C# 完成（2 模块，快照族名已置空）==';
