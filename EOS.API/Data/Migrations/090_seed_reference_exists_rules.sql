-- ============================================================================
-- EOS.ERP migration 091: 保存侧引用存在性校验入目录（库存/生产/模具/制程族）
-- ----------------------------------------------------------------------------
-- 依《D9 校验侧目录化迁移计划》B3：把各领域规则里的"引用资料是否存在"断言迁到
-- MODULE_VALIDATION_RULE（key = reference-exists，STAGE = SAVE）。
--
-- 判据来源与文案均逐条对照 C# 领域规则（InvDomainRules / MocDomainRules /
-- MouDomainRules / SfcDomainRules）与共享 helper：
--   · ValidateDetailAsync：`以下序号项X不存在 ` + CRLF + 最多 10 行 SERIAL_NO（升序）
--   · FindLinesAsync：无条数上限，故 130101 取 maxRows=100（模板上限）
--   · 主表级断言不列行（如 moc-produce 的订单校验、moc-produce-process 的制令单校验）
--
-- 只迁"引用存在性"部分；各族其余逻辑仍留在 C#：
--   · 批号条件必填（ISNULL(BATCH_NO,'')='' AND 产品 MANAGE_BATCH=1）
--   · 数量比较（盘点数<0、入库/出库超制令、生产计划超订单、报关超合同等）
--   · 回写（MOC_PRODUCE_D 订单字段回填、SFC 计划展开等）
-- 明细行的来源范围由明细表携带的主表主键列限定在当前单据内，不会扫描他单历史行。
--
-- 幂等：按 模块+SAVE+顺序 覆盖参数、文案与启用位。
-- ============================================================================

SET NOCOUNT ON;

DECLARE @GUARD_MESSAGE NVARCHAR(400) = N'本脚本只能在 EOS.ERP 数据库内执行，当前库为 ' + DB_NAME() + N'。';
IF DB_NAME() <> N'EOS.ERP'
    THROW 50000, @GUARD_MESSAGE, 1;

PRINT N'== 播种引用存在性校验（库存/生产/模具/制程） ==';

DECLARE @NL NVARCHAR(2) = NCHAR(13) + NCHAR(10);
-- JSON 字符串内不能出现裸控制字符，换行必须写成转义序列（反斜杠 + r + 反斜杠 + n）。
DECLARE @NLJ NVARCHAR(4) = N'\r\n';

-- 共用断言片段：refKey 形态下两侧同列名，lineField 定位到缺失明细行的行号。
DECLARE @DEPOT NVARCHAR(MAX) =
    N'{"refTable":"DEPOT","refKey":{"scope":"DETAIL","field":"DEPOT_ID"},'
    + N'"lineField":"SERIAL_NO","message":"以下序号项库别编号不存在 ' + @NLJ + N'{ROWS}"}';
DECLARE @DEPOT_IN NVARCHAR(MAX) =
    N'{"refTable":"DEPOT","refKey":{"scope":"DETAIL","field":"IN_DEPOT_ID"},'
    + N'"lineField":"SERIAL_NO","message":"以下序号项入库别编号不存在 ' + @NLJ + N'{ROWS}"}';
DECLARE @PRODUCT NVARCHAR(MAX) =
    N'{"refTable":"PRODUCT","refKey":{"scope":"DETAIL","field":"PRO_NO"},'
    + N'"lineField":"SERIAL_NO","message":"以下序号项产品编号不存在 ' + @NLJ + N'{ROWS}"}';
DECLARE @MOULD NVARCHAR(MAX) =
    N'{"refTable":"MOU_MOULD","refKey":{"scope":"DETAIL","field":"MOULD_ID"},'
    + N'"lineField":"SERIAL_NO","message":"以下序号项模具编号不存在 ' + @NLJ + N'{ROWS}"}';
DECLARE @PRODUCE_LINE NVARCHAR(MAX) =
    N'{"refTable":"MOC_PRODUCE_M","join":['
    + N'{"target":"PRODUCE_TYPE","source":{"scope":"DETAIL","field":"PRODUCE_TYPE"}},'
    + N'{"target":"PRODUCE_NO","source":{"scope":"DETAIL","field":"PRODUCE_NO"}}],'
    + N'"lineField":"SERIAL_NO","message":"以下序号项制令单不存在 ' + @NLJ + N'{ROWS}"}';
DECLARE @PROCESS_LINE NVARCHAR(MAX) =
    N'{"refTable":"MOC_PRODUCE_PROCESS_D","join":['
    + N'{"target":"PRODUCE_TYPE","source":{"scope":"DETAIL","field":"PRODUCE_TYPE"}},'
    + N'{"target":"PRODUCE_NO","source":{"scope":"DETAIL","field":"PRODUCE_NO"}},'
    + N'{"target":"PROCEDURE_ID","source":{"scope":"DETAIL","field":"PROCEDURE_ID"}}],'
    + N'"lineField":"SERIAL_NO","message":"以下序号项制令制程不存在 ' + @NLJ + N'{ROWS}"}';
-- 主表级：订单别为空的行视为未引用订单（旧实现的 ISNULL(ORDER_NO,'')<>'' 语义）。
DECLARE @ORDER_MASTER NVARCHAR(MAX) =
    N'{"refTable":"COP_ORDER_D","join":['
    + N'{"target":"ORDER_TYPE","source":{"scope":"MASTER","field":"ORDER_TYPE"}},'
    + N'{"target":"ORDER_NO","source":{"scope":"MASTER","field":"ORDER_NO"}},'
    + N'{"target":"SERIAL_NO","source":{"scope":"MASTER","field":"ORDER_SERIAL_NO"}}],'
    + N'"allowEmpty":[{"scope":"MASTER","field":"ORDER_NO"}],'
    + N'"message":"订单不存在  ' + @NLJ + N'"}';
DECLARE @PRODUCE_MASTER NVARCHAR(MAX) =
    N'{"refTable":"MOC_PRODUCE_M","join":['
    + N'{"target":"PRODUCE_TYPE","source":{"scope":"MASTER","field":"PRODUCE_TYPE"}},'
    + N'{"target":"PRODUCE_NO","source":{"scope":"MASTER","field":"PRODUCE_NO"}}],'
    + N'"message":"制令单不存在。 "}';
DECLARE @SFC_PRODUCT NVARCHAR(MAX) =
    N'{"refTable":"PRODUCT","refKey":{"scope":"MASTER","field":"PRO_NO"},'
    + N'"message":"产品编号不存在 ' + @NLJ + N'"}';
-- 盘点单旧实现不设条数上限，取模板上限 100 以尽量贴近日志式清单。
DECLARE @DEPOT_WIDE NVARCHAR(MAX) =
    N'{"refTable":"DEPOT","refKey":{"scope":"DETAIL","field":"DEPOT_ID"},'
    + N'"lineField":"SERIAL_NO","maxRows":100,"message":"以下序号项库别编号不存在 ' + @NLJ + N'{ROWS}"}';
DECLARE @PRODUCT_WIDE NVARCHAR(MAX) =
    N'{"refTable":"PRODUCT","refKey":{"scope":"DETAIL","field":"PRO_NO"},'
    + N'"lineField":"SERIAL_NO","maxRows":100,"message":"以下序号项产品编号不存在 ' + @NLJ + N'{ROWS}"}';

DECLARE @RULES TABLE (
    MODULE_ID INT, SEQ INT, VALIDATION_KEY NVARCHAR(50),
    PARAM_STRUCT NVARCHAR(MAX), MESSAGE NVARCHAR(500), REMARK NVARCHAR(500), SOURCE_REF NVARCHAR(100),
    PRIMARY KEY (MODULE_ID, SEQ));

-- 库存发生/借出/返还：库别 + 产品
INSERT INTO @RULES (MODULE_ID, SEQ, VALIDATION_KEY, PARAM_STRUCT, MESSAGE, REMARK, SOURCE_REF) VALUES
(130102, 1, N'reference-exists', N'{"checks":[' + @DEPOT + N',' + @PRODUCT + N']}', N'以下序号项库别编号不存在 ' + @NL + N'{ROWS}', N'领用单库别/产品引用校验', N'inv-init'),
(130103, 1, N'reference-exists', N'{"checks":[' + @DEPOT + N',' + @PRODUCT + N']}', N'以下序号项库别编号不存在 ' + @NL + N'{ROWS}', N'入库单库别/产品引用校验', N'inv-in'),
(2817,   1, N'reference-exists', N'{"checks":[' + @DEPOT + N',' + @PRODUCT + N']}', N'以下序号项库别编号不存在 ' + @NL + N'{ROWS}', N'入库单库别/产品引用校验', N'inv-in'),
(130104, 1, N'reference-exists', N'{"checks":[' + @DEPOT + N',' + @PRODUCT + N']}', N'以下序号项库别编号不存在 ' + @NL + N'{ROWS}', N'出库单库别/产品引用校验', N'inv-out'),
(130110, 1, N'reference-exists', N'{"checks":[' + @DEPOT + N',' + @PRODUCT + N']}', N'以下序号项库别编号不存在 ' + @NL + N'{ROWS}', N'出库单库别/产品引用校验', N'inv-out'),
(2818,   1, N'reference-exists', N'{"checks":[' + @DEPOT + N',' + @PRODUCT + N']}', N'以下序号项库别编号不存在 ' + @NL + N'{ROWS}', N'出库单库别/产品引用校验', N'inv-out'),
(3901,   1, N'reference-exists', N'{"checks":[' + @DEPOT + N',' + @PRODUCT + N']}', N'以下序号项库别编号不存在 ' + @NL + N'{ROWS}', N'出库单库别/产品引用校验', N'inv-out'),
(130107, 1, N'reference-exists', N'{"checks":[' + @DEPOT + N',' + @PRODUCT + N']}', N'以下序号项库别编号不存在 ' + @NL + N'{ROWS}', N'调整单库别/产品引用校验', N'inv-adjust'),
(130108, 1, N'reference-exists', N'{"checks":[' + @DEPOT + N',' + @PRODUCT + N']}', N'以下序号项库别编号不存在 ' + @NL + N'{ROWS}', N'借出单库别/产品引用校验', N'inv-loan'),
(130109, 1, N'reference-exists', N'{"checks":[' + @DEPOT + N',' + @PRODUCT + N']}', N'以下序号项库别编号不存在 ' + @NL + N'{ROWS}', N'返还单库别/产品引用校验', N'inv-return');

-- 调拨/报废：入库别 + 出库别 + 产品
INSERT INTO @RULES (MODULE_ID, SEQ, VALIDATION_KEY, PARAM_STRUCT, MESSAGE, REMARK, SOURCE_REF) VALUES
(130105, 1, N'reference-exists', N'{"checks":[' + @DEPOT_IN + N',' + @DEPOT + N',' + @PRODUCT + N']}', N'以下序号项入库别编号不存在 ' + @NL + N'{ROWS}', N'调拨单入/出库别与产品引用校验', N'inv-transfer'),
(130106, 1, N'reference-exists', N'{"checks":[' + @DEPOT_IN + N',' + @DEPOT + N',' + @PRODUCT + N']}', N'以下序号项入库别编号不存在 ' + @NL + N'{ROWS}', N'报废单入/出库别与产品引用校验', N'inv-scrap');

-- 盘点单：库别 + 产品（旧实现清单无上限）
INSERT INTO @RULES (MODULE_ID, SEQ, VALIDATION_KEY, PARAM_STRUCT, MESSAGE, REMARK, SOURCE_REF) VALUES
(130101, 1, N'reference-exists', N'{"checks":[' + @DEPOT_WIDE + N',' + @PRODUCT_WIDE + N']}', N'以下序号项库别编号不存在 ' + @NL + N'{ROWS}', N'盘点单库别/产品引用校验', N'inv-check-stock');

-- 模具领用/返还/报废/量产模入库：模具
INSERT INTO @RULES (MODULE_ID, SEQ, VALIDATION_KEY, PARAM_STRUCT, MESSAGE, REMARK, SOURCE_REF) VALUES
(2908, 1, N'reference-exists', N'{"checks":[' + @MOULD + N']}', N'以下序号项模具编号不存在 ' + @NL + N'{ROWS}', N'模具出库明细模具引用校验', N'mou-out'),
(2909, 1, N'reference-exists', N'{"checks":[' + @MOULD + N']}', N'以下序号项模具编号不存在 ' + @NL + N'{ROWS}', N'模具入库明细模具引用校验', N'mou-in'),
(2910, 1, N'reference-exists', N'{"checks":[' + @MOULD + N']}', N'以下序号项模具编号不存在 ' + @NL + N'{ROWS}', N'模具报废明细模具引用校验', N'mou-scrap'),
(2912, 1, N'reference-exists', N'{"checks":[' + @MOULD + N']}', N'以下序号项模具编号不存在 ' + @NL + N'{ROWS}', N'量产模入库明细模具引用校验', N'mou-batchin');

-- 模具领用单（另一形态）：库别 + 产品
INSERT INTO @RULES (MODULE_ID, SEQ, VALIDATION_KEY, PARAM_STRUCT, MESSAGE, REMARK, SOURCE_REF) VALUES
(2907, 1, N'reference-exists', N'{"checks":[' + @DEPOT + N',' + @PRODUCT + N']}', N'以下序号项库别编号不存在 ' + @NL + N'{ROWS}', N'模具领用单库别/产品引用校验', N'mou-get'),
(2913, 1, N'reference-exists', N'{"checks":[' + @DEPOT + N',' + @PRODUCT + N']}', N'以下序号项库别编号不存在 ' + @NL + N'{ROWS}', N'模具领用单库别/产品引用校验', N'mou-get2');

-- 制令单：订单行（主表级）+ 明细产品
INSERT INTO @RULES (MODULE_ID, SEQ, VALIDATION_KEY, PARAM_STRUCT, MESSAGE, REMARK, SOURCE_REF) VALUES
(1502, 1, N'reference-exists', N'{"checks":[' + @ORDER_MASTER + N',' + @PRODUCT + N']}', NULL, N'制令单订单序号与明细产品引用校验', N'moc-produce'),
(1512, 1, N'reference-exists', N'{"checks":[' + @ORDER_MASTER + N',' + @PRODUCT + N']}', NULL, N'制令单订单序号与明细产品引用校验', N'moc-produce'),
(1522, 1, N'reference-exists', N'{"checks":[' + @ORDER_MASTER + N',' + @PRODUCT + N']}', NULL, N'制令单订单序号与明细产品引用校验', N'moc-produce'),
(2803, 1, N'reference-exists', N'{"checks":[' + @ORDER_MASTER + N',' + @PRODUCT + N']}', NULL, N'制令单订单序号与明细产品引用校验', N'moc-produce'),
(2804, 1, N'reference-exists', N'{"checks":[' + @ORDER_MASTER + N',' + @PRODUCT + N']}', NULL, N'制令单订单序号与明细产品引用校验', N'moc-produce');

-- 生产领料/退料单：库别 + 产品
INSERT INTO @RULES (MODULE_ID, SEQ, VALIDATION_KEY, PARAM_STRUCT, MESSAGE, REMARK, SOURCE_REF) VALUES
(1503, 1, N'reference-exists', N'{"checks":[' + @DEPOT + N',' + @PRODUCT + N']}', N'以下序号项库别编号不存在 ' + @NL + N'{ROWS}', N'生产领料单库别/产品引用校验', N'moc-get'),
(1514, 1, N'reference-exists', N'{"checks":[' + @DEPOT + N',' + @PRODUCT + N']}', N'以下序号项库别编号不存在 ' + @NL + N'{ROWS}', N'生产领料单库别/产品引用校验', N'moc-get'),
(1517, 1, N'reference-exists', N'{"checks":[' + @DEPOT + N',' + @PRODUCT + N']}', N'以下序号项库别编号不存在 ' + @NL + N'{ROWS}', N'生产领料单库别/产品引用校验', N'moc-get'),
(2805, 1, N'reference-exists', N'{"checks":[' + @DEPOT + N',' + @PRODUCT + N']}', N'以下序号项库别编号不存在 ' + @NL + N'{ROWS}', N'生产领料单库别/产品引用校验', N'moc-get'),
(2806, 1, N'reference-exists', N'{"checks":[' + @DEPOT + N',' + @PRODUCT + N']}', N'以下序号项库别编号不存在 ' + @NL + N'{ROWS}', N'生产领料单库别/产品引用校验', N'moc-get');

-- 生产入库/出库单：制令单 + 库别 + 产品
INSERT INTO @RULES (MODULE_ID, SEQ, VALIDATION_KEY, PARAM_STRUCT, MESSAGE, REMARK, SOURCE_REF) VALUES
(1505, 1, N'reference-exists', N'{"checks":[' + @PRODUCE_LINE + N',' + @DEPOT + N',' + @PRODUCT + N']}', N'以下序号项制令单不存在 ' + @NL + N'{ROWS}', N'生产入库单制令单/库别/产品引用校验', N'moc-product-in'),
(1519, 1, N'reference-exists', N'{"checks":[' + @PRODUCE_LINE + N',' + @DEPOT + N',' + @PRODUCT + N']}', N'以下序号项制令单不存在 ' + @NL + N'{ROWS}', N'生产入库单制令单/库别/产品引用校验', N'moc-product-in'),
(2816, 1, N'reference-exists', N'{"checks":[' + @PRODUCE_LINE + N',' + @DEPOT + N',' + @PRODUCT + N']}', N'以下序号项制令单不存在 ' + @NL + N'{ROWS}', N'生产入库单制令单/库别/产品引用校验', N'moc-product-in'),
(2815, 1, N'reference-exists', N'{"checks":[' + @PRODUCE_LINE + N',' + @DEPOT + N',' + @PRODUCT + N']}', N'以下序号项制令单不存在 ' + @NL + N'{ROWS}', N'生产出库单制令单/库别/产品引用校验', N'moc-product-out'),
(1515, 1, N'reference-exists', N'{"checks":[' + @PRODUCE_LINE + N',' + @DEPOT + N',' + @PRODUCT + N']}', N'以下序号项制令单不存在 ' + @NL + N'{ROWS}', N'生产出库单制令单/库别/产品引用校验', N'moc-product-out');

-- 制令制程（主表级）：制令单
INSERT INTO @RULES (MODULE_ID, SEQ, VALIDATION_KEY, PARAM_STRUCT, MESSAGE, REMARK, SOURCE_REF) VALUES
(2704, 1, N'reference-exists', N'{"checks":[' + @PRODUCE_MASTER + N']}', N'制令单不存在。 ', N'制令制程单制令单引用校验', N'moc-produce-process');

-- 制程产品（主表级）：产品
INSERT INTO @RULES (MODULE_ID, SEQ, VALIDATION_KEY, PARAM_STRUCT, MESSAGE, REMARK, SOURCE_REF) VALUES
(2703, 1, N'reference-exists', N'{"checks":[' + @SFC_PRODUCT + N']}', N'产品编号不存在 ' + @NL, N'制程设定产品引用校验', N'sfc-process');

-- 生产日报：制令制程
INSERT INTO @RULES (MODULE_ID, SEQ, VALIDATION_KEY, PARAM_STRUCT, MESSAGE, REMARK, SOURCE_REF) VALUES
(180401, 1, N'reference-exists', N'{"checks":[' + @PROCESS_LINE + N']}', N'以下序号项制令制程不存在 ' + @NL + N'{ROWS}', N'生产日报制令制程引用校验', N'sfc-daily');

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
            LAST_UPDATE_BY = N'migration-091', LAST_UPDATE_DATE = SYSDATETIME()
        WHERE MODULE_ID = @MODULE_ID AND STAGE = N'SAVE' AND SEQ = @SEQ;
    ELSE
        INSERT INTO dbo.MODULE_VALIDATION_RULE
            (MODULE_ID, STAGE, SEQ, VALIDATION_KEY, ENABLED, PARAM_STRUCT, MESSAGE, REMARK, SOURCE_REF, CREATE_PERSON, CREATE_DATE)
        VALUES
            (@MODULE_ID, N'SAVE', @SEQ, @KEY, 1, @PARAM, @MESSAGE, @REMARK, @SOURCE, N'migration-091', SYSDATETIME());

    FETCH NEXT FROM cur INTO @MODULE_ID, @SEQ, @KEY, @PARAM, @MESSAGE, @REMARK, @SOURCE;
END
CLOSE cur;
DEALLOCATE cur;

PRINT N'-- 引用存在性实例现状 --';
SELECT CONCAT(N'  module=', MODULE_ID, N' checks=', (SELECT COUNT(*) FROM OPENJSON(PARAM_STRUCT, '$.checks')),
              N' src=', SOURCE_REF) AS SUMMARY
FROM dbo.MODULE_VALIDATION_RULE
WHERE VALIDATION_KEY = N'reference-exists' AND LAST_UPDATE_BY = N'migration-091'
  AND STAGE = N'SAVE'
ORDER BY MODULE_ID;

-- 影响面快照：按新判据，现存单据中被判"引用不存在"的数量（只读统计，用于评估放量风险）
PRINT N'-- 现存数据按新判据的不通过量（抽样：库存入库明细） --';
SELECT CONCAT(N'  INV_OCCUR_IN_D 明细行=', COUNT(*),
              N' 库别缺失=', SUM(d.NoDepot), N' 产品缺失=', SUM(d.NoProduct)) AS IMPACT
FROM (
    SELECT CASE WHEN EXISTS (SELECT 1 FROM dbo.DEPOT r WHERE r.DEPOT_ID = x.DEPOT_ID) THEN 0 ELSE 1 END AS NoDepot,
           CASE WHEN EXISTS (SELECT 1 FROM dbo.PRODUCT r WHERE r.PRO_NO = x.PRO_NO) THEN 0 ELSE 1 END AS NoProduct
    FROM dbo.INV_OCCUR_IN_D x
) d;

PRINT N'== 引用存在性校验播种完成 ==';
