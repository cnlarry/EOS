-- ============================================================================
-- EOS.ERP migration 155: 离职工资表入效果目录并退役 C#（hr-wage-lz：180310 / 1803101）
-- ----------------------------------------------------------------------------
-- C# 侧一对**有序步骤**（`HrDomainRules.HrWageAfterSaveAsync`）：
--   ① 取本单 COUNT_MONTH，为空则不动作；
--   ② 删除同月其它单据中、与本单员工重叠的明细行（离职补发替换同月同人旧明细）；
--   ③ 再按"同月每员工一份"判重，命中即拒绝（文案「以下人员当月工资表重复 \r\n…」）。
-- 为什么两步放进**同一个处理器**：引擎保存链固定"先校验目录、后效果动作"，而判重的正确性依赖
-- 删除已发生；拆成"校验规则 + 删除动作"会**先判重后删除**，把本应被删除化解的情形误判为重复
-- （迁移 082 记录过该假拒绝，故当时把目录实例置 ENABLED=0）。本迁移新增服务处理器
-- **`wage-month-doc-prune`**，把两步按原顺序整体承接；目录实例保持停用（避免重复判重）。
-- 两模块此前已接管（`EFFECT_ENGINE_TAG` 已为 1），故只需播动作 + 置空快照族名。
-- 幂等：动作按 模块+SAVE+SEQ 合并；快照族名仅当仍含 `hr-wage-lz` 时改写，改写后复核。
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

IF OBJECT_ID(N'tempdb..#Family') IS NOT NULL DROP TABLE #Family;
CREATE TABLE #Family (MODULE_ID INT PRIMARY KEY, FAMILY NVARCHAR(40) NOT NULL);
INSERT INTO #Family (MODULE_ID, FAMILY) VALUES (180310, N'hr-wage-lz'), (1803101, N'hr-wage-lz');

IF EXISTS (SELECT 1 FROM #Family f
           WHERE NOT EXISTS (SELECT 1 FROM dbo.MODULES m WHERE m.M_IDX = f.MODULE_ID
                               AND LTRIM(RTRIM(ISNULL(m.MASTER_TABLE, ''))) = N'HR_WAGE_M'
                               AND LTRIM(RTRIM(ISNULL(m.DETAIL_TABLE, ''))) = N'HR_WAGE_D'))
    THROW 50001, N'离职工资表模块形态不符（应为 HR_WAGE_M / HR_WAGE_D），迁移中止。', 1;

IF EXISTS (SELECT 1 FROM #Family f
           WHERE EXISTS (SELECT 1 FROM dbo.MODULE_BUSINESS_ACTION a
                         WHERE a.MODULE_ID = f.MODULE_ID AND a.EVENT_CODE = N'SAVE'))
    THROW 50002, N'离职工资表模块已有 SAVE 期业务动作配置，迁移中止（先核对配置）。', 1;

/* 守卫：既有动作只能是批核期的离职工资同步（本迁移不改批核链） */
IF EXISTS (SELECT 1 FROM #Family f
           WHERE EXISTS (SELECT 1 FROM dbo.MODULE_BUSINESS_ACTION a
                         WHERE a.MODULE_ID = f.MODULE_ID
                           AND (a.EVENT_CODE <> N'APPROVE_EFFECT' OR a.EFFECT_KEY <> N'employee-dimission-sync')))
    THROW 50007, N'离职工资表模块存在非预期的业务动作配置，迁移中止（先核对配置）。', 1;

/* 守卫：目录判重实例必须保持停用（该判重由新处理器在删除之后执行，避免重复判重/假拒绝） */
IF EXISTS (SELECT 1 FROM #Family f
           WHERE NOT EXISTS (SELECT 1 FROM dbo.MODULE_VALIDATION_RULE r
                             WHERE r.MODULE_ID = f.MODULE_ID AND r.STAGE = N'SAVE'
                               AND r.VALIDATION_KEY = N'duplicate-check' AND r.ENABLED = 0))
    THROW 50003, N'离职工资表的目录判重实例不是"存在且停用"的状态，迁移中止（先核对配置）。', 1;

/* ① 播种 SAVE 期动作 */
DECLARE @Params NVARCHAR(MAX) =
    N'{"masterTable":"HR_WAGE_M","detailTable":"HR_WAGE_D","typeField":"WAGE_TYPE","noField":"WAGE_NO",'
    + N'"monthField":"COUNT_MONTH","empField":"EMP_ID","duplicateMessage":"以下人员当月工资表重复 \r\n"}';

MERGE dbo.MODULE_BUSINESS_ACTION AS T
USING (SELECT MODULE_ID FROM #Family) AS S
   ON T.MODULE_ID = S.MODULE_ID AND T.EVENT_CODE = N'SAVE' AND T.SEQ = 1
WHEN MATCHED THEN
    UPDATE SET T.EFFECT_KEY = N'wage-month-doc-prune', T.EFFECT_NAME = N'同月旧档清理与每人每月判重（保存期）',
               T.ENABLED = 1, T.FAIL_MODE = N'BLOCK', T.PARAM_STRUCT = @Params,
               T.REVERSE_STRUCT = N'{"kind":"none"}',
               T.REMARK = N'原 C# 离职工资表保存后动作的忠实移植（先删同月旧档、再判每人每月一份）',
               T.LAST_UPDATE_BY = N'DbUp', T.LAST_UPDATE_DATE = SYSDATETIME()
WHEN NOT MATCHED THEN
    INSERT (MODULE_ID, EVENT_CODE, SEQ, EFFECT_KEY, EFFECT_NAME, ENABLED, FAIL_MODE, CONDITION_STRUCT,
            PARAM_STRUCT, REVERSE_STRUCT, REMARK, SOURCE_REF, CREATE_PERSON, CREATE_DATE, LAST_UPDATE_BY, LAST_UPDATE_DATE)
    VALUES (S.MODULE_ID, N'SAVE', 1, N'wage-month-doc-prune', N'同月旧档清理与每人每月判重（保存期）', 1, N'BLOCK', NULL,
            @Params, N'{"kind":"none"}',
            N'原 C# 离职工资表保存后动作的忠实移植（先删同月旧档、再判每人每月一份）', N'hr-wage-lz',
            N'DbUp', SYSDATETIME(), N'DbUp', SYSDATETIME());

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

IF EXISTS (SELECT 1 FROM #Family f
           WHERE NOT EXISTS (SELECT 1 FROM dbo.MODULE_BUSINESS_ACTION a
                             WHERE a.MODULE_ID = f.MODULE_ID AND a.EVENT_CODE = N'SAVE' AND a.SEQ = 1
                               AND a.EFFECT_KEY = N'wage-month-doc-prune' AND a.ENABLED = 1))
    THROW 50005, N'退役 C# 后缺少启用的 SAVE 期 wage-month-doc-prune 动作，迁移中止。', 1;

DROP TABLE #Family;

PRINT N'== hr-wage-lz 入效果目录并退役 C# 完成（180310/1803101，SAVE 期 wage-month-doc-prune；快照族名已置空）==';
