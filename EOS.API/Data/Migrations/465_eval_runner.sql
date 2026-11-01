-- ============================================================================
-- EOS.ERP migration 465：应用内评估运行器（模块 3107 · 评估运行）
-- ----------------------------------------------------------------------------
--  背景（ADR-046 缺口 #4）：用例已经在库里（3106），失败也带了分诊，但"跑一轮"仍只能在
--  开发机上 `dotnet test`。客户运维没有仓库、没有 SDK，于是"评估"这件事依旧要回开发。
--
--  本迁移补两件事：
--   1. 结果落库：ASSISTANT_EVAL_RUN（一轮）/ ASSISTANT_EVAL_RESULT（逐用例结果）。
--      结果里**抄一份分诊**（FIX_AT / FIX_SUGGESTION / FIX_PERMISSION）：报告要能回答
--      "当时这一条该去哪里改"，用例日后被改掉不能让历史结论跟着变。
--   2. 承载页 3107：`/admin/assistant/eval`，权限走模块权限（谁跑评估由管理员在 2306 分配）。
--
--  只跑**确定性层**：判的是"用例与当前系统有没有漂移"（闭集、注册表、库内元数据），
--  不调用模型、不判回答对错（那需要判官，见 ADR-044）。零模型调用意味着零费用、可随时跑。
--
--  命名上刻意不用 CREATED_BY / CREATED_TIME：那两个列名属于单据生命周期列，
--  建在同一张表上会被 `check-lifecycle-columns` 要求 NOT NULL + DEFAULT（那是给单据表的约束）。
--  这里用 RUN_BY / STARTED_TIME / FINISHED_TIME，表是运行记录，不是单据。
-- ============================================================================
SET NOCOUNT ON;
SET XACT_ABORT ON;
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;

IF DB_NAME() <> N'EOS.ERP'
    THROW 51960, N'本脚本只允许在 EOS.ERP 数据库上执行。', 1;

IF OBJECT_ID(N'dbo.ASSISTANT_EVAL_CASE', N'U') IS NULL
    THROW 51961, N'ASSISTANT_EVAL_CASE 不存在（迁移 458 未执行），迁移终止。', 1;

/* ---------- 1. 一轮运行 ---------- */
IF OBJECT_ID(N'dbo.ASSISTANT_EVAL_RUN', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.ASSISTANT_EVAL_RUN
    (
        RUN_ID          int            IDENTITY(1, 1) NOT NULL,
        LAYERS          nvarchar(200)  NOT NULL,
        RUN_BY          nvarchar(20)   NOT NULL,
        STARTED_TIME    datetime       NOT NULL CONSTRAINT DF_ASSISTANT_EVAL_RUN_STARTED_TIME DEFAULT GETDATE(),
        FINISHED_TIME   datetime       NULL,
        STATUS          nvarchar(20)   NOT NULL CONSTRAINT DF_ASSISTANT_EVAL_RUN_STATUS DEFAULT N'RUNNING',
        CASE_TOTAL      int            NOT NULL CONSTRAINT DF_ASSISTANT_EVAL_RUN_CASE_TOTAL DEFAULT 0,
        PASSED          int            NOT NULL CONSTRAINT DF_ASSISTANT_EVAL_RUN_PASSED DEFAULT 0,
        FAILED          int            NOT NULL CONSTRAINT DF_ASSISTANT_EVAL_RUN_FAILED DEFAULT 0,
        SKIPPED         int            NOT NULL CONSTRAINT DF_ASSISTANT_EVAL_RUN_SKIPPED DEFAULT 0,
        NOTE            nvarchar(500)  NULL,
        CONSTRAINT PK_ASSISTANT_EVAL_RUN PRIMARY KEY CLUSTERED (RUN_ID)
    );

    PRINT N'== ASSISTANT_EVAL_RUN 已创建 ==';
END
ELSE
    PRINT N'== ASSISTANT_EVAL_RUN 已存在，跳过建表 ==';
GO

/* ---------- 2. 逐用例结果（分诊随结果抄一份） ---------- */
IF OBJECT_ID(N'dbo.ASSISTANT_EVAL_RESULT', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.ASSISTANT_EVAL_RESULT
    (
        RUN_ID          int            NOT NULL,
        CASE_ID         nvarchar(40)   NOT NULL,
        LAYER           nvarchar(20)   NOT NULL,
        OUTCOME         nvarchar(10)   NOT NULL,
        CHECK_COUNT     int            NOT NULL CONSTRAINT DF_ASSISTANT_EVAL_RESULT_CHECK_COUNT DEFAULT 0,
        REASON          nvarchar(1000) NULL,
        FIX_AT          nvarchar(20)   NULL,
        FIX_SUGGESTION  nvarchar(300)  NULL,
        FIX_PERMISSION  nvarchar(20)   NULL,
        CONSTRAINT PK_ASSISTANT_EVAL_RESULT PRIMARY KEY CLUSTERED (RUN_ID, CASE_ID),
        CONSTRAINT FK_ASSISTANT_EVAL_RESULT_RUN FOREIGN KEY (RUN_ID)
            REFERENCES dbo.ASSISTANT_EVAL_RUN (RUN_ID) ON DELETE CASCADE
    );

    -- 失败清单按"该去哪里改"分组是本页的主用法，故在 OUTCOME + FIX_AT 上留索引
    CREATE INDEX IX_ASSISTANT_EVAL_RESULT_OUTCOME
        ON dbo.ASSISTANT_EVAL_RESULT (RUN_ID, OUTCOME, FIX_AT);

    PRINT N'== ASSISTANT_EVAL_RESULT 已创建 ==';
END
ELSE
    PRINT N'== ASSISTANT_EVAL_RESULT 已存在，跳过建表 ==';
GO

/* ---------- 3. 模块 3107 · 评估运行（挂「助手」根节点 31） ---------- */
IF NOT EXISTS (SELECT 1 FROM dbo.MODULES WHERE M_IDX = 3107)
BEGIN
    INSERT INTO dbo.MODULES
        (M_IDX, M_DESC, M_URL, M_P_IDX, M_ROOT_IDX, SORT_IDX, M_TAG, MASTER_TABLE, FILTER, DETAIL_TABLE,
         REMARK, MODIFIED_BY, MODIFIED_TIME, M_ALIAS, DETAIL_NO_FIELDS, DETAIL_NO_SAVE,
         NOT_BACK_FIELDS, NOT_BACK_FIELDS_M, AUTO_APPROVE, ERROR_NO_SAVE, SEARCH_1, SEARCH_2,
         SORT_FIELDS, IF_COPY, M_ICON, FORM_OPEN_MODE, FORM_DIALOG_WIDTH, FORM_DIALOG_HEIGHT,
         APPROVE_ENABLED, UNIFIED_FORM_ENABLED, UNIFIED_FORM_EDIT_ENABLED)
    VALUES
        (3107, N'评估运行', N'/admin/assistant/eval', 31, 31, 50, 1, NULL, NULL, NULL,
         N'评估运行：把库内评估用例（3106）跑一轮确定性检查，逐用例结果落库，失败按分诊（FIX_AT）'
         + N' 分组指向"该去哪里改、需要哪个模块的权限"。只跑确定性层：不调用模型、不判回答对错。',
         N'465', GETDATE(), NULL, NULL, NULL, NULL, NULL, 0, 0, 0, 0,
         NULL, 0, NULL, NULL, NULL, NULL, 0, 0, 0);

    PRINT N'== 模块 3107 已登记 ==';
END
ELSE
    PRINT N'== 模块 3107 已存在，跳过 ==';
GO

/* ---------- 4. 开箱可用：给现有 admin 一条 3107 授权 ---------- */
IF EXISTS (SELECT 1 FROM dbo.SYS_USER_ACCOUNT WHERE USER_ID = N'admin')
   AND NOT EXISTS (SELECT 1 FROM dbo.SYS_USER_MODULE_PERMISSION WHERE USER_ID = N'admin' AND M_IDX = 3107)
BEGIN
    INSERT INTO dbo.SYS_USER_MODULE_PERMISSION
        (USER_ID, M_IDX, EXEC_TAG, ADDNEW_TAG, DELETE_TAG, EDIT_TAG, REPORT_TAG, COST_TAG,
         SETUP_TAG, SECRECY_TAG, ENDCASE_TAG, UNENDCASE_TAG, OTHER1_TAG, OTHER2_TAG, OTHER3_TAG, OTHER4_TAG,
         DENY_VIEW_FIELD_MASTER, DENY_VIEW_FIELD_DETAIL, DENY_NEW_FIELD_MASTER, DENY_NEW_FIELD_DETAIL,
         DENY_MODI_FIELD_MASTER, DENY_MODI_FIELD_DETAIL, DATA_FILTER, OPERFLAG,
         APPROVE_TAG, DEAPPROVE_TAG, FILE_VIEW_TAG, FILE_UPDA_TAG, FILE_EDIT_TAG, FILE_DELE_TAG,
         FORM_DESIGN_TAG, MODULE_CONFIG_TAG)
    VALUES
        (N'admin', 3107, N'Z', 0, 0, 0, 1, 0,
         1, 0, 0, 0, 0, 0, 0, 0,
         N'', N'', N'', N'',
         N'', N'', N'', 1,
         0, 0, 1, 0, 0, 0,
         0, 0);

    PRINT N'== 已给 admin 授权模块 3107 ==';
END
ELSE
    PRINT N'== admin 已存在或已授权 3107，跳过 ==';
