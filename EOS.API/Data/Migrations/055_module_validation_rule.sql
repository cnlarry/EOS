-- ============================================================================
-- EOS.ERP migration 056: MODULE_VALIDATION_RULE — 模块校验规则工作区表
-- ----------------------------------------------------------------------------
-- 模块校验规则工作区表：校验规则与业务动作同构，均为模块定义数据的一部分；
--   - 校验规则 = 模块 × 阶段 × 顺序 一行（工作区可编辑），发布时随
--     WORKBENCH_DEFINITION_SNAPSHOT.DEFINITION_JSON.validationRules 版本化；
--   - PARAM_STRUCT 为闭式 JSON（按 VALIDATION_KEY 的 Schema 保存即校验），不拆子表；
--   - 两表皆空模块禁止有行；核心默认校验（必填/金额复算/DF_VERIFY/状态守卫）不落此表。
-- 命名全大写；幂等：IF OBJECT_ID / COL_LENGTH 守卫。
-- ============================================================================

SET NOCOUNT ON;

DECLARE @GUARD_MESSAGE NVARCHAR(400) = N'本脚本只能在 EOS.ERP 数据库内执行，当前库为 ' + DB_NAME() + N'。';
IF DB_NAME() <> N'EOS.ERP'
    THROW 50000, @GUARD_MESSAGE, 1;

PRINT N'== 开始创建 EOS.ERP 模块校验规则表 ==';

IF OBJECT_ID(N'dbo.MODULE_VALIDATION_RULE', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.MODULE_VALIDATION_RULE
    (
        RULE_ID          BIGINT         IDENTITY(1,1) NOT NULL,
        MODULE_ID        INT            NOT NULL,
        STAGE            NVARCHAR(20)   NOT NULL,
        SEQ              INT            NOT NULL,
        VALIDATION_KEY   NVARCHAR(50)   NOT NULL,
        ENABLED          BIT            NOT NULL CONSTRAINT DF_MODULE_VALIDATION_RULE_ENABLED DEFAULT (1),
        PARAM_STRUCT     NVARCHAR(MAX)  NOT NULL,
        MESSAGE          NVARCHAR(500)  NULL,
        REMARK           NVARCHAR(500)  NULL,
        SOURCE_REF       NVARCHAR(100)  NULL,
        CREATE_PERSON    NVARCHAR(40)   NULL,
        CREATE_DATE      DATETIME2(3)   NOT NULL CONSTRAINT DF_MODULE_VALIDATION_RULE_CREATE_DATE DEFAULT (SYSDATETIME()),
        LAST_UPDATE_BY   NVARCHAR(40)   NULL,
        LAST_UPDATE_DATE DATETIME2(3)   NULL
    );

    ALTER TABLE dbo.MODULE_VALIDATION_RULE ADD CONSTRAINT PK_MODULE_VALIDATION_RULE PRIMARY KEY CLUSTERED (RULE_ID);
    ALTER TABLE dbo.MODULE_VALIDATION_RULE ADD CONSTRAINT UQ_MODULE_VALIDATION_RULE_STAGE UNIQUE (MODULE_ID, STAGE, SEQ);
    ALTER TABLE dbo.MODULE_VALIDATION_RULE ADD CONSTRAINT FK_MODULE_VALIDATION_RULE_MODULE
        FOREIGN KEY (MODULE_ID) REFERENCES dbo.MODULES (M_IDX) ON DELETE CASCADE;
END

IF NOT EXISTS (SELECT 1 FROM sys.extended_properties ep WHERE ep.major_id = OBJECT_ID(N'dbo.MODULE_VALIDATION_RULE') AND ep.minor_id = 0 AND ep.name = N'MS_Description')
    EXEC sys.sp_addextendedproperty @name=N'MS_Description', @value=N'模块校验规则工作区表：模块 × 阶段 × 顺序 一行；发布时并入 Definition JSON.validationRules 版本化。', @level0type=N'SCHEMA', @level0name=N'dbo', @level1type=N'TABLE', @level1name=N'MODULE_VALIDATION_RULE';

DECLARE @COL_DESC TABLE (COL_NAME NVARCHAR(128), COL_DESC NVARCHAR(400));
INSERT INTO @COL_DESC (COL_NAME, COL_DESC) VALUES
    (N'RULE_ID', N'规则行主键。'),
    (N'MODULE_ID', N'模块号（MODULES.M_IDX）；两表皆空模块禁止有行。'),
    (N'STAGE', N'触发阶段：SAVE / APPROVE / DEAPPROVE。'),
    (N'SEQ', N'同阶段内执行顺序（1 起，决定先报哪个错）。'),
    (N'VALIDATION_KEY', N'§14 校验模板 Key（qty-not-exceed / reference-exists / duplicate-check / …，服务端注册表校验）。'),
    (N'ENABLED', N'是否启用。'),
    (N'PARAM_STRUCT', N'模板参数（闭式 JSON，按 VALIDATION_KEY 的 Schema 保存即校验）。'),
    (N'MESSAGE', N'失败文案覆盖（缺省取模板默认）。'),
    (N'REMARK', N'给业务看的说明。'),
    (N'SOURCE_REF', N'溯源（如 pur-receive 族 / P_PUR_RECEIVE_CHECK）。'),
    (N'CREATE_PERSON', N'建立人。'),
    (N'CREATE_DATE', N'建立时间。'),
    (N'LAST_UPDATE_BY', N'修改人。'),
    (N'LAST_UPDATE_DATE', N'修改时间。');

DECLARE @col NVARCHAR(128), @desc NVARCHAR(400);
DECLARE cur CURSOR LOCAL FAST_FORWARD FOR SELECT COL_NAME, COL_DESC FROM @COL_DESC;
OPEN cur;
FETCH NEXT FROM cur INTO @col, @desc;
WHILE @@FETCH_STATUS = 0
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM sys.extended_properties ep
        WHERE ep.major_id = OBJECT_ID(N'dbo.MODULE_VALIDATION_RULE')
          AND ep.minor_id = COLUMNPROPERTY(OBJECT_ID(N'dbo.MODULE_VALIDATION_RULE'), @col, N'ColumnId')
          AND ep.name = N'MS_Description'
    )
        EXEC sys.sp_addextendedproperty @name=N'MS_Description', @value=@desc,
            @level0type=N'SCHEMA', @level0name=N'dbo',
            @level1type=N'TABLE', @level1name=N'MODULE_VALIDATION_RULE',
            @level2type=N'COLUMN', @level2name=@col;
    FETCH NEXT FROM cur INTO @col, @desc;
END
CLOSE cur;
DEALLOCATE cur;

PRINT N'== EOS.ERP 模块校验规则表创建完成 ==';
