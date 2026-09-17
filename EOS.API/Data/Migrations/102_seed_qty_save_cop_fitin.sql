-- ============================================================================
-- EOS.ERP migration 102: 保存侧不超量校验入目录（B4：1412 备货返仓单）
-- ----------------------------------------------------------------------------
-- C# 判据（CopDomainRules.CopFitinCheckAsync，经 CopFitinAfterSaveAsync 的
-- MODULES.ERROR_NO_SAVE 门控调用，实测 1412 = 1 即生效）。四条判据各自把"数量/备品"
-- 写在同一段 SQL、同一条文案里（OR），本迁移按"一条 check = 一个比较式"拆成 8 条：
-- 语义与旧实现完全等价（同一组内任一命中即违规，与 OR 一致）；差异仅在诊断**列数**
-- （旧实现数量组与备品组同列于一行，本实现各取本组列 + 本单量），已登记于 D9 计划。
--
-- ① 返仓不超备货（无条件，目标 COP_FITOUT_D，按备货行分组）
--    QTY-已送货-已返仓 < 本次量            → 本次量+已送货+已返仓 > 数量
--    备品-已送备品-已返仓备品 < 本次备品   → 本次备品+已送备品+已返仓备品 > 备品
-- ② 已送货不超已备货（FITOUT_ORDER_TAG=1，目标 COP_ORDER_D，按订单行分组）
--    已备货-本次量 < 已送货
-- ③ 同 ② 但按工单（FITOUT_PRODUCE_TAG=1，目标 MOC_PRODUCE_M，按制令分组）
-- ④ 已调拔不超已备货（FITOUT_PRODUCE_TRANSFER_TAG=1，目标 MOC_PRODUCE_M）
-- 幂等：按 模块+SAVE+顺序 覆盖参数与启用位。
-- ============================================================================

SET NOCOUNT ON;

DECLARE @GUARD_MESSAGE NVARCHAR(400) = N'本脚本只能在 EOS.ERP 数据库内执行，当前库为 ' + DB_NAME() + N'。';
IF DB_NAME() <> N'EOS.ERP'
    THROW 50000, @GUARD_MESSAGE, 1;

PRINT N'== 播种 1412 备货返仓单保存侧不超量 ==';

DECLARE @NLJ NVARCHAR(4) = N'\r\n';
DECLARE @M_FITOUT NVARCHAR(MAX) = N'"match":[{"target":"FITOUT_TYPE","source":{"scope":"DETAIL","field":"FITOUT_TYPE"}},{"target":"FITOUT_NO","source":{"scope":"DETAIL","field":"FITOUT_NO"}},{"target":"SERIAL_NO","source":{"scope":"DETAIL","field":"FITOUT_SERIAL_NO"}}]';
DECLARE @M_ORDER NVARCHAR(MAX) = N'"match":[{"target":"ORDER_TYPE","source":{"scope":"DETAIL","field":"ORDER_TYPE"}},{"target":"ORDER_NO","source":{"scope":"DETAIL","field":"ORDER_NO"}},{"target":"SERIAL_NO","source":{"scope":"DETAIL","field":"ORDER_SERIAL_NO"}}]';
DECLARE @M_PRODUCE NVARCHAR(MAX) = N'"match":[{"target":"PRODUCE_TYPE","source":{"scope":"DETAIL","field":"PRODUCE_TYPE"}},{"target":"PRODUCE_NO","source":{"scope":"DETAIL","field":"PRODUCE_NO"}}]';
DECLARE @THISQ NVARCHAR(MAX) = N'"thisQty":{"scope":"DETAIL","agg":"SUM","terms":[{"field":"QTY","coef":1}]}';
DECLARE @THISS NVARCHAR(MAX) = N'"thisQty":{"scope":"DETAIL","agg":"SUM","terms":[{"field":"SPARE_QTY","coef":1}]}';

DECLARE @MSG1 NVARCHAR(300) = N'以下返仓已超出备货单数量 ' + @NLJ + N'备货单号   数量  已送货   已返仓数量  单据数量  备品  已送备品  已返仓备品  单据备品 ' + @NLJ + N'{ROWS}';
DECLARE @MSG2 NVARCHAR(300) = N'以下会出现已送货数量超出已备货数量 ' + @NLJ + N'订单号  备货数量  已送货  单据数量  备货备品  已送备品  单据备品 ' + @NLJ + N'{ROWS}';
DECLARE @MSG3 NVARCHAR(300) = N'以下会出现已送货数量超出已备货数量 ' + @NLJ + N'工单号  备货数量  已送货  单据数量  备货备品  已送备品  单据备品 ' + @NLJ + N'{ROWS}';
DECLARE @MSG4 NVARCHAR(300) = N'以下会出现已调拔数量超出已备货数量 ' + @NLJ + N'工单号  备货数量  已调拔  单据数量  备货备品  已调拔备品  单据备品 ' + @NLJ + N'{ROWS}';

-- ① 返仓不超备货
DECLARE @C1 NVARCHAR(MAX) =
    N'{"targetTable":"COP_FITOUT_D",' + @M_FITOUT + N',' + @THISQ
    + N',"usage":{"scope":"TARGET","fields":["FINISHED_QTY","RETURN_QTY"]},"limit":{"scope":"TARGET","fields":["QTY"]},'
    + N'"diagnosticFields":[{"scope":"TARGET","field":"FITOUT_NO"},{"scope":"TARGET","field":"QTY"},{"scope":"TARGET","field":"FINISHED_QTY"},{"scope":"TARGET","field":"RETURN_QTY"},{"scope":"THIS"}],'
    + N'"message":"' + @MSG1 + N'"}';
DECLARE @C2 NVARCHAR(MAX) =
    N'{"targetTable":"COP_FITOUT_D",' + @M_FITOUT + N',' + @THISS
    + N',"usage":{"scope":"TARGET","fields":["FINISHED_SPARE_QTY","RETURN_SPARE_QTY"]},"limit":{"scope":"TARGET","fields":["SPARE_QTY"]},'
    + N'"diagnosticFields":[{"scope":"TARGET","field":"FITOUT_NO"},{"scope":"TARGET","field":"SPARE_QTY"},{"scope":"TARGET","field":"FINISHED_SPARE_QTY"},{"scope":"TARGET","field":"RETURN_SPARE_QTY"},{"scope":"THIS"}],'
    + N'"message":"' + @MSG1 + N'"}';
-- ② 订单侧已送货不超已备货
DECLARE @C3 NVARCHAR(MAX) =
    N'{"targetTable":"COP_ORDER_D",' + @M_ORDER + N',"switch":{"key":"FITOUT_ORDER_TAG","expect":1},' + @THISQ
    + N',"usage":{"scope":"TARGET","fields":["FINISHED_SEND_QTY"]},"limit":{"scope":"TARGET","fields":["FINISHED_FITOUT_QTY"]},'
    + N'"diagnosticFields":[{"scope":"TARGET","field":"ORDER_NO"},{"scope":"TARGET","field":"FINISHED_FITOUT_QTY"},{"scope":"TARGET","field":"FINISHED_SEND_QTY"},{"scope":"THIS"}],'
    + N'"message":"' + @MSG2 + N'"}';
DECLARE @C4 NVARCHAR(MAX) =
    N'{"targetTable":"COP_ORDER_D",' + @M_ORDER + N',"switch":{"key":"FITOUT_ORDER_TAG","expect":1},' + @THISS
    + N',"usage":{"scope":"TARGET","fields":["FINISHED_SPARE_QTY"]},"limit":{"scope":"TARGET","fields":["FINISHED_FITOUT_SPARE_QTY"]},'
    + N'"diagnosticFields":[{"scope":"TARGET","field":"ORDER_NO"},{"scope":"TARGET","field":"FINISHED_FITOUT_SPARE_QTY"},{"scope":"TARGET","field":"FINISHED_SPARE_QTY"},{"scope":"THIS"}],'
    + N'"message":"' + @MSG2 + N'"}';
-- ③ 工单侧已送货不超已备货
DECLARE @C5 NVARCHAR(MAX) =
    N'{"targetTable":"MOC_PRODUCE_M",' + @M_PRODUCE + N',"switch":{"key":"FITOUT_PRODUCE_TAG","expect":1},' + @THISQ
    + N',"usage":{"scope":"TARGET","fields":["FINISHED_SEND_QTY"]},"limit":{"scope":"TARGET","fields":["FINISHED_FITOUT_QTY"]},'
    + N'"diagnosticFields":[{"scope":"TARGET","field":"PRODUCE_NO"},{"scope":"TARGET","field":"FINISHED_FITOUT_QTY"},{"scope":"TARGET","field":"FINISHED_SEND_QTY"},{"scope":"THIS"}],'
    + N'"message":"' + @MSG3 + N'"}';
DECLARE @C6 NVARCHAR(MAX) =
    N'{"targetTable":"MOC_PRODUCE_M",' + @M_PRODUCE + N',"switch":{"key":"FITOUT_PRODUCE_TAG","expect":1},' + @THISS
    + N',"usage":{"scope":"TARGET","fields":["FINISHED_SEND_SPARE_QTY"]},"limit":{"scope":"TARGET","fields":["FINISHED_FITOUT_SPARE_QTY"]},'
    + N'"diagnosticFields":[{"scope":"TARGET","field":"PRODUCE_NO"},{"scope":"TARGET","field":"FINISHED_FITOUT_SPARE_QTY"},{"scope":"TARGET","field":"FINISHED_SEND_SPARE_QTY"},{"scope":"THIS"}],'
    + N'"message":"' + @MSG3 + N'"}';
-- ④ 工单侧已调拔不超已备货
DECLARE @C7 NVARCHAR(MAX) =
    N'{"targetTable":"MOC_PRODUCE_M",' + @M_PRODUCE + N',"switch":{"key":"FITOUT_PRODUCE_TRANSFER_TAG","expect":1},' + @THISQ
    + N',"usage":{"scope":"TARGET","fields":["FINISHED_TRANSFER_QTY"]},"limit":{"scope":"TARGET","fields":["FINISHED_FITOUT_QTY"]},'
    + N'"diagnosticFields":[{"scope":"TARGET","field":"PRODUCE_NO"},{"scope":"TARGET","field":"FINISHED_FITOUT_QTY"},{"scope":"TARGET","field":"FINISHED_TRANSFER_QTY"},{"scope":"THIS"}],'
    + N'"message":"' + @MSG4 + N'"}';
DECLARE @C8 NVARCHAR(MAX) =
    N'{"targetTable":"MOC_PRODUCE_M",' + @M_PRODUCE + N',"switch":{"key":"FITOUT_PRODUCE_TRANSFER_TAG","expect":1},' + @THISS
    + N',"usage":{"scope":"TARGET","fields":["FINISHED_TRANSFER_SPARE_QTY"]},"limit":{"scope":"TARGET","fields":["FINISHED_FITOUT_SPARE_QTY"]},'
    + N'"diagnosticFields":[{"scope":"TARGET","field":"PRODUCE_NO"},{"scope":"TARGET","field":"FINISHED_FITOUT_SPARE_QTY"},{"scope":"TARGET","field":"FINISHED_TRANSFER_SPARE_QTY"},{"scope":"THIS"}],'
    + N'"message":"' + @MSG4 + N'"}';

DECLARE @PARAM NVARCHAR(MAX) = N'{"mode":"usage-not-exceed","checks":['
    + @C1 + N',' + @C2 + N',' + @C3 + N',' + @C4 + N',' + @C5 + N',' + @C6 + N',' + @C7 + N',' + @C8 + N']}';

IF EXISTS (SELECT 1 FROM dbo.MODULE_VALIDATION_RULE WHERE MODULE_ID = 1412 AND STAGE = N'SAVE' AND SEQ = 2)
    UPDATE dbo.MODULE_VALIDATION_RULE
    SET VALIDATION_KEY = N'qty-not-exceed', ENABLED = 1, PARAM_STRUCT = @PARAM, MESSAGE = NULL,
        REMARK = N'保存侧返仓/送货/调拔不超备货（数量与备品各一条，SYSSS 门控）', SOURCE_REF = N'cop-fitin',
        LAST_UPDATE_BY = N'migration-102', LAST_UPDATE_DATE = SYSDATETIME()
    WHERE MODULE_ID = 1412 AND STAGE = N'SAVE' AND SEQ = 2;
ELSE
    INSERT INTO dbo.MODULE_VALIDATION_RULE
        (MODULE_ID, STAGE, SEQ, VALIDATION_KEY, ENABLED, PARAM_STRUCT, MESSAGE, REMARK, SOURCE_REF, CREATE_PERSON, CREATE_DATE)
    VALUES
        (1412, N'SAVE', 2, N'qty-not-exceed', 1, @PARAM, NULL,
         N'保存侧返仓/送货/调拔不超备货（数量与备品各一条，SYSSS 门控）', N'cop-fitin', N'migration-102', SYSDATETIME());

PRINT N'-- 落库结果 --';
SELECT CONCAT(N'  module=', MODULE_ID, N' checks=', (SELECT COUNT(*) FROM OPENJSON(PARAM_STRUCT, '$.checks'))) AS SUMMARY
FROM dbo.MODULE_VALIDATION_RULE WHERE MODULE_ID = 1412 AND STAGE = N'SAVE';

PRINT N'== 播种完成 ==';
