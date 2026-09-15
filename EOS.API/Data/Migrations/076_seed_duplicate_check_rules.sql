-- ============================================================================
-- EOS.ERP migration 077: duplicate-check 目录实例落地（mou-assess / hr-employee / curr）
-- ----------------------------------------------------------------------------
-- duplicate-check 的 entity 模式在同一语句内定位「当前单据行」与「候选行」：
--   M  = 当前单据主表行（由主表主键参数限定）；
--   D  = 当前单据明细行（键来源声明为 DETAIL 时引入）；
--   X  = 候选表行（待检出的重复行）。
-- 参数分工：keyFields/keySource 描述候选行的唯一键来源；excludeSelf.keyFields 描述候选行
-- 主键列，按当前单据主键排除自身；filter 为作用于候选行的闭式条件；diagnostics 为回填
-- 消息占位符的候选行列。既有 mou-assess 行沿用旧形态（excludeSelf.source 兼作键来源与
-- 自排除来源），其 keyFields 与 source.fields 数量本就不一致，一并改写为新形态。
--
-- 仅落配置工作区表；配置随模块重新发布后进入 Definition 快照并对保存生效。
-- 命名全大写；幂等：按 模块+阶段+顺序 存在则更新，不存在则插入。
-- ============================================================================

SET NOCOUNT ON;

DECLARE @GUARD_MESSAGE NVARCHAR(400) = N'本脚本只能在 EOS.ERP 数据库内执行，当前库为 ' + DB_NAME() + N'。';
IF DB_NAME() <> N'EOS.ERP'
    THROW 50000, @GUARD_MESSAGE, 1;

PRINT N'== 开始落地 duplicate-check 目录实例 ==';

-- 排除在职以外的记录后按员工工号唯一：工号重复时提示占用者。
DECLARE @HR_EMPLOYEE_PARAM NVARCHAR(MAX) = N'{"mode":"entity","table":"HR_EMPLOYEE",'
    + N'"keyFields":["EMP_NO"],"excludeSelf":{"keyFields":["EMP_ID"]},'
    + N'"filter":{"logic":"AND","items":[{"type":"VALUE-NEQ","field":{"scope":"TARGET","field":"STATE"},"value":5}]},'
    + N'"diagnostics":["EMP_NO","EMP_NAME"]}';
DECLARE @HR_EMPLOYEE_MESSAGE NVARCHAR(500) =
    NCHAR(13) + NCHAR(10) + N'员工工号：{EMP_NO}' + NCHAR(13) + NCHAR(10) + N'已分配给：{EMP_NAME}';

DECLARE @HR_MODULES TABLE (MODULE_ID INT PRIMARY KEY);
INSERT INTO @HR_MODULES (MODULE_ID) VALUES (180102), (180105), (180110), (180111);

DECLARE @MODULE_ID INT;
DECLARE cur CURSOR LOCAL FAST_FORWARD FOR SELECT MODULE_ID FROM @HR_MODULES;
OPEN cur;
FETCH NEXT FROM cur INTO @MODULE_ID;
WHILE @@FETCH_STATUS = 0
BEGIN
    IF EXISTS (SELECT 1 FROM dbo.MODULE_VALIDATION_RULE WHERE MODULE_ID = @MODULE_ID AND STAGE = N'SAVE' AND SEQ = 1)
        UPDATE dbo.MODULE_VALIDATION_RULE
        SET VALIDATION_KEY = N'duplicate-check', ENABLED = 1,
            PARAM_STRUCT = @HR_EMPLOYEE_PARAM, MESSAGE = @HR_EMPLOYEE_MESSAGE,
            REMARK = N'员工工号唯一（排除离职等非在职记录）', SOURCE_REF = N'hr-employee',
            LAST_UPDATE_BY = N'migration-077', LAST_UPDATE_DATE = SYSDATETIME()
        WHERE MODULE_ID = @MODULE_ID AND STAGE = N'SAVE' AND SEQ = 1;
    ELSE
        INSERT INTO dbo.MODULE_VALIDATION_RULE
            (MODULE_ID, STAGE, SEQ, VALIDATION_KEY, ENABLED, PARAM_STRUCT, MESSAGE, REMARK, SOURCE_REF, CREATE_PERSON, CREATE_DATE)
        VALUES
            (@MODULE_ID, N'SAVE', 1, N'duplicate-check', 1, @HR_EMPLOYEE_PARAM, @HR_EMPLOYEE_MESSAGE,
             N'员工工号唯一（排除离职等非在职记录）', N'hr-employee', N'migration-077', SYSDATETIME());

    FETCH NEXT FROM cur INTO @MODULE_ID;
END
CLOSE cur;
DEALLOCATE cur;

-- 本位币唯一：本位币标记为真时不得存在另一种本位币，提示已在用的币别。
DECLARE @CURR_PARAM NVARCHAR(MAX) = N'{"mode":"entity","table":"CURR",'
    + N'"keyFields":["IS_BASE"],"excludeSelf":{"keyFields":["CURR_ID"]},'
    + N'"filter":{"logic":"AND","items":[{"type":"VALUE-EQ","field":{"scope":"TARGET","field":"IS_BASE"},"value":true}]},'
    + N'"diagnostics":["CURR_ID"]}';
DECLARE @CURR_MESSAGE NVARCHAR(500) = N'已将币别 [{CURR_ID}] 设为本位币，不能存在两种本位币';

IF EXISTS (SELECT 1 FROM dbo.MODULE_VALIDATION_RULE WHERE MODULE_ID = 110103 AND STAGE = N'SAVE' AND SEQ = 1)
    UPDATE dbo.MODULE_VALIDATION_RULE
    SET VALIDATION_KEY = N'duplicate-check', ENABLED = 1,
        PARAM_STRUCT = @CURR_PARAM, MESSAGE = @CURR_MESSAGE,
        REMARK = N'本位币唯一', SOURCE_REF = N'curr',
        LAST_UPDATE_BY = N'migration-077', LAST_UPDATE_DATE = SYSDATETIME()
    WHERE MODULE_ID = 110103 AND STAGE = N'SAVE' AND SEQ = 1;
ELSE
    INSERT INTO dbo.MODULE_VALIDATION_RULE
        (MODULE_ID, STAGE, SEQ, VALIDATION_KEY, ENABLED, PARAM_STRUCT, MESSAGE, REMARK, SOURCE_REF, CREATE_PERSON, CREATE_DATE)
    VALUES
        (110103, N'SAVE', 1, N'duplicate-check', 1, @CURR_PARAM, @CURR_MESSAGE,
         N'本位币唯一', N'curr', N'migration-077', SYSDATETIME());

-- 料号开模评估资料唯一：同一料号只允许一笔评估资料。
DECLARE @MOU_ASSESS_PARAM NVARCHAR(MAX) = N'{"mode":"entity","table":"MOU_ASSESS_M",'
    + N'"keyFields":["PRO_NO"],"excludeSelf":{"keyFields":["ASSESS_TYPE","ASSESS_NO"]}}';

UPDATE dbo.MODULE_VALIDATION_RULE
SET PARAM_STRUCT = @MOU_ASSESS_PARAM, ENABLED = 1,
    LAST_UPDATE_BY = N'migration-077', LAST_UPDATE_DATE = SYSDATETIME()
WHERE MODULE_ID = 2914 AND STAGE = N'SAVE' AND VALIDATION_KEY = N'duplicate-check';

PRINT N'-- duplicate-check 实例现状 --';
SELECT CONCAT(N'  module=', MODULE_ID, N' stage=', STAGE, N' seq=', SEQ,
              N' enabled=', ENABLED, N' source=', ISNULL(SOURCE_REF, N'-')) AS SUMMARY
FROM dbo.MODULE_VALIDATION_RULE
WHERE VALIDATION_KEY = N'duplicate-check'
ORDER BY MODULE_ID;

PRINT N'== duplicate-check 目录实例落地完成 ==';
