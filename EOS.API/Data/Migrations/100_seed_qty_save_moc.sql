-- ============================================================================
-- EOS.ERP migration 100: 保存侧不超量校验入目录（B4：1507 生产计划 / 1502 制令单）
-- ----------------------------------------------------------------------------
-- 判据与文案逐条对照 C#（MocDomainRules），均受 MODULES.ERROR_NO_SAVE 门控且实测为 1，
-- 故 SAVE 期只给这两个模块配规则（门控即"只有它们配"，语义等价）。
--
-- 1507 生产计划（MocPlanAfterSaveAsync）：
--   a.QTY       > b.QTY - b.DO_PLAN_QTY          → 本次量 + 已计划量 > 订单量
--   a.SPARE_QTY > b.QTY - b.DO_PLAN_SPARE_QTY    → 本次备品 + 已计划备品 > 订单量
--   （第二式的减数历史实现用的也是 b.QTY，不是 b.SPARE_QTY —— 逐字照抄，不修正）
--   文案「以下序号项生产计划超出订单\r\n序号  订单数量  已计划数  本次数量  订单备品  已计划备品  本次备品\r\n」+ 7 列行
--
-- 1502 制令单（MocProduceAfterSaveAsync，SYSSS 开关门控）：
--   ① PRODUCE_ORDER_TAG=1：PLAN_QTY < 已计划 + 本次（订单量/备品两条同文案）
--   ② PRODUCE_PLAN_MOC_TAG=1：REQUIRE_QTY < 已生产 + 本次
--   文案为固定单行，不带明细行。
-- 幂等：按 模块+SAVE+顺序 覆盖参数与启用位。
-- ============================================================================

SET NOCOUNT ON;

DECLARE @GUARD_MESSAGE NVARCHAR(400) = N'本脚本只能在 EOS.ERP 数据库内执行，当前库为 ' + DB_NAME() + N'。';
IF DB_NAME() <> N'EOS.ERP'
    THROW 50000, @GUARD_MESSAGE, 1;

PRINT N'== 播种 1507 生产计划 / 1502 制令单 保存侧不超量 ==';

DECLARE @NLJ NVARCHAR(4) = N'\r\n';
DECLARE @PLAN_MSG NVARCHAR(200) = N'以下序号项生产计划超出订单 ' + @NLJ
    + N'序号  订单数量  已计划数  本次数量  订单备品  已计划备品  本次备品 ' + @NLJ + N'{ROWS}';
DECLARE @PLAN_MATCH NVARCHAR(MAX) =
    N'"match":['
    + N'{"target":"ORDER_TYPE","source":{"scope":"DETAIL","field":"ORDER_TYPE"}},'
    + N'{"target":"ORDER_NO","source":{"scope":"DETAIL","field":"ORDER_NO"}},'
    + N'{"target":"SERIAL_NO","source":{"scope":"DETAIL","field":"ORDER_SERIAL_NO"}}]';
DECLARE @PLAN_DIAG NVARCHAR(MAX) =
    N'"diagnosticFields":['
    + N'{"scope":"SOURCE","field":"SERIAL_NO"},'
    + N'{"scope":"TARGET","field":"QTY"},'
    + N'{"scope":"TARGET","field":"DO_PLAN_QTY"},'
    + N'{"scope":"SOURCE","field":"QTY"},'
    + N'{"scope":"TARGET","field":"SPARE_QTY"},'
    + N'{"scope":"TARGET","field":"DO_PLAN_SPARE_QTY"},'
    + N'{"scope":"SOURCE","field":"SPARE_QTY"}]';

DECLARE @PARAM_1507 NVARCHAR(MAX) =
    N'{"mode":"usage-not-exceed","checks":['
    + N'{"targetTable":"COP_ORDER_D",' + @PLAN_MATCH + N','
    + N'"thisQty":{"scope":"DETAIL","terms":[{"field":"QTY","coef":1}]},'
    + N'"usage":{"scope":"TARGET","fields":["DO_PLAN_QTY"]},'
    + N'"limit":{"scope":"TARGET","fields":["QTY"]},'
    + @PLAN_DIAG + N',"message":"' + @PLAN_MSG + N'"},'
    + N'{"targetTable":"COP_ORDER_D",' + @PLAN_MATCH + N','
    + N'"thisQty":{"scope":"DETAIL","terms":[{"field":"SPARE_QTY","coef":1}]},'
    + N'"usage":{"scope":"TARGET","fields":["DO_PLAN_SPARE_QTY"]},'
    + N'"limit":{"scope":"TARGET","fields":["QTY"]},'
    + @PLAN_DIAG + N',"message":"' + @PLAN_MSG + N'"}]}';

DECLARE @ORDER_MATCH NVARCHAR(MAX) =
    N'"match":['
    + N'{"target":"ORDER_TYPE","source":{"scope":"MASTER","field":"ORDER_TYPE"}},'
    + N'{"target":"ORDER_NO","source":{"scope":"MASTER","field":"ORDER_NO"}},'
    + N'{"target":"SERIAL_NO","source":{"scope":"MASTER","field":"ORDER_SERIAL_NO"}}]';
DECLARE @PARAM_1502 NVARCHAR(MAX) =
    N'{"mode":"usage-not-exceed","checks":['
    + N'{"targetTable":"COP_ORDER_D",' + @ORDER_MATCH + N','
    + N'"switch":{"key":"PRODUCE_ORDER_TAG","expect":1},'
    + N'"thisQty":{"scope":"MASTER","terms":[{"field":"QTY","coef":1}]},'
    + N'"usage":{"scope":"TARGET","fields":["FINISHED_PLAN_QTY"]},'
    + N'"limit":{"scope":"TARGET","fields":["PLAN_QTY"]},'
    + N'"message":"生产数量或备品生产数量超出订单数量"},'
    + N'{"targetTable":"COP_ORDER_D",' + @ORDER_MATCH + N','
    + N'"switch":{"key":"PRODUCE_ORDER_TAG","expect":1},'
    + N'"thisQty":{"scope":"MASTER","terms":[{"field":"SPARE_QTY","coef":1}]},'
    + N'"usage":{"scope":"TARGET","fields":["FINISHED_PLAN_SPARE_QTY"]},'
    + N'"limit":{"scope":"TARGET","fields":["PLAN_SPARE_QTY"]},'
    + N'"message":"生产数量或备品生产数量超出订单数量"},'
    + N'{"targetTable":"MOC_PLAN_MOC",'
    + N'"match":['
    + N'{"target":"PLAN_TYPE","source":{"scope":"MASTER","field":"PLAN_TYPE"}},'
    + N'{"target":"PLAN_NO","source":{"scope":"MASTER","field":"PLAN_NO"}},'
    + N'{"target":"SERIAL_NO","source":{"scope":"MASTER","field":"PLAN_SERIAL_NO"}}],'
    + N'"switch":{"key":"PRODUCE_PLAN_MOC_TAG","expect":1},'
    + N'"thisQty":{"scope":"MASTER","terms":[{"field":"QTY","coef":1}]},'
    + N'"usage":{"scope":"TARGET","fields":["PRODUCE_QTY"]},'
    + N'"limit":{"scope":"TARGET","fields":["REQUIRE_QTY"]},'
    + N'"message":"生产数量超出生产计划数量"}]}';

MERGE dbo.MODULE_VALIDATION_RULE AS t
USING (VALUES
    (1507, 1, N'qty-not-exceed', @PARAM_1507, N'生产计划超订单'),
    (1502, 1, N'qty-not-exceed', @PARAM_1502, N'制令单超订单/生产计划')
) AS s (MODULE_ID, SEQ, VALIDATION_KEY, PARAM_STRUCT, REMARK)
ON t.MODULE_ID = s.MODULE_ID AND t.STAGE = N'SAVE' AND t.SEQ = s.SEQ
WHEN MATCHED THEN UPDATE SET
    t.VALIDATION_KEY = s.VALIDATION_KEY, t.ENABLED = 1, t.PARAM_STRUCT = s.PARAM_STRUCT,
    t.MESSAGE = NULL, t.REMARK = s.REMARK,
    t.LAST_UPDATE_BY = N'migration-100', t.LAST_UPDATE_DATE = SYSDATETIME()
WHEN NOT MATCHED THEN INSERT
    (MODULE_ID, STAGE, SEQ, VALIDATION_KEY, ENABLED, PARAM_STRUCT, MESSAGE, REMARK, SOURCE_REF, CREATE_PERSON, CREATE_DATE)
    VALUES (s.MODULE_ID, N'SAVE', s.SEQ, s.VALIDATION_KEY, 1, s.PARAM_STRUCT, NULL, s.REMARK,
            N'moc-plan/moc-produce', N'migration-100', SYSDATETIME());

PRINT N'-- 落库结果 --';
SELECT CONCAT(N'  module=', MODULE_ID, N' key=', VALIDATION_KEY,
              N' checks=', (SELECT COUNT(*) FROM OPENJSON(PARAM_STRUCT, '$.checks'))) AS SUMMARY
FROM dbo.MODULE_VALIDATION_RULE WHERE MODULE_ID IN (1502, 1507) AND STAGE = N'SAVE' ORDER BY MODULE_ID;

PRINT N'== 播种完成 ==';
