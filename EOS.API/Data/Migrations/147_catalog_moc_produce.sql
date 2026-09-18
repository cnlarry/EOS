-- ============================================================================
-- EOS.ERP migration 148: 制令单族入效果目录并退役 C#（moc-produce：1502 制令单 / 1512 · 1522 · 2803 · 2804）
-- ----------------------------------------------------------------------------
-- C# 侧唯一动作（`MocDomainRules.MocProduceAfterSaveAsync`）是把**主表的订单号三列回填到本单明细**：
--     UPDATE d SET d.ORDER_TYPE=m.ORDER_TYPE, d.ORDER_NO=m.ORDER_NO, d.ORDER_SERIAL_NO=m.ORDER_SERIAL_NO
--     FROM dbo.MOC_PRODUCE_D d INNER JOIN dbo.MOC_PRODUCE_M m ON <本单主键>
--     WHERE d.PRODUCE_TYPE=@Type AND d.PRODUCE_NO=@No
-- 该形态无需新能力：效果目录的 **`link-stamp`**（`fromDetail` 缺省即"来源=主表"）正是
-- "按主表主键定位目标行、把来源列写进去"，且执行器在 `EffectEvent.Save` 上即生效
-- （`LinkStampHandler.ExecuteAsync` 对 `ApproveEffect`/`Save` 走写入分支）。
-- 五个模块的 `EFFECT_ENGINE_TAG` 均为 1、主/明细表同为 `MOC_PRODUCE_M`/`MOC_PRODUCE_D`
-- （且各已有 APPROVE_EFFECT 链），故只需补一条 **SAVE** 期动作。
-- 幂等：按 模块 + SAVE + SEQ 合并；快照族名仅当仍含 `moc-produce` 时改写，改写后复核。
-- 注意：REVERSE_STRUCT 用 `{"kind":"none"}`（SAVE 期不参与解批，仅为目录完备性留位）。
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

IF OBJECT_ID(N'tempdb..#Family') IS NOT NULL DROP TABLE #Family;
CREATE TABLE #Family (MODULE_ID INT PRIMARY KEY, FAMILY NVARCHAR(40) NOT NULL);
INSERT INTO #Family (MODULE_ID, FAMILY) VALUES
    (1502, N'moc-produce'), (1512, N'moc-produce'), (1522, N'moc-produce'),
    (2803, N'moc-produce'), (2804, N'moc-produce');

/* 守卫：模块存在、效果引擎已开、主/明细表符合 link-stamp 形态 */
IF EXISTS (
    SELECT 1 FROM #Family f
    WHERE NOT EXISTS (SELECT 1 FROM dbo.MODULES m
                      WHERE m.M_IDX = f.MODULE_ID AND m.EFFECT_ENGINE_TAG = 1
                        AND LTRIM(RTRIM(ISNULL(m.MASTER_TABLE, ''))) = N'MOC_PRODUCE_M'
                        AND LTRIM(RTRIM(ISNULL(m.DETAIL_TABLE, ''))) = N'MOC_PRODUCE_D')
)
    THROW 50001, N'制令单族模块形态不符（需要 EFFECT_ENGINE_TAG=1 且主/明细表为 MOC_PRODUCE_M/MOC_PRODUCE_D），迁移中止。', 1;

/* ① 播种 SAVE 期回填动作（链上只有这一步，SEQ=1） */
DECLARE @Params NVARCHAR(MAX) =
    N'{"fields":[{"target":"ORDER_TYPE","source":"ORDER_TYPE"},'
    + N'{"target":"ORDER_NO","source":"ORDER_NO"},'
    + N'{"target":"ORDER_SERIAL_NO","source":"ORDER_SERIAL_NO"}],'
    + N'"targets":[{"table":"MOC_PRODUCE_D","refs":["PRODUCE_TYPE","PRODUCE_NO"]}]}';

MERGE dbo.MODULE_BUSINESS_ACTION AS T
USING (SELECT MODULE_ID FROM #Family) AS S
   ON T.MODULE_ID = S.MODULE_ID AND T.EVENT_CODE = N'SAVE' AND T.SEQ = 1
WHEN MATCHED THEN
    UPDATE SET T.EFFECT_KEY = N'link-stamp', T.EFFECT_NAME = N'明细订单号回填（保存期）',
               T.ENABLED = 1, T.FAIL_MODE = N'BLOCK', T.PARAM_STRUCT = @Params,
               T.REVERSE_STRUCT = N'{"kind":"none"}',
               T.REMARK = N'原 C# 制令单保存后动作的忠实移植（主表订单号三列回填本单明细）',
               T.LAST_UPDATE_BY = N'DbUp', T.LAST_UPDATE_DATE = SYSDATETIME()
WHEN NOT MATCHED THEN
    INSERT (MODULE_ID, EVENT_CODE, SEQ, EFFECT_KEY, EFFECT_NAME, ENABLED, FAIL_MODE, CONDITION_STRUCT,
            PARAM_STRUCT, REVERSE_STRUCT, REMARK, SOURCE_REF, CREATE_PERSON, CREATE_DATE, LAST_UPDATE_BY, LAST_UPDATE_DATE)
    VALUES (S.MODULE_ID, N'SAVE', 1, N'link-stamp', N'明细订单号回填（保存期）', 1, N'BLOCK', NULL,
            @Params, N'{"kind":"none"}',
            N'原 C# 制令单保存后动作的忠实移植（主表订单号三列回填本单明细）', N'moc-produce',
            N'DbUp', SYSDATETIME(), N'DbUp', SYSDATETIME());

/* ② 置空已发布快照里的族名 */
UPDATE S
   SET S.DEFINITION_JSON = REPLACE(S.DEFINITION_JSON,
                                   N'"DomainRule":"' + F.FAMILY + N'"',
                                   N'"DomainRule":null')
FROM dbo.WORKBENCH_DEFINITION_SNAPSHOT S
JOIN #Family F ON F.MODULE_ID = S.MODULE_ID
WHERE S.IS_CURRENT = 1
  AND S.DEFINITION_JSON LIKE N'%"DomainRule":"' + F.FAMILY + N'"%';

/* ③ 收口断言 */
IF EXISTS (
    SELECT 1 FROM dbo.WORKBENCH_DEFINITION_SNAPSHOT S
    JOIN #Family F ON F.MODULE_ID = S.MODULE_ID
    WHERE S.IS_CURRENT = 1 AND S.DEFINITION_JSON LIKE N'%"DomainRule":"' + F.FAMILY + N'"%'
)
    THROW 50002, N'当前快照仍残留已退役的族名，迁移中止。', 1;

IF EXISTS (
    SELECT 1 FROM #Family F
    WHERE NOT EXISTS (SELECT 1 FROM dbo.MODULE_BUSINESS_ACTION A
                      WHERE A.MODULE_ID = F.MODULE_ID AND A.EVENT_CODE = N'SAVE'
                        AND A.SEQ = 1 AND A.EFFECT_KEY = N'link-stamp' AND A.ENABLED = 1)
)
    THROW 50003, N'有模块在退役 C# 后缺少启用的 SAVE 期 link-stamp 动作，迁移中止。', 1;

IF OBJECT_ID(N'tempdb..#Family') IS NOT NULL DROP TABLE #Family;

PRINT N'== moc-produce 入效果目录并退役 C# 完成（5 模块，SAVE 期 link-stamp 回填；快照族名已置空）==';
