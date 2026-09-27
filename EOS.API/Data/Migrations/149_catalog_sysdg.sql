-- ============================================================================
-- EOS.ERP migration 150: 用户组权限入效果目录并退役 C#（sysdg：2305）
-- ----------------------------------------------------------------------------
-- C# 侧唯一动作（`SysDomainRules.SysdgAfterSaveAsync`）是**孤儿权限行清理**：
--     DELETE FROM dbo.SYSDH        WHERE M_IDX NOT IN (SELECT M_IDX FROM dbo.MODULES);
--     DELETE FROM dbo.SYSDH_REPORT WHERE M_IDX NOT IN (SELECT M_IDX FROM dbo.MODULES)
--                                     OR REPORT_ID NOT IN (SELECT REPORT_ID FROM dbo.REPORT);
-- 该形态与具体单据无关、可复用，故新增**通用服务处理器** `orphan-cleanup`（效果目录新键）：
--   params.targets = [{table, column, refTable, refColumn}, …]，逐条执行
--   `DELETE FROM 目标表 WHERE 目标列 NOT IN (SELECT 参照列 FROM 参照表)`——与既有实现逐字同口径
--   （含 NULL 时不误删）；四个标识符必须都是物理列（fail-closed）。删除不可逆，反向 kind=none。
-- **本迁移把 2305 接进效果引擎**（`EFFECT_ENGINE_TAG` 0 → 1）：该模块此前未接管、且目录里
-- 既无校验规则也无业务动作，接管后保存链＝零校验（空规则集）+ 一条清理动作，行为等价。
-- 为免误激活未知配置，迁移先断言"该模块无任何校验规则、无任何业务动作"，不符即中止。
-- 幂等：按 模块 + SAVE + SEQ 合并；快照族名仅当仍含 `sysdg` 时改写，改写后复核。
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

DECLARE @ModuleId INT = 2305;
DECLARE @Family NVARCHAR(40) = N'sysdg';

IF NOT EXISTS (SELECT 1 FROM dbo.MODULES m WHERE m.M_IDX = @ModuleId
                 AND LTRIM(RTRIM(ISNULL(m.MASTER_TABLE, ''))) = N'SYSDG')
    THROW 50001, N'模块 2305 形态不符（主表应为 SYSDG），迁移中止。', 1;

IF (SELECT COUNT(*) FROM dbo.MODULE_VALIDATION_RULE WHERE MODULE_ID = @ModuleId) <> 0
    THROW 50002, N'模块 2305 已有校验规则，迁移中止（先核对配置）。', 1;

IF (SELECT COUNT(*) FROM dbo.MODULE_BUSINESS_ACTION WHERE MODULE_ID = @ModuleId) <> 0
    THROW 50003, N'模块 2305 已有业务动作配置，迁移中止（先核对配置）。', 1;

/* ① 接管：打开效果引擎 */
UPDATE dbo.MODULES SET EFFECT_ENGINE_TAG = 1, LAST_UPDATE_BY = N'DbUp', LAST_UPDATE_DATE = SYSDATETIME()
WHERE M_IDX = @ModuleId AND ISNULL(EFFECT_ENGINE_TAG, 0) = 0;

/* ② 播种 SAVE 期孤儿清理动作 */
DECLARE @Params NVARCHAR(MAX) =
    N'{"targets":[' 
    + N'{"table":"SYSDH","column":"M_IDX","refTable":"MODULES","refColumn":"M_IDX"},'
    + N'{"table":"SYSDH_REPORT","column":"M_IDX","refTable":"MODULES","refColumn":"M_IDX"},'
    + N'{"table":"SYSDH_REPORT","column":"REPORT_ID","refTable":"REPORT","refColumn":"REPORT_ID"}]}';

MERGE dbo.MODULE_BUSINESS_ACTION AS T
USING (SELECT @ModuleId AS MODULE_ID) AS S
   ON T.MODULE_ID = S.MODULE_ID AND T.EVENT_CODE = N'SAVE' AND T.SEQ = 1
WHEN MATCHED THEN
    UPDATE SET T.EFFECT_KEY = N'orphan-cleanup', T.EFFECT_NAME = N'孤儿权限行清理（保存期）',
               T.ENABLED = 1, T.FAIL_MODE = N'BLOCK', T.PARAM_STRUCT = @Params,
               T.REVERSE_STRUCT = N'{"kind":"none"}',
               T.REMARK = N'原 C# 用户组保存后动作的忠实移植（清理指向已删模块/报表的权限行）',
               T.LAST_UPDATE_BY = N'DbUp', T.LAST_UPDATE_DATE = SYSDATETIME()
WHEN NOT MATCHED THEN
    INSERT (MODULE_ID, EVENT_CODE, SEQ, EFFECT_KEY, EFFECT_NAME, ENABLED, FAIL_MODE, CONDITION_STRUCT,
            PARAM_STRUCT, REVERSE_STRUCT, REMARK, SOURCE_REF, CREATE_PERSON, CREATE_DATE, LAST_UPDATE_BY, LAST_UPDATE_DATE)
    VALUES (S.MODULE_ID, N'SAVE', 1, N'orphan-cleanup', N'孤儿权限行清理（保存期）', 1, N'BLOCK', NULL,
            @Params, N'{"kind":"none"}',
            N'原 C# 用户组保存后动作的忠实移植（清理指向已删模块/报表的权限行）', @Family,
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
                 AND A.EFFECT_KEY = N'orphan-cleanup' AND A.ENABLED = 1)
    THROW 50005, N'退役 C# 后缺少启用的 SAVE 期 orphan-cleanup 动作，迁移中止。', 1;

IF NOT EXISTS (SELECT 1 FROM dbo.MODULES WHERE M_IDX = @ModuleId AND ISNULL(EFFECT_ENGINE_TAG, 0) = 1)
    THROW 50006, N'模块 2305 未接管进效果引擎（EFFECT_ENGINE_TAG 仍为 0），迁移中止。', 1;

PRINT N'== sysdg 入效果目录并退役 C# 完成（2305，SAVE 期 orphan-cleanup；快照族名已置空）==';
