-- ============================================================================
-- EOS.ERP migration 296: 助手参数的**作用域表** dbo.ASSISTANT_PARAM_SCOPE
-- ----------------------------------------------------------------------------
--  来源：ADR-030 §6.2。助手参数分三层取值：**用户 > 模块 > 全局**。
--  全局值在 dbo.SYSSS（OWNER_MODULE = 3105），本表只装"某一层相对上一层的覆盖"。
--
--  为什么另立一张表而不是塞进 SYSSS：SYSSS 的主键是 (OWNER_MODULE, PARAM_KEY)，
--  它装的是"全库唯一值"；带作用域的参数**另立领域表**是既有先例
--  （DEPOT_STOCK_POLICY，见 ADR-014 / ADR-017 §9）。把它塞进 SYSSS 就得改主键，
--  那会波及 113 行既有参数与全部读取点。
--
--  两条硬规则（服务端强制，不靠界面自觉）：
--    ① 只有参数目录里声明为**可作用域化**的键能进本表（`ScopePolicy <> None`）。
--       未声明的键写进来 = 保存拒绝；库里已存在的未声明键由门禁报出，**不静默忽略**
--       （静默忽略的表现是"界面上写着它是关的、实际却是开的"）。
--    ② **收紧型（Tighten）参数的覆盖值只能比上级更严**（布尔只能关、阈值只能更小）。
--       于是"给某模块单独打开一个全局已关的能力"在参数层根本表达不出来。
--       只有成本限额（Override）允许放宽——给别人调高限额是管理决定，不是越权。
--
--  生命周期列按 ADR-013 口径：建立组 NOT NULL + DEFAULT、最后修改组可空。
--  列名与 SYSSS 同形（CREATE_PERSON / CREATE_DATE / LAST_UPDATE_BY / LAST_UPDATE_DATE），
--  免得同一件事在两处叫两个名字。
--
--  幂等：建表按"不存在才做"；索引同理。可重复执行。
-- ============================================================================
SET NOCOUNT ON;
SET XACT_ABORT ON;

IF DB_NAME() <> N'EOS.ERP'
    THROW 50000, N'本脚本只能在 EOS.ERP 数据库内执行。', 1;

BEGIN TRANSACTION;

IF OBJECT_ID(N'dbo.ASSISTANT_PARAM_SCOPE', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.ASSISTANT_PARAM_SCOPE (
        -- MODULE（值为模块号）/ USER（值为 USER_ID）
        [SCOPE_TYPE]     NVARCHAR(10)  NOT NULL,
        [SCOPE_KEY]      NVARCHAR(50)  NOT NULL,
        -- 必须是参数目录里声明为可作用域化的键（服务端校验，见 ADR-030 §6.2）
        [PARAM_KEY]      NVARCHAR(64)  NOT NULL,
        -- 取值；NULL 一律解释为"本层不覆盖，继续向上取值"（与 SYSSS 的 PARAM_VALUE 同口径）
        [PARAM_VALUE]    NVARCHAR(4000) NULL,
        [CREATE_PERSON]  NCHAR(10)     NOT NULL CONSTRAINT [DF_ASSISTANT_PARAM_SCOPE_CREATE_PERSON] DEFAULT (N''),
        [CREATE_DATE]    DATETIME      NOT NULL CONSTRAINT [DF_ASSISTANT_PARAM_SCOPE_CREATE_DATE]   DEFAULT (GETDATE()),
        [LAST_UPDATE_BY] NCHAR(10)     NULL,
        [LAST_UPDATE_DATE] DATETIME    NULL,
        CONSTRAINT [PK_ASSISTANT_PARAM_SCOPE] PRIMARY KEY CLUSTERED ([SCOPE_TYPE], [SCOPE_KEY], [PARAM_KEY]),
        CONSTRAINT [CK_ASSISTANT_PARAM_SCOPE_TYPE] CHECK ([SCOPE_TYPE] IN (N'MODULE', N'USER'))
    );

    PRINT N'== 新建表 dbo.ASSISTANT_PARAM_SCOPE（用户 > 模块 > 全局）==';
END
ELSE
    PRINT N'== 表 dbo.ASSISTANT_PARAM_SCOPE 已存在，跳过 ==';

-- 按 KEY 反查"谁被单独设过"是这个表的次要读法（主读法是按 (SCOPE_TYPE, SCOPE_KEY) 取本层）
IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = N'IX_ASSISTANT_PARAM_SCOPE_KEY'
                 AND object_id = OBJECT_ID(N'dbo.ASSISTANT_PARAM_SCOPE'))
    CREATE INDEX [IX_ASSISTANT_PARAM_SCOPE_KEY]
        ON dbo.ASSISTANT_PARAM_SCOPE ([PARAM_KEY], [SCOPE_TYPE], [SCOPE_KEY]);

COMMIT TRANSACTION;

/* ---------- 收口断言 ---------- */
IF OBJECT_ID(N'dbo.ASSISTANT_PARAM_SCOPE', N'U') IS NULL
    THROW 50300, N'表 dbo.ASSISTANT_PARAM_SCOPE 未就位，迁移中止。', 1;

IF NOT EXISTS (SELECT 1 FROM sys.columns
               WHERE object_id = OBJECT_ID(N'dbo.ASSISTANT_PARAM_SCOPE')
                 AND name IN (N'SCOPE_TYPE', N'SCOPE_KEY', N'PARAM_KEY', N'PARAM_VALUE'))
    THROW 50301, N'dbo.ASSISTANT_PARAM_SCOPE 的列不完整，迁移中止。', 1;

-- 密钥绝不入库（ADR-030 §3）：本表同样不该出现凭据类列
IF EXISTS (SELECT 1 FROM sys.columns
           WHERE object_id = OBJECT_ID(N'dbo.ASSISTANT_PARAM_SCOPE')
             AND name IN (N'API_KEY', N'APIKEY', N'SECRET', N'TOKEN'))
    THROW 50302, N'dbo.ASSISTANT_PARAM_SCOPE 里出现了疑似密钥列——密钥不入库（ADR-030 §3）。', 1;

PRINT N'== 收口：助手参数作用域表已就位 ==';
