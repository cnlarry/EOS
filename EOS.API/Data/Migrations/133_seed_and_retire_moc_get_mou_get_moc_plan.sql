-- ============================================================================
-- EOS.ERP migration 133: 同形族继续入目录并退役 C#（moc-get 1503/1514/1517/2805/2806、
--                            mou-get 2907），并退役空实现族 moc-plan（1507）
-- ----------------------------------------------------------------------------
-- ① 同形族：三族在 C# 侧都只有一条判据「批管品必填批号」（`ValidateDetailAsync`，条件与文案逐字一致）：
--      moc-get：MOC_GET_D（GET_TYPE/GET_NO）  mou-get：MOU_GET_D（GET_TYPE/GET_NO）
--    播种保存期 `line-require`（跨表 condition + 序号诊断）。SEQ 取 2（各模块 SAVE 期 1 已被
--    reference-exists 占用；1507 的 2 已被 qty-not-exceed 占用，与本迁移目标集不重叠）。
-- ② moc-plan：C# 侧计划量不超订单的判据**已由目录 qty-not-exceed 承担**，方法只剩无条件返回成功，
--    属空实现族（与迁移 110 的 mou-apply/mou-accept 同形），无需播种、只需删码。
-- ③ 就地置空已发布快照里的族名（与 105/107/110/112/114/115 同法），使运行期不再分派到已删族。
--    新规则进入运行期需重发布这些模块（发布走新版参数校验）。
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
DECLARE @MSG NVARCHAR(200) = N'以下序号项需要输入批号 ';
DECLARE @PARAM NVARCHAR(MAX) =
    N'{"checks":[{"scope":"DETAIL","field":"BATCH_NO","message":"' + @MSG + N'",'
    + N'"diagnosticFields":["SERIAL_NO"],'
    + N'"condition":{"logic":"AND","items":['
    + N'{"type":"not-exists","targetTable":"PRODUCT","negate":true,'
    + N'"condition":{"type":"value-eq","field":{"scope":"TARGET","field":"MANAGE_BATCH"},"value":1},'
    + N'"match":[{"target":"PRO_NO","source":{"scope":"DETAIL","field":"PRO_NO"}}]}]}}]}';

DECLARE @Targets TABLE (MODULE_ID INT PRIMARY KEY);
INSERT INTO @Targets (MODULE_ID) VALUES (1503),(1514),(1517),(2805),(2806),(2907);

MERGE dbo.MODULE_VALIDATION_RULE AS T
USING (SELECT MODULE_ID, N'SAVE' AS STAGE, 2 AS SEQ, N'line-require' AS VALIDATION_KEY FROM @Targets) AS S
   ON T.MODULE_ID = S.MODULE_ID AND T.STAGE = S.STAGE AND T.VALIDATION_KEY = S.VALIDATION_KEY AND T.SEQ = S.SEQ
WHEN MATCHED THEN
    UPDATE SET T.PARAM_STRUCT = @PARAM, T.MESSAGE = @MSG, T.ENABLED = 1,
               T.LAST_UPDATE_BY = N'DbUp', T.LAST_UPDATE_DATE = SYSDATETIME()
WHEN NOT MATCHED THEN
    INSERT (MODULE_ID, STAGE, SEQ, VALIDATION_KEY, ENABLED, PARAM_STRUCT, MESSAGE, REMARK, SOURCE_REF,
            CREATE_PERSON, CREATE_DATE, LAST_UPDATE_BY, LAST_UPDATE_DATE)
    VALUES (S.MODULE_ID, S.STAGE, S.SEQ, S.VALIDATION_KEY, 1, @PARAM, @MSG,
            N'批管品必填批号（保存期；原 C# ValidateDetailAsync 判据的忠实移植）', N'P_WF_*',
            N'DbUp', SYSDATETIME(), N'DbUp', SYSDATETIME());

/* ② 置空已发布快照里的族名 */
IF OBJECT_ID(N'tempdb..#PortedFamily') IS NOT NULL DROP TABLE #PortedFamily;
CREATE TABLE #PortedFamily (MODULE_ID INT PRIMARY KEY, FAMILY NVARCHAR(40) NOT NULL);
INSERT INTO #PortedFamily (MODULE_ID, FAMILY) VALUES
    (1503, N'moc-get'), (1514, N'moc-get'), (1517, N'moc-get'), (2805, N'moc-get'), (2806, N'moc-get'),
    (2907, N'mou-get'), (1507, N'moc-plan');

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

PRINT N'== moc-get / mou-get 入目录（6 模块）+ moc-plan 空实现退役（1 模块）完成，快照族名已置空 ==';
