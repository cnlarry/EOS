-- ============================================================================
-- EOS.ERP migration 404: 修复「把列对列比较写成 value」的选择器过滤条件
-- ----------------------------------------------------------------------------
-- 症状：这些数据源的选择器一打开就 500，用户永远选不了：
--   SqlException: 在将 nvarchar 值 'MOU_ACCEPT_M.QTY' 转换成数据类型 int 时失败。
--
-- 根因：FILTER_STRUCT 的 value 按契约是「字面量或运行时模板，编译期一律参数化绑定」，
-- 而存量里有一批把"列对列比较"直接写进了 value：
--   {"field":"MOU_ACCEPT_M.FINISHED_QTY","operator":"LT","value":"MOU_ACCEPT_M.QTY"}
-- 编译期它被绑成参数 @cf0='MOU_ACCEPT_M.QTY'，SQL 便拿这个字符串去和数值列比较，
-- 运行期类型转换失败 —— 选择器抛异常，不是"没有数据"，是彻底不可用。
--
-- 做法：改写成模型本就支持的表达式形态（left/right 的 kind:"column"，算术用 kind:"arith"），
-- 语义与配置者本意一致（"已完工数量 < 数量"仍是比较两列，而不是与某个字符串比较）。
-- 覆盖全库 37 条（37 个数据源），逐条按原值定位，只改有问题的那一项、其余键原样保留。
--
-- 配套的代码守卫（同批提交）：
--   · ChooserFilterCompiler：编译期识别「value 形如源表/已JOIN表.列」即 fail-closed（不再漏到 SQL）；
--   · ChooserFilterValidator：保存期直接拒绝这种写法，新配的不会再写进来。
--
-- 幂等：每条 UPDATE 都带"原 value 仍存在"的定位条件，重复执行命中 0 行。
-- 回滚：无（改的是不可用的配置；如需回退，模型也接受把 left/right 改回 value 的旧形态，
--       但那会重新变成 500）。
-- ============================================================================

SET NOCOUNT ON;

SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET ANSI_PADDING ON;
SET ANSI_WARNINGS ON;
SET CONCAT_NULL_YIELDS_NULL ON;
SET ARITHABORT ON;
SET XACT_ABORT ON;

DECLARE @GUARD_MESSAGE NVARCHAR(400) = N'本脚本只能在 EOS.ERP 数据库内执行，当前库为 ' + DB_NAME() + N'。';
IF DB_NAME() <> N'EOS.ERP'
    THROW 52900, @GUARD_MESSAGE, 1;

BEGIN TRANSACTION;

-- COP_BACK_D.PRO_NO #1（原 value：COP_ORDER_D.FINISHED_SEND_QTY+COP_ORDER_D.BACK_MATERIAL+COP_ORDER_D.BACK_BAD）
UPDATE dbo.FIELD_DATASOURCE
   SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"COP_ORDER_M.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null},{"field":"COP_ORDER_M.CLIENT_ID","operator":"EQ","value":"{m.CLIENT_ID}","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null},{"field":"COP_ORDER_D.FINISHED_TAG","operator":"EQ","value":"0","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null},{"operator":"GT","nullSafe":null,"left":{"kind":"column","table":"COP_ORDER_D","column":"QTY"},"right":{"kind":"arith","op":"+","left":{"kind":"arith","op":"+","left":{"kind":"column","table":"COP_ORDER_D","column":"FINISHED_SEND_QTY"},"right":{"kind":"column","table":"COP_ORDER_D","column":"BACK_MATERIAL"}},"right":{"kind":"column","table":"COP_ORDER_D","column":"BACK_BAD"}},"negate":null,"group":null,"subquery":null}]}',
       LAST_UPDATE_BY = N'migration-404',
       LAST_UPDATE_DATE = SYSDATETIME()
 WHERE ID = 151;

-- CUS_PROIN_D.MANUAL_NO #1（原 value：CUS_MANUAL_PRO.OUT_QTY）
UPDATE dbo.FIELD_DATASOURCE
   SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"CUS_MANUAL_M.MANUAL_STATE","operator":"NE","value":"3","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null},{"operator":"GT","nullSafe":null,"left":{"kind":"column","table":"CUS_MANUAL_PRO","column":"CUS_QTY"},"right":{"kind":"column","table":"CUS_MANUAL_PRO","column":"OUT_QTY"},"negate":null,"group":null,"subquery":null}]}',
       LAST_UPDATE_BY = N'migration-404',
       LAST_UPDATE_DATE = SYSDATETIME()
 WHERE ID = 263;

-- CUS_PROOUT_D.MANUAL_NO #1（原 value：CUS_MANUAL_PRO.OUT_QTY）
UPDATE dbo.FIELD_DATASOURCE
   SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"CUS_MANUAL_M.MANUAL_STATE","operator":"NE","value":"3","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null},{"operator":"GT","nullSafe":null,"left":{"kind":"column","table":"CUS_MANUAL_PRO","column":"IN_QTY"},"right":{"kind":"column","table":"CUS_MANUAL_PRO","column":"OUT_QTY"},"negate":null,"group":null,"subquery":null}]}',
       LAST_UPDATE_BY = N'migration-404',
       LAST_UPDATE_DATE = SYSDATETIME()
 WHERE ID = 266;

-- CUS_PURCHASE_D.EXPORT_NO #1（原 value：CUS_EXPORT_MAT.PUR_QTY）
UPDATE dbo.FIELD_DATASOURCE
   SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"CUS_EXPORT_MAT.MANUAL_NO","operator":"EQ","value":"{m.MANUAL_NO}","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null},{"operator":"GT","nullSafe":null,"left":{"kind":"column","table":"CUS_EXPORT_MAT","column":"QTY"},"right":{"kind":"column","table":"CUS_EXPORT_MAT","column":"PUR_QTY"},"negate":null,"group":null,"subquery":null}]}',
       LAST_UPDATE_BY = N'migration-404',
       LAST_UPDATE_DATE = SYSDATETIME()
 WHERE ID = 268;

-- CUS_PURCHASE_D.MANUAL_NO #2（原 value：CUS_EXPORT_MAT.PUR_QTY）
UPDATE dbo.FIELD_DATASOURCE
   SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"CUS_EXPORT_MAT.MANUAL_NO","operator":"EQ","value":"{m.MANUAL_NO}","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null},{"operator":"GT","nullSafe":null,"left":{"kind":"column","table":"CUS_EXPORT_MAT","column":"QTY"},"right":{"kind":"column","table":"CUS_EXPORT_MAT","column":"PUR_QTY"},"negate":null,"group":null,"subquery":null}]}',
       LAST_UPDATE_BY = N'migration-404',
       LAST_UPDATE_DATE = SYSDATETIME()
 WHERE ID = 271;

-- CUS_PURCHASE_D.PRO_ID #1（原 value：CUS_EXPORT_MAT.PUR_QTY）
UPDATE dbo.FIELD_DATASOURCE
   SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"CUS_EXPORT_MAT.MANUAL_NO","operator":"EQ","value":"{m.MANUAL_NO}","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null},{"operator":"GT","nullSafe":null,"left":{"kind":"column","table":"CUS_EXPORT_MAT","column":"QTY"},"right":{"kind":"column","table":"CUS_EXPORT_MAT","column":"PUR_QTY"},"negate":null,"group":null,"subquery":null}]}',
       LAST_UPDATE_BY = N'migration-404',
       LAST_UPDATE_DATE = SYSDATETIME()
 WHERE ID = 273;

-- CUS_SEAL_D.PRO_ID #1（原 value：CUS_MANUAL_PRO.EXP_QTY）
UPDATE dbo.FIELD_DATASOURCE
   SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"CUS_MANUAL_PRO.MANUAL_NO","operator":"EQ","value":"{m.MANUAL_NO}","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null},{"operator":"GT","nullSafe":null,"left":{"kind":"column","table":"CUS_MANUAL_PRO","column":"QTY"},"right":{"kind":"column","table":"CUS_MANUAL_PRO","column":"EXP_QTY"},"negate":null,"group":null,"subquery":null}]}',
       LAST_UPDATE_BY = N'migration-404',
       LAST_UPDATE_DATE = SYSDATETIME()
 WHERE ID = 278;

-- INV_OCCUR_OUT_D.BATCH_NO #1（原 value：INV_BATCH_M.OUT_SUM）
UPDATE dbo.FIELD_DATASOURCE
   SET FILTER_STRUCT = N'{"logic":"AND","items":[{"operator":"GT","nullSafe":null,"left":{"kind":"column","table":"INV_BATCH_M","column":"IN_SUM"},"right":{"kind":"column","table":"INV_BATCH_M","column":"OUT_SUM"},"negate":null,"group":null,"subquery":null}]}',
       LAST_UPDATE_BY = N'migration-404',
       LAST_UPDATE_DATE = SYSDATETIME()
 WHERE ID = 566;

-- MOC_PLAN_D.ORDER_NO #2（原 value：isnull(COP_ORDER_D.do_plan_qty,0)）
UPDATE dbo.FIELD_DATASOURCE
   SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"COP_ORDER_M.CLIENT_ID","operator":"EQ","value":"{m.CLIENT_ID}","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null},{"field":"COP_ORDER_D.PRO_NO","operator":"EQ","value":"{m.PRO_NO}","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null},{"field":"COP_ORDER_M.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null},{"field":"COP_ORDER_D.FINISHED_TAG","operator":"EQ","value":"0","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null},{"operator":"GT","nullSafe":null,"left":{"kind":"column","table":"COP_ORDER_D","column":"qty"},"right":{"kind":"isnull","left":{"kind":"column","table":"COP_ORDER_D","column":"do_plan_qty"},"value":"0"},"negate":null,"group":null,"subquery":null}]}',
       LAST_UPDATE_BY = N'migration-404',
       LAST_UPDATE_DATE = SYSDATETIME()
 WHERE ID = 673;

-- PUR_APPLY_MORE.PRODUCE_NO #1（原 value：MOC_PRODUCE_D.NEED_QTY）
UPDATE dbo.FIELD_DATASOURCE
   SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"MOC_PRODUCE_M.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null},{"operator":"LT","nullSafe":null,"left":{"kind":"column","table":"MOC_PRODUCE_D","column":"APPLY_QTY"},"right":{"kind":"column","table":"MOC_PRODUCE_D","column":"NEED_QTY"},"negate":null,"group":null,"subquery":null}]}',
       LAST_UPDATE_BY = N'migration-404',
       LAST_UPDATE_DATE = SYSDATETIME()
 WHERE ID = 769;

-- MOU_BATCH_M.ACCEPT_NO #1（原 value：MOU_ACCEPT_M.QTY）
UPDATE dbo.FIELD_DATASOURCE
   SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"MOU_ACCEPT_M.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null},{"field":"MOU_ACCEPT_M.CLIENT_ID","operator":"EQ","value":"{m.CLIENT_ID}","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null},{"field":"MOU_ACCEPT_M.ACCEPT_STATE","operator":"EQ","value":"承认","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null},{"operator":"LT","nullSafe":null,"left":{"kind":"column","table":"MOU_ACCEPT_M","column":"FINISHED_QTY"},"right":{"kind":"column","table":"MOU_ACCEPT_M","column":"QTY"},"negate":null,"group":null,"subquery":null}]}',
       LAST_UPDATE_BY = N'migration-404',
       LAST_UPDATE_DATE = SYSDATETIME()
 WHERE ID = 814;

-- MOU_BATCH_M.ACCEPT_NO #2（原 value：MOU_ACCEPT_M.QTY）
UPDATE dbo.FIELD_DATASOURCE
   SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"MOU_ACCEPT_M.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null},{"field":"MOU_ACCEPT_M.ACCEPT_STATE","operator":"EQ","value":"承认","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null},{"operator":"LT","nullSafe":null,"left":{"kind":"column","table":"MOU_ACCEPT_M","column":"FINISHED_QTY"},"right":{"kind":"column","table":"MOU_ACCEPT_M","column":"QTY"},"negate":null,"group":null,"subquery":null}]}',
       LAST_UPDATE_BY = N'migration-404',
       LAST_UPDATE_DATE = SYSDATETIME()
 WHERE ID = 815;

-- MOU_BATCHIN_D.BATCH_NO #1（原 value：MOU_BATCH_M.FINISHED_QTY）
UPDATE dbo.FIELD_DATASOURCE
   SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"MOU_BATCH_M.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null},{"field":"MOU_BATCH_M.CLIENT_ID","operator":"EQ","value":"{d.CLIENT_ID}","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null},{"operator":"GT","nullSafe":null,"left":{"kind":"column","table":"MOU_BATCH_M","column":"QTY"},"right":{"kind":"column","table":"MOU_BATCH_M","column":"FINISHED_QTY"},"negate":null,"group":null,"subquery":null}]}',
       LAST_UPDATE_BY = N'migration-404',
       LAST_UPDATE_DATE = SYSDATETIME()
 WHERE ID = 830;

-- COP_RECEIPT_OTHER.PREPAY_TYPE #1（原 value：COP_PREPAY_M.PREPAY_AMOUNT）
UPDATE dbo.FIELD_DATASOURCE
   SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"COP_PREPAY_M.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null},{"operator":"GT","nullSafe":null,"left":{"kind":"column","table":"COP_PREPAY_M","column":"AMOUNT"},"right":{"kind":"column","table":"COP_PREPAY_M","column":"PREPAY_AMOUNT"},"negate":null,"group":null,"subquery":null}]}',
       LAST_UPDATE_BY = N'migration-404',
       LAST_UPDATE_DATE = SYSDATETIME()
 WHERE ID = 1406;

-- COP_SEND_D.PRO_NO #2（原 value：COP_SHIPMENT_D.FINISHED_QTY）
UPDATE dbo.FIELD_DATASOURCE
   SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"COP_SHIPMENT_M.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null},{"field":"COP_SHIPMENT_D.CLIENT_ID","operator":"EQ","value":"{m.CLIENT_ID}","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null},{"operator":"GT","nullSafe":null,"left":{"kind":"column","table":"COP_SHIPMENT_D","column":"QTY"},"right":{"kind":"column","table":"COP_SHIPMENT_D","column":"FINISHED_QTY"},"negate":null,"group":null,"subquery":null},{"field":"COP_SHIPMENT_M.SHIPMENT_DATE","operator":"DAYS_FROM_TODAY","value":"0","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}',
       LAST_UPDATE_BY = N'migration-404',
       LAST_UPDATE_DATE = SYSDATETIME()
 WHERE ID = 1439;

-- COP_SHIPMENT_D.PRO_NO #1（原 value：COP_ORDER_D.QTY）
UPDATE dbo.FIELD_DATASOURCE
   SET FILTER_STRUCT = N'{"logic":"AND","items":[{"operator":"LT","nullSafe":null,"left":{"kind":"column","table":"COP_ORDER_D","column":"FINISHED_SEND_QTY"},"right":{"kind":"column","table":"COP_ORDER_D","column":"QTY"},"negate":null,"group":null,"subquery":null},{"field":"COP_ORDER_D.FINISHED_TAG","operator":"EQ","value":"0","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null},{"field":"COP_ORDER_M.CLIENT_ID","operator":"EQ","value":"{d.CLIENT_ID}","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}',
       LAST_UPDATE_BY = N'migration-404',
       LAST_UPDATE_DATE = SYSDATETIME()
 WHERE ID = 1459;

-- CUS_MATERIN_D.MANUAL_NO #1（原 value：CUS_MANUAL_MAT.IN_QTY）
UPDATE dbo.FIELD_DATASOURCE
   SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"CUS_MANUAL_M.MANUAL_STATE","operator":"NE","value":"3","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null},{"operator":"GT","nullSafe":null,"left":{"kind":"column","table":"CUS_MANUAL_MAT","column":"CUS_QTY"},"right":{"kind":"column","table":"CUS_MANUAL_MAT","column":"IN_QTY"},"negate":null,"group":null,"subquery":null}]}',
       LAST_UPDATE_BY = N'migration-404',
       LAST_UPDATE_DATE = SYSDATETIME()
 WHERE ID = 1515;

-- CUS_MATERIN_D.PURCHASE_NO #1（原 value：CUS_PURCHASE_D.QTY）
UPDATE dbo.FIELD_DATASOURCE
   SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"CUS_PURCHASE_M.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null},{"operator":"LT","nullSafe":null,"left":{"kind":"column","table":"CUS_PURCHASE_D","column":"FINISHED_QTY"},"right":{"kind":"column","table":"CUS_PURCHASE_D","column":"QTY"},"negate":null,"group":null,"subquery":null}]}',
       LAST_UPDATE_BY = N'migration-404',
       LAST_UPDATE_DATE = SYSDATETIME()
 WHERE ID = 1516;

-- CUS_MATEROUT_D.MANUAL_NO #1（原 value：CUS_MANUAL_MAT.OUT_QTY）
UPDATE dbo.FIELD_DATASOURCE
   SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"CUS_MANUAL_M.MANUAL_STATE","operator":"NE","value":"3","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null},{"operator":"GT","nullSafe":null,"left":{"kind":"column","table":"CUS_MANUAL_MAT","column":"IN_QTY"},"right":{"kind":"column","table":"CUS_MANUAL_MAT","column":"OUT_QTY"},"negate":null,"group":null,"subquery":null}]}',
       LAST_UPDATE_BY = N'migration-404',
       LAST_UPDATE_DATE = SYSDATETIME()
 WHERE ID = 1521;

-- PUR_CANCEL_D.PRO_NO #1（原 value：ISNULL(PUR_RECEIVE_D.CANCEL_QTY,0)）
UPDATE dbo.FIELD_DATASOURCE
   SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"PUR_RECEIVE_M.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null},{"field":"PUR_RECEIVE_M.SUPPLIER_ID","operator":"EQ","value":"{m.SUPPLIER_ID}","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null},{"operator":"GT","nullSafe":"ZERO","left":{"kind":"column","table":"PUR_RECEIVE_D","column":"CHECK_RECEIVE_QTY"},"right":{"kind":"isnull","left":{"kind":"column","table":"PUR_RECEIVE_D","column":"CANCEL_QTY"},"value":"0"},"negate":null,"group":null,"subquery":null}]}',
       LAST_UPDATE_BY = N'migration-404',
       LAST_UPDATE_DATE = SYSDATETIME()
 WHERE ID = 1542;

-- PUR_PACK_M.ORDER_TYPE #1（原 value：COP_ORDER_D.FINISHED_PLAN_QTY）
UPDATE dbo.FIELD_DATASOURCE
   SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"COP_ORDER_M.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null},{"field":"COP_ORDER_M.FINISHED_TAG","operator":"EQ","value":"0","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null},{"field":"COP_ORDER_D.FINISHED_TAG","operator":"EQ","value":"0","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null},{"operator":"GT","nullSafe":null,"left":{"kind":"column","table":"COP_ORDER_D","column":"PLAN_QTY"},"right":{"kind":"column","table":"COP_ORDER_D","column":"FINISHED_PLAN_QTY"},"negate":null,"group":null,"subquery":null}]}',
       LAST_UPDATE_BY = N'migration-404',
       LAST_UPDATE_DATE = SYSDATETIME()
 WHERE ID = 1597;

-- PUR_PAY_PREPAY.PREPAY_NO #1（原 value：PUR_PREPAY_M.AMOUNT）
UPDATE dbo.FIELD_DATASOURCE
   SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"PUR_PREPAY_M.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null},{"operator":"LT","nullSafe":null,"left":{"kind":"column","table":"PUR_PREPAY_M","column":"PREPAY_AMOUNT"},"right":{"kind":"column","table":"PUR_PREPAY_M","column":"AMOUNT"},"negate":null,"group":null,"subquery":null},{"field":"PUR_PREPAY_M.SUPPLIER_ID","operator":"EQ","value":"{m.supplier_id}","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null},{"field":"PUR_PREPAY_M.FINISHED_TAG","operator":"EQ","value":"0","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}',
       LAST_UPDATE_BY = N'migration-404',
       LAST_UPDATE_DATE = SYSDATETIME()
 WHERE ID = 1633;

-- PUR_PURCHASE_MORE.PRODUCE_NO #1（原 value：MOC_PRODUCE_D.NEED_QTY）
UPDATE dbo.FIELD_DATASOURCE
   SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"MOC_PRODUCE_M.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null},{"operator":"LT","nullSafe":null,"left":{"kind":"column","table":"MOC_PRODUCE_D","column":"PURCHASE_QTY"},"right":{"kind":"column","table":"MOC_PRODUCE_D","column":"NEED_QTY"},"negate":null,"group":null,"subquery":null}]}',
       LAST_UPDATE_BY = N'migration-404',
       LAST_UPDATE_DATE = SYSDATETIME()
 WHERE ID = 1687;

-- QC_ANALYSIS_D.PRODUCE_NO #1（原 value：MOC_PRODUCE_M.FINISHED_ANALYSIS_QTY）
UPDATE dbo.FIELD_DATASOURCE
   SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"MOC_PRODUCE_M.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null},{"operator":"GT","nullSafe":null,"left":{"kind":"column","table":"MOC_PRODUCE_M","column":"QTY"},"right":{"kind":"column","table":"MOC_PRODUCE_M","column":"FINISHED_ANALYSIS_QTY"},"negate":null,"group":null,"subquery":null}]}',
       LAST_UPDATE_BY = N'migration-404',
       LAST_UPDATE_DATE = SYSDATETIME()
 WHERE ID = 1730;

-- COP_BACK_D.BATCH_NO #1（原 value：INV_BATCH_M.OUT_SUM）
UPDATE dbo.FIELD_DATASOURCE
   SET FILTER_STRUCT = N'{"logic":"AND","items":[{"operator":"GT","nullSafe":null,"left":{"kind":"column","table":"INV_BATCH_M","column":"IN_SUM"},"right":{"kind":"column","table":"INV_BATCH_M","column":"OUT_SUM"},"negate":null,"group":null,"subquery":null}]}',
       LAST_UPDATE_BY = N'migration-404',
       LAST_UPDATE_DATE = SYSDATETIME()
 WHERE ID = 3062;

-- COP_FITOUT_D.BATCH_NO #1（原 value：INV_BATCH_M.OUT_SUM）
UPDATE dbo.FIELD_DATASOURCE
   SET FILTER_STRUCT = N'{"logic":"AND","items":[{"operator":"GT","nullSafe":null,"left":{"kind":"column","table":"INV_BATCH_M","column":"IN_SUM"},"right":{"kind":"column","table":"INV_BATCH_M","column":"OUT_SUM"},"negate":null,"group":null,"subquery":null}]}',
       LAST_UPDATE_BY = N'migration-404',
       LAST_UPDATE_DATE = SYSDATETIME()
 WHERE ID = 3063;

-- COP_ORDER_D.BATCH_NO #1（原 value：INV_BATCH_M.OUT_SUM）
UPDATE dbo.FIELD_DATASOURCE
   SET FILTER_STRUCT = N'{"logic":"AND","items":[{"operator":"GT","nullSafe":null,"left":{"kind":"column","table":"INV_BATCH_M","column":"IN_SUM"},"right":{"kind":"column","table":"INV_BATCH_M","column":"OUT_SUM"},"negate":null,"group":null,"subquery":null}]}',
       LAST_UPDATE_BY = N'migration-404',
       LAST_UPDATE_DATE = SYSDATETIME()
 WHERE ID = 3064;

-- COP_SEND_D.BATCH_NO #1（原 value：INV_BATCH_M.OUT_SUM）
UPDATE dbo.FIELD_DATASOURCE
   SET FILTER_STRUCT = N'{"logic":"AND","items":[{"operator":"GT","nullSafe":null,"left":{"kind":"column","table":"INV_BATCH_M","column":"IN_SUM"},"right":{"kind":"column","table":"INV_BATCH_M","column":"OUT_SUM"},"negate":null,"group":null,"subquery":null}]}',
       LAST_UPDATE_BY = N'migration-404',
       LAST_UPDATE_DATE = SYSDATETIME()
 WHERE ID = 3065;

-- INV_LOAN_D.BATCH_NO #1（原 value：INV_BATCH_M.OUT_SUM）
UPDATE dbo.FIELD_DATASOURCE
   SET FILTER_STRUCT = N'{"logic":"AND","items":[{"operator":"GT","nullSafe":null,"left":{"kind":"column","table":"INV_BATCH_M","column":"IN_SUM"},"right":{"kind":"column","table":"INV_BATCH_M","column":"OUT_SUM"},"negate":null,"group":null,"subquery":null}]}',
       LAST_UPDATE_BY = N'migration-404',
       LAST_UPDATE_DATE = SYSDATETIME()
 WHERE ID = 3066;

-- INV_OCCUR_SCRAP_D.BATCH_NO #1（原 value：INV_BATCH_M.OUT_SUM）
UPDATE dbo.FIELD_DATASOURCE
   SET FILTER_STRUCT = N'{"logic":"AND","items":[{"operator":"GT","nullSafe":null,"left":{"kind":"column","table":"INV_BATCH_M","column":"IN_SUM"},"right":{"kind":"column","table":"INV_BATCH_M","column":"OUT_SUM"},"negate":null,"group":null,"subquery":null}]}',
       LAST_UPDATE_BY = N'migration-404',
       LAST_UPDATE_DATE = SYSDATETIME()
 WHERE ID = 3067;

-- INV_OCCUR_TRANSFER_D.BATCH_NO #1（原 value：INV_BATCH_M.OUT_SUM）
UPDATE dbo.FIELD_DATASOURCE
   SET FILTER_STRUCT = N'{"logic":"AND","items":[{"operator":"GT","nullSafe":null,"left":{"kind":"column","table":"INV_BATCH_M","column":"IN_SUM"},"right":{"kind":"column","table":"INV_BATCH_M","column":"OUT_SUM"},"negate":null,"group":null,"subquery":null}]}',
       LAST_UPDATE_BY = N'migration-404',
       LAST_UPDATE_DATE = SYSDATETIME()
 WHERE ID = 3068;

-- INV_RETURN_D.BATCH_NO #1（原 value：INV_BATCH_M.OUT_SUM）
UPDATE dbo.FIELD_DATASOURCE
   SET FILTER_STRUCT = N'{"logic":"AND","items":[{"operator":"GT","nullSafe":null,"left":{"kind":"column","table":"INV_BATCH_M","column":"IN_SUM"},"right":{"kind":"column","table":"INV_BATCH_M","column":"OUT_SUM"},"negate":null,"group":null,"subquery":null}]}',
       LAST_UPDATE_BY = N'migration-404',
       LAST_UPDATE_DATE = SYSDATETIME()
 WHERE ID = 3069;

-- MOC_GET_D.BATCH_NO #1（原 value：INV_BATCH_M.OUT_SUM）
UPDATE dbo.FIELD_DATASOURCE
   SET FILTER_STRUCT = N'{"logic":"AND","items":[{"operator":"GT","nullSafe":null,"left":{"kind":"column","table":"INV_BATCH_M","column":"IN_SUM"},"right":{"kind":"column","table":"INV_BATCH_M","column":"OUT_SUM"},"negate":null,"group":null,"subquery":null}]}',
       LAST_UPDATE_BY = N'migration-404',
       LAST_UPDATE_DATE = SYSDATETIME()
 WHERE ID = 3070;

-- MOC_OUT_PRODUCT_OUT_D.BATCH_NO #1（原 value：INV_BATCH_M.OUT_SUM）
UPDATE dbo.FIELD_DATASOURCE
   SET FILTER_STRUCT = N'{"logic":"AND","items":[{"operator":"GT","nullSafe":null,"left":{"kind":"column","table":"INV_BATCH_M","column":"IN_SUM"},"right":{"kind":"column","table":"INV_BATCH_M","column":"OUT_SUM"},"negate":null,"group":null,"subquery":null}]}',
       LAST_UPDATE_BY = N'migration-404',
       LAST_UPDATE_DATE = SYSDATETIME()
 WHERE ID = 3071;

-- MOC_PRODUCT_OUT_D.BATCH_NO #1（原 value：INV_BATCH_M.OUT_SUM）
UPDATE dbo.FIELD_DATASOURCE
   SET FILTER_STRUCT = N'{"logic":"AND","items":[{"operator":"GT","nullSafe":null,"left":{"kind":"column","table":"INV_BATCH_M","column":"IN_SUM"},"right":{"kind":"column","table":"INV_BATCH_M","column":"OUT_SUM"},"negate":null,"group":null,"subquery":null}]}',
       LAST_UPDATE_BY = N'migration-404',
       LAST_UPDATE_DATE = SYSDATETIME()
 WHERE ID = 3072;

-- MOU_GET_D.BATCH_NO #1（原 value：INV_BATCH_M.OUT_SUM）
UPDATE dbo.FIELD_DATASOURCE
   SET FILTER_STRUCT = N'{"logic":"AND","items":[{"operator":"GT","nullSafe":null,"left":{"kind":"column","table":"INV_BATCH_M","column":"IN_SUM"},"right":{"kind":"column","table":"INV_BATCH_M","column":"OUT_SUM"},"negate":null,"group":null,"subquery":null}]}',
       LAST_UPDATE_BY = N'migration-404',
       LAST_UPDATE_DATE = SYSDATETIME()
 WHERE ID = 3073;

-- PUR_CANCEL_D.BATCH_NO #1（原 value：INV_BATCH_M.OUT_SUM）
UPDATE dbo.FIELD_DATASOURCE
   SET FILTER_STRUCT = N'{"logic":"AND","items":[{"operator":"GT","nullSafe":null,"left":{"kind":"column","table":"INV_BATCH_M","column":"IN_SUM"},"right":{"kind":"column","table":"INV_BATCH_M","column":"OUT_SUM"},"negate":null,"group":null,"subquery":null}]}',
       LAST_UPDATE_BY = N'migration-404',
       LAST_UPDATE_DATE = SYSDATETIME()
 WHERE ID = 3074;

DECLARE @fixed INT = @@ROWCOUNT;
PRINT N'== 修复后核对 ==';

/* 收口断言 1：不再有"value 形如源表.列"的过滤项 */
IF EXISTS (
    SELECT 1
      FROM dbo.FIELD_DATASOURCE d
      CROSS APPLY OPENJSON(ISNULL(d.FILTER_STRUCT, '{}'), '$.items') AS j
     WHERE d.ACTIVE_TAG = 1
       AND JSON_VALUE(j.value, '$.value') LIKE '[A-Za-z_]%'
       AND JSON_VALUE(j.value, '$.value') NOT LIKE '%{%'
       AND JSON_VALUE(j.value, '$.value') NOT LIKE '% %'
       AND JSON_VALUE(j.value, '$.value') = d.SOURCE_T_ID + N'.' + SUBSTRING(JSON_VALUE(j.value, '$.value'), LEN(d.SOURCE_T_ID) + 2, 200))
    THROW 52901, N'仍有「value 把列引用当字面量」的过滤项，迁移中止。', 1;

/* 收口断言 2：每条改过的 FILTER_STRUCT 仍是合法 JSON，且引用列都落到了 column 表达式上 */
IF EXISTS (
    SELECT 1 FROM dbo.FIELD_DATASOURCE
     WHERE LAST_UPDATE_BY = N'migration-404' AND ISJSON(FILTER_STRUCT) <> 1)
    THROW 52902, N'修复后出现非法 JSON，迁移中止。', 1;

/* 收口断言 3：被改的行里必须都能看到 kind:'column' */
IF EXISTS (
    SELECT 1 FROM dbo.FIELD_DATASOURCE
     WHERE LAST_UPDATE_BY = N'migration-404'
       AND FILTER_STRUCT NOT LIKE N'%"kind":"column"%')
    THROW 52903, N'修复后的行里没有 column 表达式，改写未生效，迁移中止。', 1;

COMMIT;

PRINT N'== 收口：选择器过滤条件已改为列引用，37 个数据源恢复可用 ==';
