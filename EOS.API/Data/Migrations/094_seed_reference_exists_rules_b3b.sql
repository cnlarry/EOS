-- ============================================================================
-- EOS.ERP migration 095: 保存侧引用存在性校验入目录（收付/采购/关务/模具对照族）
-- ----------------------------------------------------------------------------
-- 依《D9 校验侧目录化迁移计划》B3b：把各领域规则里的"引用资料是否存在"与"引用的单据
-- 其客户/厂商是否与本单一致"入 MODULE_VALIDATION_RULE（key = reference-exists，STAGE = SAVE）。
--
-- 判据与文案逐条对照 C#（CopDomainRules / PurDomainRules / CusDomainRules / MouDomainRules）：
--   · 反向一致性断言用 mismatch（EXISTS + <>）：旧实现是"存在一行且两列不等"，
--     若写成"NOT EXISTS 两列相等"，引用本身缺失时会被误报成"客户不符"。
--   · 明细级断言的来源行由明细表携带的主表主键列限定在当前单据内。
--   · 缺失行按 lineField 回填 {ROWS}；pur-apply 旧实现无条数上限，取模板上限 100。
--
-- 1606 采购单在原目录里只有厂商一条（迁移 083），现按 C# 判据补回"申购单/产品"两条为
-- 独立规则（SEQ=2），带 ISNULL(类型)<>'' 的空值放行与单据范围限定——这正是 083 当时
-- 判为"与真实数据不符"的原因，历史 BLD/BMQG 型申购单在库内已无对应主档。
--
-- 幂等：按 模块+SAVE+顺序 覆盖参数、文案与启用位。
-- ============================================================================

SET NOCOUNT ON;

DECLARE @GUARD_MESSAGE NVARCHAR(400) = N'本脚本只能在 EOS.ERP 数据库内执行，当前库为 ' + DB_NAME() + N'。';
IF DB_NAME() <> N'EOS.ERP'
    THROW 50000, @GUARD_MESSAGE, 1;

PRINT N'== 播种引用存在性校验（收付/采购/关务/模具对照） ==';

DECLARE @NL NVARCHAR(2) = NCHAR(13) + NCHAR(10);
DECLARE @NLJ NVARCHAR(4) = N'\r\n';

-- 主档级：客户/厂商"存在且未停止交易"
DECLARE @CLIENT NVARCHAR(MAX) =
    N'{"refTable":"CLIENT","refKey":{"scope":"MASTER","field":"CLIENT_ID"},'
    + N'"activeTag":{"field":"BUSINESS_TAG","expect":0},"message":"客户编号不存在或已停止交易。"}';
DECLARE @CLIENT_SP NVARCHAR(MAX) =
    N'{"refTable":"CLIENT","refKey":{"scope":"MASTER","field":"CLIENT_ID"},'
    + N'"activeTag":{"field":"BUSINESS_TAG","expect":0},"message":"客户编号不存在或已停止交易。 "}';
DECLARE @SUPPLIER NVARCHAR(MAX) =
    N'{"refTable":"SUPPLIER","refKey":{"scope":"MASTER","field":"SUPPLIER_ID"},'
    + N'"activeTag":{"field":"BUSINESS_TAG","expect":0},"message":"厂商编号不存在或已停止交易。"}';

-- 明细级：产品
DECLARE @PRODUCT NVARCHAR(MAX) =
    N'{"refTable":"PRODUCT","refKey":{"scope":"DETAIL","field":"PRO_NO"},'
    + N'"lineField":"SERIAL_NO","message":"以下序号项产品编号不存在 ' + @NLJ + N'{ROWS}"}';
DECLARE @PRODUCT_WIDE NVARCHAR(MAX) =
    N'{"refTable":"PRODUCT","refKey":{"scope":"DETAIL","field":"PRO_NO"},'
    + N'"lineField":"SERIAL_NO","maxRows":100,"message":"以下序号项产品编号不存在 ' + @NLJ + N'{ROWS}"}';

-- 明细级：订单（存在 / 行匹配）
DECLARE @ORDER_EXISTS NVARCHAR(MAX) =
    N'{"refTable":"COP_ORDER_M","join":['
    + N'{"target":"ORDER_TYPE","source":{"scope":"DETAIL","field":"ORDER_TYPE"}},'
    + N'{"target":"ORDER_NO","source":{"scope":"DETAIL","field":"ORDER_NO"}}],'
    + N'"allowEmpty":[{"scope":"DETAIL","field":"ORDER_TYPE"}],'
    + N'"lineField":"SERIAL_NO","message":"以下序号项订单不存在 ' + @NLJ + N'{ROWS}"}';
DECLARE @ORDER_LINE NVARCHAR(MAX) =
    N'{"refTable":"COP_ORDER_D","join":['
    + N'{"target":"ORDER_TYPE","source":{"scope":"DETAIL","field":"ORDER_TYPE"}},'
    + N'{"target":"ORDER_NO","source":{"scope":"DETAIL","field":"ORDER_NO"}},'
    + N'{"target":"SERIAL_NO","source":{"scope":"DETAIL","field":"ORDER_SERIAL_NO"}},'
    + N'{"target":"PRO_NO","source":{"scope":"DETAIL","field":"PRO_NO"}}],'
    + N'"allowEmpty":[{"scope":"DETAIL","field":"ORDER_TYPE"}],'
    + N'"lineField":"SERIAL_NO","message":"以下序号项订单序号与产品编号不相符 ' + @NLJ + N'{ROWS}"}';

-- 明细级：采购单（存在 / 行匹配）
DECLARE @PURCHASE_EXISTS NVARCHAR(MAX) =
    N'{"refTable":"PUR_PURCHASE_M","join":['
    + N'{"target":"PURCHASE_TYPE","source":{"scope":"DETAIL","field":"PURCHASE_TYPE"}},'
    + N'{"target":"PURCHASE_NO","source":{"scope":"DETAIL","field":"PURCHASE_NO"}}],'
    + N'"allowEmpty":[{"scope":"DETAIL","field":"PURCHASE_TYPE"}],'
    + N'"lineField":"SERIAL_NO","message":"以下序号项采购订单不存在 ' + @NLJ + N'{ROWS}"}';
DECLARE @PURCHASE_LINE NVARCHAR(MAX) =
    N'{"refTable":"PUR_PURCHASE_D","join":['
    + N'{"target":"PURCHASE_TYPE","source":{"scope":"DETAIL","field":"PURCHASE_TYPE"}},'
    + N'{"target":"PURCHASE_NO","source":{"scope":"DETAIL","field":"PURCHASE_NO"}},'
    + N'{"target":"SERIAL_NO","source":{"scope":"DETAIL","field":"PURCHASE_SERIAL_NO"}},'
    + N'{"target":"PRO_NO","source":{"scope":"DETAIL","field":"PRO_NO"}}],'
    + N'"allowEmpty":[{"scope":"DETAIL","field":"PURCHASE_TYPE"}],'
    + N'"lineField":"SERIAL_NO","message":"以下序号项采购订单序号与产品编号不相符 ' + @NLJ + N'{ROWS}"}';

-- 反向一致性断言：引用单据的客户/厂商必须与本单一致
DECLARE @ORDER_CLIENT_MISMATCH NVARCHAR(MAX) =
    N'{"refTable":"COP_ORDER_M","join":['
    + N'{"target":"ORDER_TYPE","source":{"scope":"DETAIL","field":"ORDER_TYPE"}},'
    + N'{"target":"ORDER_NO","source":{"scope":"DETAIL","field":"ORDER_NO"}}],'
    + N'"mismatch":{"target":"CLIENT_ID","source":{"scope":"MASTER","field":"CLIENT_ID"}},'
    + N'"lineField":"SERIAL_NO","message":"';
DECLARE @PURCHASE_SUPPLIER_MISMATCH NVARCHAR(MAX) =
    N'{"refTable":"PUR_PURCHASE_M","join":['
    + N'{"target":"PURCHASE_TYPE","source":{"scope":"DETAIL","field":"PURCHASE_TYPE"}},'
    + N'{"target":"PURCHASE_NO","source":{"scope":"DETAIL","field":"PURCHASE_NO"}}],'
    + N'"mismatch":{"target":"SUPPLIER_ID","source":{"scope":"MASTER","field":"SUPPLIER_ID"}},'
    + N'"lineField":"SERIAL_NO","message":"';

DECLARE @RULES TABLE (
    MODULE_ID INT, SEQ INT, VALIDATION_KEY NVARCHAR(50),
    PARAM_STRUCT NVARCHAR(MAX), MESSAGE NVARCHAR(500), REMARK NVARCHAR(500), SOURCE_REF NVARCHAR(100),
    PRIMARY KEY (MODULE_ID, SEQ));

-- 收付：退货/备货/返仓/退料/送货 —— 客户一致性 + 订单/行/产品
INSERT INTO @RULES (MODULE_ID, SEQ, VALIDATION_KEY, PARAM_STRUCT, MESSAGE, REMARK, SOURCE_REF) VALUES
(1407, 1, N'reference-exists',
 N'{"checks":[' + @ORDER_CLIENT_MISMATCH + N'以下序号项退货单与订单客户不符 ' + @NLJ + N'{ROWS}"},'
 + @ORDER_EXISTS + N',' + @ORDER_LINE + N',' + @PRODUCT + N']}',
 NULL, N'退货单订单客户一致性/订单/订单行/产品引用校验', N'cop-return'),
(1409, 1, N'reference-exists',
 N'{"checks":[' + @ORDER_CLIENT_MISMATCH + N'以下序号项退货单与订单客户不符 ' + @NLJ + N'{ROWS}"},'
 + @ORDER_EXISTS + N',' + @ORDER_LINE + N',' + @PRODUCT + N']}',
 NULL, N'退货单订单客户一致性/订单/订单行/产品引用校验', N'cop-return'),
(1411, 1, N'reference-exists',
 N'{"checks":[' + @ORDER_CLIENT_MISMATCH + N'以下序号项送货单与订单客户不符 ' + @NLJ + N'{ROWS}"},'
 + @ORDER_EXISTS + N',' + @ORDER_LINE + N',' + @PRODUCT + N']}',
 NULL, N'备货单订单客户一致性/订单/订单行/产品引用校验', N'cop-fitout'),
(1412, 1, N'reference-exists',
 N'{"checks":[' + @ORDER_CLIENT_MISMATCH + N'以下序号项与订单客户不符 ' + @NLJ + N'{ROWS}"},'
 + @ORDER_EXISTS + N',' + @ORDER_LINE + N',' + @PRODUCT + N']}',
 NULL, N'备货返仓单订单客户一致性/订单/订单行/产品引用校验', N'cop-fitin'),
(1423, 1, N'reference-exists',
 N'{"checks":[' + @ORDER_CLIENT_MISMATCH + N'以下序号项退料单与订单客户不符 ' + @NLJ + N'{ROWS}"},'
 + @ORDER_EXISTS + N',' + @ORDER_LINE + N',' + @PRODUCT + N']}',
 NULL, N'退料单订单客户一致性/订单/订单行/产品引用校验', N'cop-back'),
(1406, 1, N'reference-exists',
 N'{"checks":[' + @ORDER_CLIENT_MISMATCH + N'以下序号项送货单与订单客户不符 ' + @NLJ + N'{ROWS}"},'
 + @ORDER_EXISTS + N',' + @ORDER_LINE + N',' + @PRODUCT + N']}',
 NULL, N'送货单订单客户一致性/订单/订单行/产品引用校验（库别断言受 SYSSS 开关门控，仍留 C#）', N'cop-send');

-- 收付：对帐/预收/预付 —— 客户存在
INSERT INTO @RULES (MODULE_ID, SEQ, VALIDATION_KEY, PARAM_STRUCT, MESSAGE, REMARK, SOURCE_REF) VALUES
(170101, 1, N'reference-exists', N'{"checks":[' + @CLIENT + N']}', N'客户编号不存在或已停止交易。', N'应收对帐单客户引用校验', N'cop-account'),
(170103, 1, N'reference-exists', N'{"checks":[' + @CLIENT + N']}', N'客户编号不存在或已停止交易。', N'预收帐款单客户引用校验', N'cop-prepay'),
(170203, 1, N'reference-exists', N'{"checks":[' + @SUPPLIER + N']}', N'厂商编号不存在或已停止交易。', N'预付帐款单厂商引用校验', N'pur-prepay'),
(1610, 1, N'reference-exists', N'{"checks":[' + @SUPPLIER + N']}', N'厂商编号不存在或已停止交易。', N'厂商回执单厂商引用校验', N'pur-callback');

-- 收付：对帐的送/退货单"任一存在"
INSERT INTO @RULES (MODULE_ID, SEQ, VALIDATION_KEY, PARAM_STRUCT, MESSAGE, REMARK, SOURCE_REF) VALUES
(170101, 2, N'reference-exists',
 N'{"checks":[{"targets":['
 + N'{"refTable":"COP_SEND_D","join":['
 + N'{"target":"SEND_TYPE","source":{"scope":"DETAIL","field":"S_R_TYPE"}},'
 + N'{"target":"SEND_NO","source":{"scope":"DETAIL","field":"S_R_NO"}},'
 + N'{"target":"SERIAL_NO","source":{"scope":"DETAIL","field":"S_R_SERIAL_NO"}}]},'
 + N'{"refTable":"COP_RETURN_D","join":['
 + N'{"target":"RETURN_TYPE","source":{"scope":"DETAIL","field":"S_R_TYPE"}},'
 + N'{"target":"RETURN_NO","source":{"scope":"DETAIL","field":"S_R_NO"}},'
 + N'{"target":"SERIAL_NO","source":{"scope":"DETAIL","field":"S_R_SERIAL_NO"}}]}],'
 + N'"lineField":"SERIAL_NO","message":"以下序号项送/退货单不存在 ' + @NLJ + N'{ROWS}"}]}',
 NULL, N'应收对帐单送/退货单引用校验', N'cop-account'),
(3014, 1, N'reference-exists', N'{"checks":[' + @CLIENT_SP + N']}', N'客户编号不存在或已停止交易。 ', N'海关对帐单客户引用校验', N'cus-account'),
(3014, 2, N'reference-exists',
 N'{"checks":[{"targets":['
 + N'{"refTable":"COP_SEND_D","join":['
 + N'{"target":"SEND_TYPE","source":{"scope":"DETAIL","field":"S_R_TYPE"}},'
 + N'{"target":"SEND_NO","source":{"scope":"DETAIL","field":"S_R_NO"}},'
 + N'{"target":"SERIAL_NO","source":{"scope":"DETAIL","field":"S_R_SERIAL_NO"}}]},'
 + N'{"refTable":"COP_RETURN_D","join":['
 + N'{"target":"RETURN_TYPE","source":{"scope":"DETAIL","field":"S_R_TYPE"}},'
 + N'{"target":"RETURN_NO","source":{"scope":"DETAIL","field":"S_R_NO"}},'
 + N'{"target":"SERIAL_NO","source":{"scope":"DETAIL","field":"S_R_SERIAL_NO"}}]}],'
 + N'"lineField":"SERIAL_NO","message":"以下序号项送/退货单不存在 ' + @NLJ + N'{ROWS}"}]}',
 NULL, N'海关对帐单送/退货单引用校验', N'cus-account');

-- 客户订单：客户存在 + 报价单客户一致性 + 报价单/行/产品
INSERT INTO @RULES (MODULE_ID, SEQ, VALIDATION_KEY, PARAM_STRUCT, MESSAGE, REMARK, SOURCE_REF) VALUES
(1405, 1, N'reference-exists',
 N'{"checks":[' + @CLIENT + N','
 + N'{"refTable":"COP_QUOTE_M","join":['
 + N'{"target":"QUOTE_TYPE","source":{"scope":"DETAIL","field":"QUOTE_TYPE"}},'
 + N'{"target":"QUOTE_NO","source":{"scope":"DETAIL","field":"QUOTE_NO"}}],'
 + N'"mismatch":{"target":"CLIENT_ID","source":{"scope":"MASTER","field":"CLIENT_ID"}},'
 + N'"lineField":"SERIAL_NO","message":"以下序号项报价单与订单客户不符 ' + @NLJ + N'{ROWS}"},'
 + N'{"refTable":"COP_QUOTE_M","join":['
 + N'{"target":"QUOTE_TYPE","source":{"scope":"DETAIL","field":"QUOTE_TYPE"}},'
 + N'{"target":"QUOTE_NO","source":{"scope":"DETAIL","field":"QUOTE_NO"}}],'
 + N'"allowEmpty":[{"scope":"DETAIL","field":"QUOTE_TYPE"}],'
 + N'"lineField":"SERIAL_NO","message":"以下序号项报价单不存在 ' + @NLJ + N'{ROWS}"},'
 + N'{"refTable":"COP_QUOTE_D","join":['
 + N'{"target":"QUOTE_TYPE","source":{"scope":"DETAIL","field":"QUOTE_TYPE"}},'
 + N'{"target":"QUOTE_NO","source":{"scope":"DETAIL","field":"QUOTE_NO"}},'
 + N'{"target":"SERIAL_NO","source":{"scope":"DETAIL","field":"QUOTE_SERIAL_NO"}},'
 + N'{"target":"PRO_NO","source":{"scope":"DETAIL","field":"PRO_NO"}}],'
 + N'"allowEmpty":[{"scope":"DETAIL","field":"QUOTE_TYPE"}],'
 + N'"lineField":"SERIAL_NO","message":"以下序号项报价单序号与产品编号不相符 ' + @NLJ + N'{ROWS}"},'
 + @PRODUCT + N']}',
 NULL, N'客户订单客户/报价单/报价行/产品引用校验', N'cop-order');

-- 客户报价单：客户存在 + 询价单客户一致性 + 询价单
INSERT INTO @RULES (MODULE_ID, SEQ, VALIDATION_KEY, PARAM_STRUCT, MESSAGE, REMARK, SOURCE_REF) VALUES
(1404, 1, N'reference-exists',
 N'{"checks":[' + @CLIENT + N','
 + N'{"refTable":"COP_CHAFFER_M","join":['
 + N'{"target":"CHAFFER_TYPE","source":{"scope":"DETAIL","field":"CHAFFER_TYPE"}},'
 + N'{"target":"CHAFFER_NO","source":{"scope":"DETAIL","field":"CHAFFER_NO"}}],'
 + N'"mismatch":{"target":"CLIENT_ID","source":{"scope":"MASTER","field":"CLIENT_ID"}},'
 + N'"lineField":"SERIAL_NO","message":"以下序号项询价单与报价单客户不符 ' + @NLJ + N'{ROWS}"},'
 + N'{"refTable":"COP_CHAFFER_M","join":['
 + N'{"target":"CHAFFER_TYPE","source":{"scope":"DETAIL","field":"CHAFFER_TYPE"}},'
 + N'{"target":"CHAFFER_NO","source":{"scope":"DETAIL","field":"CHAFFER_NO"}}],'
 + N'"allowEmpty":[{"scope":"DETAIL","field":"CHAFFER_TYPE"}],'
 + N'"lineField":"SERIAL_NO","message":"以下序号项询价单不存在 ' + @NLJ + N'{ROWS}"}]}',
 NULL, N'客户报价单客户/询价单引用校验', N'cop-quote');

-- 采购收料：厂商一致性 + 采购单/行/产品
INSERT INTO @RULES (MODULE_ID, SEQ, VALIDATION_KEY, PARAM_STRUCT, MESSAGE, REMARK, SOURCE_REF) VALUES
(1607, 1, N'reference-exists',
 N'{"checks":[' + @PURCHASE_SUPPLIER_MISMATCH + N'以下序号项收料单与采购单厂商不符 ' + @NLJ + N'{ROWS}"},'
 + @PURCHASE_EXISTS + N',' + @PURCHASE_LINE + N',' + @PRODUCT + N']}',
 NULL, N'收料单厂商一致性/采购单/采购行/产品引用校验', N'pur-receive'),
(1608, 1, N'reference-exists',
 N'{"checks":[' + @PURCHASE_SUPPLIER_MISMATCH + N'以下序号项收料单与采购单厂商不符 ' + @NLJ + N'{ROWS}"},'
 + @PURCHASE_EXISTS + N',' + @PURCHASE_LINE + N','
 + N'{"refTable":"PUR_RECEIVE_D","join":['
 + N'{"target":"RECEIVE_TYPE","source":{"scope":"DETAIL","field":"RECEIVE_TYPE"}},'
 + N'{"target":"RECEIVE_NO","source":{"scope":"DETAIL","field":"RECEIVE_NO"}},'
 + N'{"target":"SERIAL_NO","source":{"scope":"DETAIL","field":"RECEIVE_SERIAL_NO"}},'
 + N'{"target":"PRO_NO","source":{"scope":"DETAIL","field":"PRO_NO"}}],'
 + N'"allowEmpty":[{"scope":"DETAIL","field":"RECEIVE_TYPE"}],'
 + N'"lineField":"SERIAL_NO","message":"以下序号项送货单序号与产品编号不相符 ' + @NLJ + N'{ROWS}"},'
 + @PRODUCT + N']}',
 NULL, N'采购退料单厂商一致性/采购单/采购行/收料行/产品引用校验', N'pur-cancel'),
(1612, 1, N'reference-exists',
 N'{"checks":[' + @PURCHASE_SUPPLIER_MISMATCH + N'以下序号项收料单与采购单厂商不符 ' + @NLJ + N'{ROWS}"},'
 + @PURCHASE_EXISTS + N',' + @PURCHASE_LINE + N','
 + N'{"refTable":"PUR_RECEIVE_D","join":['
 + N'{"target":"RECEIVE_TYPE","source":{"scope":"DETAIL","field":"RECEIVE_TYPE"}},'
 + N'{"target":"RECEIVE_NO","source":{"scope":"DETAIL","field":"RECEIVE_NO"}},'
 + N'{"target":"SERIAL_NO","source":{"scope":"DETAIL","field":"RECEIVE_SERIAL_NO"}},'
 + N'{"target":"PRO_NO","source":{"scope":"DETAIL","field":"PRO_NO"}}],'
 + N'"allowEmpty":[{"scope":"DETAIL","field":"RECEIVE_TYPE"}],'
 + N'"lineField":"SERIAL_NO","message":"以下序号项送货单序号与产品编号不相符 ' + @NLJ + N'{ROWS}"},'
 + @PRODUCT + N']}',
 NULL, N'采购退料单厂商一致性/采购单/采购行/收料行/产品引用校验', N'pur-cancel');

-- 厂商报价单：厂商存在 + 询价单厂商一致性 + 询价单/行
INSERT INTO @RULES (MODULE_ID, SEQ, VALIDATION_KEY, PARAM_STRUCT, MESSAGE, REMARK, SOURCE_REF) VALUES
(1604, 1, N'reference-exists',
 N'{"checks":[' + @SUPPLIER + N','
 + N'{"refTable":"PUR_CHAFFER_M","join":['
 + N'{"target":"CHAFFER_TYPE","source":{"scope":"DETAIL","field":"CHAFFER_TYPE"}},'
 + N'{"target":"CHAFFER_NO","source":{"scope":"DETAIL","field":"CHAFFER_NO"}}],'
 + N'"mismatch":{"target":"SUPPLIER_ID","source":{"scope":"MASTER","field":"SUPPLIER_ID"}},'
 + N'"lineField":"SERIAL_NO","message":"以下序号项询价单与报价单厂商不符 ' + @NLJ + N'{ROWS}"},'
 + N'{"refTable":"PUR_CHAFFER_M","join":['
 + N'{"target":"CHAFFER_TYPE","source":{"scope":"DETAIL","field":"CHAFFER_TYPE"}},'
 + N'{"target":"CHAFFER_NO","source":{"scope":"DETAIL","field":"CHAFFER_NO"}}],'
 + N'"allowEmpty":[{"scope":"DETAIL","field":"CHAFFER_TYPE"}],'
 + N'"lineField":"SERIAL_NO","message":"以下序号项询价单不存在 ' + @NLJ + N'{ROWS}"},'
 + N'{"refTable":"PUR_CHAFFER_D","join":['
 + N'{"target":"CHAFFER_TYPE","source":{"scope":"DETAIL","field":"CHAFFER_TYPE"}},'
 + N'{"target":"CHAFFER_NO","source":{"scope":"DETAIL","field":"CHAFFER_NO"}},'
 + N'{"target":"SERIAL_NO","source":{"scope":"DETAIL","field":"CHAFFER_SERIAL_NO"}},'
 + N'{"target":"PRO_NO","source":{"scope":"DETAIL","field":"PRO_NO"}}],'
 + N'"allowEmpty":[{"scope":"DETAIL","field":"CHAFFER_TYPE"}],'
 + N'"lineField":"SERIAL_NO","message":"以下序号项询价单序号与产品编号不相符 ' + @NLJ + N'{ROWS}"}]}',
 NULL, N'厂商报价单厂商/询价单/询价行引用校验', N'pur-quote');

-- 申购单：仅产品（旧实现无条数上限）
INSERT INTO @RULES (MODULE_ID, SEQ, VALIDATION_KEY, PARAM_STRUCT, MESSAGE, REMARK, SOURCE_REF) VALUES
(1615, 1, N'reference-exists', N'{"checks":[' + @PRODUCT_WIDE + N']}', N'以下序号项产品编号不存在 ' + @NLJ + N'{ROWS}', N'成品请购单产品引用校验', N'pur-apply'),
(1616, 1, N'reference-exists', N'{"checks":[' + @PRODUCT_WIDE + N']}', N'以下序号项产品编号不存在 ' + @NLJ + N'{ROWS}', N'备料单产品引用校验', N'pur-apply');

-- 采购单（1606）：SEQ=1 厂商已在迁移 083 落地，这里补 SEQ=2 的申购单/行/产品
INSERT INTO @RULES (MODULE_ID, SEQ, VALIDATION_KEY, PARAM_STRUCT, MESSAGE, REMARK, SOURCE_REF) VALUES
(1606, 2, N'reference-exists',
 N'{"checks":['
 + N'{"refTable":"PUR_APPLY_M","join":['
 + N'{"target":"APPLY_TYPE","source":{"scope":"DETAIL","field":"APPLY_TYPE"}},'
 + N'{"target":"APPLY_NO","source":{"scope":"DETAIL","field":"APPLY_NO"}}],'
 + N'"allowEmpty":[{"scope":"DETAIL","field":"APPLY_TYPE"}],'
 + N'"lineField":"SERIAL_NO","message":"以下序号项申购单不存在 ' + @NLJ + N'{ROWS}"},'
 + N'{"refTable":"PUR_APPLY_D","join":['
 + N'{"target":"APPLY_TYPE","source":{"scope":"DETAIL","field":"APPLY_TYPE"}},'
 + N'{"target":"APPLY_NO","source":{"scope":"DETAIL","field":"APPLY_NO"}},'
 + N'{"target":"SERIAL_NO","source":{"scope":"DETAIL","field":"APPLY_SERIAL_NO"}},'
 + N'{"target":"PRO_NO","source":{"scope":"DETAIL","field":"PRO_NO"}}],'
 + N'"allowEmpty":[{"scope":"DETAIL","field":"APPLY_TYPE"}],'
 + N'"lineField":"SERIAL_NO","message":"以下序号项申购单序号与产品编号不相符 ' + @NLJ + N'{ROWS}"},'
 + @PRODUCT + N']}',
 NULL, N'采购单申购单/申购行/产品引用校验（空类型放行）', N'pur-purchase');

-- 品检分析：制令单 + 产品
INSERT INTO @RULES (MODULE_ID, SEQ, VALIDATION_KEY, PARAM_STRUCT, MESSAGE, REMARK, SOURCE_REF) VALUES
(3303, 1, N'reference-exists',
 N'{"checks":['
 + N'{"refTable":"MOC_PRODUCE_M","join":['
 + N'{"target":"PRODUCE_TYPE","source":{"scope":"DETAIL","field":"PRODUCE_TYPE"}},'
 + N'{"target":"PRODUCE_NO","source":{"scope":"DETAIL","field":"PRODUCE_NO"}}],'
 + N'"lineField":"SERIAL_NO","message":"以下序号项制令单不存在 ' + @NLJ + N'{ROWS}"},'
 + @PRODUCT + N']}',
 NULL, N'品检分析制令单/产品引用校验', N'qc-analysis');

-- 产品模具对照：产品（主表级）+ 明细模具
INSERT INTO @RULES (MODULE_ID, SEQ, VALIDATION_KEY, PARAM_STRUCT, MESSAGE, REMARK, SOURCE_REF) VALUES
(2911, 1, N'reference-exists',
 N'{"checks":['
 + N'{"refTable":"PRODUCT","refKey":{"scope":"MASTER","field":"PRO_NO"},"message":"产品编号不存在"},'
 + N'{"refTable":"MOU_MOULD","refKey":{"scope":"DETAIL","field":"MOULD_ID"},'
 + N'"lineField":"SERIAL_NO","message":"以下序号项模具编号不存在 ' + @NLJ + N'{ROWS}"}]}',
 NULL, N'产品模具对照产品/模具引用校验', N'mou-pro');

DECLARE @MODULE_ID INT, @SEQ INT, @KEY NVARCHAR(50), @PARAM NVARCHAR(MAX), @MESSAGE NVARCHAR(500), @REMARK NVARCHAR(500), @SOURCE NVARCHAR(100);
DECLARE cur CURSOR LOCAL FAST_FORWARD FOR SELECT MODULE_ID, SEQ, VALIDATION_KEY, PARAM_STRUCT, MESSAGE, REMARK, SOURCE_REF FROM @RULES ORDER BY MODULE_ID, SEQ;
OPEN cur;
FETCH NEXT FROM cur INTO @MODULE_ID, @SEQ, @KEY, @PARAM, @MESSAGE, @REMARK, @SOURCE;
WHILE @@FETCH_STATUS = 0
BEGIN
    IF EXISTS (SELECT 1 FROM dbo.MODULE_VALIDATION_RULE WHERE MODULE_ID = @MODULE_ID AND STAGE = N'SAVE' AND SEQ = @SEQ)
        UPDATE dbo.MODULE_VALIDATION_RULE
        SET VALIDATION_KEY = @KEY, ENABLED = 1, PARAM_STRUCT = @PARAM, MESSAGE = @MESSAGE,
            REMARK = @REMARK, SOURCE_REF = @SOURCE,
            LAST_UPDATE_BY = N'migration-095', LAST_UPDATE_DATE = SYSDATETIME()
        WHERE MODULE_ID = @MODULE_ID AND STAGE = N'SAVE' AND SEQ = @SEQ;
    ELSE
        INSERT INTO dbo.MODULE_VALIDATION_RULE
            (MODULE_ID, STAGE, SEQ, VALIDATION_KEY, ENABLED, PARAM_STRUCT, MESSAGE, REMARK, SOURCE_REF, CREATE_PERSON, CREATE_DATE)
        VALUES
            (@MODULE_ID, N'SAVE', @SEQ, @KEY, 1, @PARAM, @MESSAGE, @REMARK, @SOURCE, N'migration-095', SYSDATETIME());

    FETCH NEXT FROM cur INTO @MODULE_ID, @SEQ, @KEY, @PARAM, @MESSAGE, @REMARK, @SOURCE;
END
CLOSE cur;
DEALLOCATE cur;

PRINT N'-- 引用存在性实例现状（B3b） --';
SELECT CONCAT(N'  module=', MODULE_ID, N' seq=', SEQ, N' checks=', (SELECT COUNT(*) FROM OPENJSON(PARAM_STRUCT, '$.checks')),
              N' src=', SOURCE_REF) AS SUMMARY
FROM dbo.MODULE_VALIDATION_RULE
WHERE LAST_UPDATE_BY = N'migration-095' AND VALIDATION_KEY = N'reference-exists' AND STAGE = N'SAVE'
ORDER BY MODULE_ID, SEQ;

PRINT N'== 播种完成 ==';
