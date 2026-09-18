-- ============================================================================
-- EOS.ERP migration 134: 行字段断言入目录并退役 C#（curr 110103、inv-check-stock 130101）
-- ----------------------------------------------------------------------------
-- 两族在 C# 侧都是"按行取值约束"，此前目录表达不了，故新增加法式能力：
--   `line-require` 的 check 支持 `assert`（`{"op":"EQ|GE|…","value":<数值>}`，未命中即违规；
--   给出 assert 时触发器可省略 ⇒ 无条件生效）与 `scope=MASTER`（断言行改为当前主表行，
--   供无明细表的模块使用）。
--   · 110103：本位币（IS_BASE=1）的汇率必须为 1 —— scope=MASTER + 触发器 IS_BASE=1 + assert EQ 1
--   · 130101：盘点数不得小于 0 —— scope=DETAIL + 无条件 assert GE 0 + 序号诊断
-- 播种 SAVE 期规则（SEQ 取 2；两模块的 1 已分别被 duplicate-check / reference-exists 占用），
-- 并就地置空已发布快照里的族名（与 105/107/110/112/114/115/133 同法）。
-- 幂等：播种按 模块+SAVE+VALIDATION_KEY+SEQ 覆盖；快照仅当仍含族名时改写，改写后复核。
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
DECLARE @BaseCurrencyMessage NVARCHAR(200) = N'本位币汇率只能为1';
DECLARE @BaseCurrencyParam NVARCHAR(MAX) =
    N'{"checks":[{"scope":"MASTER","field":"CURR_RATE","message":"' + @BaseCurrencyMessage + N'",'
    + N'"triggers":[{"scope":"MASTER","field":"IS_BASE","op":"EQ","value":1}],'
    + N'"assert":{"op":"EQ","value":1}}]}';

DECLARE @StockCountMessage NVARCHAR(200) = N'以下序号项盘点数小于0 ';
DECLARE @StockCountParam NVARCHAR(MAX) =
    N'{"checks":[{"scope":"DETAIL","field":"CHECK_QTY","message":"' + @StockCountMessage + N'",'
    + N'"diagnosticFields":["SERIAL_NO"],"assert":{"op":"GE","value":0}}]}';

MERGE dbo.MODULE_VALIDATION_RULE AS T
USING (SELECT 110103 AS MODULE_ID, @BaseCurrencyParam AS PARAM_STRUCT, @BaseCurrencyMessage AS MESSAGE,
              N'本位币汇率只能为1（保存期；原 C# 货币资料判据的忠实移植）' AS REMARK
       UNION ALL
       SELECT 130101, @StockCountParam, @StockCountMessage,
              N'盘点数不小于零（保存期；原 C# 库存盘点判据的忠实移植）') AS S
   ON T.MODULE_ID = S.MODULE_ID AND T.STAGE = N'SAVE' AND T.VALIDATION_KEY = N'line-require' AND T.SEQ = 2
WHEN MATCHED THEN
    UPDATE SET T.PARAM_STRUCT = S.PARAM_STRUCT, T.MESSAGE = S.MESSAGE, T.REMARK = S.REMARK, T.ENABLED = 1,
               T.LAST_UPDATE_BY = N'DbUp', T.LAST_UPDATE_DATE = SYSDATETIME()
WHEN NOT MATCHED THEN
    INSERT (MODULE_ID, STAGE, SEQ, VALIDATION_KEY, ENABLED, PARAM_STRUCT, MESSAGE, REMARK, SOURCE_REF,
            CREATE_PERSON, CREATE_DATE, LAST_UPDATE_BY, LAST_UPDATE_DATE)
    VALUES (S.MODULE_ID, N'SAVE', 2, N'line-require', 1, S.PARAM_STRUCT, S.MESSAGE, S.REMARK, N'P_WF_*',
            N'DbUp', SYSDATETIME(), N'DbUp', SYSDATETIME());

/* ② 置空已发布快照里的族名 */
IF OBJECT_ID(N'tempdb..#PortedFamily') IS NOT NULL DROP TABLE #PortedFamily;
CREATE TABLE #PortedFamily (MODULE_ID INT PRIMARY KEY, FAMILY NVARCHAR(40) NOT NULL);
INSERT INTO #PortedFamily (MODULE_ID, FAMILY) VALUES (110103, N'curr'), (130101, N'inv-check-stock');

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

PRINT N'== curr / inv-check-stock 入目录并退役 C# 完成（2 模块，快照族名已置空）==';
