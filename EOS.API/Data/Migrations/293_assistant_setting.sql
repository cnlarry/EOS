-- ============================================================================
-- EOS.ERP migration 293: 助手设置表（dbo.ASSISTANT_SETTING）+ 模块 3105 助手设置
-- ----------------------------------------------------------------------------
--  来源：ADR-030 §8 ——「工作助手的任何配置都不应该在 appsettings 中」。
--  模型与供应商已在 3102 落库（迁移 292）；这一条收口剩下那部分**全局策略**：
--  系统提示词、自动提炼开关、日上限、单价兜底、熔断阈值与冷却、每轮预留额。
--
--  表结构照「系统参数纵向化」（迁移 207 的 dbo.SYSSS / ADR-017）的既有范式：
--  一行一个参数、值为文本、用 VALUE_TYPE 说明怎么解析。这样加参数只加代码里的默认值与页面字段，
--  不需要改表；也不会出现"为每个参数加一列"的横向膨胀。
--
--  **不预置任何行**：缺行 = 用代码默认值。于是"恢复默认"就是一个删除动作，
--  而且升级调整默认值时，没被管理员改过的参数会**自动跟着走**——这正是想要的语义
--  （管理员改过的那些必须留住，没改过的不该被旧值钉死）。
--
--  幂等：建表与模块登记都按"不存在才做"；授权行按"不存在才插"。可重复执行。
-- ============================================================================
SET NOCOUNT ON;
SET XACT_ABORT ON;
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET CONCAT_NULL_YIELDS_NULL ON;
SET ARITHABORT ON;

DECLARE @GuardMessage NVARCHAR(400) = N'本脚本只能在 EOS.ERP 数据库内执行，当前库为 ' + DB_NAME() + N'。';
IF DB_NAME() <> N'EOS.ERP'
    THROW 51700, @GuardMessage, 1;

IF NOT EXISTS (SELECT 1 FROM dbo.MODULES WHERE M_IDX = 31)
    THROW 51701, N'根组 31 工作助手管理不存在，迁移中止（先跑迁移 288）。', 1;

IF NOT EXISTS (SELECT 1 FROM dbo.MODULES WHERE M_IDX = 2306)
    THROW 51702, N'参照模块 2306 用户权限设定不存在，迁移中止（授权镜像以它为准）。', 1;

BEGIN TRANSACTION;

/* ---------- 1. 设置表 ---------- */
IF OBJECT_ID(N'dbo.ASSISTANT_SETTING', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.ASSISTANT_SETTING (
        -- 参数键与代码里的 AssistantSettingKeys 常量一一对应（如 SystemPrompt / UserDailyCapYuan）
        [PARAM_KEY]   NVARCHAR(60)   NOT NULL CONSTRAINT [PK_ASSISTANT_SETTING] PRIMARY KEY,
        -- 值统一存文本，按 VALUE_TYPE 解析：纵向表的代价，换来"加参数不改表"
        [PARAM_VALUE] NVARCHAR(4000) NULL,
        [VALUE_TYPE]  NVARCHAR(20)   NOT NULL CONSTRAINT [DF_ASSISTANT_SETTING_TYPE] DEFAULT (N'string'),
        [DESC_TEXT]   NVARCHAR(200)  NULL,
        [UPDATED_AT]  DATETIME2(3)   NOT NULL CONSTRAINT [DF_ASSISTANT_SETTING_UPDATED] DEFAULT (SYSUTCDATETIME()),
        [UPDATED_BY]  NVARCHAR(50)   NULL,
        -- 类型白名单：解析器只认这几种，写进来别的会静默变成"用默认值"而没人发现
        CONSTRAINT [CK_ASSISTANT_SETTING_TYPE] CHECK (
            [VALUE_TYPE] IN (N'string', N'bool', N'int', N'decimal', N'long'))
    );

    PRINT N'== 新建表 dbo.ASSISTANT_SETTING（缺行 = 用代码默认值）==';
END
ELSE
    PRINT N'== 表 dbo.ASSISTANT_SETTING 已存在，跳过 ==';

/* ---------- 2. 子模块 3105 助手设置 ---------- */
IF NOT EXISTS (SELECT 1 FROM dbo.MODULES WHERE M_IDX = 3105)
BEGIN
    INSERT INTO dbo.MODULES (M_IDX, M_DESC, M_URL, M_P_IDX, M_ROOT_IDX, SORT_IDX, M_TAG, MASTER_TABLE)
    VALUES (3105, N'助手设置', N'/admin/assistant/settings', 31, 31, 30, 1, NULL);
    PRINT N'== 新增模块 3105 助手设置（/admin/assistant/settings）==';
END
ELSE
    PRINT N'== 模块 3105 已存在，跳过 ==';

/* ---------- 3. 组权限镜像（照 2306 的口径）---------- */
INSERT INTO dbo.SYSDH
    (G_IDX, M_IDX, EXEC_TAG, ADDNEW_TAG, DELETE_TAG, EDIT_TAG, REPORT_TAG, COST_TAG, SETUP_TAG, SECRECY_TAG,
     ENDCASE_TAG, UNENDCASE_TAG, OTHER1_TAG, OTHER2_TAG, OTHER3_TAG, OTHER4_TAG,
     DENY_VIEW_FIELD_MASTER, DENY_VIEW_FIELD_DETAIL, DENY_NEW_FIELD_MASTER, DENY_NEW_FIELD_DETAIL,
     DENY_MODI_FIELD_MASTER, DENY_MODI_FIELD_DETAIL, DATA_FILTER, CI, OPERFLAG,
     APPROVE_TAG, DEAPPROVE_TAG, FILE_VIEW_TAG, FILE_UPDA_TAG, FILE_EDIT_TAG, FILE_DELE_TAG,
     FORM_DESIGN_TAG, MODULE_CONFIG_TAG)
SELECT h.G_IDX, 3105, h.EXEC_TAG, h.ADDNEW_TAG, h.DELETE_TAG, h.EDIT_TAG, h.REPORT_TAG, h.COST_TAG,
       h.SETUP_TAG, h.SECRECY_TAG, h.ENDCASE_TAG, h.UNENDCASE_TAG, h.OTHER1_TAG, h.OTHER2_TAG,
       h.OTHER3_TAG, h.OTHER4_TAG, h.DENY_VIEW_FIELD_MASTER, h.DENY_VIEW_FIELD_DETAIL,
       h.DENY_NEW_FIELD_MASTER, h.DENY_NEW_FIELD_DETAIL, h.DENY_MODI_FIELD_MASTER, h.DENY_MODI_FIELD_DETAIL,
       h.DATA_FILTER, h.CI, h.OPERFLAG, h.APPROVE_TAG, h.DEAPPROVE_TAG, h.FILE_VIEW_TAG, h.FILE_UPDA_TAG,
       h.FILE_EDIT_TAG, h.FILE_DELE_TAG, h.FORM_DESIGN_TAG, h.MODULE_CONFIG_TAG
FROM dbo.SYSDH h
WHERE h.M_IDX = 2306
  AND NOT EXISTS (SELECT 1 FROM dbo.SYSDH x WHERE x.G_IDX = h.G_IDX AND x.M_IDX = 3105);

PRINT N'== 已镜像组权限 ' + CONVERT(NVARCHAR(10), @@ROWCOUNT) + N' 行 ==';

/* ---------- 4. 个人权限镜像（admin 靠这一步才看得见新菜单）---------- */
INSERT INTO dbo.SYSDD
    (USER_ID, M_IDX, EXEC_TAG, ADDNEW_TAG, DELETE_TAG, EDIT_TAG, REPORT_TAG, COST_TAG, SETUP_TAG, SECRECY_TAG,
     ENDCASE_TAG, UNENDCASE_TAG, OTHER1_TAG, OTHER2_TAG, OTHER3_TAG, OTHER4_TAG,
     DENY_VIEW_FIELD_MASTER, DENY_VIEW_FIELD_DETAIL, DENY_NEW_FIELD_MASTER, DENY_NEW_FIELD_DETAIL,
     DENY_MODI_FIELD_MASTER, DENY_MODI_FIELD_DETAIL, DATA_FILTER, CI, OPERFLAG,
     APPROVE_TAG, DEAPPROVE_TAG, FILE_VIEW_TAG, FILE_UPDA_TAG, FILE_EDIT_TAG, FILE_DELE_TAG,
     FORM_DESIGN_TAG, MODULE_CONFIG_TAG)
SELECT d.USER_ID, 3105, d.EXEC_TAG, d.ADDNEW_TAG, d.DELETE_TAG, d.EDIT_TAG, d.REPORT_TAG, d.COST_TAG,
       d.SETUP_TAG, d.SECRECY_TAG, d.ENDCASE_TAG, d.UNENDCASE_TAG, d.OTHER1_TAG, d.OTHER2_TAG,
       d.OTHER3_TAG, d.OTHER4_TAG, d.DENY_VIEW_FIELD_MASTER, d.DENY_VIEW_FIELD_DETAIL,
       d.DENY_NEW_FIELD_MASTER, d.DENY_NEW_FIELD_DETAIL, d.DENY_MODI_FIELD_MASTER, d.DENY_MODI_FIELD_DETAIL,
       d.DATA_FILTER, d.CI, d.OPERFLAG, d.APPROVE_TAG, d.DEAPPROVE_TAG, d.FILE_VIEW_TAG, d.FILE_UPDA_TAG,
       d.FILE_EDIT_TAG, d.FILE_DELE_TAG, d.FORM_DESIGN_TAG, d.MODULE_CONFIG_TAG
FROM dbo.SYSDD d
WHERE d.M_IDX = 2306
  AND NOT EXISTS (SELECT 1 FROM dbo.SYSDD x WHERE x.USER_ID = d.USER_ID AND x.M_IDX = 3105);

PRINT N'== 已镜像个人权限 ' + CONVERT(NVARCHAR(10), @@ROWCOUNT) + N' 行 ==';

/* ---------- 5. 收口断言 ---------- */
IF OBJECT_ID(N'dbo.ASSISTANT_SETTING', N'U') IS NULL
    THROW 51703, N'表 dbo.ASSISTANT_SETTING 未就位，迁移中止。', 1;

IF NOT EXISTS (SELECT 1 FROM dbo.MODULES
               WHERE M_IDX = 3105 AND M_P_IDX = 31 AND M_ROOT_IDX = 31
                 AND M_URL = N'/admin/assistant/settings' AND M_TAG = 1)
    THROW 51704, N'模块 3105 未就位或 URL 不是 /admin/assistant/settings，迁移中止。', 1;

-- 密钥列绝不该存在：本表只放策略参数，密钥永远只走环境变量（ADR-030 §3）
IF EXISTS (SELECT 1 FROM sys.columns
           WHERE object_id = OBJECT_ID(N'dbo.ASSISTANT_SETTING')
             AND name IN (N'API_KEY', N'APIKEY', N'SECRET', N'TOKEN'))
    THROW 51705, N'dbo.ASSISTANT_SETTING 里出现了疑似密钥列——密钥不入库（ADR-030 §3），迁移中止。', 1;

DECLARE @RefUsers INT = (SELECT COUNT(*) FROM dbo.SYSDD WHERE M_IDX = 2306);
DECLARE @NewUsers INT = (SELECT COUNT(*) FROM dbo.SYSDD WHERE M_IDX = 3105);
IF @NewUsers < @RefUsers
    THROW 51706, N'模块 3105 的个人权限行数少于参照模块 2306，迁移中止（admin 会看不到新菜单）。', 1;

COMMIT TRANSACTION;

PRINT N'== 收口：助手设置（3105）已就位；表内无行 = 全部使用代码默认值 ==';
