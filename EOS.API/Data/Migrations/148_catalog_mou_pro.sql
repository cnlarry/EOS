-- ============================================================================
-- EOS.ERP migration 149: 产品模具对照入效果目录并退役 C#（mou-pro：2911）
-- ----------------------------------------------------------------------------
-- C# 侧唯一动作（`MouDomainRules.MouProAfterSaveAsync`）是按产品回写"所用模具汇总"：
--     UPDATE dbo.MOU_PRO_M SET MOULD_IDS = dbo.f_get_pro_moulds(PRO_NO) WHERE PRO_NO=@ProNo
-- 汇总口径来自一个**标量函数**：既不是"从某列取值"（`link-stamp`/`field-copy` 覆盖不了），
-- 也不该让配置直接写函数名（等于把任意表达式带进执行期）。故新增**专用服务处理器**
-- `mould-ids-sync`（效果目录新键）：配置只能给 `table/keyField/valueField` 三个闭合参数，
-- 函数名固定在服务端，且 `keyField` 必须等于本模块主键首列（禁止回写他表）。
-- 处理期校验：列必须物理存在、函数 `dbo.f_get_pro_moulds` 必须存在（fail-closed）。
-- **本迁移同时把 2911 接进效果引擎**（`EFFECT_ENGINE_TAG` 0 → 1）：该模块此前未接管，C# 只做回写、
-- 目录里那条 `reference-exists`（产品编号 / 模具编号存在性）一直**休眠**；接管后它随保存生效
-- （这正是 "校验进目录并真正执行"的目标）。为免误激活未知配置，迁移先断言
-- "该模块的 SAVE 配置恰为 1 条 reference-exists + 0 条动作"，不符合即中止。
-- 本迁移为该模块播种 SAVE 期动作并置空快照族名。
-- 幂等：按 模块 + SAVE + SEQ 合并；快照族名仅当仍含 `mou-pro` 时改写，改写后复核。
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

DECLARE @ModuleId INT = 2911;
DECLARE @Family NVARCHAR(40) = N'mou-pro';

/* 守卫：模块存在、主表与汇总函数符合形态 */
IF NOT EXISTS (SELECT 1 FROM dbo.MODULES m WHERE m.M_IDX = @ModuleId
                 AND LTRIM(RTRIM(ISNULL(m.MASTER_TABLE, ''))) = N'MOU_PRO_M')
    THROW 50001, N'模块 2911 形态不符（主表应为 MOU_PRO_M），迁移中止。', 1;

IF OBJECT_ID(N'dbo.f_get_pro_moulds', N'FN') IS NULL
    THROW 50002, N'汇总函数 dbo.f_get_pro_moulds 不存在，迁移中止。', 1;

/* 守卫：接管前该模块的 SAVE 配置必须恰为"1 条 reference-exists + 0 条动作"，
   避免把未知/额外的配置一并激活（本迁移只新增 mould-ids-sync 一条 SAVE 动作）。 */
IF (SELECT COUNT(*) FROM dbo.MODULE_VALIDATION_RULE WHERE MODULE_ID = @ModuleId AND STAGE = N'SAVE'
      AND VALIDATION_KEY = N'reference-exists' AND ENABLED = 1) <> 1
   OR (SELECT COUNT(*) FROM dbo.MODULE_VALIDATION_RULE WHERE MODULE_ID = @ModuleId) <> 1
    THROW 50005, N'模块 2911 的校验规则不是预期的"仅 1 条 reference-exists"，迁移中止（先核对配置）。', 1;

IF (SELECT COUNT(*) FROM dbo.MODULE_BUSINESS_ACTION WHERE MODULE_ID = @ModuleId) <> 0
    THROW 50006, N'模块 2911 已有业务动作配置，迁移中止（先核对配置）。', 1;

/* ① 接管：打开效果引擎（接管后目录校验与动作随保存生效） */
UPDATE dbo.MODULES SET EFFECT_ENGINE_TAG = 1, LAST_UPDATE_BY = N'DbUp', LAST_UPDATE_DATE = SYSDATETIME()
WHERE M_IDX = @ModuleId AND ISNULL(EFFECT_ENGINE_TAG, 0) = 0;

/* ① 播种 SAVE 期回写动作 */
DECLARE @Params NVARCHAR(MAX) =
    N'{"table":"MOU_PRO_M","keyField":"PRO_NO","valueField":"MOULD_IDS"}';

MERGE dbo.MODULE_BUSINESS_ACTION AS T
USING (SELECT @ModuleId AS MODULE_ID) AS S
   ON T.MODULE_ID = S.MODULE_ID AND T.EVENT_CODE = N'SAVE' AND T.SEQ = 1
WHEN MATCHED THEN
    UPDATE SET T.EFFECT_KEY = N'mould-ids-sync', T.EFFECT_NAME = N'所用模具汇总回写（保存期）',
               T.ENABLED = 1, T.FAIL_MODE = N'BLOCK', T.PARAM_STRUCT = @Params,
               T.REVERSE_STRUCT = N'{"kind":"none"}',
               T.REMARK = N'原 C# 产品模具对照保存后动作的忠实移植（按产品回写 f_get_pro_moulds 汇总）',
               T.LAST_UPDATE_BY = N'DbUp', T.LAST_UPDATE_DATE = SYSDATETIME()
WHEN NOT MATCHED THEN
    INSERT (MODULE_ID, EVENT_CODE, SEQ, EFFECT_KEY, EFFECT_NAME, ENABLED, FAIL_MODE, CONDITION_STRUCT,
            PARAM_STRUCT, REVERSE_STRUCT, REMARK, SOURCE_REF, CREATE_PERSON, CREATE_DATE, LAST_UPDATE_BY, LAST_UPDATE_DATE)
    VALUES (S.MODULE_ID, N'SAVE', 1, N'mould-ids-sync', N'所用模具汇总回写（保存期）', 1, N'BLOCK', NULL,
            @Params, N'{"kind":"none"}',
            N'原 C# 产品模具对照保存后动作的忠实移植（按产品回写 f_get_pro_moulds 汇总）', @Family,
            N'DbUp', SYSDATETIME(), N'DbUp', SYSDATETIME());

/* ② 置空已发布快照里的族名 */
UPDATE S
   SET S.DEFINITION_JSON = REPLACE(S.DEFINITION_JSON, N'"DomainRule":"' + @Family + N'"', N'"DomainRule":null')
FROM dbo.WORKBENCH_DEFINITION_SNAPSHOT S
WHERE S.MODULE_ID = @ModuleId AND S.IS_CURRENT = 1
  AND S.DEFINITION_JSON LIKE N'%"DomainRule":"' + @Family + N'"%';

/* ③ 收口断言 */
IF EXISTS (SELECT 1 FROM dbo.WORKBENCH_DEFINITION_SNAPSHOT S
           WHERE S.MODULE_ID = @ModuleId AND S.IS_CURRENT = 1
             AND S.DEFINITION_JSON LIKE N'%"DomainRule":"' + @Family + N'"%')
    THROW 50003, N'当前快照仍残留已退役的族名，迁移中止。', 1;

IF NOT EXISTS (SELECT 1 FROM dbo.MODULE_BUSINESS_ACTION A
               WHERE A.MODULE_ID = @ModuleId AND A.EVENT_CODE = N'SAVE' AND A.SEQ = 1
                 AND A.EFFECT_KEY = N'mould-ids-sync' AND A.ENABLED = 1)
    THROW 50004, N'退役 C# 后缺少启用的 SAVE 期 mould-ids-sync 动作，迁移中止。', 1;

IF NOT EXISTS (SELECT 1 FROM dbo.MODULES WHERE M_IDX = @ModuleId AND ISNULL(EFFECT_ENGINE_TAG, 0) = 1)
    THROW 50007, N'模块 2911 未接管进效果引擎（EFFECT_ENGINE_TAG 仍为 0），迁移中止。', 1;

PRINT N'== mou-pro 入效果目录并退役 C# 完成（2911，SAVE 期 mould-ids-sync；快照族名已置空）==';
