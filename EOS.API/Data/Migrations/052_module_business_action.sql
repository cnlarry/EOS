-- ============================================================================
-- EOS.ERP migration 053: MODULE_BUSINESS_ACTION — 单据业务动作配置
-- ----------------------------------------------------------------------------
-- 单据业务动作的模块级工作区配置：
--   1. MODULE_BUSINESS_ACTION        业务动作（模块 × 事件 × 顺序 一行）
--   2. MODULE_BUSINESS_ACTION_OP     公式行（业务动作展开后的字段级运算）
--
-- 设计约定：
--   - 公式型效果（field-accumulate / completion-close / stamp-last-activity /
--     adjust-projection / set-state）以公式行为主存储；服务型效果
--     （inventory-move / meta-link / flow-trigger / historic-sproc）无公式行，--     参数存 MODULE_BUSINESS_ACTION.PARAM_STRUCT。
--   - 两表为模块的“工作区配置”：2301 保存后标记 WORKBENCH_MODULE_DIRTY，
--     发布时随 WORKBENCH_DEFINITION_SNAPSHOT.DEFINITION_JSON 版本化。
--   - EVENT_CODE / EFFECT_KEY / OP_CODE / SOURCE_SCOPE / SOURCE_AGG 为封闭枚举，
--     由服务端注册表与保存即校验保证，不在库上加 CHECK（枚举允许受控演进）。
--   - 子表不重复审计列：审计与版本归属业务动作行/Definition 快照。
-- 幂等：IF OBJECT_ID 守卫，可重复执行。
-- ============================================================================

SET NOCOUNT ON;

DECLARE @GUARD_MESSAGE NVARCHAR(400) = N'本脚本只能在 EOS.ERP 数据库内执行，当前库为 ' + DB_NAME() + N'。';
IF DB_NAME() <> N'EOS.ERP'
    THROW 50000, @GUARD_MESSAGE, 1;

PRINT N'== 开始创建 EOS.ERP 单据业务动作配置表 ==';

IF OBJECT_ID(N'dbo.MODULE_BUSINESS_ACTION', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.MODULE_BUSINESS_ACTION
    (
        ACTION_ID          BIGINT         IDENTITY(1,1) NOT NULL,
        MODULE_ID          INT            NOT NULL,
        EVENT_CODE         NVARCHAR(30)   NOT NULL,
        SEQ                INT            NOT NULL,
        EFFECT_KEY         NVARCHAR(50)   NOT NULL,
        EFFECT_NAME        NVARCHAR(200)  NULL,
        ENABLED            BIT            NOT NULL CONSTRAINT DF_MODULE_BUSINESS_ACTION_ENABLED DEFAULT (1),
        FAIL_MODE          NVARCHAR(10)   NOT NULL CONSTRAINT DF_MODULE_BUSINESS_ACTION_FAIL_MODE DEFAULT (N'BLOCK'),
        CONDITION_STRUCT   NVARCHAR(MAX)  NULL,
        PARAM_STRUCT       NVARCHAR(MAX)  NULL,
        REVERSE_STRUCT     NVARCHAR(MAX)  NULL,
        REMARK             NVARCHAR(500)  NULL,
        SOURCE_REF         NVARCHAR(100)  NULL,
        CREATE_PERSON      NVARCHAR(40)   NULL,
        CREATE_DATE        DATETIME2(3)   NOT NULL CONSTRAINT DF_MODULE_BUSINESS_ACTION_CREATE_DATE DEFAULT (SYSDATETIME()),
        LAST_UPDATE_BY     NVARCHAR(40)   NULL,
        LAST_UPDATE_DATE   DATETIME2(3)   NULL
    );

    ALTER TABLE dbo.MODULE_BUSINESS_ACTION ADD CONSTRAINT PK_MODULE_BUSINESS_ACTION PRIMARY KEY CLUSTERED (ACTION_ID);
    ALTER TABLE dbo.MODULE_BUSINESS_ACTION ADD CONSTRAINT UQ_MODULE_BUSINESS_ACTION_EVENT UNIQUE (MODULE_ID, EVENT_CODE, SEQ);
    ALTER TABLE dbo.MODULE_BUSINESS_ACTION ADD CONSTRAINT FK_MODULE_BUSINESS_ACTION_MODULE
        FOREIGN KEY (MODULE_ID) REFERENCES dbo.MODULES (M_IDX) ON DELETE CASCADE;
END

IF OBJECT_ID(N'dbo.MODULE_BUSINESS_ACTION_OP', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.MODULE_BUSINESS_ACTION_OP
    (
        OP_ID             BIGINT         IDENTITY(1,1) NOT NULL,
        ACTION_ID         BIGINT         NOT NULL,
        OP_SEQ            INT            NOT NULL,
        TARGET_TABLE      NVARCHAR(64)   NOT NULL,
        TARGET_FIELD      NVARCHAR(64)   NOT NULL,
        OP_CODE           NVARCHAR(20)   NOT NULL,
        SOURCE_SCOPE      NVARCHAR(10)   NOT NULL,
        SOURCE_FIELD      NVARCHAR(64)   NULL,
        SOURCE_AGG        NVARCHAR(10)   NULL,
        SOURCE_CONSTANT   NVARCHAR(MAX)  NULL,
        MATCH_STRUCT      NVARCHAR(MAX)  NULL,
        CONDITION_STRUCT  NVARCHAR(MAX)  NULL,
        REMARK            NVARCHAR(200)  NULL
    );

    ALTER TABLE dbo.MODULE_BUSINESS_ACTION_OP ADD CONSTRAINT PK_MODULE_BUSINESS_ACTION_OP PRIMARY KEY CLUSTERED (OP_ID);
    ALTER TABLE dbo.MODULE_BUSINESS_ACTION_OP ADD CONSTRAINT UQ_MODULE_BUSINESS_ACTION_OP_SEQ UNIQUE (ACTION_ID, OP_SEQ);
    ALTER TABLE dbo.MODULE_BUSINESS_ACTION_OP ADD CONSTRAINT FK_MODULE_BUSINESS_ACTION_OP_ACTION
        FOREIGN KEY (ACTION_ID) REFERENCES dbo.MODULE_BUSINESS_ACTION (ACTION_ID) ON DELETE CASCADE;
END

EXEC sys.sp_addextendedproperty @name=N'MS_Description', @value=N'单据业务动作配置：模块 × 事件 × 顺序下的一条可配置动作；公式型效果在 MODULE_BUSINESS_ACTION_OP 展开，服务型效果参数存 PARAM_STRUCT。', @level0type=N'SCHEMA', @level0name=N'dbo', @level1type=N'TABLE', @level1name=N'MODULE_BUSINESS_ACTION';
EXEC sys.sp_addextendedproperty @name=N'MS_Description', @value=N'业务动作行主键。', @level0type=N'SCHEMA', @level0name=N'dbo', @level1type=N'TABLE', @level1name=N'MODULE_BUSINESS_ACTION', @level2type=N'COLUMN', @level2name=N'ACTION_ID';
EXEC sys.sp_addextendedproperty @name=N'MS_Description', @value=N'模块号（MODULES.M_IDX）；两表皆空模块禁止有行。', @level0type=N'SCHEMA', @level0name=N'dbo', @level1type=N'TABLE', @level1name=N'MODULE_BUSINESS_ACTION', @level2type=N'COLUMN', @level2name=N'MODULE_ID';
EXEC sys.sp_addextendedproperty @name=N'MS_Description', @value=N'触发事件：SAVE / APPROVE_EFFECT / DEAPPROVE / ENDCASE / UNENDCASE。', @level0type=N'SCHEMA', @level0name=N'dbo', @level1type=N'TABLE', @level1name=N'MODULE_BUSINESS_ACTION', @level2type=N'COLUMN', @level2name=N'EVENT_CODE';
EXEC sys.sp_addextendedproperty @name=N'MS_Description', @value=N'同一事件内执行顺序（1 起）。', @level0type=N'SCHEMA', @level0name=N'dbo', @level1type=N'TABLE', @level1name=N'MODULE_BUSINESS_ACTION', @level2type=N'COLUMN', @level2name=N'SEQ';
EXEC sys.sp_addextendedproperty @name=N'MS_Description', @value=N'注册效果键（field-accumulate / completion-close / inventory-move / …），服务端注册表校验。', @level0type=N'SCHEMA', @level0name=N'dbo', @level1type=N'TABLE', @level1name=N'MODULE_BUSINESS_ACTION', @level2type=N'COLUMN', @level2name=N'EFFECT_KEY';
EXEC sys.sp_addextendedproperty @name=N'MS_Description', @value=N'人类可读名称（缺省取注册目录默认）。', @level0type=N'SCHEMA', @level0name=N'dbo', @level1type=N'TABLE', @level1name=N'MODULE_BUSINESS_ACTION', @level2type=N'COLUMN', @level2name=N'EFFECT_NAME';
EXEC sys.sp_addextendedproperty @name=N'MS_Description', @value=N'是否启用。', @level0type=N'SCHEMA', @level0name=N'dbo', @level1type=N'TABLE', @level1name=N'MODULE_BUSINESS_ACTION', @level2type=N'COLUMN', @level2name=N'ENABLED';
EXEC sys.sp_addextendedproperty @name=N'MS_Description', @value=N'失败模式：BLOCK=失败整链回滚；WARN=仅警告继续。', @level0type=N'SCHEMA', @level0name=N'dbo', @level1type=N'TABLE', @level1name=N'MODULE_BUSINESS_ACTION', @level2type=N'COLUMN', @level2name=N'FAIL_MODE';
EXEC sys.sp_addextendedproperty @name=N'MS_Description', @value=N'步骤级共享条件（结构化 JSON：SYSSS 开关 / 状态守卫 / 字段比较，封闭算子集）。', @level0type=N'SCHEMA', @level0name=N'dbo', @level1type=N'TABLE', @level1name=N'MODULE_BUSINESS_ACTION', @level2type=N'COLUMN', @level2name=N'CONDITION_STRUCT';
EXEC sys.sp_addextendedproperty @name=N'MS_Description', @value=N'服务型效果参数（结构化 JSON，按 EFFECT_KEY Schema 校验）；公式型效果可为空。', @level0type=N'SCHEMA', @level0name=N'dbo', @level1type=N'TABLE', @level1name=N'MODULE_BUSINESS_ACTION', @level2type=N'COLUMN', @level2name=N'PARAM_STRUCT';
EXEC sys.sp_addextendedproperty @name=N'MS_Description', @value=N'整步解批反向覆盖（不对称语义，如“批核按数量判断、解批无条件清完工码”）。', @level0type=N'SCHEMA', @level0name=N'dbo', @level1type=N'TABLE', @level1name=N'MODULE_BUSINESS_ACTION', @level2type=N'COLUMN', @level2name=N'REVERSE_STRUCT';
EXEC sys.sp_addextendedproperty @name=N'MS_Description', @value=N'给业务看的说明。', @level0type=N'SCHEMA', @level0name=N'dbo', @level1type=N'TABLE', @level1name=N'MODULE_BUSINESS_ACTION', @level2type=N'COLUMN', @level2name=N'REMARK';
EXEC sys.sp_addextendedproperty @name=N'MS_Description', @value=N'溯源：配置来源标识（源过程名/段落等）。', @level0type=N'SCHEMA', @level0name=N'dbo', @level1type=N'TABLE', @level1name=N'MODULE_BUSINESS_ACTION', @level2type=N'COLUMN', @level2name=N'SOURCE_REF';
EXEC sys.sp_addextendedproperty @name=N'MS_Description', @value=N'建立人。', @level0type=N'SCHEMA', @level0name=N'dbo', @level1type=N'TABLE', @level1name=N'MODULE_BUSINESS_ACTION', @level2type=N'COLUMN', @level2name=N'CREATE_PERSON';
EXEC sys.sp_addextendedproperty @name=N'MS_Description', @value=N'建立时间。', @level0type=N'SCHEMA', @level0name=N'dbo', @level1type=N'TABLE', @level1name=N'MODULE_BUSINESS_ACTION', @level2type=N'COLUMN', @level2name=N'CREATE_DATE';
EXEC sys.sp_addextendedproperty @name=N'MS_Description', @value=N'修改人。', @level0type=N'SCHEMA', @level0name=N'dbo', @level1type=N'TABLE', @level1name=N'MODULE_BUSINESS_ACTION', @level2type=N'COLUMN', @level2name=N'LAST_UPDATE_BY';
EXEC sys.sp_addextendedproperty @name=N'MS_Description', @value=N'修改时间。', @level0type=N'SCHEMA', @level0name=N'dbo', @level1type=N'TABLE', @level1name=N'MODULE_BUSINESS_ACTION', @level2type=N'COLUMN', @level2name=N'LAST_UPDATE_DATE';

EXEC sys.sp_addextendedproperty @name=N'MS_Description', @value=N'业务动作公式行：动作展开后的字段级运算，lint / 执行 / 对拍的最小单元。', @level0type=N'SCHEMA', @level0name=N'dbo', @level1type=N'TABLE', @level1name=N'MODULE_BUSINESS_ACTION_OP';
EXEC sys.sp_addextendedproperty @name=N'MS_Description', @value=N'公式行主键。', @level0type=N'SCHEMA', @level0name=N'dbo', @level1type=N'TABLE', @level1name=N'MODULE_BUSINESS_ACTION_OP', @level2type=N'COLUMN', @level2name=N'OP_ID';
EXEC sys.sp_addextendedproperty @name=N'MS_Description', @value=N'所属业务动作（MODULE_BUSINESS_ACTION.ACTION_ID，级联删除）。', @level0type=N'SCHEMA', @level0name=N'dbo', @level1type=N'TABLE', @level1name=N'MODULE_BUSINESS_ACTION_OP', @level2type=N'COLUMN', @level2name=N'ACTION_ID';
EXEC sys.sp_addextendedproperty @name=N'MS_Description', @value=N'动作内公式行顺序。', @level0type=N'SCHEMA', @level0name=N'dbo', @level1type=N'TABLE', @level1name=N'MODULE_BUSINESS_ACTION_OP', @level2type=N'COLUMN', @level2name=N'OP_SEQ';
EXEC sys.sp_addextendedproperty @name=N'MS_Description', @value=N'目标表（必须物理存在，保存即校验）。', @level0type=N'SCHEMA', @level0name=N'dbo', @level1type=N'TABLE', @level1name=N'MODULE_BUSINESS_ACTION_OP', @level2type=N'COLUMN', @level2name=N'TARGET_TABLE';
EXEC sys.sp_addextendedproperty @name=N'MS_Description', @value=N'目标字段（必须物理存在，保存即校验）。', @level0type=N'SCHEMA', @level0name=N'dbo', @level1type=N'TABLE', @level1name=N'MODULE_BUSINESS_ACTION_OP', @level2type=N'COLUMN', @level2name=N'TARGET_FIELD';
EXEC sys.sp_addextendedproperty @name=N'MS_Description', @value=N'运算：ACCUM / DEACCUM / ASSIGN / ASSIGN_MAX / ASSIGN_MIN / APPEND_UNIQ / SET_WHEN。', @level0type=N'SCHEMA', @level0name=N'dbo', @level1type=N'TABLE', @level1name=N'MODULE_BUSINESS_ACTION_OP', @level2type=N'COLUMN', @level2name=N'OP_CODE';
EXEC sys.sp_addextendedproperty @name=N'MS_Description', @value=N'源范围：MASTER / DETAIL / CONSTANT。', @level0type=N'SCHEMA', @level0name=N'dbo', @level1type=N'TABLE', @level1name=N'MODULE_BUSINESS_ACTION_OP', @level2type=N'COLUMN', @level2name=N'SOURCE_SCOPE';
EXEC sys.sp_addextendedproperty @name=N'MS_Description', @value=N'源字段（CONSTANT 时为空）。', @level0type=N'SCHEMA', @level0name=N'dbo', @level1type=N'TABLE', @level1name=N'MODULE_BUSINESS_ACTION_OP', @level2type=N'COLUMN', @level2name=N'SOURCE_FIELD';
EXEC sys.sp_addextendedproperty @name=N'MS_Description', @value=N'源聚合：NONE / SUM / MAX / MIN / DISTINCT。', @level0type=N'SCHEMA', @level0name=N'dbo', @level1type=N'TABLE', @level1name=N'MODULE_BUSINESS_ACTION_OP', @level2type=N'COLUMN', @level2name=N'SOURCE_AGG';
EXEC sys.sp_addextendedproperty @name=N'MS_Description', @value=N'常量值（SOURCE_SCOPE=CONSTANT 时使用，如 SYSTEM / 当前时间标记）。', @level0type=N'SCHEMA', @level0name=N'dbo', @level1type=N'TABLE', @level1name=N'MODULE_BUSINESS_ACTION_OP', @level2type=N'COLUMN', @level2name=N'SOURCE_CONSTANT';
EXEC sys.sp_addextendedproperty @name=N'MS_Description', @value=N'定位键 JSON：目标行匹配条件（引用已登记单据关系）。', @level0type=N'SCHEMA', @level0name=N'dbo', @level1type=N'TABLE', @level1name=N'MODULE_BUSINESS_ACTION_OP', @level2type=N'COLUMN', @level2name=N'MATCH_STRUCT';
EXEC sys.sp_addextendedproperty @name=N'MS_Description', @value=N'公式行级条件（如完成判定“数量与备品分别达到”）。', @level0type=N'SCHEMA', @level0name=N'dbo', @level1type=N'TABLE', @level1name=N'MODULE_BUSINESS_ACTION_OP', @level2type=N'COLUMN', @level2name=N'CONDITION_STRUCT';
EXEC sys.sp_addextendedproperty @name=N'MS_Description', @value=N'给业务看的行级说明。', @level0type=N'SCHEMA', @level0name=N'dbo', @level1type=N'TABLE', @level1name=N'MODULE_BUSINESS_ACTION_OP', @level2type=N'COLUMN', @level2name=N'REMARK';

PRINT N'== EOS.ERP 单据业务动作配置表创建完成 ==';
