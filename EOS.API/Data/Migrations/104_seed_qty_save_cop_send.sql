-- ============================================================================
-- EOS.ERP migration 104: 保存侧不超量校验入目录（B4：1406 送货单）
-- ----------------------------------------------------------------------------
-- C# 判据（CopDomainRules.CopSendAfterSaveAsync 首段）：
--   ① 排程量（无条件）：已送 + 本次 > 排程量
--        FINISHED_QTY + sd.QTY > od.QTY   （目标 COP_SHIPMENT_D，按排程行分组）
--      文案「以下会出现已送货数量超出排程数量\r\n排程单号   数量  已送数量  单据数量\r\n」+ 4 列行
--   ② 订单量（SEND_ORDER_TAG=1）：
--        数量：FINISHED_SEND_QTY + BACK_MATERIAL + BACK_BAD + sd.QTY > od.QTY
--        备品：FINISHED_SPARE_QTY + sd.SPARE_QTY > od.SPARE_QTY
--      文案「以下会出现订单已送货数量超出订单数量\r\n订单单号   数量  已送数量  单据数量  备品  已送备品  单据备品\r\n」+ 7 列行
--
-- 未入目录的部分（仍留 C#，理由见 D9 计划）：
--   · SEND_TAG=1 的"库别存在""库存数量不足""批号库存数量不足" —— 比的是库存可用量
--     （INV_PRO_DEPOT 现存/批号现存），不是"单据数量 vs 被引用单据数量"，与 qty-not-exceed
--     的语义（本次量 + 已用量 超 上限）不同类，硬塞会引入错误抽象。
--
-- 差异登记：② 的旧诊断把"数量组"与"备品组"列在同一行（7 列），本实现按 check 拆分后
-- 各取本组列（跨组列仍取静态值，缺本组聚合之外的那一列），列数略少于旧实现，语义等价。
-- 幂等：按 模块+SAVE+顺序 覆盖参数与启用位。
-- ============================================================================

SET NOCOUNT ON;

DECLARE @GUARD_MESSAGE NVARCHAR(400) = N'本脚本只能在 EOS.ERP 数据库内执行，当前库为 ' + DB_NAME() + N'。';
IF DB_NAME() <> N'EOS.ERP'
    THROW 50000, @GUARD_MESSAGE, 1;

PRINT N'== 播种 1406 送货单保存侧不超量 ==';

DECLARE @NLJ NVARCHAR(4) = N'\r\n';
DECLARE @M_SHIP NVARCHAR(MAX) = N'"match":[{"target":"SHIPMENT_TYPE","source":{"scope":"DETAIL","field":"SHIPMENT_TYPE"}},{"target":"SHIPMENT_NO","source":{"scope":"DETAIL","field":"SHIPMENT_NO"}},{"target":"SERIAL_NO","source":{"scope":"DETAIL","field":"SHIPMENT_SERIAL_NO"}}]';
DECLARE @M_ORDER NVARCHAR(MAX) = N'"match":[{"target":"ORDER_TYPE","source":{"scope":"DETAIL","field":"ORDER_TYPE"}},{"target":"ORDER_NO","source":{"scope":"DETAIL","field":"ORDER_NO"}},{"target":"SERIAL_NO","source":{"scope":"DETAIL","field":"ORDER_SERIAL_NO"}}]';
DECLARE @MSG1 NVARCHAR(300) = N'以下会出现已送货数量超出排程数量 ' + @NLJ + N'排程单号   数量  已送数量  单据数量 ' + @NLJ + N'{ROWS}';
DECLARE @MSG2 NVARCHAR(300) = N'以下会出现订单已送货数量超出订单数量 ' + @NLJ + N'订单单号   数量  已送数量  单据数量  备品  已送备品  单据备品 ' + @NLJ + N'{ROWS}';

DECLARE @PARAM NVARCHAR(MAX) =
    N'{"mode":"usage-not-exceed","checks":['
    -- ① 排程量
    + N'{"targetTable":"COP_SHIPMENT_D",' + @M_SHIP + N','
    + N'"thisQty":{"scope":"DETAIL","agg":"SUM","terms":[{"field":"QTY","coef":1}]},'
    + N'"usage":{"scope":"TARGET","fields":["FINISHED_QTY"]},"limit":{"scope":"TARGET","fields":["QTY"]},'
    + N'"diagnosticFields":[{"scope":"TARGET","field":"SHIPMENT_NO"},{"scope":"TARGET","field":"QTY"},{"scope":"TARGET","field":"FINISHED_QTY"},{"scope":"THIS"}],'
    + N'"message":"' + @MSG1 + N'"},'
    -- ② 订单量·数量
    + N'{"targetTable":"COP_ORDER_D",' + @M_ORDER + N',"switch":{"key":"SEND_ORDER_TAG","expect":1},'
    + N'"thisQty":{"scope":"DETAIL","agg":"SUM","terms":[{"field":"QTY","coef":1}]},'
    + N'"usage":{"scope":"TARGET","fields":["FINISHED_SEND_QTY","BACK_MATERIAL","BACK_BAD"]},"limit":{"scope":"TARGET","fields":["QTY"]},'
    + N'"diagnosticFields":[{"scope":"TARGET","field":"ORDER_NO"},{"scope":"TARGET","field":"QTY"},{"scope":"TARGET","field":"FINISHED_SEND_QTY"},{"scope":"THIS"},{"scope":"TARGET","field":"SPARE_QTY"},{"scope":"TARGET","field":"FINISHED_SPARE_QTY"}],'
    + N'"message":"' + @MSG2 + N'"},'
    -- ② 订单量·备品
    + N'{"targetTable":"COP_ORDER_D",' + @M_ORDER + N',"switch":{"key":"SEND_ORDER_TAG","expect":1},'
    + N'"thisQty":{"scope":"DETAIL","agg":"SUM","terms":[{"field":"SPARE_QTY","coef":1}]},'
    + N'"usage":{"scope":"TARGET","fields":["FINISHED_SPARE_QTY"]},"limit":{"scope":"TARGET","fields":["SPARE_QTY"]},'
    + N'"diagnosticFields":[{"scope":"TARGET","field":"ORDER_NO"},{"scope":"TARGET","field":"QTY"},{"scope":"TARGET","field":"FINISHED_SEND_QTY"},{"scope":"TARGET","field":"SPARE_QTY"},{"scope":"TARGET","field":"FINISHED_SPARE_QTY"},{"scope":"THIS"}],'
    + N'"message":"' + @MSG2 + N'"}]}';

IF EXISTS (SELECT 1 FROM dbo.MODULE_VALIDATION_RULE WHERE MODULE_ID = 1406 AND STAGE = N'SAVE' AND SEQ = 2)
    UPDATE dbo.MODULE_VALIDATION_RULE
    SET VALIDATION_KEY = N'qty-not-exceed', ENABLED = 1, PARAM_STRUCT = @PARAM, MESSAGE = NULL,
        REMARK = N'保存侧送货不超排程/订单（订单侧受 SEND_ORDER_TAG 门控）', SOURCE_REF = N'cop-send',
        LAST_UPDATE_BY = N'migration-104', LAST_UPDATE_DATE = SYSDATETIME()
    WHERE MODULE_ID = 1406 AND STAGE = N'SAVE' AND SEQ = 2;
ELSE
    INSERT INTO dbo.MODULE_VALIDATION_RULE
        (MODULE_ID, STAGE, SEQ, VALIDATION_KEY, ENABLED, PARAM_STRUCT, MESSAGE, REMARK, SOURCE_REF, CREATE_PERSON, CREATE_DATE)
    VALUES
        (1406, N'SAVE', 2, N'qty-not-exceed', 1, @PARAM, NULL,
         N'保存侧送货不超排程/订单（订单侧受 SEND_ORDER_TAG 门控）', N'cop-send', N'migration-104', SYSDATETIME());

PRINT N'-- 落库结果 --';
SELECT CONCAT(N'  module=', MODULE_ID, N' checks=', (SELECT COUNT(*) FROM OPENJSON(PARAM_STRUCT, '$.checks'))) AS SUMMARY
FROM dbo.MODULE_VALIDATION_RULE WHERE MODULE_ID = 1406 AND STAGE = N'SAVE';

PRINT N'== 播种完成 ==';
