-- EOS.ERP migration 170: 工时录入入校验目录并退役 C#（hr-worktime：180207）
-- ----------------------------------------------------------------------------
-- C# `HrDomainRules.HrWorktimeAfterSaveAsync`（判据来源旧过程 `P_HR_WORKTIME_After_Save`）：
--   "本月加班工时不得超过已申请加班工时"——两张表各自**按员工聚合**后逐员工比较：
--   本单工时明细按员工汇总 vs 本单出勤日所属月的加班申请明细按员工汇总。
--   聚合跨单据（申请侧是当月全部加班申请单），现有模板的聚合都在"本单/被引用行"范围内
--   ⇒ 走校验目录的 `custom-validation`（注册实现 **hr-worktime-check**）。
--   门控：业务设置表 `HR_SETUP.REQUIRE_ENACTMENT=1` 时才判（设置关闭即放行，与旧实现一致）。
--
-- **移植时按意图修正的一处缺陷（同批登记到覆盖率报告 §八"有意差异"）**：
--   旧实现的左侧聚合写成 `HR_WORKTIME_M.EMP_ID / OVERTIME / REST_OVERTIME / HOLIDAY_OVERTIME`，
--   而这四列都在**明细** `HR_WORKTIME_D` 上、主表并没有——门控一旦打开，旧实现必抛
--   "列名 'EMP_ID' 无效"（保存 500），而不是给出校验结论；本移植改为按明细 `EMP_ID` 分组
--   （即原意），并把申请侧缺失员工的显示值按 0 呈现（旧实现在该分支同样抛异常）。
--   当前库内 `HR_SETUP.REQUIRE_ENACTMENT=0`，门控关闭，故本次退役**运行期行为不变**。
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

DECLARE @ModuleId INT = 180207;
DECLARE @Family NVARCHAR(40) = N'hr-worktime';

IF NOT EXISTS (SELECT 1 FROM dbo.MODULES m WHERE m.M_IDX = @ModuleId
                 AND LTRIM(RTRIM(ISNULL(m.MASTER_TABLE, ''))) = N'HR_WORKTIME_M'
                 AND LTRIM(RTRIM(ISNULL(m.DETAIL_TABLE, ''))) = N'HR_WORKTIME_D')
    THROW 50001, N'模块 180207 形态不符（应为 HR_WORKTIME_M / HR_WORKTIME_D），迁移中止。', 1;

IF EXISTS (SELECT 1 FROM dbo.MODULE_VALIDATION_RULE
            WHERE MODULE_ID = @ModuleId AND STAGE = N'SAVE' AND VALIDATION_KEY = N'custom-validation')
    THROW 50002, N'模块 180207 已有 custom-validation 规则，迁移中止（先核对配置）。', 1;

/* 依赖列存在性（移植实现引用：工时明细四列 + 申请明细四列 + 两张主表的期间列 + 业务设置开关） */
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.HR_WORKTIME_D')
                 AND name IN (N'EMP_ID', N'OVERTIME', N'REST_OVERTIME', N'HOLIDAY_OVERTIME')
               HAVING COUNT(*) = 4)
    THROW 50003, N'HR_WORKTIME_D 缺少移植实现所需的员工/加班列，迁移中止。', 1;

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.HR_APPLY_D')
                 AND name IN (N'EMP_ID', N'OVERTIME', N'REST_OVERTIME', N'HOLIDAY_OVERTIME')
               HAVING COUNT(*) = 4)
    THROW 50004, N'HR_APPLY_D 缺少移植实现所需的员工/加班列，迁移中止。', 1;

/* ① 校验规则（custom-validation → hr-worktime-check），SEQ=1（该模块此前没有 SAVE 期规则） */
DECLARE @Check NVARCHAR(MAX) =
    N'{"worktime":{"table":"HR_WORKTIME_M","detailTable":"HR_WORKTIME_D",'
    + N'"typeField":"WORKTIME_TYPE","noField":"WORKTIME_NO","dateField":"COUNT_DATE","empField":"EMP_ID",'
    + N'"overTimeField":"OVERTIME","restOverTimeField":"REST_OVERTIME","holidayOverTimeField":"HOLIDAY_OVERTIME"},'
    + N'"apply":{"table":"HR_APPLY_M","detailTable":"HR_APPLY_D",'
    + N'"typeField":"APPLY_TYPE","noField":"APPLY_NO","dateField":"COUNT_DATE","empField":"EMP_ID",'
    + N'"overTimeField":"OVERTIME","restOverTimeField":"REST_OVERTIME","holidayOverTimeField":"HOLIDAY_OVERTIME"},'
    + N'"setup":{"table":"HR_SETUP","flagField":"REQUIRE_ENACTMENT"},'
    + N'"message":"以下人员时间超出:\r\n工号---加班时--休息日加班时--节假日加班时\r\n"}';

DECLARE @Param NVARCHAR(MAX) = N'{"handler":"hr-worktime-check","check":' + @Check + N'}';
DECLARE @Remark NVARCHAR(400) =
    N'工时录入保存期判据：本月加班工时不超过已申请加班工时（两张表各自按员工聚合后比较；受业务设置表门控；原 C# 判据的移植）';

MERGE dbo.MODULE_VALIDATION_RULE AS T
USING (SELECT @ModuleId AS MODULE_ID) AS S
   ON T.MODULE_ID = S.MODULE_ID AND T.STAGE = N'SAVE'
  AND T.VALIDATION_KEY = N'custom-validation' AND T.SEQ = 1
WHEN MATCHED THEN
    UPDATE SET T.PARAM_STRUCT = @Param, T.MESSAGE = NULL, T.ENABLED = 1, T.REMARK = @Remark,
               T.LAST_UPDATE_BY = N'DbUp', T.LAST_UPDATE_DATE = SYSDATETIME()
WHEN NOT MATCHED THEN
    INSERT (MODULE_ID, STAGE, SEQ, VALIDATION_KEY, ENABLED, PARAM_STRUCT, MESSAGE, REMARK, SOURCE_REF,
            CREATE_PERSON, CREATE_DATE, LAST_UPDATE_BY, LAST_UPDATE_DATE)
    VALUES (S.MODULE_ID, N'SAVE', 1, N'custom-validation', 1, @Param, NULL, @Remark,
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
    THROW 50005, N'当前快照仍残留已退役的族名，迁移中止。', 1;

IF NOT EXISTS (SELECT 1 FROM dbo.MODULE_VALIDATION_RULE
               WHERE MODULE_ID = @ModuleId AND STAGE = N'SAVE' AND ENABLED = 1
                 AND VALIDATION_KEY = N'custom-validation' AND SEQ = 1
                 AND PARAM_STRUCT LIKE N'%"handler":"hr-worktime-check"%')
    THROW 50006, N'退役 C# 后缺少启用的 SAVE 期 custom-validation（hr-worktime-check）规则，迁移中止。', 1;

PRINT N'== hr-worktime 入目录并退役 C# 完成（180207，custom-validation → hr-worktime-check）==';
