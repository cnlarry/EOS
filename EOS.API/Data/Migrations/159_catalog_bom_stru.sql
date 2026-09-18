-- ============================================================================
-- EOS.ERP migration 160: 产品 BOM 入效果目录并退役 C#（bom-stru：1204）
-- ----------------------------------------------------------------------------
-- 旧过程 `P_BOM_STRU_After_Save`（成环检测调 `P_BOM_CHECK`）是本源，
-- C# 侧为 `SysDomainRules.BomStruAfterSaveAsync`：
--   ① 产品编号不存在 ⇒ 拒绝（文案"产品编号不存在。 "）；
--   ② 元件编号不存在 ⇒ 拒绝（回报明细序号，逐行诊断）；
--   ③ 元件底数不大于零 ⇒ 拒绝（回报明细序号，逐行诊断）；
--   ④ BOM 成环 ⇒ 拒绝（回报循环所在行的父项产品号）；
--   ⑤ 明细/校验全部通过后，把产品档案的历史长宽列复制到本单 `P_LENGTH_OLD`/`P_WIDTH_OLD`。
-- 承载方式：
--   · ① = SAVE 期 `reference-exists`（主表域，`PRO_NO` 必须存在于 `PRODUCT`）；
--   · ② = SAVE 期 `reference-exists`（明细域，`ELEMENT_PRO_NO` 必须存在于 `PRODUCT`，诊断=序号）；
--   · ③ = SAVE 期 `line-require` 的 `assert`（明细行 `BASE_QTY > 0`，诊断=序号）；
--   · ④ = **新增校验模板 `no-cycle`**（成环检测：从主表行起点值按层展开引用关系，回到起点即判循环；
--     `maxDepth` 默认 100，把旧过程"没有回到起点的环会无限展开、保存挂死"换成 fail-closed 拒绝）；
--   · ⑤ = 新增服务处理器 `bom-size-backfill`（来源表 + 定位列 + 目标/来源字段对照，全部校验为物理列）。
-- **本迁移把 1204 接进效果引擎**（`EFFECT_ENGINE_TAG` 0 → 1）：该模块此前未接管，接入后上述四条
-- 校验与写动作才开始生效（与 2911/3014 的"接管即激活"同形）。
-- 幂等：规则按 模块+SAVE+VALIDATION_KEY+SEQ 合并；动作按 模块+SAVE+SEQ 合并；接管开关仅当为 0 时置 1；
--       快照族名仅当仍含 `bom-stru` 时改写。
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

DECLARE @ModuleId INT = 1204;
DECLARE @Family NVARCHAR(40) = N'bom-stru';

IF NOT EXISTS (SELECT 1 FROM dbo.MODULES m WHERE m.M_IDX = @ModuleId
                 AND LTRIM(RTRIM(ISNULL(m.MASTER_TABLE, ''))) = N'BOM_STRU_M'
                 AND LTRIM(RTRIM(ISNULL(m.DETAIL_TABLE, ''))) = N'BOM_STRU_D')
    THROW 50001, N'模块 1204 形态不符（应为 BOM_STRU_M / BOM_STRU_D），迁移中止。', 1;

IF (SELECT COUNT(*) FROM dbo.MODULE_VALIDATION_RULE WHERE MODULE_ID = @ModuleId) <> 0
    THROW 50002, N'模块 1204 已有校验规则，迁移中止（先核对配置）。', 1;

IF EXISTS (SELECT 1 FROM dbo.MODULE_BUSINESS_ACTION WHERE MODULE_ID = @ModuleId AND EVENT_CODE = N'SAVE')
    THROW 50003, N'模块 1204 已有 SAVE 期业务动作配置，迁移中止（先核对配置）。', 1;

/* ① 产品编号存在性（主表域） */
DECLARE @ProductMessage NVARCHAR(200) = N'产品编号不存在。 ';
DECLARE @ProductParam NVARCHAR(MAX) =
    N'{"checks":[{"refTable":"PRODUCT","refKey":{"scope":"MASTER","field":"PRO_NO"},'
    + N'"message":"产品编号不存在。 "}]}';

MERGE dbo.MODULE_VALIDATION_RULE AS T
USING (SELECT @ModuleId AS MODULE_ID) AS S
   ON T.MODULE_ID = S.MODULE_ID AND T.STAGE = N'SAVE'
  AND T.VALIDATION_KEY = N'reference-exists' AND T.SEQ = 1
WHEN MATCHED THEN
    UPDATE SET T.PARAM_STRUCT = @ProductParam, T.MESSAGE = @ProductMessage, T.ENABLED = 1,
               T.REMARK = N'BOM 主表产品编号必须存在于产品档案（原 C# 判据的忠实移植）',
               T.LAST_UPDATE_BY = N'DbUp', T.LAST_UPDATE_DATE = SYSDATETIME()
WHEN NOT MATCHED THEN
    INSERT (MODULE_ID, STAGE, SEQ, VALIDATION_KEY, ENABLED, PARAM_STRUCT, MESSAGE, REMARK, SOURCE_REF,
            CREATE_PERSON, CREATE_DATE, LAST_UPDATE_BY, LAST_UPDATE_DATE)
    VALUES (S.MODULE_ID, N'SAVE', 1, N'reference-exists', 1, @ProductParam, @ProductMessage,
            N'BOM 主表产品编号必须存在于产品档案（原 C# 判据的忠实移植）', @Family,
            N'DbUp', SYSDATETIME(), N'DbUp', SYSDATETIME());

/* ② 元件编号存在性（明细域，逐行诊断）
   注意：`refKey` 是"源列与引用列**同名**"的简写（生成 `R.<列> = D.<列>`）；本族两列名不同
   （引用列 `PRODUCT.PRO_NO`、源列 `BOM_STRU_D.ELEMENT_PRO_NO`），必须用 `join` 显式给出对照。 */
DECLARE @ElementMessage NVARCHAR(300) = N'以下序号项元件编号不存在 ' + CHAR(13) + CHAR(10) + N'{ROWS}';
DECLARE @ElementParam NVARCHAR(MAX) =
    N'{"checks":[{"refTable":"PRODUCT",'
    + N'"join":[{"target":"PRO_NO","source":{"scope":"DETAIL","field":"ELEMENT_PRO_NO"}}],'
    + N'"lineField":"SERIAL_NO","maxRows":10,'
    + N'"message":"以下序号项元件编号不存在 \r\n{ROWS}"}]}';

MERGE dbo.MODULE_VALIDATION_RULE AS T
USING (SELECT @ModuleId AS MODULE_ID) AS S
   ON T.MODULE_ID = S.MODULE_ID AND T.STAGE = N'SAVE'
  AND T.VALIDATION_KEY = N'reference-exists' AND T.SEQ = 2
WHEN MATCHED THEN
    UPDATE SET T.PARAM_STRUCT = @ElementParam, T.MESSAGE = @ElementMessage, T.ENABLED = 1,
               T.REMARK = N'BOM 明细元件编号必须存在于产品档案（逐行回报序号；原 C# 判据的忠实移植）',
               T.LAST_UPDATE_BY = N'DbUp', T.LAST_UPDATE_DATE = SYSDATETIME()
WHEN NOT MATCHED THEN
    INSERT (MODULE_ID, STAGE, SEQ, VALIDATION_KEY, ENABLED, PARAM_STRUCT, MESSAGE, REMARK, SOURCE_REF,
            CREATE_PERSON, CREATE_DATE, LAST_UPDATE_BY, LAST_UPDATE_DATE)
    VALUES (S.MODULE_ID, N'SAVE', 2, N'reference-exists', 1, @ElementParam, @ElementMessage,
            N'BOM 明细元件编号必须存在于产品档案（逐行回报序号；原 C# 判据的忠实移植）', @Family,
            N'DbUp', SYSDATETIME(), N'DbUp', SYSDATETIME());

/* ③ 元件底数不得小于等于零（明细行断言，逐行诊断） */
DECLARE @BaseQtyMessage NVARCHAR(300) = N'以下序号项元件底数不能小于0 ' + CHAR(13) + CHAR(10) + N'{ROWS}';
DECLARE @BaseQtyParam NVARCHAR(MAX) =
    N'{"checks":[{"scope":"DETAIL","field":"BASE_QTY","assert":{"op":"GT","value":0,"nullSkips":true},'
    + N'"message":"以下序号项元件底数不能小于0 \r\n{ROWS}","diagnosticFields":["SERIAL_NO"]}]}';

MERGE dbo.MODULE_VALIDATION_RULE AS T
USING (SELECT @ModuleId AS MODULE_ID) AS S
   ON T.MODULE_ID = S.MODULE_ID AND T.STAGE = N'SAVE'
  AND T.VALIDATION_KEY = N'line-require' AND T.SEQ = 3
WHEN MATCHED THEN
    UPDATE SET T.PARAM_STRUCT = @BaseQtyParam, T.MESSAGE = @BaseQtyMessage, T.ENABLED = 1,
               T.REMARK = N'BOM 明细元件底数必须大于零（逐行回报序号；原 C# 判据的忠实移植）',
               T.LAST_UPDATE_BY = N'DbUp', T.LAST_UPDATE_DATE = SYSDATETIME()
WHEN NOT MATCHED THEN
    INSERT (MODULE_ID, STAGE, SEQ, VALIDATION_KEY, ENABLED, PARAM_STRUCT, MESSAGE, REMARK, SOURCE_REF,
            CREATE_PERSON, CREATE_DATE, LAST_UPDATE_BY, LAST_UPDATE_DATE)
    VALUES (S.MODULE_ID, N'SAVE', 3, N'line-require', 1, @BaseQtyParam, @BaseQtyMessage,
            N'BOM 明细元件底数必须大于零（逐行回报序号；原 C# 判据的忠实移植）', @Family,
            N'DbUp', SYSDATETIME(), N'DbUp', SYSDATETIME());

/* ④ 成环检测（新模板 no-cycle） */
DECLARE @CycleMessage NVARCHAR(300) = N'以下元件在BOM结构中循环使用 ' + CHAR(13) + CHAR(10) + N'{ROWS}';
DECLARE @CycleParam NVARCHAR(MAX) =
    N'{"checks":[{"table":"BOM_STRU_D","parentField":"PRO_NO","childField":"ELEMENT_PRO_NO",'
    + N'"start":{"scope":"MASTER","field":"PRO_NO"},"maxDepth":100,'
    + N'"message":"以下元件在BOM结构中循环使用 \r\n{ROWS}"}]}';

MERGE dbo.MODULE_VALIDATION_RULE AS T
USING (SELECT @ModuleId AS MODULE_ID) AS S
   ON T.MODULE_ID = S.MODULE_ID AND T.STAGE = N'SAVE'
  AND T.VALIDATION_KEY = N'no-cycle' AND T.SEQ = 4
WHEN MATCHED THEN
    UPDATE SET T.PARAM_STRUCT = @CycleParam, T.MESSAGE = @CycleMessage, T.ENABLED = 1,
               T.REMARK = N'BOM 不得循环引用（原 P_BOM_CHECK 的忠实移植，超深按 fail-closed 拒绝）',
               T.LAST_UPDATE_BY = N'DbUp', T.LAST_UPDATE_DATE = SYSDATETIME()
WHEN NOT MATCHED THEN
    INSERT (MODULE_ID, STAGE, SEQ, VALIDATION_KEY, ENABLED, PARAM_STRUCT, MESSAGE, REMARK, SOURCE_REF,
            CREATE_PERSON, CREATE_DATE, LAST_UPDATE_BY, LAST_UPDATE_DATE)
    VALUES (S.MODULE_ID, N'SAVE', 4, N'no-cycle', 1, @CycleParam, @CycleMessage,
            N'BOM 不得循环引用（原 P_BOM_CHECK 的忠实移植，超深按 fail-closed 拒绝）', @Family,
            N'DbUp', SYSDATETIME(), N'DbUp', SYSDATETIME());

/* ⑤ 历史长宽列回填（写动作） */
DECLARE @EffectParam NVARCHAR(MAX) =
    N'{"sourceTable":"PRODUCT","sourceKey":"PRO_NO","masterKey":"PRO_NO",'
    + N'"fields":[{"target":"P_LENGTH_OLD","source":"P_LENGTH"},{"target":"P_WIDTH_OLD","source":"P_WIDTH"}]}';

MERGE dbo.MODULE_BUSINESS_ACTION AS T
USING (SELECT @ModuleId AS MODULE_ID) AS S
   ON T.MODULE_ID = S.MODULE_ID AND T.EVENT_CODE = N'SAVE' AND T.SEQ = 1
WHEN MATCHED THEN
    UPDATE SET T.EFFECT_KEY = N'bom-size-backfill', T.EFFECT_NAME = N'BOM 历史长宽列回填（保存期）',
               T.ENABLED = 1, T.FAIL_MODE = N'BLOCK', T.PARAM_STRUCT = @EffectParam,
               T.REVERSE_STRUCT = N'{"kind":"none"}',
               T.REMARK = N'原 C# BOM 保存后动作的忠实移植（产品档案历史长宽列复制到本单旧长宽列）',
               T.LAST_UPDATE_BY = N'DbUp', T.LAST_UPDATE_DATE = SYSDATETIME()
WHEN NOT MATCHED THEN
    INSERT (MODULE_ID, EVENT_CODE, SEQ, EFFECT_KEY, EFFECT_NAME, ENABLED, FAIL_MODE, CONDITION_STRUCT,
            PARAM_STRUCT, REVERSE_STRUCT, REMARK, SOURCE_REF, CREATE_PERSON, CREATE_DATE, LAST_UPDATE_BY, LAST_UPDATE_DATE)
    VALUES (S.MODULE_ID, N'SAVE', 1, N'bom-size-backfill', N'BOM 历史长宽列回填（保存期）', 1, N'BLOCK', NULL,
            @EffectParam, N'{"kind":"none"}',
            N'原 C# BOM 保存后动作的忠实移植（产品档案历史长宽列复制到本单旧长宽列）', @Family,
            N'DbUp', SYSDATETIME(), N'DbUp', SYSDATETIME());

/* ⑥ 接进效果引擎 */
UPDATE dbo.MODULES SET EFFECT_ENGINE_TAG = 1, LAST_UPDATE_BY = N'DbUp', LAST_UPDATE_DATE = SYSDATETIME()
WHERE M_IDX = @ModuleId AND ISNULL(EFFECT_ENGINE_TAG, 0) = 0;

/* ⑦ 置空已发布快照里的族名 */
UPDATE S
   SET S.DEFINITION_JSON = REPLACE(S.DEFINITION_JSON, N'"DomainRule":"' + @Family + N'"', N'"DomainRule":null')
 FROM dbo.WORKBENCH_DEFINITION_SNAPSHOT S
WHERE S.MODULE_ID = @ModuleId AND S.IS_CURRENT = 1
  AND S.DEFINITION_JSON LIKE N'%"DomainRule":"' + @Family + N'"%';

/* ⑧ 收口断言 */
IF EXISTS (SELECT 1 FROM dbo.WORKBENCH_DEFINITION_SNAPSHOT S
           WHERE S.MODULE_ID = @ModuleId AND S.IS_CURRENT = 1
             AND S.DEFINITION_JSON LIKE N'%"DomainRule":"' + @Family + N'"%')
    THROW 50004, N'当前快照仍残留已退役的族名，迁移中止。', 1;

IF EXISTS (SELECT 1 FROM (VALUES (N'reference-exists', 1), (N'reference-exists', 2),
                                 (N'line-require', 3), (N'no-cycle', 4)) AS V(KEY_NAME, SEQ_NO)
           WHERE NOT EXISTS (SELECT 1 FROM dbo.MODULE_VALIDATION_RULE R
                             WHERE R.MODULE_ID = @ModuleId AND R.STAGE = N'SAVE' AND R.ENABLED = 1
                               AND R.VALIDATION_KEY = V.KEY_NAME AND R.SEQ = V.SEQ_NO))
    THROW 50005, N'退役 C# 后缺少启用的 SAVE 期目录规则（reference-exists/line-require/no-cycle），迁移中止。', 1;

IF NOT EXISTS (SELECT 1 FROM dbo.MODULE_BUSINESS_ACTION
               WHERE MODULE_ID = @ModuleId AND EVENT_CODE = N'SAVE' AND SEQ = 1
                 AND EFFECT_KEY = N'bom-size-backfill' AND ENABLED = 1)
    THROW 50006, N'退役 C# 后缺少启用的 SAVE 期 bom-size-backfill 动作，迁移中止。', 1;

IF NOT EXISTS (SELECT 1 FROM dbo.MODULES WHERE M_IDX = @ModuleId AND ISNULL(EFFECT_ENGINE_TAG, 0) = 1)
    THROW 50007, N'模块 1204 未接管进效果引擎（EFFECT_ENGINE_TAG 仍为 0），迁移中止。', 1;

PRINT N'== bom-stru 入效果目录并退役 C# 完成（1204，四条 SAVE 规则 + bom-size-backfill；快照族名已置空）==';
