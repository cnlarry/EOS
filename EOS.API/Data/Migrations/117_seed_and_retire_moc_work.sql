-- ============================================================================
-- EOS.ERP migration 117: moc-work / moc-work-in 入目录并退役 C#
-- ----------------------------------------------------------------------------
-- 两族的 C# 判据都是「本单数量 + 目标累计量 不得超目标限额」（usage-not-exceed）：
--   2705 moc-work   ：明细 MOC_WORK_D（按制令聚合 PROCESS_QTY）→ 目标 MOC_PRODUCE_PROCESS_D
--                     匹配 PRODUCE_TYPE/PRODUCE_NO/SERIAL_NO←PRODUCE_SERIAL_NO；
--                     thisQty=SUM(PROCESS_QTY)，usage=FINISHED_PLAN_QTY，limit=PROCESS_QTY。
--   2706 moc-work-in：明细 MOC_WORK_IN_D（按工序工单聚合 QTY）→ 目标 MOC_WORK_D
--                     匹配 WORK_TYPE/WORK_NO/SERIAL_NO←WORK_SERIAL_NO；
--                     thisQty=SUM(QTY)，usage=FINISHED_IN_QTY，limit=PROCESS_QTY+ULLAGE_QTY。
-- 2707 moc-work-out 暂不迁移：其 C# 判据受 MODULES.ERROR_NO_SAVE 门控且该门为 0（判据当前不生效），
--   目录规则无法表达该门控，直接播种会把原本关闭的校验激活（行为变化）。
-- 本迁移同时就地置空已发布快照里的族名；新规则进入运行期需重发布这些模块。幂等 + 退役后守卫。
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

/* 2705：工序工单不超工单制程数量 */
DECLARE @WORK_MSG NVARCHAR(300) = N'以下工序工单超出工单制程数量\r\n工单单别   单号   数量   已下工序工单数量   单据数量\r\n{ROWS}';
DECLARE @WORK_PARAM NVARCHAR(MAX) =
    N'{"mode":"usage-not-exceed","checks":[{'
    + N'"targetTable":"MOC_PRODUCE_PROCESS_D",'
    + N'"match":[{"target":"PRODUCE_TYPE","source":{"scope":"DETAIL","field":"PRODUCE_TYPE"}},'
    + N'{"target":"PRODUCE_NO","source":{"scope":"DETAIL","field":"PRODUCE_NO"}},'
    + N'{"target":"SERIAL_NO","source":{"scope":"DETAIL","field":"PRODUCE_SERIAL_NO"}}],'
    + N'"thisQty":{"scope":"DETAIL","agg":"SUM","terms":[{"field":"PROCESS_QTY","coef":1}]},'
    + N'"usage":{"scope":"TARGET","fields":["FINISHED_PLAN_QTY"]},'
    + N'"limit":{"scope":"TARGET","fields":["PROCESS_QTY"]},'
    + N'"diagnosticFields":[{"scope":"TARGET","field":"PRODUCE_TYPE"},{"scope":"TARGET","field":"PRODUCE_NO"},'
    + N'{"scope":"TARGET","field":"PROCESS_QTY"},{"scope":"TARGET","field":"FINISHED_PLAN_QTY"},'
    + N'{"scope":"SOURCE","field":"PROCESS_QTY"}],'
    + N'"diagnosticCellSeparator":"    ",'
    + N'"message":"' + @WORK_MSG + N'"}]}';

MERGE dbo.MODULE_VALIDATION_RULE AS T
USING (SELECT 2705 AS MODULE_ID, N'SAVE' AS STAGE, 1 AS SEQ, N'qty-not-exceed' AS VALIDATION_KEY) AS S
   ON T.MODULE_ID = S.MODULE_ID AND T.STAGE = S.STAGE AND T.VALIDATION_KEY = S.VALIDATION_KEY AND T.SEQ = S.SEQ
WHEN MATCHED THEN
    UPDATE SET T.PARAM_STRUCT = @WORK_PARAM, T.MESSAGE = @WORK_MSG, T.ENABLED = 1,
               T.LAST_UPDATE_BY = N'DbUp', T.LAST_UPDATE_DATE = SYSDATETIME()
WHEN NOT MATCHED THEN
    INSERT (MODULE_ID, STAGE, SEQ, VALIDATION_KEY, ENABLED, PARAM_STRUCT, MESSAGE, REMARK, SOURCE_REF,
            CREATE_PERSON, CREATE_DATE, LAST_UPDATE_BY, LAST_UPDATE_DATE)
    VALUES (S.MODULE_ID, S.STAGE, S.SEQ, S.VALIDATION_KEY, 1, @WORK_PARAM, @WORK_MSG,
            N'工序工单不超工单制程数量（保存期；原 C# MocWorkAfterSaveAsync 判据的忠实移植）', N'P_WF_MOC_PRODUCE_PROCESS',
            N'DbUp', SYSDATETIME(), N'DbUp', SYSDATETIME());

/* 2706：工序入库不超工序工单数量（限额含损耗量） */
DECLARE @WORKIN_MSG NVARCHAR(300) = N'以下入库超出工序工单数量\r\n工序工单单别   单号   数量   已入库数量   单据数量\r\n{ROWS}';
DECLARE @WORKIN_PARAM NVARCHAR(MAX) =
    N'{"mode":"usage-not-exceed","checks":[{'
    + N'"targetTable":"MOC_WORK_D",'
    + N'"match":[{"target":"WORK_TYPE","source":{"scope":"DETAIL","field":"WORK_TYPE"}},'
    + N'{"target":"WORK_NO","source":{"scope":"DETAIL","field":"WORK_NO"}},'
    + N'{"target":"SERIAL_NO","source":{"scope":"DETAIL","field":"WORK_SERIAL_NO"}}],'
    + N'"thisQty":{"scope":"DETAIL","agg":"SUM","terms":[{"field":"QTY","coef":1}]},'
    + N'"usage":{"scope":"TARGET","fields":["FINISHED_IN_QTY"]},'
    + N'"limit":{"scope":"TARGET","fields":["PROCESS_QTY","ULLAGE_QTY"]},'
    + N'"diagnosticFields":[{"scope":"TARGET","field":"WORK_TYPE"},{"scope":"TARGET","field":"WORK_NO"},'
    + N'{"scope":"TARGET","field":"PROCESS_QTY"},{"scope":"TARGET","field":"FINISHED_IN_QTY"},'
    + N'{"scope":"SOURCE","field":"QTY"}],'
    + N'"diagnosticCellSeparator":"    ",'
    + N'"message":"' + @WORKIN_MSG + N'"}]}';

MERGE dbo.MODULE_VALIDATION_RULE AS T
USING (SELECT 2706 AS MODULE_ID, N'SAVE' AS STAGE, 1 AS SEQ, N'qty-not-exceed' AS VALIDATION_KEY) AS S
   ON T.MODULE_ID = S.MODULE_ID AND T.STAGE = S.STAGE AND T.VALIDATION_KEY = S.VALIDATION_KEY AND T.SEQ = S.SEQ
WHEN MATCHED THEN
    UPDATE SET T.PARAM_STRUCT = @WORKIN_PARAM, T.MESSAGE = @WORKIN_MSG, T.ENABLED = 1,
               T.LAST_UPDATE_BY = N'DbUp', T.LAST_UPDATE_DATE = SYSDATETIME()
WHEN NOT MATCHED THEN
    INSERT (MODULE_ID, STAGE, SEQ, VALIDATION_KEY, ENABLED, PARAM_STRUCT, MESSAGE, REMARK, SOURCE_REF,
            CREATE_PERSON, CREATE_DATE, LAST_UPDATE_BY, LAST_UPDATE_DATE)
    VALUES (S.MODULE_ID, S.STAGE, S.SEQ, S.VALIDATION_KEY, 1, @WORKIN_PARAM, @WORKIN_MSG,
            N'工序入库不超工序工单数量（保存期；原 C# MocWorkInAfterSaveAsync 判据的忠实移植）', N'P_WF_MOC_PRODUCT_IN',
            N'DbUp', SYSDATETIME(), N'DbUp', SYSDATETIME());

/* 置空已发布快照里的族名 */
IF OBJECT_ID(N'tempdb..#PortedFamily') IS NOT NULL DROP TABLE #PortedFamily;
CREATE TABLE #PortedFamily (MODULE_ID INT PRIMARY KEY, FAMILY NVARCHAR(40) NOT NULL);
INSERT INTO #PortedFamily (MODULE_ID, FAMILY) VALUES (2705, N'moc-work'), (2706, N'moc-work-in');

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

PRINT N'== moc-work / moc-work-in 入目录并退役 C# 完成（2 模块）==';
