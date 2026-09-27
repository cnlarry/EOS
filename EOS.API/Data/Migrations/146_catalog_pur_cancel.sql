-- ============================================================================
-- EOS.ERP migration 147: 采购退料单族入目录并退役 C#（pur-cancel：1608 采购退料单、1612 …）
-- ----------------------------------------------------------------------------
-- C# 侧原有两条判据（`PurDomainRules.PurCancelAfterSaveAsync` + `PurCancelQtyCheckAsync`）：
--   ① 批管品必填批号：明细行 `BATCH_NO` 为空、且该料号 `PRODUCT.MANAGE_BATCH=1` ⇒ 拒绝
--      （line-require，与 130102 等库存异动族同形）；
--   ② 退料合计 > 收料合计：原判据
--        SUM(CASE WHEN p.SRC='C' THEN p.QTY END) > ISNULL(SUM(CASE WHEN p.SRC='R' THEN p.QTY END),0)
--      （备品同形），其中 p 是 `PUR_PURCHASE_D ∪ PUR_RECEIVE_D ∪ PUR_CANCEL_D` 按本单明细行的
--      (采购类型, 采购单号, 采购行) 关联后的并集 —— **两侧都取自被引用侧**。
-- 承载方式：
--   · 三表并集按 (采购类型, 采购单号, 采购行) 预聚合为视图 `V_PUR_CANCEL_ALLOC`，
--     "已收 / 已收备品"以 `ISNULL(SUM(...),0)` 直接落列（与旧 `ISNULL` 口径一致，
--     也避开引擎对空限额的处理差异）；
--   · 规则用 `qty-not-exceed` 的新能力 **`usageOnly`**（本单侧贡献为 0，比较式退化为
--     `usage(T) > limit(T)`），数量与备品两量纲写在 `dimensions` 里（命中只输出一次诊断，
--     与既有实现那句 `WHERE a OR b` 同形）。
-- 新增能力见；诊断列 = 本单明细行序号 + 被引用侧 6 个聚合值（与旧文案逐字一致）。
-- 幂等：视图 CREATE OR ALTER；规则按 模块+SAVE+VALIDATION_KEY+SEQ 合并；快照仅当仍含族名时改写。
-- 注意：PARAM_STRUCT 里的换行必须是转义的 `\r\n`（JSON 不允许裸控制字符），MESSAGE 列才用真实 CRLF。
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

IF NOT EXISTS (SELECT 1 FROM dbo.MODULES WHERE M_IDX IN (1608, 1612))
    THROW 50001, N'采购退料单模块（1608/1612）不存在，迁移中止。', 1;

/* ① 承载三表并集的预聚合视图 */
EXEC(N'
CREATE OR ALTER VIEW dbo.V_PUR_CANCEL_ALLOC
AS
SELECT x.PURCHASE_TYPE, x.PURCHASE_NO, x.PURCHASE_SERIAL_NO,
       SUM(CASE WHEN x.SRC = ''P'' THEN x.QTY END) AS PUR_QTY,
       ISNULL(SUM(CASE WHEN x.SRC = ''R'' THEN x.QTY END), 0) AS REC_QTY,
       SUM(CASE WHEN x.SRC = ''C'' THEN x.QTY END) AS RET_QTY,
       SUM(CASE WHEN x.SRC = ''P'' THEN x.SPARE_QTY END) AS PUR_SPARE_QTY,
       ISNULL(SUM(CASE WHEN x.SRC = ''R'' THEN x.SPARE_QTY END), 0) AS REC_SPARE_QTY,
       SUM(CASE WHEN x.SRC = ''C'' THEN x.SPARE_QTY END) AS RET_SPARE_QTY
FROM (
    SELECT PURCHASE_TYPE, PURCHASE_NO, SERIAL_NO AS PURCHASE_SERIAL_NO, QTY, SPARE_QTY, ''P'' AS SRC FROM dbo.PUR_PURCHASE_D
    UNION ALL
    SELECT PURCHASE_TYPE, PURCHASE_NO, PURCHASE_SERIAL_NO, QTY, SPARE_QTY, ''R'' FROM dbo.PUR_RECEIVE_D
    UNION ALL
    SELECT PURCHASE_TYPE, PURCHASE_NO, PURCHASE_SERIAL_NO, QTY, SPARE_QTY, ''C'' FROM dbo.PUR_CANCEL_D
) x
GROUP BY x.PURCHASE_TYPE, x.PURCHASE_NO, x.PURCHASE_SERIAL_NO;');

/* ② 播种保存期规则 */
DECLARE @BatchMessage NVARCHAR(200) = N'以下序号项需要输入批号 ';
DECLARE @BatchParam NVARCHAR(MAX) =
    N'{"checks":[{"scope":"DETAIL","field":"BATCH_NO","message":"以下序号项需要输入批号 ",'
    + N'"diagnosticFields":["SERIAL_NO"],'
    + N'"condition":{"logic":"AND","items":[{"type":"not-exists","targetTable":"PRODUCT","negate":true,'
    + N'"condition":{"type":"value-eq","field":{"scope":"TARGET","field":"MANAGE_BATCH"},"value":1},'
    + N'"match":[{"target":"PRO_NO","source":{"scope":"DETAIL","field":"PRO_NO"}}]}]}}]}';

DECLARE @QtyMessage NVARCHAR(300) =
    N'以下序号项退料数量大于收料' + CHAR(13) + CHAR(10)
    + N' 序号  采购数量   已收  退料   采购备品   已收备品  退备品'
    + CHAR(13) + CHAR(10) + N'{ROWS}';
DECLARE @QtyParam NVARCHAR(MAX) =
    N'{"mode":"usage-not-exceed","checks":[{"targetTable":"V_PUR_CANCEL_ALLOC","usageOnly":true,'
    + N'"match":[{"target":"PURCHASE_TYPE","source":{"scope":"DETAIL","field":"PURCHASE_TYPE"}},'
    + N'{"target":"PURCHASE_NO","source":{"scope":"DETAIL","field":"PURCHASE_NO"}},'
    + N'{"target":"PURCHASE_SERIAL_NO","source":{"scope":"DETAIL","field":"PURCHASE_SERIAL_NO"}}],'
    + N'"dimensions":['
    + N'{"usage":{"scope":"TARGET","fields":["RET_QTY"]},"limit":{"scope":"TARGET","fields":["REC_QTY"]}},'
    + N'{"usage":{"scope":"TARGET","fields":["RET_SPARE_QTY"]},"limit":{"scope":"TARGET","fields":["REC_SPARE_QTY"]}}],'
    + N'"message":"以下序号项退料数量大于收料\r\n 序号  采购数量   已收  退料   采购备品   已收备品  退备品\r\n{ROWS}",'
    + N'"maxRows":100,'
    + N'"diagnosticFields":["SERIAL_NO",'
    + N'{"scope":"TARGET","field":"PUR_QTY"},{"scope":"TARGET","field":"REC_QTY"},{"scope":"TARGET","field":"RET_QTY"},'
    + N'{"scope":"TARGET","field":"PUR_SPARE_QTY"},{"scope":"TARGET","field":"REC_SPARE_QTY"},{"scope":"TARGET","field":"RET_SPARE_QTY"}]}]}';

MERGE dbo.MODULE_VALIDATION_RULE AS T
USING (SELECT 1608 AS MODULE_ID
       UNION ALL SELECT 1612) AS S
   ON T.MODULE_ID = S.MODULE_ID AND T.STAGE = N'SAVE'
  AND T.VALIDATION_KEY = N'line-require' AND T.SEQ = 2
WHEN MATCHED THEN
    UPDATE SET T.PARAM_STRUCT = @BatchParam, T.MESSAGE = @BatchMessage, T.ENABLED = 1,
               T.REMARK = N'批管品必填批号（保存期；原 C# 采购退料单判据的忠实移植）',
               T.LAST_UPDATE_BY = N'DbUp', T.LAST_UPDATE_DATE = SYSDATETIME()
WHEN NOT MATCHED THEN
    INSERT (MODULE_ID, STAGE, SEQ, VALIDATION_KEY, ENABLED, PARAM_STRUCT, MESSAGE, REMARK, SOURCE_REF,
            CREATE_PERSON, CREATE_DATE, LAST_UPDATE_BY, LAST_UPDATE_DATE)
    VALUES (S.MODULE_ID, N'SAVE', 2, N'line-require', 1, @BatchParam, @BatchMessage,
            N'批管品必填批号（保存期；原 C# 采购退料单判据的忠实移植）', N'pur-cancel',
            N'DbUp', SYSDATETIME(), N'DbUp', SYSDATETIME());

MERGE dbo.MODULE_VALIDATION_RULE AS T
USING (SELECT 1608 AS MODULE_ID
       UNION ALL SELECT 1612) AS S
   ON T.MODULE_ID = S.MODULE_ID AND T.STAGE = N'SAVE'
  AND T.VALIDATION_KEY = N'qty-not-exceed' AND T.SEQ = 3
WHEN MATCHED THEN
    UPDATE SET T.PARAM_STRUCT = @QtyParam, T.MESSAGE = @QtyMessage, T.ENABLED = 1,
               T.REMARK = N'退料合计不超收料合计（数量与备品两量纲；原 C# 采购退料单判据的忠实移植）',
               T.LAST_UPDATE_BY = N'DbUp', T.LAST_UPDATE_DATE = SYSDATETIME()
WHEN NOT MATCHED THEN
    INSERT (MODULE_ID, STAGE, SEQ, VALIDATION_KEY, ENABLED, PARAM_STRUCT, MESSAGE, REMARK, SOURCE_REF,
            CREATE_PERSON, CREATE_DATE, LAST_UPDATE_BY, LAST_UPDATE_DATE)
    VALUES (S.MODULE_ID, N'SAVE', 3, N'qty-not-exceed', 1, @QtyParam, @QtyMessage,
            N'退料合计不超收料合计（数量与备品两量纲；原 C# 采购退料单判据的忠实移植）', N'pur-cancel',
            N'DbUp', SYSDATETIME(), N'DbUp', SYSDATETIME());

/* ③ 置空已发布快照里的族名 */
IF OBJECT_ID(N'tempdb..#PortedFamily') IS NOT NULL DROP TABLE #PortedFamily;
CREATE TABLE #PortedFamily (MODULE_ID INT PRIMARY KEY, FAMILY NVARCHAR(40) NOT NULL);
INSERT INTO #PortedFamily (MODULE_ID, FAMILY) VALUES (1608, N'pur-cancel'), (1612, N'pur-cancel');

UPDATE S
   SET S.DEFINITION_JSON = REPLACE(S.DEFINITION_JSON,
                                   N'"DomainRule":"' + F.FAMILY + N'"',
                                   N'"DomainRule":null')
FROM dbo.WORKBENCH_DEFINITION_SNAPSHOT S
JOIN #PortedFamily F ON F.MODULE_ID = S.MODULE_ID
WHERE S.IS_CURRENT = 1
  AND S.DEFINITION_JSON LIKE N'%"DomainRule":"' + F.FAMILY + N'"%';

/* ④ 收口断言 */
IF EXISTS (
    SELECT 1 FROM dbo.WORKBENCH_DEFINITION_SNAPSHOT S
    JOIN #PortedFamily F ON F.MODULE_ID = S.MODULE_ID
    WHERE S.IS_CURRENT = 1 AND S.DEFINITION_JSON LIKE N'%"DomainRule":"' + F.FAMILY + N'"%'
)
    THROW 50002, N'当前快照仍残留已退役的族名，迁移中止。', 1;

IF EXISTS (
    SELECT 1 FROM #PortedFamily F
    WHERE NOT EXISTS (SELECT 1 FROM dbo.MODULE_VALIDATION_RULE R
                      WHERE R.MODULE_ID = F.MODULE_ID AND R.STAGE = N'SAVE' AND R.ENABLED = 1
                        AND R.VALIDATION_KEY = N'line-require')
       OR NOT EXISTS (SELECT 1 FROM dbo.MODULE_VALIDATION_RULE R
                      WHERE R.MODULE_ID = F.MODULE_ID AND R.STAGE = N'SAVE' AND R.ENABLED = 1
                        AND R.VALIDATION_KEY = N'qty-not-exceed')
)
    THROW 50003, N'有模块在退役 C# 后缺少启用的 SAVE 期目录规则（line-require / qty-not-exceed），迁移中止。', 1;

IF NOT EXISTS (SELECT 1 FROM sys.views WHERE object_id = OBJECT_ID(N'dbo.V_PUR_CANCEL_ALLOC'))
    THROW 50004, N'预聚合视图 V_PUR_CANCEL_ALLOC 未建立，迁移中止。', 1;

IF OBJECT_ID(N'tempdb..#PortedFamily') IS NOT NULL DROP TABLE #PortedFamily;

PRINT N'== pur-cancel 入目录并退役 C# 完成（1608/1612，视图 V_PUR_CANCEL_ALLOC + 两条 SAVE 规则，快照族名已置空）==';
