-- ============================================================================
-- EOS.ERP migration 086: 期重叠族迁移到 period-overlap（180106/180107/180108）
-- ----------------------------------------------------------------------------
-- 三族结构一致：期间在**明细行**上（BEGIN_DATE/END_DATE），跨单比较的维度键不同——
--   合同 180106：HR_CONTRACT_D，维度＝EMP_ID
--   保险 180107：HR_SAFE_D，维度＝EMP_ID + SAFE_ID（同人不同险种可并存）
--   证件 180108：HR_CERTIFY_D，维度＝EMP_ID + CERTIFY_ID（同人不同证件可并存）
-- 规则拆两条：
--   SEQ 1 period-overlap（跨单期间重叠）：闭区间相交（端点相接算重叠）、任一侧结束日为空
--         视为无限期、排除当前单据自身、诊断回报「人员 + 冲突单据 + 冲突期间」；
--   SEQ 2 duplicate-check within-doc（同单重复）：同一单据内同维度键重复，经 displayLookup
--         回报人员名。
--
-- 与旧实现的**有意差异**：旧判据只检查"新单端点是否落在旧区间内"，漏判"旧区间被新区间包含"
-- 的情形（例：旧 3/1–3/31、新 1/1–12/31）；新模板用标准区间相交，更严且更符合业务意图。
--
-- 仅落配置工作区表；配置随模块重新发布后生效。幂等：按 模块+阶段+顺序 覆盖。
-- ============================================================================

SET NOCOUNT ON;

DECLARE @GUARD_MESSAGE NVARCHAR(400) = N'本脚本只能在 EOS.ERP 数据库内执行，当前库为 ' + DB_NAME() + N'。';
IF DB_NAME() <> N'EOS.ERP'
    THROW 50000, @GUARD_MESSAGE, 1;

PRINT N'== 开始落地期重叠族配置 ==';

DECLARE @NL NVARCHAR(2) = NCHAR(13) + NCHAR(10);

DECLARE @RULES TABLE (
    MODULE_ID INT, SEQ INT, VALIDATION_KEY NVARCHAR(50),
    PARAM_STRUCT NVARCHAR(MAX), MESSAGE NVARCHAR(500), REMARK NVARCHAR(500), SOURCE_REF NVARCHAR(100), PRIMARY KEY (MODULE_ID, SEQ));

-- 合同签订：人力合同期间不得与其它合同重叠
INSERT INTO @RULES (MODULE_ID, SEQ, VALIDATION_KEY, PARAM_STRUCT, MESSAGE, REMARK, SOURCE_REF) VALUES
(180106, 1, N'period-overlap',
 N'{"detailTable":"HR_CONTRACT_D","rangeFields":{"begin":"BEGIN_DATE","end":"END_DATE"},'
 + N'"scopeFields":["CONT_TYPE","CONT_NO"],"groupFields":["EMP_ID"],'
 + N'"displayLookup":{"table":"HR_EMPLOYEE","linkField":"EMP_ID","displayField":"EMP_NAME"},'
 + N'"diagnosticFields":["@display","CONT_NO","BEGIN_DATE","END_DATE"],"maxRows":10}',
 N'以下人员的合同期间与其它单据重叠 ' + @NL + N'{ROWS}',
 N'同一人的合同期间不得与其它单据重叠（端点相接算重叠；结束日为空视为无限期）', N'hr-contract'),
(180106, 2, N'duplicate-check',
 N'{"mode":"within-doc","keyFields":["EMP_ID"],'
 + N'"displayLookup":{"table":"HR_EMPLOYEE","linkField":"EMP_ID","displayField":"EMP_NAME"},'
 + N'"diagnosticFields":["@display","EMP_ID"]}',
 N'以下人员资料重复 ' + @NL + N'{ROWS}',
 N'同一单据内同一人员只能出现一次', N'hr-contract');

-- 保险投保：同人同险种的期间不得重叠
INSERT INTO @RULES (MODULE_ID, SEQ, VALIDATION_KEY, PARAM_STRUCT, MESSAGE, REMARK, SOURCE_REF) VALUES
(180107, 1, N'period-overlap',
 N'{"detailTable":"HR_SAFE_D","rangeFields":{"begin":"BEGIN_DATE","end":"END_DATE"},'
 + N'"scopeFields":["SAFE_TYPE","SAFE_NO"],"groupFields":["EMP_ID","SAFE_ID"],'
 + N'"displayLookup":{"table":"HR_EMPLOYEE","linkField":"EMP_ID","displayField":"EMP_NAME"},'
 + N'"diagnosticFields":["@display","SAFE_NO","BEGIN_DATE","END_DATE"],"maxRows":10}',
 N'以下人员在此期间重复投保 ' + @NL + N'{ROWS}',
 N'同一人同一险种的期间不得与其它单据重叠', N'hr-safe'),
(180107, 2, N'duplicate-check',
 N'{"mode":"within-doc","keyFields":["EMP_ID","SAFE_ID"],'
 + N'"displayLookup":{"table":"HR_EMPLOYEE","linkField":"EMP_ID","displayField":"EMP_NAME"},'
 + N'"diagnosticFields":["@display","EMP_ID"]}',
 N'以下人员重复投保 ' + @NL + N'{ROWS}',
 N'同一单据内同一人同一险种只能出现一次', N'hr-safe');

-- 证件资料：同人同证件的期间不得重叠
INSERT INTO @RULES (MODULE_ID, SEQ, VALIDATION_KEY, PARAM_STRUCT, MESSAGE, REMARK, SOURCE_REF) VALUES
(180108, 1, N'period-overlap',
 N'{"detailTable":"HR_CERTIFY_D","rangeFields":{"begin":"BEGIN_DATE","end":"END_DATE"},'
 + N'"scopeFields":["CERTIFY_TYPE","CERTIFY_NO"],"groupFields":["EMP_ID","CERTIFY_ID"],'
 + N'"displayLookup":{"table":"HR_EMPLOYEE","linkField":"EMP_ID","displayField":"EMP_NAME"},'
 + N'"diagnosticFields":["@display","CERTIFY_NO","BEGIN_DATE","END_DATE"],"maxRows":10}',
 N'以下人员在此期间证件重复 ' + @NL + N'{ROWS}',
 N'同一人同一证件的期间不得与其它单据重叠', N'hr-certify'),
(180108, 2, N'duplicate-check',
 N'{"mode":"within-doc","keyFields":["EMP_ID","CERTIFY_ID"],'
 + N'"displayLookup":{"table":"HR_EMPLOYEE","linkField":"EMP_ID","displayField":"EMP_NAME"},'
 + N'"diagnosticFields":["@display","EMP_ID"]}',
 N'以下人员证件重复 ' + @NL + N'{ROWS}',
 N'同一单据内同一人同一证件只能出现一次', N'hr-certify');

DECLARE @MODULE_ID INT, @SEQ INT, @KEY NVARCHAR(50), @PARAM NVARCHAR(MAX), @MESSAGE NVARCHAR(500), @REMARK NVARCHAR(500), @SOURCE NVARCHAR(100);
DECLARE cur CURSOR LOCAL FAST_FORWARD FOR SELECT MODULE_ID, SEQ, VALIDATION_KEY, PARAM_STRUCT, MESSAGE, REMARK, SOURCE_REF FROM @RULES ORDER BY MODULE_ID, SEQ;
OPEN cur;
FETCH NEXT FROM cur INTO @MODULE_ID, @SEQ, @KEY, @PARAM, @MESSAGE, @REMARK, @SOURCE;
WHILE @@FETCH_STATUS = 0
BEGIN
    IF EXISTS (SELECT 1 FROM dbo.MODULE_VALIDATION_RULE WHERE MODULE_ID = @MODULE_ID AND STAGE = N'SAVE' AND SEQ = @SEQ)
        UPDATE dbo.MODULE_VALIDATION_RULE
        SET VALIDATION_KEY = @KEY, ENABLED = 1, PARAM_STRUCT = @PARAM, MESSAGE = @MESSAGE,
            REMARK = @REMARK, SOURCE_REF = @SOURCE,
            LAST_UPDATE_BY = N'migration-086', LAST_UPDATE_DATE = SYSDATETIME()
        WHERE MODULE_ID = @MODULE_ID AND STAGE = N'SAVE' AND SEQ = @SEQ;
    ELSE
        INSERT INTO dbo.MODULE_VALIDATION_RULE
            (MODULE_ID, STAGE, SEQ, VALIDATION_KEY, ENABLED, PARAM_STRUCT, MESSAGE, REMARK, SOURCE_REF, CREATE_PERSON, CREATE_DATE)
        VALUES
            (@MODULE_ID, N'SAVE', @SEQ, @KEY, 1, @PARAM, @MESSAGE, @REMARK, @SOURCE, N'migration-086', SYSDATETIME());

    FETCH NEXT FROM cur INTO @MODULE_ID, @SEQ, @KEY, @PARAM, @MESSAGE, @REMARK, @SOURCE;
END
CLOSE cur;
DEALLOCATE cur;

PRINT N'-- 期重叠族实例现状 --';
SELECT CONCAT(N'  module=', MODULE_ID, N' seq=', SEQ, N' key=', VALIDATION_KEY, N' enabled=', ENABLED) AS SUMMARY
FROM dbo.MODULE_VALIDATION_RULE WHERE MODULE_ID IN (180106, 180107, 180108) ORDER BY MODULE_ID, SEQ;

PRINT N'== 期重叠族配置落地完成 ==';
