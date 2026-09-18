-- ============================================================================
-- EOS.ERP migration 156: 工单 BOM 入效果目录并退役 C#（moc-bom-stru：1506）
-- ----------------------------------------------------------------------------
-- C# 侧是一个**单据内的孤儿清理循环**（`MocDomainRules.MocBomStruAfterSaveAsync`）：
--   while(true):
--     删主表 m：本单内、产品既不是"本行产品"也不是"制令主产品"、且没有任何明细引用该产品；
--     删明细 d：本单内、产品在主表中已无对应行；
--     一轮下来两表都无删除即结束。
-- 承接：新增服务处理器 **`doc-orphan-prune`**（主/明细表 + 六个列名 + 制令根表与根列，
-- 全闭合参数并逐个校验为物理列；"本行产品"取模块主键第三列，循环上限 100 轮 fail-closed）。
-- **本迁移把 1506 接进效果引擎**（`EFFECT_ENGINE_TAG` 0 → 1）：该模块此前未接管、目录里既无校验
-- 规则也无业务动作；守卫断言接管前为空配置。
-- 幂等：动作按 模块+SAVE+SEQ 合并；快照族名仅当仍含 `moc-bom-stru` 时改写，改写后复核。
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

DECLARE @ModuleId INT = 1506;
DECLARE @Family NVARCHAR(40) = N'moc-bom-stru';

IF NOT EXISTS (SELECT 1 FROM dbo.MODULES m WHERE m.M_IDX = @ModuleId
                 AND LTRIM(RTRIM(ISNULL(m.MASTER_TABLE, ''))) = N'MOC_BOM_STRU_M'
                 AND LTRIM(RTRIM(ISNULL(m.DETAIL_TABLE, ''))) = N'MOC_BOM_STRU_D')
    THROW 50001, N'模块 1506 形态不符（应为 MOC_BOM_STRU_M / MOC_BOM_STRU_D），迁移中止。', 1;

IF (SELECT COUNT(*) FROM dbo.MODULE_VALIDATION_RULE WHERE MODULE_ID = @ModuleId) <> 0
    THROW 50002, N'模块 1506 已有校验规则，迁移中止（先核对配置）。', 1;

IF (SELECT COUNT(*) FROM dbo.MODULE_BUSINESS_ACTION WHERE MODULE_ID = @ModuleId) <> 0
    THROW 50003, N'模块 1506 已有业务动作配置，迁移中止（先核对配置）。', 1;

/* ① 接管 */
UPDATE dbo.MODULES SET EFFECT_ENGINE_TAG = 1, LAST_UPDATE_BY = N'DbUp', LAST_UPDATE_DATE = SYSDATETIME()
WHERE M_IDX = @ModuleId AND ISNULL(EFFECT_ENGINE_TAG, 0) = 0;

/* ② 播种 SAVE 期动作 */
DECLARE @Params NVARCHAR(MAX) =
    N'{"masterTable":"MOC_BOM_STRU_M","detailTable":"MOC_BOM_STRU_D","typeField":"PRODUCE_TYPE",'
    + N'"noField":"PRODUCE_NO","masterProductField":"PRO_NO","detailProductField":"PRO_NO",'
    + N'"detailRefField":"ELEMENT_PRO_NO","rootTable":"MOC_PRODUCE_M","rootField":"PRO_NO"}';

MERGE dbo.MODULE_BUSINESS_ACTION AS T
USING (SELECT @ModuleId AS MODULE_ID) AS S
   ON T.MODULE_ID = S.MODULE_ID AND T.EVENT_CODE = N'SAVE' AND T.SEQ = 1
WHEN MATCHED THEN
    UPDATE SET T.EFFECT_KEY = N'doc-orphan-prune', T.EFFECT_NAME = N'主/明细孤儿行清理（保存期）',
               T.ENABLED = 1, T.FAIL_MODE = N'BLOCK', T.PARAM_STRUCT = @Params,
               T.REVERSE_STRUCT = N'{"kind":"none"}',
               T.REMARK = N'原 C# 工单 BOM 保存后动作的忠实移植（循环清理互不引用的主/明细行）',
               T.LAST_UPDATE_BY = N'DbUp', T.LAST_UPDATE_DATE = SYSDATETIME()
WHEN NOT MATCHED THEN
    INSERT (MODULE_ID, EVENT_CODE, SEQ, EFFECT_KEY, EFFECT_NAME, ENABLED, FAIL_MODE, CONDITION_STRUCT,
            PARAM_STRUCT, REVERSE_STRUCT, REMARK, SOURCE_REF, CREATE_PERSON, CREATE_DATE, LAST_UPDATE_BY, LAST_UPDATE_DATE)
    VALUES (S.MODULE_ID, N'SAVE', 1, N'doc-orphan-prune', N'主/明细孤儿行清理（保存期）', 1, N'BLOCK', NULL,
            @Params, N'{"kind":"none"}',
            N'原 C# 工单 BOM 保存后动作的忠实移植（循环清理互不引用的主/明细行）', @Family,
            N'DbUp', SYSDATETIME(), N'DbUp', SYSDATETIME());

/* ③ 置空已发布快照里的族名 */
UPDATE S
   SET S.DEFINITION_JSON = REPLACE(S.DEFINITION_JSON, N'"DomainRule":"' + @Family + N'"', N'"DomainRule":null')
FROM dbo.WORKBENCH_DEFINITION_SNAPSHOT S
WHERE S.MODULE_ID = @ModuleId AND S.IS_CURRENT = 1
  AND S.DEFINITION_JSON LIKE N'%"DomainRule":"' + @Family + N'"%';

/* ④ 收口断言 */
IF EXISTS (SELECT 1 FROM dbo.WORKBENCH_DEFINITION_SNAPSHOT S
           WHERE S.MODULE_ID = @ModuleId AND S.IS_CURRENT = 1
             AND S.DEFINITION_JSON LIKE N'%"DomainRule":"' + @Family + N'"%')
    THROW 50004, N'当前快照仍残留已退役的族名，迁移中止。', 1;

IF NOT EXISTS (SELECT 1 FROM dbo.MODULE_BUSINESS_ACTION A
               WHERE A.MODULE_ID = @ModuleId AND A.EVENT_CODE = N'SAVE' AND A.SEQ = 1
                 AND A.EFFECT_KEY = N'doc-orphan-prune' AND A.ENABLED = 1)
    THROW 50005, N'退役 C# 后缺少启用的 SAVE 期 doc-orphan-prune 动作，迁移中止。', 1;

IF NOT EXISTS (SELECT 1 FROM dbo.MODULES WHERE M_IDX = @ModuleId AND ISNULL(EFFECT_ENGINE_TAG, 0) = 1)
    THROW 50006, N'模块 1506 未接管进效果引擎（EFFECT_ENGINE_TAG 仍为 0），迁移中止。', 1;

PRINT N'== moc-bom-stru 入效果目录并退役 C# 完成（1506，SAVE 期 doc-orphan-prune；快照族名已置空）==';
