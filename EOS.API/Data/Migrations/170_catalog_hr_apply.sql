-- EOS.ERP migration 171: 加班申请入校验目录并退役 C#（hr-apply：180206）——过渡桥清零
-- ----------------------------------------------------------------------------
-- C# `HrDomainRules.HrApplyAfterSaveAsync`（判据来源旧过程 `P_HR_APPLY_After_Save`）两条判据：
--   ① **先决条件型拒绝**：本单出勤日所属月份未维护「每月出勤参数」即拒存，文案带 `yyyyMM`；
--   ② **跨单据聚合比较**：该月**全部加班申请单**按员工累计的加班工时，不得超过当月出勤参数
--      明细给该员工的额度（额度行缺失且累计大于零同样拒绝；命中至多回报 10 行）。
--   两条都超出既有模板的表达范围（聚合跨单据、文案含动态月份）⇒ 校验目录的 `custom-validation`
--   （注册实现 **hr-apply-check**）。
--   "每日每人一单"由同模块既有的 SAVE 期 `duplicate-check`（SEQ=1）承担，本迁移不重复播种。
--
-- 本迁移之后 `DomainRuleMap` 登记族为 0：`ModuleBusinessMap.DomainRuleMap` 清空、
-- `HrDomainRules.cs` 整文件删除、`DomainRuleService` 的族分派分支清空（保留"未登记即拒绝"的兜底）。
--
-- 幂等：规则按 模块+SAVE+VALIDATION_KEY+SEQ 合并；快照族名仅当仍含族名时改写。
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

DECLARE @ModuleId INT = 180206;
DECLARE @Family NVARCHAR(40) = N'hr-apply';

IF NOT EXISTS (SELECT 1 FROM dbo.MODULES m WHERE m.M_IDX = @ModuleId
                 AND LTRIM(RTRIM(ISNULL(m.MASTER_TABLE, ''))) = N'HR_APPLY_M'
                 AND LTRIM(RTRIM(ISNULL(m.DETAIL_TABLE, ''))) = N'HR_APPLY_D')
    THROW 50001, N'模块 180206 形态不符（应为 HR_APPLY_M / HR_APPLY_D），迁移中止。', 1;

IF EXISTS (SELECT 1 FROM dbo.MODULE_VALIDATION_RULE
            WHERE MODULE_ID = @ModuleId AND STAGE = N'SAVE' AND VALIDATION_KEY = N'custom-validation')
    THROW 50002, N'模块 180206 已有 custom-validation 规则，迁移中止（先核对配置）。', 1;

/* 既有规则不得被顶掉：SEQ=1 的 duplicate-check（每日每人一单）必须仍在 */
IF NOT EXISTS (SELECT 1 FROM dbo.MODULE_VALIDATION_RULE
               WHERE MODULE_ID = @ModuleId AND STAGE = N'SAVE' AND VALIDATION_KEY = N'duplicate-check' AND ENABLED = 1)
    THROW 50003, N'模块 180206 缺少既有的 duplicate-check（每日每人一单）规则，迁移中止（先核对配置）。', 1;

/* 依赖列存在性（申请侧四列 + 出勤参数侧四列 + 两张明细表的员工列） */
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.HR_APPLY_D')
                 AND name IN (N'EMP_ID', N'OVERTIME', N'REST_OVERTIME', N'HOLIDAY_OVERTIME')
               HAVING COUNT(*) = 4)
    THROW 50004, N'HR_APPLY_D 缺少移植实现所需的员工/加班列，迁移中止。', 1;

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.HR_ENACTMENT_D')
                 AND name IN (N'EMP_ID', N'OVERTIME', N'REST_OVERTIME', N'HOLIDAY_OVERTIME')
               HAVING COUNT(*) = 4)
    THROW 50005, N'HR_ENACTMENT_D 缺少移植实现所需的员工/额度列，迁移中止。', 1;

/* ① 校验规则（custom-validation → hr-apply-check），SEQ=2（SEQ=1 已被 duplicate-check 占用） */
DECLARE @Check NVARCHAR(MAX) =
    N'{"apply":{"table":"HR_APPLY_M","detailTable":"HR_APPLY_D",'
    + N'"typeField":"APPLY_TYPE","noField":"APPLY_NO","dateField":"COUNT_DATE","empField":"EMP_ID",'
    + N'"overTimeField":"OVERTIME","restOverTimeField":"REST_OVERTIME","holidayOverTimeField":"HOLIDAY_OVERTIME"},'
    + N'"enactment":{"table":"HR_ENACTMENT_M","detailTable":"HR_ENACTMENT_D",'
    + N'"typeField":"ENACTMENT_TYPE","noField":"ENACTMENT_NO","monthField":"COUNT_MONTH","empField":"EMP_ID",'
    + N'"overTimeField":"OVERTIME","restOverTimeField":"REST_OVERTIME","holidayOverTimeField":"HOLIDAY_OVERTIME"},'
    + N'"messages":{"missingParams":"未生成 {month} 月度出勤参数，加班申请不能保存；请先在「每月出勤参数」维护本月的加班额度。",'
    + N'"exceeded":"以下人员时间超出:\r\n工号--加班时--休息日加班时--节假日加班时\r\n"}}';

DECLARE @Param NVARCHAR(MAX) = N'{"handler":"hr-apply-check","check":' + @Check + N'}';
DECLARE @Remark NVARCHAR(400) =
    N'加班申请保存期两条判据：当月出勤参数未维护即拒存 + 当月（跨本单）按员工累计加班不超月度额度（原 C# 判据的移植）';

MERGE dbo.MODULE_VALIDATION_RULE AS T
USING (SELECT @ModuleId AS MODULE_ID) AS S
   ON T.MODULE_ID = S.MODULE_ID AND T.STAGE = N'SAVE'
  AND T.VALIDATION_KEY = N'custom-validation' AND T.SEQ = 2
WHEN MATCHED THEN
    UPDATE SET T.PARAM_STRUCT = @Param, T.MESSAGE = NULL, T.ENABLED = 1, T.REMARK = @Remark,
               T.LAST_UPDATE_BY = N'DbUp', T.LAST_UPDATE_DATE = SYSDATETIME()
WHEN NOT MATCHED THEN
    INSERT (MODULE_ID, STAGE, SEQ, VALIDATION_KEY, ENABLED, PARAM_STRUCT, MESSAGE, REMARK, SOURCE_REF,
            CREATE_PERSON, CREATE_DATE, LAST_UPDATE_BY, LAST_UPDATE_DATE)
    VALUES (S.MODULE_ID, N'SAVE', 2, N'custom-validation', 1, @Param, NULL, @Remark,
            @Family, N'DbUp', SYSDATETIME(), N'DbUp', SYSDATETIME());

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
    THROW 50006, N'当前快照仍残留已退役的族名，迁移中止。', 1;

IF NOT EXISTS (SELECT 1 FROM dbo.MODULE_VALIDATION_RULE
               WHERE MODULE_ID = @ModuleId AND STAGE = N'SAVE' AND ENABLED = 1
                 AND VALIDATION_KEY = N'custom-validation' AND SEQ = 2
                 AND PARAM_STRUCT LIKE N'%"handler":"hr-apply-check"%')
    THROW 50007, N'退役 C# 后缺少启用的 SAVE 期 custom-validation（hr-apply-check）规则，迁移中止。', 1;

IF EXISTS (SELECT 1 FROM dbo.WORKBENCH_DEFINITION_SNAPSHOT S
           WHERE S.IS_CURRENT = 1 AND S.DEFINITION_JSON LIKE N'%"DomainRule":"%')
    THROW 50008, N'仍有当前快照带着领域规则族名，迁移中止（过渡桥清零的前提是所有快照都已迁目录）。', 1;

PRINT N'== hr-apply 入目录并退役 C# 完成（180206，custom-validation → hr-apply-check；DomainRuleMap 清空）==';
