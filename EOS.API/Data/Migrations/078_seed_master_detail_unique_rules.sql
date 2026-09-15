-- ============================================================================
-- EOS.ERP migration 079: HR 主从跨单唯一族迁移到 duplicate-check.master-detail
-- ----------------------------------------------------------------------------
-- 这 9 个模块的保存后规则是同一形状：「主表维度（月份/日期）× 明细分组键（员工）」
-- 在跨单据范围内不得重复。原 C# 逐族手写 SQL，现收敛为目录实例（新形态 master-detail）：
--   cur = 当前单据主表行（按主表主键参数限定）；m = 同维度维度的其它单据主表行；
--   d = 参与比对的明细行；命中组数 > 1 即重复。
--   documentDetailFields 给出"只看本单出现过的分组键"的明细列（=主键列，值取 @mk*），
--   不提供时与旧实现一致按整个维度扫描（工资表族即此形态）。
--
-- 与旧实现的**有意差异**：旧 hr-enactment 是全年全局扫描（任一月存在重复即拒绝所有保存），
-- 新形态一律限定在当前单据的维度值内；失败夹具的判据与消息保持一致，无关单据的历史重复
-- 不再拦下本次保存。
--
-- 诊断列按组聚合（MAX）成多行，消息用 {ROWS} 整块回填；行数上限默认 10。
-- 仅落配置工作区表；配置随模块重新发布后生效。幂等：按 模块+阶段+顺序 存在则更新。
-- ============================================================================

SET NOCOUNT ON;

DECLARE @GUARD_MESSAGE NVARCHAR(400) = N'本脚本只能在 EOS.ERP 数据库内执行，当前库为 ' + DB_NAME() + N'。';
IF DB_NAME() <> N'EOS.ERP'
    THROW 50000, @GUARD_MESSAGE, 1;

PRINT N'== 开始落地 HR 主从跨单唯一目录实例 ==';

DECLARE @NL NVARCHAR(2) = NCHAR(13) + NCHAR(10);

DECLARE @RULES TABLE (MODULE_ID INT PRIMARY KEY, PARAM_STRUCT NVARCHAR(MAX), MESSAGE NVARCHAR(500), REMARK NVARCHAR(500), SOURCE_REF NVARCHAR(100));

-- 每月出勤参数：每人每月一笔（无诊断行，消息固定）
INSERT INTO @RULES (MODULE_ID, PARAM_STRUCT, MESSAGE, REMARK, SOURCE_REF) VALUES
(180205,
 N'{"mode":"master-detail","masterTable":"HR_ENACTMENT_M","detailTable":"HR_ENACTMENT_D",'
 + N'"joinFields":{"master":["ENACTMENT_TYPE","ENACTMENT_NO"],"detail":["ENACTMENT_TYPE","ENACTMENT_NO"]},'
 + N'"masterGroupFields":["COUNT_MONTH"],"groupFields":["EMP_ID"]}',
 N'资料重复!每个员工每个月份只可有一笔资料', N'每人每月一笔出勤参数', N'hr-enactment');

-- 排班：当月每人一班（回报重复的明细序号）
INSERT INTO @RULES (MODULE_ID, PARAM_STRUCT, MESSAGE, REMARK, SOURCE_REF) VALUES
(180211,
 N'{"mode":"master-detail","masterTable":"HR_PLAN_M","detailTable":"HR_PLAN_D",'
 + N'"joinFields":{"master":["PLAN_TYPE","PLAN_NO"],"detail":["PLAN_TYPE","PLAN_NO"]},'
 + N'"masterGroupFields":["COUNT_MONTH"],"groupFields":["EMP_ID"],'
 + N'"documentDetailFields":["PLAN_TYPE","PLAN_NO"],"diagnosticFields":["SERIAL_NO"]}',
 N'以下序号项人员当月排班重复 ' + @NL + N'{ROWS}', N'当月每人只可排一班', N'hr-plan'),
(180651,
 N'{"mode":"master-detail","masterTable":"HRM_PLAN_M","detailTable":"HRM_PLAN_D",'
 + N'"joinFields":{"master":["PLAN_TYPE","PLAN_NO"],"detail":["PLAN_TYPE","PLAN_NO"]},'
 + N'"masterGroupFields":["COUNT_MONTH"],"groupFields":["EMP_ID"],'
 + N'"documentDetailFields":["PLAN_TYPE","PLAN_NO"],"diagnosticFields":["SERIAL_NO"]}',
 N'以下序号项人员当月排班重复 ' + @NL + N'{ROWS}', N'当月每人只可排一班', N'hrm-plan');

-- 工资表：当月每人一份（回报重复的员工工号）
INSERT INTO @RULES (MODULE_ID, PARAM_STRUCT, MESSAGE, REMARK, SOURCE_REF) VALUES
(180309,
 N'{"mode":"master-detail","masterTable":"HR_WAGE_M","detailTable":"HR_WAGE_D",'
 + N'"joinFields":{"master":["WAGE_TYPE","WAGE_NO"],"detail":["WAGE_TYPE","WAGE_NO"]},'
 + N'"masterGroupFields":["COUNT_MONTH"],"groupFields":["EMP_ID"],"diagnosticFields":["EMP_ID"]}',
 N'以下人员当月工资表重复 ' + @NL + N'{ROWS}', N'当月每人只可有一份工资表', N'hr-wage'),
(1803091,
 N'{"mode":"master-detail","masterTable":"HR_WAGE_M","detailTable":"HR_WAGE_D",'
 + N'"joinFields":{"master":["WAGE_TYPE","WAGE_NO"],"detail":["WAGE_TYPE","WAGE_NO"]},'
 + N'"masterGroupFields":["COUNT_MONTH"],"groupFields":["EMP_ID"],"diagnosticFields":["EMP_ID"]}',
 N'以下人员当月工资表重复 ' + @NL + N'{ROWS}', N'当月每人只可有一份工资表', N'hr-wage'),
(180504,
 N'{"mode":"master-detail","masterTable":"HRM_WAGE_M","detailTable":"HRM_WAGE_D",'
 + N'"joinFields":{"master":["WAGE_TYPE","WAGE_NO"],"detail":["WAGE_TYPE","WAGE_NO"]},'
 + N'"masterGroupFields":["COUNT_MONTH"],"groupFields":["EMP_ID"],"diagnosticFields":["EMP_ID"]}',
 N'以下人员当月工资表重复 ' + @NL + N'{ROWS}', N'当月每人只可有一份工资表', N'hrm-wage'),
(180310,
 N'{"mode":"master-detail","masterTable":"HR_WAGE_M","detailTable":"HR_WAGE_D",'
 + N'"joinFields":{"master":["WAGE_TYPE","WAGE_NO"],"detail":["WAGE_TYPE","WAGE_NO"]},'
 + N'"masterGroupFields":["COUNT_MONTH"],"groupFields":["EMP_ID"],"diagnosticFields":["EMP_ID"]}',
 N'以下人员当月工资表重复 ' + @NL + N'{ROWS}', N'当月每人只可有一份工资表（离职工资表另清旧档）', N'hr-wage-lz'),
(1803101,
 N'{"mode":"master-detail","masterTable":"HR_WAGE_M","detailTable":"HR_WAGE_D",'
 + N'"joinFields":{"master":["WAGE_TYPE","WAGE_NO"],"detail":["WAGE_TYPE","WAGE_NO"]},'
 + N'"masterGroupFields":["COUNT_MONTH"],"groupFields":["EMP_ID"],"diagnosticFields":["EMP_ID"]}',
 N'以下人员当月工资表重复 ' + @NL + N'{ROWS}', N'当月每人只可有一份工资表（离职工资表另清旧档）', N'hr-wage-lz');

-- 加班申请：当日每人一单（回报重复的员工工号）
INSERT INTO @RULES (MODULE_ID, PARAM_STRUCT, MESSAGE, REMARK, SOURCE_REF) VALUES
(180206,
 N'{"mode":"master-detail","masterTable":"HR_APPLY_M","detailTable":"HR_APPLY_D",'
 + N'"joinFields":{"master":["APPLY_TYPE","APPLY_NO"],"detail":["APPLY_TYPE","APPLY_NO"]},'
 + N'"masterGroupFields":["COUNT_DATE"],"groupFields":["EMP_ID"],'
 + N'"documentDetailFields":["APPLY_TYPE","APPLY_NO"],"diagnosticFields":["EMP_ID"]}',
 N'以下人员当日加班申请重复 ' + @NL + N'{ROWS}', N'当日每人只可有一单加班申请', N'hr-apply');

DECLARE @MODULE_ID INT, @PARAM NVARCHAR(MAX), @MESSAGE NVARCHAR(500), @REMARK NVARCHAR(500), @SOURCE NVARCHAR(100);
DECLARE cur CURSOR LOCAL FAST_FORWARD FOR SELECT MODULE_ID, PARAM_STRUCT, MESSAGE, REMARK, SOURCE_REF FROM @RULES ORDER BY MODULE_ID;
OPEN cur;
FETCH NEXT FROM cur INTO @MODULE_ID, @PARAM, @MESSAGE, @REMARK, @SOURCE;
WHILE @@FETCH_STATUS = 0
BEGIN
    IF EXISTS (SELECT 1 FROM dbo.MODULE_VALIDATION_RULE WHERE MODULE_ID = @MODULE_ID AND STAGE = N'SAVE' AND SEQ = 1)
        UPDATE dbo.MODULE_VALIDATION_RULE
        SET VALIDATION_KEY = N'duplicate-check', ENABLED = 1,
            PARAM_STRUCT = @PARAM, MESSAGE = @MESSAGE, REMARK = @REMARK, SOURCE_REF = @SOURCE,
            LAST_UPDATE_BY = N'migration-079', LAST_UPDATE_DATE = SYSDATETIME()
        WHERE MODULE_ID = @MODULE_ID AND STAGE = N'SAVE' AND SEQ = 1;
    ELSE
        INSERT INTO dbo.MODULE_VALIDATION_RULE
            (MODULE_ID, STAGE, SEQ, VALIDATION_KEY, ENABLED, PARAM_STRUCT, MESSAGE, REMARK, SOURCE_REF, CREATE_PERSON, CREATE_DATE)
        VALUES
            (@MODULE_ID, N'SAVE', 1, N'duplicate-check', 1, @PARAM, @MESSAGE, @REMARK, @SOURCE, N'migration-079', SYSDATETIME());

    FETCH NEXT FROM cur INTO @MODULE_ID, @PARAM, @MESSAGE, @REMARK, @SOURCE;
END
CLOSE cur;
DEALLOCATE cur;

PRINT N'-- duplicate-check 实例现状 --';
SELECT CONCAT(N'  module=', MODULE_ID, N' stage=', STAGE, N' seq=', SEQ, N' source=', ISNULL(SOURCE_REF, N'-'),
              N' mode=', ISNULL(JSON_VALUE(PARAM_STRUCT, '$.mode'), N'-')) AS SUMMARY
FROM dbo.MODULE_VALIDATION_RULE
WHERE VALIDATION_KEY = N'duplicate-check'
ORDER BY MODULE_ID;

PRINT N'== HR 主从跨单唯一目录实例落地完成 ==';
