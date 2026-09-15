-- ============================================================================
-- EOS.ERP migration 080: 查重/唯一族补齐规则级适用条件（params.when）
-- ----------------------------------------------------------------------------
-- 旧域规则普遍先读当前单据的判据字段、为空则直接放行（string.IsNullOrWhiteSpace 语义）。
-- 目录实例此前只能表达"候选行键 = 当前单据键"，无法表达"本单判据为空则不校验"。
-- 本次为已落地的实例补上 when（blank/negate 算子，等价 LTRIM/RTRIM 后判空）：
--   · 员工工号 / 料号 / 本位币：本单键为空则不校验；
--   · 出勤参数 / 排班 / 工资：本单月份为空则不校验；加班申请：本单加班日期为空则不校验。
-- 条件不成立即跳过该校验；NULL 与空白串都按"空"处理，与旧实现一致。
--
-- PARAM_STRUCT 一律整串显式重写（不用 REPLACE 拼接：嵌套对象的 '}' 会被误伤）。
-- 仅改配置工作区表；随模块重新发布后生效。幂等：按 模块+阶段+顺序 覆盖。
-- ============================================================================

SET NOCOUNT ON;

DECLARE @GUARD_MESSAGE NVARCHAR(400) = N'本脚本只能在 EOS.ERP 数据库内执行，当前库为 ' + DB_NAME() + N'。';
IF DB_NAME() <> N'EOS.ERP'
    THROW 50000, @GUARD_MESSAGE, 1;

PRINT N'== 开始补齐查重/唯一族的适用条件 ==';

-- ---------- 单表唯一族 ----------

-- 员工工号唯一（180102/180105/180110/180111）：工号为空则不校验
UPDATE dbo.MODULE_VALIDATION_RULE
SET PARAM_STRUCT = N'{"mode":"entity","table":"HR_EMPLOYEE","keyFields":["EMP_NO"],"excludeSelf":{"keyFields":["EMP_ID"]},'
    + N'"filter":{"logic":"AND","items":[{"type":"VALUE-NEQ","field":{"scope":"TARGET","field":"STATE"},"value":5}]},'
    + N'"diagnostics":["EMP_NO","EMP_NAME"],'
    + N'"when":{"logic":"AND","items":[{"type":"BLANK","field":{"scope":"MASTER","field":"EMP_NO"},"negate":true}]}}',
    LAST_UPDATE_BY = N'migration-080', LAST_UPDATE_DATE = SYSDATETIME()
WHERE MODULE_ID IN (180102, 180105, 180110, 180111) AND STAGE = N'SAVE' AND VALIDATION_KEY = N'duplicate-check';

-- 本位币唯一（110103）：本单不是本位币则不校验
UPDATE dbo.MODULE_VALIDATION_RULE
SET PARAM_STRUCT = N'{"mode":"entity","table":"CURR","keyFields":["IS_BASE"],"excludeSelf":{"keyFields":["CURR_ID"]},'
    + N'"filter":{"logic":"AND","items":[{"type":"VALUE-EQ","field":{"scope":"TARGET","field":"IS_BASE"},"value":true}]},'
    + N'"diagnostics":["CURR_ID"],'
    + N'"when":{"logic":"AND","items":[{"type":"VALUE-EQ","field":{"scope":"MASTER","field":"IS_BASE"},"value":true}]}}',
    LAST_UPDATE_BY = N'migration-080', LAST_UPDATE_DATE = SYSDATETIME()
WHERE MODULE_ID = 110103 AND STAGE = N'SAVE' AND VALIDATION_KEY = N'duplicate-check';

-- 料号开模评估唯一（2914）：料号为空则不校验
UPDATE dbo.MODULE_VALIDATION_RULE
SET PARAM_STRUCT = N'{"mode":"entity","table":"MOU_ASSESS_M","keyFields":["PRO_NO"],'
    + N'"excludeSelf":{"keyFields":["ASSESS_TYPE","ASSESS_NO"]},'
    + N'"when":{"logic":"AND","items":[{"type":"BLANK","field":{"scope":"MASTER","field":"PRO_NO"},"negate":true}]}}',
    LAST_UPDATE_BY = N'migration-080', LAST_UPDATE_DATE = SYSDATETIME()
WHERE MODULE_ID = 2914 AND STAGE = N'SAVE' AND VALIDATION_KEY = N'duplicate-check';

-- ---------- 主从跨单唯一族 ----------

DECLARE @MONTH_WHEN NVARCHAR(400) = N'"when":{"logic":"AND","items":[{"type":"BLANK","field":{"scope":"MASTER","field":"COUNT_MONTH"},"negate":true}]}';
DECLARE @DATE_WHEN  NVARCHAR(400) = N'"when":{"logic":"AND","items":[{"type":"BLANK","field":{"scope":"MASTER","field":"COUNT_DATE"},"negate":true}]}';

-- 每月出勤参数（180205）：本单月份为空则不校验（整维度扫描、无诊断行）
UPDATE dbo.MODULE_VALIDATION_RULE
SET PARAM_STRUCT = N'{"mode":"master-detail","masterTable":"HR_ENACTMENT_M","detailTable":"HR_ENACTMENT_D",'
    + N'"joinFields":{"master":["ENACTMENT_TYPE","ENACTMENT_NO"],"detail":["ENACTMENT_TYPE","ENACTMENT_NO"]},'
    + N'"masterGroupFields":["COUNT_MONTH"],"groupFields":["EMP_ID"],' + @MONTH_WHEN + N'}',
    LAST_UPDATE_BY = N'migration-080', LAST_UPDATE_DATE = SYSDATETIME()
WHERE MODULE_ID = 180205 AND STAGE = N'SAVE' AND VALIDATION_KEY = N'duplicate-check';

-- 排班（180211 / 180651）：只看本单出现过的员工，回报重复的明细序号
UPDATE dbo.MODULE_VALIDATION_RULE
SET PARAM_STRUCT = N'{"mode":"master-detail","masterTable":"HR_PLAN_M","detailTable":"HR_PLAN_D",'
    + N'"joinFields":{"master":["PLAN_TYPE","PLAN_NO"],"detail":["PLAN_TYPE","PLAN_NO"]},'
    + N'"masterGroupFields":["COUNT_MONTH"],"groupFields":["EMP_ID"],'
    + N'"documentDetailFields":["PLAN_TYPE","PLAN_NO"],"diagnosticFields":["SERIAL_NO"],' + @MONTH_WHEN + N'}',
    LAST_UPDATE_BY = N'migration-080', LAST_UPDATE_DATE = SYSDATETIME()
WHERE MODULE_ID = 180211 AND STAGE = N'SAVE' AND VALIDATION_KEY = N'duplicate-check';

UPDATE dbo.MODULE_VALIDATION_RULE
SET PARAM_STRUCT = N'{"mode":"master-detail","masterTable":"HRM_PLAN_M","detailTable":"HRM_PLAN_D",'
    + N'"joinFields":{"master":["PLAN_TYPE","PLAN_NO"],"detail":["PLAN_TYPE","PLAN_NO"]},'
    + N'"masterGroupFields":["COUNT_MONTH"],"groupFields":["EMP_ID"],'
    + N'"documentDetailFields":["PLAN_TYPE","PLAN_NO"],"diagnosticFields":["SERIAL_NO"],' + @MONTH_WHEN + N'}',
    LAST_UPDATE_BY = N'migration-080', LAST_UPDATE_DATE = SYSDATETIME()
WHERE MODULE_ID = 180651 AND STAGE = N'SAVE' AND VALIDATION_KEY = N'duplicate-check';

-- 工资表（180309 / 1803091 / 180310 / 1803101）：整月扫描，回报重复的员工工号
UPDATE dbo.MODULE_VALIDATION_RULE
SET PARAM_STRUCT = N'{"mode":"master-detail","masterTable":"HR_WAGE_M","detailTable":"HR_WAGE_D",'
    + N'"joinFields":{"master":["WAGE_TYPE","WAGE_NO"],"detail":["WAGE_TYPE","WAGE_NO"]},'
    + N'"masterGroupFields":["COUNT_MONTH"],"groupFields":["EMP_ID"],"diagnosticFields":["EMP_ID"],' + @MONTH_WHEN + N'}',
    LAST_UPDATE_BY = N'migration-080', LAST_UPDATE_DATE = SYSDATETIME()
WHERE MODULE_ID IN (180309, 1803091, 180310, 1803101) AND STAGE = N'SAVE' AND VALIDATION_KEY = N'duplicate-check';

UPDATE dbo.MODULE_VALIDATION_RULE
SET PARAM_STRUCT = N'{"mode":"master-detail","masterTable":"HRM_WAGE_M","detailTable":"HRM_WAGE_D",'
    + N'"joinFields":{"master":["WAGE_TYPE","WAGE_NO"],"detail":["WAGE_TYPE","WAGE_NO"]},'
    + N'"masterGroupFields":["COUNT_MONTH"],"groupFields":["EMP_ID"],"diagnosticFields":["EMP_ID"],' + @MONTH_WHEN + N'}',
    LAST_UPDATE_BY = N'migration-080', LAST_UPDATE_DATE = SYSDATETIME()
WHERE MODULE_ID = 180504 AND STAGE = N'SAVE' AND VALIDATION_KEY = N'duplicate-check';

-- 加班申请（180206）：本单加班日期为空则不校验，只看本单出现过的员工
UPDATE dbo.MODULE_VALIDATION_RULE
SET PARAM_STRUCT = N'{"mode":"master-detail","masterTable":"HR_APPLY_M","detailTable":"HR_APPLY_D",'
    + N'"joinFields":{"master":["APPLY_TYPE","APPLY_NO"],"detail":["APPLY_TYPE","APPLY_NO"]},'
    + N'"masterGroupFields":["COUNT_DATE"],"groupFields":["EMP_ID"],'
    + N'"documentDetailFields":["APPLY_TYPE","APPLY_NO"],"diagnosticFields":["EMP_ID"],' + @DATE_WHEN + N'}',
    LAST_UPDATE_BY = N'migration-080', LAST_UPDATE_DATE = SYSDATETIME()
WHERE MODULE_ID = 180206 AND STAGE = N'SAVE' AND VALIDATION_KEY = N'duplicate-check';

PRINT N'-- duplicate-check 实例现状 --';
SELECT CONCAT(N'  module=', MODULE_ID, N' mode=', ISNULL(JSON_VALUE(PARAM_STRUCT, '$.mode'), N'-'),
              N' hasWhen=', CASE WHEN PARAM_STRUCT LIKE N'%"when"%' THEN N'Y' ELSE N'N' END,
              N' source=', ISNULL(SOURCE_REF, N'-')) AS SUMMARY
FROM dbo.MODULE_VALIDATION_RULE
WHERE VALIDATION_KEY = N'duplicate-check'
ORDER BY MODULE_ID;

PRINT N'== 查重/唯一族适用条件补齐完成 ==';
