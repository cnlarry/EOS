-- ============================================================================
-- EOS.ERP migration 152: 员工发卡入目录并退役 C#（employee-card：180208）
-- ----------------------------------------------------------------------------
-- C# 侧两条语义（`HrDomainRules.EmployeeCardAfterSaveAsync` + `CloseConflictingCardsAsync`）：
--   ① 校验：失效日期不得早于生效日期（旧文案「失于日期应在生效日期后」，可空日期比较为 false ⇒ 空值不违规）；
--   ② 写入：作废冲突旧卡——同一卡号下的其他持卡人、同员工名下其他卡，到期日置为本次生效日前一天
--      （仅当旧卡未填到期日或不早于本次生效日）。
-- 承接方式：
--   · ① 用 `line-require` 的**主表行断言**（`scope=MASTER` + `assert`）表达；
--     `assert` 新增 **`compareField`** 形态（与同行另一列比较，任一为空不违规——复刻旧语义），
--     与既有"数值 value"形态二选一（注册表 fail-closed）。
--   · ② 用新增服务处理器 **`card-sibling-close`**（闭合参数：表 + 生效日/到期日/卡号列/员工列 + 偏移天数，
--     后两列必须是本模块主键列）。该处理器是**唯一实现**：批量发卡 `/jobs/card-batch` 也改调它的同一静态核心
--     （原来那份 `HrDomainRules.CloseConflictingCardsAsync` 已删除，避免同一语义两处实现）。
-- **本迁移把 180208 接进效果引擎**（`EFFECT_ENGINE_TAG` 0 → 1）：该模块此前未接管、目录里既无校验规则
-- 也无业务动作，接管后保存链＝本条校验 + 本项动作，与旧 C# 行为对齐。守卫断言接管前为空配置。
-- 幂等：规则/动作按 模块+SAVE+KEY+SEQ 合并；快照族名仅当仍含 `employee-card` 时改写，改写后复核。
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

DECLARE @ModuleId INT = 180208;
DECLARE @Family NVARCHAR(40) = N'employee-card';

IF NOT EXISTS (SELECT 1 FROM dbo.MODULES m WHERE m.M_IDX = @ModuleId
                 AND LTRIM(RTRIM(ISNULL(m.MASTER_TABLE, ''))) = N'HR_EMPLOYEE_CARD')
    THROW 50001, N'模块 180208 形态不符（主表应为 HR_EMPLOYEE_CARD），迁移中止。', 1;

IF (SELECT COUNT(*) FROM dbo.MODULE_VALIDATION_RULE WHERE MODULE_ID = @ModuleId) <> 0
    THROW 50002, N'模块 180208 已有校验规则，迁移中止（先核对配置）。', 1;

IF (SELECT COUNT(*) FROM dbo.MODULE_BUSINESS_ACTION WHERE MODULE_ID = @ModuleId) <> 0
    THROW 50003, N'模块 180208 已有业务动作配置，迁移中止（先核对配置）。', 1;

/* ① 接管：打开效果引擎 */
UPDATE dbo.MODULES SET EFFECT_ENGINE_TAG = 1, LAST_UPDATE_BY = N'DbUp', LAST_UPDATE_DATE = SYSDATETIME()
WHERE M_IDX = @ModuleId AND ISNULL(EFFECT_ENGINE_TAG, 0) = 0;

/* ② 播种校验：失效日期不得早于生效日期（主表行断言，空值不违规） */
DECLARE @CardCheckParam NVARCHAR(MAX) =
    N'{"checks":[{"scope":"MASTER","field":"END_DATE","assert":{"op":"GE","compareField":"BEGIN_DATE"}}]}';
DECLARE @CardCheckMessage NVARCHAR(200) = N'失于日期应在生效日期后';

MERGE dbo.MODULE_VALIDATION_RULE AS T
USING (SELECT @ModuleId AS MODULE_ID) AS S
   ON T.MODULE_ID = S.MODULE_ID AND T.STAGE = N'SAVE' AND T.VALIDATION_KEY = N'line-require' AND T.SEQ = 1
WHEN MATCHED THEN
    UPDATE SET T.PARAM_STRUCT = @CardCheckParam, T.MESSAGE = @CardCheckMessage, T.ENABLED = 1,
               T.REMARK = N'失效日期不得早于生效日期（保存期主表行断言；原 C# 员工发卡判据的忠实移植）',
               T.LAST_UPDATE_BY = N'DbUp', T.LAST_UPDATE_DATE = SYSDATETIME()
WHEN NOT MATCHED THEN
    INSERT (MODULE_ID, STAGE, SEQ, VALIDATION_KEY, ENABLED, PARAM_STRUCT, MESSAGE, REMARK, SOURCE_REF,
            CREATE_PERSON, CREATE_DATE, LAST_UPDATE_BY, LAST_UPDATE_DATE)
    VALUES (S.MODULE_ID, N'SAVE', 1, N'line-require', 1, @CardCheckParam, @CardCheckMessage,
            N'失效日期不得早于生效日期（保存期主表行断言；原 C# 员工发卡判据的忠实移植）', @Family,
            N'DbUp', SYSDATETIME(), N'DbUp', SYSDATETIME());

/* ③ 播种动作：作废冲突旧卡 */
DECLARE @CardActionParam NVARCHAR(MAX) =
    N'{"table":"HR_EMPLOYEE_CARD","beginField":"BEGIN_DATE","endField":"END_DATE",'
    + N'"keyField":"CARD_ID","ownerField":"EMP_ID","offsetDays":-1}';

MERGE dbo.MODULE_BUSINESS_ACTION AS T
USING (SELECT @ModuleId AS MODULE_ID) AS S
   ON T.MODULE_ID = S.MODULE_ID AND T.EVENT_CODE = N'SAVE' AND T.SEQ = 1
WHEN MATCHED THEN
    UPDATE SET T.EFFECT_KEY = N'card-sibling-close', T.EFFECT_NAME = N'作废冲突旧卡（保存期）',
               T.ENABLED = 1, T.FAIL_MODE = N'BLOCK', T.PARAM_STRUCT = @CardActionParam,
               T.REVERSE_STRUCT = N'{"kind":"none"}',
               T.REMARK = N'原 C# 员工发卡保存后动作的忠实移植（同一卡号/同一员工的其它卡到期日收口）',
               T.LAST_UPDATE_BY = N'DbUp', T.LAST_UPDATE_DATE = SYSDATETIME()
WHEN NOT MATCHED THEN
    INSERT (MODULE_ID, EVENT_CODE, SEQ, EFFECT_KEY, EFFECT_NAME, ENABLED, FAIL_MODE, CONDITION_STRUCT,
            PARAM_STRUCT, REVERSE_STRUCT, REMARK, SOURCE_REF, CREATE_PERSON, CREATE_DATE, LAST_UPDATE_BY, LAST_UPDATE_DATE)
    VALUES (S.MODULE_ID, N'SAVE', 1, N'card-sibling-close', N'作废冲突旧卡（保存期）', 1, N'BLOCK', NULL,
            @CardActionParam, N'{"kind":"none"}',
            N'原 C# 员工发卡保存后动作的忠实移植（同一卡号/同一员工的其它卡到期日收口）', @Family,
            N'DbUp', SYSDATETIME(), N'DbUp', SYSDATETIME());

/* ④ 置空已发布快照里的族名 */
UPDATE S
   SET S.DEFINITION_JSON = REPLACE(S.DEFINITION_JSON, N'"DomainRule":"' + @Family + N'"', N'"DomainRule":null')
FROM dbo.WORKBENCH_DEFINITION_SNAPSHOT S
WHERE S.MODULE_ID = @ModuleId AND S.IS_CURRENT = 1
  AND S.DEFINITION_JSON LIKE N'%"DomainRule":"' + @Family + N'"%';

/* ⑤ 收口断言 */
IF EXISTS (SELECT 1 FROM dbo.WORKBENCH_DEFINITION_SNAPSHOT S
           WHERE S.MODULE_ID = @ModuleId AND S.IS_CURRENT = 1
             AND S.DEFINITION_JSON LIKE N'%"DomainRule":"' + @Family + N'"%')
    THROW 50004, N'当前快照仍残留已退役的族名，迁移中止。', 1;

IF NOT EXISTS (SELECT 1 FROM dbo.MODULE_VALIDATION_RULE R
               WHERE R.MODULE_ID = @ModuleId AND R.STAGE = N'SAVE' AND R.ENABLED = 1
                 AND R.VALIDATION_KEY = N'line-require')
    THROW 50005, N'退役 C# 后缺少启用的 SAVE 期 line-require 校验，迁移中止。', 1;

IF NOT EXISTS (SELECT 1 FROM dbo.MODULE_BUSINESS_ACTION A
               WHERE A.MODULE_ID = @ModuleId AND A.EVENT_CODE = N'SAVE' AND A.SEQ = 1
                 AND A.EFFECT_KEY = N'card-sibling-close' AND A.ENABLED = 1)
    THROW 50006, N'退役 C# 后缺少启用的 SAVE 期 card-sibling-close 动作，迁移中止。', 1;

IF NOT EXISTS (SELECT 1 FROM dbo.MODULES WHERE M_IDX = @ModuleId AND ISNULL(EFFECT_ENGINE_TAG, 0) = 1)
    THROW 50007, N'模块 180208 未接管进效果引擎（EFFECT_ENGINE_TAG 仍为 0），迁移中止。', 1;

PRINT N'== employee-card 入目录并退役 C# 完成（180208：主表行断言 + SAVE 期 card-sibling-close；快照族名已置空）==';
