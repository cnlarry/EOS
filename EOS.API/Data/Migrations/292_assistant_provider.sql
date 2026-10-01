-- ============================================================================
-- EOS.ERP migration 292: 助手模型配置改为两级（dbo.ASSISTANT_PROVIDER + 重构 ASSISTANT_MODEL）
-- ----------------------------------------------------------------------------
--  来源：用户口径 ——「同一个模型提供商可以有几个模型（比如 DeepSeek 的 Flash 与 Pro）」。
--
--  为什么要拆：291 建的表把**供应商级**与**模型级**的东西混在一行里。同一个供应商加第二个模型，
--  BASE_URL 与密钥环境变量名要重复填一遍；换一把密钥要逐条改。而现实是**一个供应商一个端点、
--  一把密钥，通吃它所有模型**。所以：
--    ASSISTANT_PROVIDER  ← 供应商/接入点：CODE、BASE_URL、**密钥环境变量名**、默认超时
--      ↑ PROVIDER_ID
--    ASSISTANT_MODEL     ← 模型：MODEL_CODE（deepseek-chat / deepseek-reasoner…）、上下文窗口、
--                          最大输出、默认温度、**单价**、是否支持工具、是否当前
--
--  **为什么是重建而不是逐列搬迁**：291 与 292 同批交付（291 从未在任何一个跑起来的环境里被写过，
--  它的端点直到重启前都还是 404），所以两者之间不存在"有数据的旧环境"。
--  但"表是空的"是个事实而不是保证 —— 下面用一条**非空即 THROW** 的守卫把它变成一个可验证的前提：
--  万一某个环境真的写进过数据，这个迁移会**大声失败**并告诉你改用逐列搬迁，而不是静默抹掉它。
--  顺带说一句，这也是 291 里那条"疑似密钥列就 THROW"的同一种写法：把前提写死在脚本里。
--
--  幂等：建表与加列都按"不存在才做"；重建段有非空守卫，可重复执行。
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

IF OBJECT_ID(N'dbo.ASSISTANT_MODEL', N'U') IS NULL
    THROW 51800, N'表 dbo.ASSISTANT_MODEL 不存在，迁移中止（先跑迁移 291）。', 1;

BEGIN TRANSACTION;

/* ---------- 1. 供应商表 ---------- */
IF OBJECT_ID(N'dbo.ASSISTANT_PROVIDER', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.ASSISTANT_PROVIDER (
        [PROVIDER_ID]     INT IDENTITY(1,1) NOT NULL CONSTRAINT [PK_ASSISTANT_PROVIDER] PRIMARY KEY,
        -- CODE 与代码里的预设目录（AssistantProviderCatalog）对应：deepseek / openai / dashscope / zhipu / moonshot / custom
        [CODE]            NVARCHAR(50)  NOT NULL,
        [DISPLAY_NAME]    NVARCHAR(100) NOT NULL,
        [BASE_URL]        NVARCHAR(300) NOT NULL,
        -- **这一列存的是环境变量名，不是密钥**（ADR-030 §3）
        [API_KEY_ENV_VAR] NVARCHAR(100) NOT NULL,
        -- 该供应商的默认超时；模型行可以覆盖它（推理模型往往需要更久）
        [TIMEOUT_SECONDS] INT NOT NULL CONSTRAINT [DF_ASSISTANT_PROVIDER_TIMEOUT] DEFAULT (300),
        [ENABLED]         BIT NOT NULL CONSTRAINT [DF_ASSISTANT_PROVIDER_ENABLED] DEFAULT (1),
        [SORT_IDX]        INT NOT NULL CONSTRAINT [DF_ASSISTANT_PROVIDER_SORT] DEFAULT (0),
        [REMARK]          NVARCHAR(200) NULL,
        [CREATED_AT]      DATETIME2(3) NOT NULL CONSTRAINT [DF_ASSISTANT_PROVIDER_CREATED] DEFAULT (SYSUTCDATETIME()),
        [UPDATED_AT]      DATETIME2(3) NOT NULL CONSTRAINT [DF_ASSISTANT_PROVIDER_UPDATED] DEFAULT (SYSUTCDATETIME()),
        [CREATED_BY]      NVARCHAR(50) NULL,
        [UPDATED_BY]      NVARCHAR(50) NULL,
        CONSTRAINT [UQ_ASSISTANT_PROVIDER_CODE] UNIQUE ([CODE]),
        CONSTRAINT [CK_ASSISTANT_PROVIDER_TIMEOUT] CHECK ([TIMEOUT_SECONDS] BETWEEN 10 AND 3600),
        CONSTRAINT [CK_ASSISTANT_PROVIDER_ENVVAR] CHECK (LEN(LTRIM(RTRIM([API_KEY_ENV_VAR]))) > 0)
    );
    PRINT N'== 新建表 dbo.ASSISTANT_PROVIDER ==';
END
ELSE
    PRINT N'== 表 dbo.ASSISTANT_PROVIDER 已存在，跳过 ==';

/* ---------- 2. 重建模型表为"模型级" ---------- */
-- 已经是两级形状（292 跑过）就把整段重建**跳过**：这样脚本对"已迁移过、且管理员已经配了型号"
-- 的库也是可重复执行的。干跑门禁 test-migration-dryrun 正是在已迁移的库上重放本脚本——
-- 不跳过去的话，管理员配好的模型会被误判成"有数据的旧库"，门禁就永远过不去。
IF COL_LENGTH(N'dbo.ASSISTANT_MODEL', N'PROVIDER_ID') IS NOT NULL
BEGIN
    PRINT N'== dbo.ASSISTANT_MODEL 已是两级形状（292 已执行过），跳过重建 ==';
    GOTO RebuildDone;
END

-- 走到这里说明还是 291 的形状（只有供应商字符串、没有 PROVIDER_ID）。
-- 表里若已有数据就**大声失败**：这是 291→292 的一次性重构，宁可让人手工搬迁，也不要静默抹掉配置。
IF EXISTS (SELECT 1 FROM dbo.ASSISTANT_MODEL)
    THROW 51801, N'dbo.ASSISTANT_MODEL 还是旧形状且已有数据，292 会重建该表。请先导出这些配置，再改用"逐列搬迁"的写法（建供应商表 → 按 PROVIDER/BASE_URL/密钥变量名 去重回填 → 再删列）。', 1;

DROP TABLE dbo.ASSISTANT_MODEL;
PRINT N'== 已丢弃旧的 dbo.ASSISTANT_MODEL（表内无数据）==';

CREATE TABLE dbo.ASSISTANT_MODEL (
    [MODEL_ID]       INT IDENTITY(1,1) NOT NULL CONSTRAINT [PK_ASSISTANT_MODEL] PRIMARY KEY,
    [PROVIDER_ID]    INT NOT NULL,
    -- 厂商侧的模型标识，例如 deepseek-chat / deepseek-reasoner：这是真正发给厂商的那个字符串
    [MODEL_CODE]     NVARCHAR(100) NOT NULL,
    [DISPLAY_NAME]   NVARCHAR(100) NOT NULL,
    -- 上下文窗口（token）。**会被消费**：用它推算能带多少历史（见 ChatService 的裁剪），不是纯展示
    [CONTEXT_WINDOW] INT NULL,
    -- 请求参数：单次回复的输出上限
    [MAX_OUTPUT_TOKENS] INT NULL,
    [DEFAULT_TEMPERATURE] DECIMAL(3,2) NULL,
    -- 超时覆盖：NULL = 用供应商的默认（推理模型要更久，所以留了覆盖）
    [TIMEOUT_SECONDS] INT NULL,
    -- **按模型计价**：换模型后成本归因才准。NULL = 退回全局兜底价（见 ASSISTANT_SETTING）
    [INPUT_PER_MILLION_YUAN]  DECIMAL(18,4) NULL,
    [OUTPUT_PER_MILLION_YUAN] DECIMAL(18,4) NULL,
    -- 有些模型不支持工具调用：不支持的还带 tools 去请求，厂商会直接 400
    [SUPPORTS_TOOLS] BIT NOT NULL CONSTRAINT [DF_ASSISTANT_MODEL_TOOLS] DEFAULT (1),
    [IS_ACTIVE]      BIT NOT NULL CONSTRAINT [DF_ASSISTANT_MODEL_ACTIVE] DEFAULT (0),
    [ENABLED]        BIT NOT NULL CONSTRAINT [DF_ASSISTANT_MODEL_ENABLED] DEFAULT (1),
    [SORT_IDX]       INT NOT NULL CONSTRAINT [DF_ASSISTANT_MODEL_SORT] DEFAULT (0),
    [REMARK]         NVARCHAR(200) NULL,
    [CREATED_AT]     DATETIME2(3) NOT NULL CONSTRAINT [DF_ASSISTANT_MODEL_CREATED] DEFAULT (SYSUTCDATETIME()),
    [UPDATED_AT]     DATETIME2(3) NOT NULL CONSTRAINT [DF_ASSISTANT_MODEL_UPDATED] DEFAULT (SYSUTCDATETIME()),
    [CREATED_BY]     NVARCHAR(50) NULL,
    [UPDATED_BY]     NVARCHAR(50) NULL,
    -- 同一供应商下同一个模型标识只能有一条：否则"当前模型"到底指哪一个会说不清
    CONSTRAINT [UQ_ASSISTANT_MODEL_CODE] UNIQUE ([PROVIDER_ID], [MODEL_CODE]),
    CONSTRAINT [FK_ASSISTANT_MODEL_PROVIDER] FOREIGN KEY ([PROVIDER_ID])
        REFERENCES dbo.ASSISTANT_PROVIDER ([PROVIDER_ID]),
    CONSTRAINT [CK_ASSISTANT_MODEL_TIMEOUT] CHECK ([TIMEOUT_SECONDS] IS NULL OR [TIMEOUT_SECONDS] BETWEEN 10 AND 3600),
    CONSTRAINT [CK_ASSISTANT_MODEL_TEMPERATURE] CHECK ([DEFAULT_TEMPERATURE] IS NULL OR ([DEFAULT_TEMPERATURE] >= 0 AND [DEFAULT_TEMPERATURE] <= 2)),
    CONSTRAINT [CK_ASSISTANT_MODEL_CONTEXT] CHECK ([CONTEXT_WINDOW] IS NULL OR [CONTEXT_WINDOW] BETWEEN 1000 AND 20000000),
    CONSTRAINT [CK_ASSISTANT_MODEL_MAXOUT] CHECK ([MAX_OUTPUT_TOKENS] IS NULL OR [MAX_OUTPUT_TOKENS] BETWEEN 1 AND 200000),
    CONSTRAINT [CK_ASSISTANT_MODEL_PRICE] CHECK (
        ([INPUT_PER_MILLION_YUAN] IS NULL OR [INPUT_PER_MILLION_YUAN] >= 0)
        AND ([OUTPUT_PER_MILLION_YUAN] IS NULL OR [OUTPUT_PER_MILLION_YUAN] >= 0))
);

-- 筛选唯一索引：至多一行 IS_ACTIVE = 1（不靠应用代码自觉）
CREATE UNIQUE INDEX [UX_ASSISTANT_MODEL_ACTIVE] ON dbo.ASSISTANT_MODEL ([IS_ACTIVE]) WHERE [IS_ACTIVE] = 1;
CREATE INDEX [IX_ASSISTANT_MODEL_PROVIDER] ON dbo.ASSISTANT_MODEL ([PROVIDER_ID]);

PRINT N'== 重建表 dbo.ASSISTANT_MODEL 为模型级（含上下文窗口 / 单价 / 工具能力）==';

RebuildDone:

/* ---------- 3. 收口断言 ---------- */
IF COL_LENGTH(N'dbo.ASSISTANT_MODEL', N'BASE_URL') IS NOT NULL
    THROW 51802, N'dbo.ASSISTANT_MODEL 里仍有 BASE_URL —— 端点是供应商级的东西，不能两处都存。', 1;

IF COL_LENGTH(N'dbo.ASSISTANT_MODEL', N'API_KEY_ENV_VAR') IS NOT NULL
    THROW 51803, N'dbo.ASSISTANT_MODEL 里仍有 API_KEY_ENV_VAR —— 密钥挂供应商，一个供应商一把。', 1;

IF COL_LENGTH(N'dbo.ASSISTANT_PROVIDER', N'API_KEY') IS NOT NULL
    THROW 51804, N'dbo.ASSISTANT_PROVIDER 里出现了 API_KEY —— 密钥不入库（ADR-030 §3）。', 1;

IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = N'FK_ASSISTANT_MODEL_PROVIDER')
    THROW 51805, N'模型与供应商的外键未就位，迁移中止。', 1;

COMMIT TRANSACTION;

PRINT N'== 收口：助手模型配置已改为两级（供应商 / 模型），模型级带上下文窗口与单价 ==';
