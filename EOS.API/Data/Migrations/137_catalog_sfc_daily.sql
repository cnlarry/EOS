-- ============================================================================
-- EOS.ERP migration 137: 生产记录单族入目录并退役 C#（sfc-daily 180401）
-- ----------------------------------------------------------------------------
-- C# 判据：本单明细按（制令别/制令号/工序）分组求和后，若
--   `制令制程允许生产最大数量 < 制令制程完工计划量 + 本单该组完工数量`
-- 则回报**本单所有越界明细的序号**（升序、最多十行）。
-- 本迁移用到本轮的**加法式能力**：
--   · `targetAgg=MAX`——被引用表（MOC_PRODUCE_PROCESS_D）的主键是（制令别/制令号/序号），
--     同一工序可能有多条制程行，既有实现按定位键取 `MAX(允许量/计划量)`；不聚合则"任意一行"
--     会比旧判据更严，故先按定位键聚合成一行再比较。
--   · `diagnosticRows=SOURCE`——诊断不按"每组一行"输出，而是列出违规分组下的源明细行
--     （与既有实现 `JOIN 分组 g ... SELECT d.SERIAL_NO ORDER BY d.SERIAL_NO` 等价）。
-- 播种 SAVE 期规则（SEQ 取 2；180401 的 1 已被 reference-exists 占用），并就地置空快照族名。
-- 幂等：播种按 模块+SAVE+VALIDATION_KEY+SEQ 覆盖；快照仅当仍含族名时改写，改写后复核。
-- 注意：PARAM_STRUCT 里的换行必须是转义的 `\r\n`（JSON 字符串不允许裸控制字符）。
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

/* ① 播种保存期规则 */
DECLARE @Message NVARCHAR(400) =
    N'以下序号项数量超过制令制程允许生产最大数量 ' + CHAR(13) + CHAR(10) + N'{ROWS}';
DECLARE @Param NVARCHAR(MAX) =
    N'{"mode":"usage-not-exceed","checks":[{"targetTable":"MOC_PRODUCE_PROCESS_D",'
    + N'"match":[{"target":"PRODUCE_TYPE","source":{"scope":"DETAIL","field":"PRODUCE_TYPE"}},'
    + N'{"target":"PRODUCE_NO","source":{"scope":"DETAIL","field":"PRODUCE_NO"}},'
    + N'{"target":"PROCEDURE_ID","source":{"scope":"DETAIL","field":"PROCEDURE_ID"}}],'
    + N'"thisQty":{"scope":"DETAIL","agg":"SUM","terms":[{"field":"FINISHED_QTY","coef":1}]},'
    + N'"usage":{"scope":"TARGET","fields":["FINISHED_PLAN_QTY"]},'
    + N'"limit":{"scope":"TARGET","fields":["PROCESS_OVER_QTY"]},'
    + N'"targetAgg":"MAX",'
    + N'"diagnosticRows":"SOURCE",'
    + N'"diagnosticFields":["SERIAL_NO"],'
    + N'"message":"以下序号项数量超过制令制程允许生产最大数量 \r\n{ROWS}"}]}';

MERGE dbo.MODULE_VALIDATION_RULE AS T
USING (SELECT 180401 AS MODULE_ID, @Param AS PARAM_STRUCT, @Message AS MESSAGE,
              N'生产记录：完工数量不超制令制程允许生产最大数量（被引用行按定位键取最大，逐明细回报序号）' AS REMARK) AS S
   ON T.MODULE_ID = S.MODULE_ID AND T.STAGE = N'SAVE' AND T.VALIDATION_KEY = N'qty-not-exceed' AND T.SEQ = 2
WHEN MATCHED THEN
    UPDATE SET T.PARAM_STRUCT = S.PARAM_STRUCT, T.MESSAGE = S.MESSAGE, T.REMARK = S.REMARK, T.ENABLED = 1,
               T.LAST_UPDATE_BY = N'DbUp', T.LAST_UPDATE_DATE = SYSDATETIME()
WHEN NOT MATCHED THEN
    INSERT (MODULE_ID, STAGE, SEQ, VALIDATION_KEY, ENABLED, PARAM_STRUCT, MESSAGE, REMARK, SOURCE_REF,
            CREATE_PERSON, CREATE_DATE, LAST_UPDATE_BY, LAST_UPDATE_DATE)
    VALUES (S.MODULE_ID, N'SAVE', 2, N'qty-not-exceed', 1, S.PARAM_STRUCT, S.MESSAGE, S.REMARK, N'P_SFC_DAILY_After_Save',
            N'DbUp', SYSDATETIME(), N'DbUp', SYSDATETIME());

/* ② 置空已发布快照里的族名 */
IF OBJECT_ID(N'tempdb..#PortedFamily') IS NOT NULL DROP TABLE #PortedFamily;
CREATE TABLE #PortedFamily (MODULE_ID INT PRIMARY KEY, FAMILY NVARCHAR(40) NOT NULL);
INSERT INTO #PortedFamily (MODULE_ID, FAMILY) VALUES (180401, N'sfc-daily');

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

PRINT N'== sfc-daily 入目录并退役 C# 完成（1 模块，快照族名已置空）==';
