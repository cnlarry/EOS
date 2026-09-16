-- ============================================================================
-- EOS.ERP migration 103: 保存侧不超量校验入目录（B4：1505 入库单 / 1519 入库单管理(外)）
-- ----------------------------------------------------------------------------
-- C# 判据（MocDomainRules.MocProductInAfterSaveAsync，受 MODULES.ERROR_NO_SAVE 门控，
-- 实测 1505=1、1519=1 即两模块都生效；两者共用同一段实现与同一明细表）：
--   SELECT TOP 11 t.PRODUCE_NO FROM
--   (SELECT PRODUCE_TYPE, PRODUCE_NO, SUM(QTY) QTY, SUM(SPARE_QTY) SPARE_QTY
--    FROM dbo.MOC_PRODUCT_IN_D WHERE PRODUCT_IN_TYPE=@Type AND PRODUCT_IN_NO=@No
--    GROUP BY PRODUCE_TYPE, PRODUCE_NO) t
--   INNER JOIN dbo.MOC_PRODUCE_M m ON m.PRODUCE_TYPE=t.PRODUCE_TYPE AND m.PRODUCE_NO=t.PRODUCE_NO
--   WHERE (t.QTY+ISNULL(m.FINISHED_QTY,0)+ISNULL(m.SCRAP_IN_QTY,0) > ISNULL(m.QTY,0)
--       OR t.SPARE_QTY+ISNULL(m.FINISHED_SPARE_QTY,0)+ISNULL(m.SCRAP_IN_SPARE_QTY,0) > ISNULL(m.SPARE_QTY,0));
--   文案「以下生产单入库数量超出制令生产 \r\n」+ 制令号列表（**一行内联，值间两个空格**，最多 10 个）
--
-- 目录表达：usage-not-exceed，usage 为两字段相加（已完工 + 已报废入库），thisQty.agg=SUM 按制令分组；
-- 诊断单列 PRODUCE_NO，行分隔符取两个空格（inline），与旧文案一致。
-- 幂等：按 模块+SAVE+顺序 覆盖参数与启用位。
-- ============================================================================

SET NOCOUNT ON;

DECLARE @GUARD_MESSAGE NVARCHAR(400) = N'本脚本只能在 EOS.ERP 数据库内执行，当前库为 ' + DB_NAME() + N'。';
IF DB_NAME() <> N'EOS.ERP'
    THROW 50000, @GUARD_MESSAGE, 1;

PRINT N'== 播种 1505/1519 保存侧入库不超制令生产 ==';

DECLARE @NLJ NVARCHAR(4) = N'\r\n';
DECLARE @M_PRODUCE NVARCHAR(MAX) = N'"match":[{"target":"PRODUCE_TYPE","source":{"scope":"DETAIL","field":"PRODUCE_TYPE"}},{"target":"PRODUCE_NO","source":{"scope":"DETAIL","field":"PRODUCE_NO"}}]';
DECLARE @DIAG NVARCHAR(MAX) = N'"diagnosticFields":[{"scope":"TARGET","field":"PRODUCE_NO"}],"diagnosticRowSeparator":"  "';
DECLARE @MSG NVARCHAR(200) = N'以下生产单入库数量超出制令生产 ' + @NLJ + N'{ROWS}';

DECLARE @PARAM NVARCHAR(MAX) =
    N'{"mode":"usage-not-exceed","checks":['
    + N'{"targetTable":"MOC_PRODUCE_M",' + @M_PRODUCE + N','
    + N'"thisQty":{"scope":"DETAIL","agg":"SUM","terms":[{"field":"QTY","coef":1}]},'
    + N'"usage":{"scope":"TARGET","fields":["FINISHED_QTY","SCRAP_IN_QTY"]},'
    + N'"limit":{"scope":"TARGET","fields":["QTY"]},' + @DIAG + N',"message":"' + @MSG + N'"},'
    + N'{"targetTable":"MOC_PRODUCE_M",' + @M_PRODUCE + N','
    + N'"thisQty":{"scope":"DETAIL","agg":"SUM","terms":[{"field":"SPARE_QTY","coef":1}]},'
    + N'"usage":{"scope":"TARGET","fields":["FINISHED_SPARE_QTY","SCRAP_IN_SPARE_QTY"]},'
    + N'"limit":{"scope":"TARGET","fields":["SPARE_QTY"]},' + @DIAG + N',"message":"' + @MSG + N'"}]}';

IF EXISTS (SELECT 1 FROM dbo.MODULE_VALIDATION_RULE WHERE MODULE_ID = 1505 AND STAGE = N'SAVE' AND SEQ = 1)
    UPDATE dbo.MODULE_VALIDATION_RULE
    SET VALIDATION_KEY = N'qty-not-exceed', ENABLED = 1, PARAM_STRUCT = @PARAM, MESSAGE = NULL,
        REMARK = N'保存侧入库不超制令生产（按制令分组求和）', SOURCE_REF = N'moc-product-in',
        LAST_UPDATE_BY = N'migration-103', LAST_UPDATE_DATE = SYSDATETIME()
    WHERE MODULE_ID = 1505 AND STAGE = N'SAVE' AND SEQ = 1;
ELSE
    INSERT INTO dbo.MODULE_VALIDATION_RULE
        (MODULE_ID, STAGE, SEQ, VALIDATION_KEY, ENABLED, PARAM_STRUCT, MESSAGE, REMARK, SOURCE_REF, CREATE_PERSON, CREATE_DATE)
    VALUES
        (1505, N'SAVE', 1, N'qty-not-exceed', 1, @PARAM, NULL,
         N'保存侧入库不超制令生产（按制令分组求和）', N'moc-product-in', N'migration-103', SYSDATETIME());

-- 1519 与 1505 同实现同明细表，配置完全相同
IF EXISTS (SELECT 1 FROM dbo.MODULE_VALIDATION_RULE WHERE MODULE_ID = 1519 AND STAGE = N'SAVE' AND SEQ = 1)
    UPDATE dbo.MODULE_VALIDATION_RULE
    SET VALIDATION_KEY = N'qty-not-exceed', ENABLED = 1, PARAM_STRUCT = @PARAM, MESSAGE = NULL,
        REMARK = N'保存侧入库不超制令生产（按制令分组求和）', SOURCE_REF = N'moc-product-in',
        LAST_UPDATE_BY = N'migration-103', LAST_UPDATE_DATE = SYSDATETIME()
    WHERE MODULE_ID = 1519 AND STAGE = N'SAVE' AND SEQ = 1;
ELSE
    INSERT INTO dbo.MODULE_VALIDATION_RULE
        (MODULE_ID, STAGE, SEQ, VALIDATION_KEY, ENABLED, PARAM_STRUCT, MESSAGE, REMARK, SOURCE_REF, CREATE_PERSON, CREATE_DATE)
    VALUES
        (1519, N'SAVE', 1, N'qty-not-exceed', 1, @PARAM, NULL,
         N'保存侧入库不超制令生产（按制令分组求和）', N'moc-product-in', N'migration-103', SYSDATETIME());

PRINT N'-- 落库结果 --';
SELECT CONCAT(N'  module=', MODULE_ID, N' checks=', (SELECT COUNT(*) FROM OPENJSON(PARAM_STRUCT, '$.checks'))) AS SUMMARY
FROM dbo.MODULE_VALIDATION_RULE WHERE MODULE_ID IN (1505, 1519) AND STAGE = N'SAVE' ORDER BY MODULE_ID;

PRINT N'== 播种完成 ==';
