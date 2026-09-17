-- ============================================================================
-- EOS.ERP migration 101: 保存侧不超量校验入目录（B4：1423 退料单）
-- ----------------------------------------------------------------------------
-- C# 判据（CopDomainRules.CopBackCheckAsync，经 CopBackAfterSaveAsync 的
-- MODULES.ERROR_NO_SAVE 门控调用，实测 1423 = 1 即生效）：
--   SELECT od.ORDER_NO, od.QTY, od.FINISHED_SEND_QTY, od.BACK_MATERIAL, od.BACK_BAD, sd.QTY
--   FROM dbo.COP_ORDER_D od
--   INNER JOIN (SELECT ORDER_TYPE, ORDER_NO, ORDER_SERIAL_NO, SUM(QTY) QTY
--               FROM dbo.COP_BACK_D WHERE BACK_TYPE=@Type AND BACK_NO=@No
--               GROUP BY ORDER_TYPE, ORDER_NO, ORDER_SERIAL_NO) sd
--     ON od.ORDER_TYPE=sd.ORDER_TYPE AND od.ORDER_NO=sd.ORDER_NO AND od.SERIAL_NO=sd.ORDER_SERIAL_NO
--   WHERE ISNULL(od.FINISHED_SEND_QTY,0)+ISNULL(od.BACK_MATERIAL,0)+ISNULL(od.BACK_BAD,0)+sd.QTY > od.QTY;
--   文案「以下退料已超出订单数量\r\n 订单单号  订单数量  已送数量  已退数量  已退次品  单据数量\r\n」+ 6 列行
--   （注意表头行首有一个空格，逐字保留）
--
-- 目录表达：usage-not-exceed，usage 为三个字段相加（已送数量 + 已退料 + 已退次品），
-- thisQty.agg=SUM 按订单行分组求和，诊断 6 列。
-- 幂等：按 模块+SAVE+顺序 覆盖参数与启用位。
-- ============================================================================

SET NOCOUNT ON;

DECLARE @GUARD_MESSAGE NVARCHAR(400) = N'本脚本只能在 EOS.ERP 数据库内执行，当前库为 ' + DB_NAME() + N'。';
IF DB_NAME() <> N'EOS.ERP'
    THROW 50000, @GUARD_MESSAGE, 1;

PRINT N'== 播种 1423 退料单保存侧不超订单 ==';

DECLARE @NLJ NVARCHAR(4) = N'\r\n';
DECLARE @PARAM NVARCHAR(MAX) =
    N'{"mode":"usage-not-exceed","checks":[{"targetTable":"COP_ORDER_D",'
    + N'"match":['
    + N'{"target":"ORDER_TYPE","source":{"scope":"DETAIL","field":"ORDER_TYPE"}},'
    + N'{"target":"ORDER_NO","source":{"scope":"DETAIL","field":"ORDER_NO"}},'
    + N'{"target":"SERIAL_NO","source":{"scope":"DETAIL","field":"ORDER_SERIAL_NO"}}],'
    + N'"thisQty":{"scope":"DETAIL","agg":"SUM","terms":[{"field":"QTY","coef":1}]},'
    + N'"usage":{"scope":"TARGET","fields":["FINISHED_SEND_QTY","BACK_MATERIAL","BACK_BAD"]},'
    + N'"limit":{"scope":"TARGET","fields":["QTY"]},'
    + N'"diagnosticFields":['
    + N'{"scope":"TARGET","field":"ORDER_NO"},'
    + N'{"scope":"TARGET","field":"QTY"},'
    + N'{"scope":"TARGET","field":"FINISHED_SEND_QTY"},'
    + N'{"scope":"TARGET","field":"BACK_MATERIAL"},'
    + N'{"scope":"TARGET","field":"BACK_BAD"},'
    + N'{"scope":"THIS"}],'
    + N'"message":"以下退料已超出订单数量 ' + @NLJ + N' 订单单号  订单数量  已送数量  已退数量  已退次品  单据数量 ' + @NLJ + N'{ROWS}"}]}';

IF EXISTS (SELECT 1 FROM dbo.MODULE_VALIDATION_RULE WHERE MODULE_ID = 1423 AND STAGE = N'SAVE' AND SEQ = 2)
    UPDATE dbo.MODULE_VALIDATION_RULE
    SET VALIDATION_KEY = N'qty-not-exceed', ENABLED = 1, PARAM_STRUCT = @PARAM, MESSAGE = NULL,
        REMARK = N'保存侧退料不超订单（按订单行分组求和）', SOURCE_REF = N'cop-back',
        LAST_UPDATE_BY = N'migration-101', LAST_UPDATE_DATE = SYSDATETIME()
    WHERE MODULE_ID = 1423 AND STAGE = N'SAVE' AND SEQ = 2;
ELSE
    INSERT INTO dbo.MODULE_VALIDATION_RULE
        (MODULE_ID, STAGE, SEQ, VALIDATION_KEY, ENABLED, PARAM_STRUCT, MESSAGE, REMARK, SOURCE_REF, CREATE_PERSON, CREATE_DATE)
    VALUES
        (1423, N'SAVE', 2, N'qty-not-exceed', 1, @PARAM, NULL,
         N'保存侧退料不超订单（按订单行分组求和）', N'cop-back', N'migration-101', SYSDATETIME());

PRINT N'-- 落库结果 --';
SELECT CONCAT(N'  module=', MODULE_ID, N' key=', VALIDATION_KEY,
              N' checks=', (SELECT COUNT(*) FROM OPENJSON(PARAM_STRUCT, '$.checks')),
              N' 诊断列=', (SELECT COUNT(*) FROM OPENJSON(PARAM_STRUCT, '$.checks[0].diagnosticFields'))) AS SUMMARY
FROM dbo.MODULE_VALIDATION_RULE WHERE MODULE_ID = 1423 AND STAGE = N'SAVE';

PRINT N'== 播种完成 ==';
