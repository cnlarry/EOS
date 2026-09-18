-- ============================================================================
-- EOS.ERP migration 135: 报关单族入目录并退役 C#（cus-export 300301/300304、cus-import 300302/300305）
-- ----------------------------------------------------------------------------
-- 两族在 C# 侧只有一条判据——`MODULES.ERROR_NO_SAVE=1` 门控的"报关数量不超备案合同数量"：
--   · 出口：CUS_MANUAL_PRO 的 (MANUAL_NO, SERIAL_NO) 对应本单明细按 (MANUAL_NO, PRO_SERIAL_NO)
--           求和后的数量，满足 `EXP_QTY+ZC_QTY+单据数量 > QTY+ZR_QTY` 即越界；
--   · 进口：CUS_MANUAL_MAT 同形，多一项 TRAN_QTY 与 BF_QTY。
-- 该判据**无需新能力**：`qty-not-exceed` 的 `usage-not-exceed` 形态正好是
-- `usage(T) + this(分组求和) > limit(T)`，`usage`/`limit` 均支持多字段求和，门控走
-- `switch.gates=[{scope:"MODULE",key:"ERROR_NO_SAVE",expect:1}]`（迁移 124 起已具备），
-- 四列诊断 = 分组键 + 两个被引用行列 + 本单求和值。四个模块的门当前为 0（判据休眠），
-- 播种带门控的规则后**开关语义与旧 C# 完全一致**。
-- 播种 SAVE 期规则（四个模块此前无任何校验规则，SEQ 取 1），并就地置空已发布快照里的族名。
-- 幂等：播种按 模块+SAVE+VALIDATION_KEY+SEQ 覆盖；快照仅当仍含族名时改写，改写后复核。
-- 注意：`maxRows` 之外还有一处易错点——**PARAM_STRUCT 里的换行必须是转义的 `\r\n`**
-- （JSON 字符串不允许裸控制字符，否则发布期报"params 不是合法 JSON"），MESSAGE 列才用真实 CRLF。
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

/* ① 播种保存期规则（受模块开关 ERROR_NO_SAVE 门控） */
DECLARE @Gate NVARCHAR(200) =
    N'"switch":{"gates":[{"scope":"MODULE","key":"ERROR_NO_SAVE","expect":1}]},';

DECLARE @ExportMessage NVARCHAR(200) =
    N'以下报关单已超出合同数量' + CHAR(13) + CHAR(10) + N' 手册编号  数 量  已出数量  单据数量'
    + CHAR(13) + CHAR(10) + N'{ROWS}';
-- JSON 字符串里必须用转义序列 `\r\n`（T-SQL 不解释反斜杠，入库即两字符转义），
-- 而 MESSAGE 列是普通 NVARCHAR，用真实 CRLF（与迁移 126/127 同法）。
DECLARE @ExportParam NVARCHAR(MAX) =
    N'{"mode":"usage-not-exceed","checks":[{"targetTable":"CUS_MANUAL_PRO",'
    + N'"match":[{"target":"MANUAL_NO","source":{"scope":"DETAIL","field":"MANUAL_NO"}},'
    + N'{"target":"SERIAL_NO","source":{"scope":"DETAIL","field":"PRO_SERIAL_NO"}}],'
    + N'"thisQty":{"scope":"DETAIL","agg":"SUM","terms":[{"field":"QTY","coef":1}]},'
    + N'"usage":{"scope":"TARGET","fields":["EXP_QTY","ZC_QTY"]},'
    + N'"limit":{"scope":"TARGET","fields":["QTY","ZR_QTY"]},'
    + @Gate
    + N'"message":"以下报关单已超出合同数量\r\n 手册编号  数 量  已出数量  单据数量\r\n{ROWS}",'
    + N'"maxRows":100,'
    + N'"diagnosticFields":["MANUAL_NO",{"scope":"TARGET","field":"QTY"},{"scope":"TARGET","field":"EXP_QTY"},{"scope":"THIS"}]}]}';

DECLARE @ImportMessage NVARCHAR(200) =
    N'以下报关单已超出合同数量' + CHAR(13) + CHAR(10) + N' 手册编号  数 量  已进数量  单据数量'
    + CHAR(13) + CHAR(10) + N'{ROWS}';
DECLARE @ImportParam NVARCHAR(MAX) =
    N'{"mode":"usage-not-exceed","checks":[{"targetTable":"CUS_MANUAL_MAT",'
    + N'"match":[{"target":"MANUAL_NO","source":{"scope":"DETAIL","field":"MANUAL_NO"}},'
    + N'{"target":"SERIAL_NO","source":{"scope":"DETAIL","field":"MAT_SERIAL_NO"}}],'
    + N'"thisQty":{"scope":"DETAIL","agg":"SUM","terms":[{"field":"QTY","coef":1}]},'
    + N'"usage":{"scope":"TARGET","fields":["IMP_QTY","TRAN_QTY","ZC_QTY","BF_QTY"]},'
    + N'"limit":{"scope":"TARGET","fields":["QTY","ZR_QTY"]},'
    + @Gate
    + N'"message":"以下报关单已超出合同数量\r\n 手册编号  数 量  已进数量  单据数量\r\n{ROWS}",'
    + N'"maxRows":100,'
    + N'"diagnosticFields":["MANUAL_NO",{"scope":"TARGET","field":"QTY"},{"scope":"TARGET","field":"IMP_QTY"},{"scope":"THIS"}]}]}';

MERGE dbo.MODULE_VALIDATION_RULE AS T
USING (SELECT 300301 AS MODULE_ID, @ExportParam AS PARAM_STRUCT, @ExportMessage AS MESSAGE,
              N'出口报关不超备案合同数量（保存期，受模块开关门控；原 C# 报关判据的忠实移植）' AS REMARK
       UNION ALL SELECT 300304, @ExportParam, @ExportMessage,
              N'出口报关不超备案合同数量（保存期，受模块开关门控；原 C# 报关判据的忠实移植）'
       UNION ALL SELECT 300302, @ImportParam, @ImportMessage,
              N'进口报关不超备案合同数量（保存期，受模块开关门控；原 C# 报关判据的忠实移植）'
       UNION ALL SELECT 300305, @ImportParam, @ImportMessage,
              N'进口报关不超备案合同数量（保存期，受模块开关门控；原 C# 报关判据的忠实移植）') AS S
   ON T.MODULE_ID = S.MODULE_ID AND T.STAGE = N'SAVE' AND T.VALIDATION_KEY = N'qty-not-exceed' AND T.SEQ = 1
WHEN MATCHED THEN
    UPDATE SET T.PARAM_STRUCT = S.PARAM_STRUCT, T.MESSAGE = S.MESSAGE, T.REMARK = S.REMARK, T.ENABLED = 1,
               T.LAST_UPDATE_BY = N'DbUp', T.LAST_UPDATE_DATE = SYSDATETIME()
WHEN NOT MATCHED THEN
    INSERT (MODULE_ID, STAGE, SEQ, VALIDATION_KEY, ENABLED, PARAM_STRUCT, MESSAGE, REMARK, SOURCE_REF,
            CREATE_PERSON, CREATE_DATE, LAST_UPDATE_BY, LAST_UPDATE_DATE)
    VALUES (S.MODULE_ID, N'SAVE', 1, N'qty-not-exceed', 1, S.PARAM_STRUCT, S.MESSAGE, S.REMARK, N'P_CUS_*',
            N'DbUp', SYSDATETIME(), N'DbUp', SYSDATETIME());

/* ② 置空已发布快照里的族名 */
IF OBJECT_ID(N'tempdb..#PortedFamily') IS NOT NULL DROP TABLE #PortedFamily;
CREATE TABLE #PortedFamily (MODULE_ID INT PRIMARY KEY, FAMILY NVARCHAR(40) NOT NULL);
INSERT INTO #PortedFamily (MODULE_ID, FAMILY) VALUES
    (300301, N'cus-export'), (300304, N'cus-export'), (300302, N'cus-import'), (300305, N'cus-import');

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

PRINT N'== cus-export / cus-import 入目录并退役 C# 完成（4 模块，快照族名已置空）==';
