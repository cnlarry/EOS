-- ============================================================================
-- EOS.ERP migration 159: 请购单族入效果目录并退役 C#（pur-apply：1615 请购单、1616 请购单(外)）
-- ----------------------------------------------------------------------------
-- C# 侧（`PurDomainRules.PurApplyAfterSaveAsync`）是旧过程 `P_PUR_APPLY_After_Save` 的整链：
--   ① 明细 `REQUIRE_QTY` 清零（无待购行时到此为止）；
--   ② 待购表 `PUR_APPLY_MORE` 中尚未成行的产品按 `SUM(REQUIRE_QTY)` 追加明细行
--      （序号在最大序号上递增，仓库/单位取产品档案；产品档案缺行时**仍补行**）；
--   ③ 明细 `REQUIRE_QTY`/`LOST_QTY` 按产品汇总回填；
--   ④ 明细 订单类型/单号/序号、客户品号、客户单号、预交日期、客户 由待购行关联的订单行回填；
--   ⑤ 申购数量分配：待购表 `QTY` 清零后，按 `(产品, 序号)` 顺序把明细 `QTY` 逐行分给待购行；
--   ⑥ 主表 `ORDER_NO`/`PRODUCE_NO` = 待购表去重非空值按序串联（全空不回写）。
-- 承接：新增服务处理器 **`pur-apply-sync`**（六张表 + 各列名分组声明，全部校验为物理列；
-- ②与⑤需逐行处理，与原实现一致在事务内读取后循环执行）。
-- 产品编号存在性校验**早已在目录里**（两模块各一条 SAVE 期 `reference-exists`），故本迁移只播写动作。
-- 幂等：动作按 模块+SAVE+SEQ 合并；快照族名仅当仍含 `pur-apply` 时改写，改写后复核。
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
INSERT INTO #Family (MODULE_ID, FAMILY) VALUES (1615, N'pur-apply'), (1616, N'pur-apply');

IF EXISTS (SELECT 1 FROM #Family f
           WHERE NOT EXISTS (SELECT 1 FROM dbo.MODULES m WHERE m.M_IDX = f.MODULE_ID
                               AND LTRIM(RTRIM(ISNULL(m.MASTER_TABLE, ''))) = N'PUR_APPLY_M'
                               AND LTRIM(RTRIM(ISNULL(m.DETAIL_TABLE, ''))) = N'PUR_APPLY_D'))
    THROW 50001, N'请购单族模块形态不符（应为 PUR_APPLY_M / PUR_APPLY_D），迁移中止。', 1;

IF EXISTS (SELECT 1 FROM #Family f
           WHERE NOT EXISTS (SELECT 1 FROM dbo.MODULE_VALIDATION_RULE r
                             WHERE r.MODULE_ID = f.MODULE_ID AND r.STAGE = N'SAVE' AND r.ENABLED = 1
                               AND r.VALIDATION_KEY = N'reference-exists'))
    THROW 50002, N'请购单族缺少既有的 SAVE 期产品编号引用校验，迁移中止（先核对配置）。', 1;

IF EXISTS (SELECT 1 FROM dbo.MODULE_BUSINESS_ACTION a
           JOIN #Family f ON f.MODULE_ID = a.MODULE_ID
           WHERE a.EVENT_CODE = N'SAVE')
    THROW 50003, N'请购单族已有 SAVE 期业务动作配置，迁移中止（先核对配置）。', 1;

/* ① 播种 SAVE 期写动作 */
DECLARE @EffectParam NVARCHAR(MAX) =
    N'{"master":{"table":"PUR_APPLY_M","typeField":"APPLY_TYPE","noField":"APPLY_NO",'
    + N'"orderNoField":"ORDER_NO","produceNoField":"PRODUCE_NO"},'
    + N'"detail":{"table":"PUR_APPLY_D","productField":"PRO_NO","serialField":"SERIAL_NO",'
    + N'"requireQtyField":"REQUIRE_QTY","lostQtyField":"LOST_QTY","qtyField":"QTY","depotField":"DEPOT_ID",'
    + N'"unitField":"UNIT_ID","orderTypeField":"ORDER_TYPE","orderNoField":"ORDER_NO",'
    + N'"orderSerialField":"ORDER_SERIAL_NO","clientProNoField":"CLIENT_PRO_NO",'
    + N'"clientOrderNoField":"CLIENT_ORDER_NO","usedDateField":"USED_DATE","clientField":"CLIENT_ID"},'
    + N'"more":{"table":"PUR_APPLY_MORE","serialField":"SERIAL_NO","productField":"PRO_NO",'
    + N'"requireQtyField":"REQUIRE_QTY","lostQtyField":"LOST_QTY","qtyField":"QTY","orderTypeField":"ORDER_TYPE",'
    + N'"orderNoField":"ORDER_NO","orderSerialField":"ORDER_SERIAL_NO","produceNoField":"PRODUCE_NO"},'
    + N'"product":{"table":"PRODUCT","productField":"PRO_NO","depotField":"DEPOT_ID","unitField":"UNIT_ID"},'
    + N'"orderMaster":{"table":"COP_ORDER_M","typeField":"ORDER_TYPE","noField":"ORDER_NO","clientField":"CLIENT_ID"},'
    + N'"orderDetail":{"table":"COP_ORDER_D","typeField":"ORDER_TYPE","noField":"ORDER_NO","serialField":"SERIAL_NO",'
    + N'"clientProNoField":"CLIENT_PRO_NO","clientOrderNoField":"CLIENT_ORDER_NO","preSendDateField":"PRE_SEND_DATE"}}';

MERGE dbo.MODULE_BUSINESS_ACTION AS T
USING (SELECT MODULE_ID FROM #Family) AS S
   ON T.MODULE_ID = S.MODULE_ID AND T.EVENT_CODE = N'SAVE' AND T.SEQ = 1
WHEN MATCHED THEN
    UPDATE SET T.EFFECT_KEY = N'pur-apply-sync', T.EFFECT_NAME = N'请购单待购表汇总与申购数量分配（保存期）',
               T.ENABLED = 1, T.FAIL_MODE = N'BLOCK', T.PARAM_STRUCT = @EffectParam,
               T.REVERSE_STRUCT = N'{"kind":"none"}',
               T.REMARK = N'原 C# 请购单保存后动作的忠实移植（明细补行、应购/损耗回填、订单字段回填、申购数量分配、主表单号串联）',
               T.LAST_UPDATE_BY = N'DbUp', T.LAST_UPDATE_DATE = SYSDATETIME()
WHEN NOT MATCHED THEN
    INSERT (MODULE_ID, EVENT_CODE, SEQ, EFFECT_KEY, EFFECT_NAME, ENABLED, FAIL_MODE, CONDITION_STRUCT,
            PARAM_STRUCT, REVERSE_STRUCT, REMARK, SOURCE_REF, CREATE_PERSON, CREATE_DATE, LAST_UPDATE_BY, LAST_UPDATE_DATE)
    VALUES (S.MODULE_ID, N'SAVE', 1, N'pur-apply-sync', N'请购单待购表汇总与申购数量分配（保存期）', 1, N'BLOCK', NULL,
            @EffectParam, N'{"kind":"none"}',
            N'原 C# 请购单保存后动作的忠实移植（明细补行、应购/损耗回填、订单字段回填、申购数量分配、主表单号串联）',
            N'pur-apply', N'DbUp', SYSDATETIME(), N'DbUp', SYSDATETIME());

/* ② 置空已发布快照里的族名 */
UPDATE S
   SET S.DEFINITION_JSON = REPLACE(S.DEFINITION_JSON, N'"DomainRule":"' + F.FAMILY + N'"', N'"DomainRule":null')
 FROM dbo.WORKBENCH_DEFINITION_SNAPSHOT S
 JOIN #Family F ON F.MODULE_ID = S.MODULE_ID
WHERE S.IS_CURRENT = 1
  AND S.DEFINITION_JSON LIKE N'%"DomainRule":"' + F.FAMILY + N'"%';

/* ③ 收口断言 */
IF EXISTS (SELECT 1 FROM dbo.WORKBENCH_DEFINITION_SNAPSHOT S JOIN #Family F ON F.MODULE_ID = S.MODULE_ID
           WHERE S.IS_CURRENT = 1 AND S.DEFINITION_JSON LIKE N'%"DomainRule":"' + F.FAMILY + N'"%')
    THROW 50004, N'当前快照仍残留已退役的族名，迁移中止。', 1;

IF EXISTS (SELECT 1 FROM #Family F
           WHERE NOT EXISTS (SELECT 1 FROM dbo.MODULE_BUSINESS_ACTION A
                             WHERE A.MODULE_ID = F.MODULE_ID AND A.EVENT_CODE = N'SAVE' AND A.SEQ = 1
                               AND A.EFFECT_KEY = N'pur-apply-sync' AND A.ENABLED = 1))
    THROW 50005, N'退役 C# 后缺少启用的 SAVE 期 pur-apply-sync 动作，迁移中止。', 1;

IF OBJECT_ID(N'tempdb..#Family') IS NOT NULL DROP TABLE #Family;

PRINT N'== pur-apply 入效果目录并退役 C# 完成（1615/1616，SAVE 期 pur-apply-sync；快照族名已置空）==';
