-- ============================================================================
-- EOS.ERP migration 136: 备货单族入目录并退役 C#（cop-fitout 1411）
-- ----------------------------------------------------------------------------
-- C# 判据两段：
--   ⒜ 受门控的"已备货不超订单/工单完工"：`MODULES.ERROR_NO_SAVE=1` 且
--      `SYSSS.FITOUT_ORDER_TAG=1`（订单口径，按订单行分组）或
--      `SYSSS.FITOUT_PRODUCE_TAG=1`（工单口径，按制令分组）；**数量与备品两个量纲合成一句
--      `WHERE a OR b`**，命中时按七列输出（单号 / 数量 / 已备货 / 单据数量 / 备品 / 已备货备品 / 单据备品）。
--   ⒝ 无门控的"批管品必填批号"（文案尾部带感叹号，与其它同形族差一个字符）。
-- 本迁移用到本轮的**加法式能力**：`qty-not-exceed` 的 check 支持 `dimensions`（多量纲按 OR 合并、
-- 诊断行只输出一次，与既有实现的单条 SELECT 等价），诊断里的"本单量"用 `{"scope":"THIS","dimension":N}`
-- 指定第几组量纲。
-- 播种 SAVE 期规则（1411 的 1 已被 reference-exists 占用，故 qty 取 2、批号取 3，
-- 保持既有实现"先量、后批号"的报错顺序），并就地置空已发布快照里的族名。
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
DECLARE @OrderGate NVARCHAR(260) =
    N'"switch":{"gates":[{"scope":"MODULE","key":"ERROR_NO_SAVE","expect":1},'
    + N'{"scope":"SYSSS","key":"FITOUT_ORDER_TAG","expect":1}]},';
DECLARE @ProduceGate NVARCHAR(260) =
    N'"switch":{"gates":[{"scope":"MODULE","key":"ERROR_NO_SAVE","expect":1},'
    + N'{"scope":"SYSSS","key":"FITOUT_PRODUCE_TAG","expect":1}]},';

DECLARE @QtyParam NVARCHAR(MAX) =
    N'{"mode":"usage-not-exceed","checks":[{"targetTable":"COP_ORDER_D",'
    + N'"match":[{"target":"ORDER_TYPE","source":{"scope":"DETAIL","field":"ORDER_TYPE"}},'
    + N'{"target":"ORDER_NO","source":{"scope":"DETAIL","field":"ORDER_NO"}},'
    + N'{"target":"SERIAL_NO","source":{"scope":"DETAIL","field":"ORDER_SERIAL_NO"}}],'
    + N'"dimensions":['
    + N'{"thisQty":{"scope":"DETAIL","agg":"SUM","terms":[{"field":"QTY","coef":1}]},'
    + N'"usage":{"scope":"TARGET","fields":["FINISHED_FITOUT_QTY"]},'
    + N'"limit":{"scope":"TARGET","fields":["QTY"]}},'
    + N'{"thisQty":{"scope":"DETAIL","agg":"SUM","terms":[{"field":"SPARE_QTY","coef":1}]},'
    + N'"usage":{"scope":"TARGET","fields":["FINISHED_FITOUT_SPARE_QTY"]},'
    + N'"limit":{"scope":"TARGET","fields":["SPARE_QTY"]}}],'
    + @OrderGate
    + N'"message":"以下会出现已备货数量超出订单数量\r\n订单号   数量   已备货数量  单据数量  备品  已备货备品  单据备品\r\n{ROWS}",'
    + N'"maxRows":100,'
    + N'"diagnosticFields":[{"scope":"TARGET","field":"ORDER_NO"},{"scope":"TARGET","field":"QTY"},'
    + N'{"scope":"TARGET","field":"FINISHED_FITOUT_QTY"},{"scope":"THIS","dimension":1},'
    + N'{"scope":"TARGET","field":"SPARE_QTY"},{"scope":"TARGET","field":"FINISHED_FITOUT_SPARE_QTY"},'
    + N'{"scope":"THIS","dimension":2}]},'
    + N'{"targetTable":"MOC_PRODUCE_M",'
    + N'"match":[{"target":"PRODUCE_TYPE","source":{"scope":"DETAIL","field":"PRODUCE_TYPE"}},'
    + N'{"target":"PRODUCE_NO","source":{"scope":"DETAIL","field":"PRODUCE_NO"}}],'
    + N'"dimensions":['
    + N'{"thisQty":{"scope":"DETAIL","agg":"SUM","terms":[{"field":"QTY","coef":1}]},'
    + N'"usage":{"scope":"TARGET","fields":["FINISHED_FITOUT_QTY"]},'
    + N'"limit":{"scope":"TARGET","fields":["FINISHED_QTY"]}},'
    + N'{"thisQty":{"scope":"DETAIL","agg":"SUM","terms":[{"field":"SPARE_QTY","coef":1}]},'
    + N'"usage":{"scope":"TARGET","fields":["FINISHED_FITOUT_SPARE_QTY"]},'
    + N'"limit":{"scope":"TARGET","fields":["FINISHED_SPARE_QTY"]}}],'
    + @ProduceGate
    + N'"message":"以下会出现已备货数量超出工单完工数量\r\n工单号   数量   已备货数量  单据数量  备品  已备货备品  单据备品\r\n{ROWS}",'
    + N'"maxRows":100,'
    + N'"diagnosticFields":[{"scope":"TARGET","field":"PRODUCE_NO"},{"scope":"TARGET","field":"FINISHED_QTY"},'
    + N'{"scope":"TARGET","field":"FINISHED_FITOUT_QTY"},{"scope":"THIS","dimension":1},'
    + N'{"scope":"TARGET","field":"FINISHED_SPARE_QTY"},{"scope":"TARGET","field":"FINISHED_FITOUT_SPARE_QTY"},'
    + N'{"scope":"THIS","dimension":2}]}]}';

DECLARE @BatchMessage NVARCHAR(200) = N'以下序号项需要输入批号! ';
DECLARE @BatchParam NVARCHAR(MAX) =
    N'{"checks":[{"scope":"DETAIL","field":"BATCH_NO","message":"' + @BatchMessage + N'",'
    + N'"diagnosticFields":["SERIAL_NO"],'
    + N'"condition":{"logic":"AND","items":['
    + N'{"type":"not-exists","targetTable":"PRODUCT","negate":true,'
    + N'"condition":{"type":"value-eq","field":{"scope":"TARGET","field":"MANAGE_BATCH"},"value":1},'
    + N'"match":[{"target":"PRO_NO","source":{"scope":"DETAIL","field":"PRO_NO"}}]}]}}]}';

MERGE dbo.MODULE_VALIDATION_RULE AS T
USING (SELECT 1411 AS MODULE_ID, N'qty-not-exceed' AS VALIDATION_KEY, 2 AS SEQ,
              @QtyParam AS PARAM_STRUCT, CAST(NULL AS NVARCHAR(600)) AS MESSAGE,
              N'备货单：已备货不超订单/工单完工（受模块与全局开关门控，数量与备品两量纲）' AS REMARK
       UNION ALL
       SELECT 1411, N'line-require', 3, @BatchParam, @BatchMessage,
              N'批管品必填批号（保存期；原 C# ValidateDetailAsync 判据的忠实移植）') AS S
   ON T.MODULE_ID = S.MODULE_ID AND T.STAGE = N'SAVE' AND T.VALIDATION_KEY = S.VALIDATION_KEY AND T.SEQ = S.SEQ
WHEN MATCHED THEN
    UPDATE SET T.PARAM_STRUCT = S.PARAM_STRUCT, T.MESSAGE = S.MESSAGE, T.REMARK = S.REMARK, T.ENABLED = 1,
               T.LAST_UPDATE_BY = N'DbUp', T.LAST_UPDATE_DATE = SYSDATETIME()
WHEN NOT MATCHED THEN
    INSERT (MODULE_ID, STAGE, SEQ, VALIDATION_KEY, ENABLED, PARAM_STRUCT, MESSAGE, REMARK, SOURCE_REF,
            CREATE_PERSON, CREATE_DATE, LAST_UPDATE_BY, LAST_UPDATE_DATE)
    VALUES (S.MODULE_ID, N'SAVE', S.SEQ, S.VALIDATION_KEY, 1, S.PARAM_STRUCT, S.MESSAGE, S.REMARK, N'P_COP_FITOUT',
            N'DbUp', SYSDATETIME(), N'DbUp', SYSDATETIME());

/* ② 置空已发布快照里的族名 */
IF OBJECT_ID(N'tempdb..#PortedFamily') IS NOT NULL DROP TABLE #PortedFamily;
CREATE TABLE #PortedFamily (MODULE_ID INT PRIMARY KEY, FAMILY NVARCHAR(40) NOT NULL);
INSERT INTO #PortedFamily (MODULE_ID, FAMILY) VALUES (1411, N'cop-fitout');

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

PRINT N'== cop-fitout 入目录并退役 C# 完成（1 模块，快照族名已置空）==';
