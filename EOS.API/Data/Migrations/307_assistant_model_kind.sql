-- ============================================================================
-- EOS.ERP migration 307: 助手模型表按**用途**区分 + 三处挡住真实数据的约束
-- ----------------------------------------------------------------------------
--  来源：ADR-031（嵌入模型接入与检索接线）
--
--  1. `ASSISTANT_MODEL` 加两列：
--       KIND      —— CHAT / EMBEDDING（默认 CHAT，历史行不动）
--       DIMENSION —— 仅嵌入有意义（对话模型为 NULL）
--     并**替换"当前模型"唯一索引**：原来是 `WHERE IS_ACTIVE = 1` 的筛选唯一索引 ⇒ 全表至多一条
--     "当前模型"，嵌入与对话无法各有一条；改为按 KIND 各一条。
--
--  2. 两处检查约束**挡住了厂商现在的真实数据**（不是假想的边界）：
--       · `MAX_OUTPUT_TOKENS ≤ 200000` —— 厂商当前给的最大输出已经到 393216（实测 /models 接口），
--         按预设落库会被库直接拒；
--       · `API_KEY_ENV_VAR` 非空 —— "无密钥端点"（自建嵌入服务）表达不出来。
--         放宽为可空（NULL = 该端点不需要凭据），但**仍不许空串**（空串是脏数据，不是"没有密钥"）。
--
--  3. 密钥依旧不入库：本脚本只动"环境变量名"这一列与索引，**新增任何密钥列都会被断言拦下**。
--
--  幂等：每一步都按"存在才做 / 不存在才加"，可重复执行（干跑门禁会在已迁移的库上重放本脚本）。
-- ============================================================================
SET NOCOUNT ON;
SET XACT_ABORT ON;
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET CONCAT_NULL_YIELDS_NULL ON;
SET ARITHABORT ON;

IF DB_NAME() <> N'EOS.ERP'
    THROW 51070, N'本脚本只能在 EOS.ERP 数据库内执行。', 1;

IF OBJECT_ID(N'dbo.ASSISTANT_MODEL', N'U') IS NULL
    THROW 51071, N'表 dbo.ASSISTANT_MODEL 不存在，迁移中止（先跑迁移 291/292）。', 1;

IF OBJECT_ID(N'dbo.ASSISTANT_PROVIDER', N'U') IS NULL
    THROW 51072, N'表 dbo.ASSISTANT_PROVIDER 不存在，迁移中止（先跑迁移 292）。', 1;

BEGIN TRANSACTION;

/* ---------- 1. 模型表：用途与维度 ---------- */
IF COL_LENGTH(N'dbo.ASSISTANT_MODEL', N'KIND') IS NULL
BEGIN
    ALTER TABLE dbo.ASSISTANT_MODEL
        ADD [KIND] NVARCHAR(16) NOT NULL
            CONSTRAINT [DF_ASSISTANT_MODEL_KIND] DEFAULT (N'CHAT');
    PRINT N'== 新增列 dbo.ASSISTANT_MODEL.KIND ==';
END
ELSE
    PRINT N'== 列 dbo.ASSISTANT_MODEL.KIND 已存在，跳过 ==';

IF COL_LENGTH(N'dbo.ASSISTANT_MODEL', N'DIMENSION') IS NULL
BEGIN
    ALTER TABLE dbo.ASSISTANT_MODEL ADD [DIMENSION] INT NULL;
    PRINT N'== 新增列 dbo.ASSISTANT_MODEL.DIMENSION ==';
END
ELSE
    PRINT N'== 列 dbo.ASSISTANT_MODEL.DIMENSION 已存在，跳过 ==';

IF OBJECT_ID(N'dbo.CK_ASSISTANT_MODEL_KIND', N'C') IS NULL
BEGIN
    -- 走动态 SQL：这两条约束引用的是**本脚本刚加的列**，同一个批次里直接引用会在编译期
    -- 报"列名无效"（SQL Server 先编译后执行）。`EXEC(N'...')` 在运行期才编译，那时列已存在。
    EXEC(N'ALTER TABLE dbo.ASSISTANT_MODEL WITH CHECK
        ADD CONSTRAINT [CK_ASSISTANT_MODEL_KIND] CHECK ([KIND] IN (N''CHAT'', N''EMBEDDING''));');
    PRINT N'== 新增约束 CK_ASSISTANT_MODEL_KIND ==';
END

IF OBJECT_ID(N'dbo.CK_ASSISTANT_MODEL_DIMENSION', N'C') IS NULL
BEGIN
    -- 维度允许为空（拉回来的嵌入模型可能还没填维度），但填了就必须是正数：
    -- 0 维或负维是脏数据，写入的向量与集合登记对不上却看不出来
    EXEC(N'ALTER TABLE dbo.ASSISTANT_MODEL WITH CHECK
        ADD CONSTRAINT [CK_ASSISTANT_MODEL_DIMENSION]
            CHECK ([DIMENSION] IS NULL OR ([DIMENSION] >= 1 AND [DIMENSION] <= 20000));');
    PRINT N'== 新增约束 CK_ASSISTANT_MODEL_DIMENSION ==';
END

/* ---------- 2. 输出上限：让厂商现在的真实值能落库 ---------- */
-- 重建（而不是"改一改"）：检查约束的表达式不可 ALTER，只能 DROP + ADD；带上守卫所以可重复执行。
IF EXISTS (SELECT 1 FROM sys.check_constraints
           WHERE name = N'CK_ASSISTANT_MODEL_MAXOUT' AND parent_object_id = OBJECT_ID(N'dbo.ASSISTANT_MODEL'))
BEGIN
    ALTER TABLE dbo.ASSISTANT_MODEL DROP CONSTRAINT [CK_ASSISTANT_MODEL_MAXOUT];
    PRINT N'== 已移除旧的 CK_ASSISTANT_MODEL_MAXOUT（上限 200000）==';
END

IF NOT EXISTS (SELECT 1 FROM sys.check_constraints
               WHERE name = N'CK_ASSISTANT_MODEL_MAXOUT' AND parent_object_id = OBJECT_ID(N'dbo.ASSISTANT_MODEL'))
BEGIN
    ALTER TABLE dbo.ASSISTANT_MODEL WITH CHECK
        ADD CONSTRAINT [CK_ASSISTANT_MODEL_MAXOUT]
            CHECK ([MAX_OUTPUT_TOKENS] IS NULL OR ([MAX_OUTPUT_TOKENS] >= 1 AND [MAX_OUTPUT_TOKENS] <= 1000000));
    PRINT N'== 新增 CK_ASSISTANT_MODEL_MAXOUT（上限 1000000）==';
END

/* ---------- 3. "当前模型"唯一索引：改为按用途各一条 ---------- */
IF EXISTS (SELECT 1 FROM sys.indexes
           WHERE name = N'UX_ASSISTANT_MODEL_ACTIVE' AND object_id = OBJECT_ID(N'dbo.ASSISTANT_MODEL'))
BEGIN
    DROP INDEX [UX_ASSISTANT_MODEL_ACTIVE] ON dbo.ASSISTANT_MODEL;
    PRINT N'== 已丢弃旧的 UX_ASSISTANT_MODEL_ACTIVE（全表至多一条）==';
END

IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = N'UX_ASSISTANT_MODEL_ACTIVE_KIND' AND object_id = OBJECT_ID(N'dbo.ASSISTANT_MODEL'))
BEGIN
    -- 按用途各一条：对话与嵌入可以同时各有一个"当前"。
    -- 仍由**数据库**保证唯一（不是靠应用代码自觉）——并发切换靠代码去保证，很容易留下两条。
    CREATE UNIQUE INDEX [UX_ASSISTANT_MODEL_ACTIVE_KIND]
        ON dbo.ASSISTANT_MODEL ([KIND]) WHERE [IS_ACTIVE] = 1;
    PRINT N'== 新建 UX_ASSISTANT_MODEL_ACTIVE_KIND（按用途各至多一条）==';
END

/* ---------- 4. 供应商：允许"无密钥端点" ---------- */
IF EXISTS (SELECT 1 FROM sys.check_constraints
           WHERE name = N'CK_ASSISTANT_PROVIDER_ENVVAR'
             AND parent_object_id = OBJECT_ID(N'dbo.ASSISTANT_PROVIDER'))
BEGIN
    ALTER TABLE dbo.ASSISTANT_PROVIDER DROP CONSTRAINT [CK_ASSISTANT_PROVIDER_ENVVAR];
    PRINT N'== 已移除 CK_ASSISTANT_PROVIDER_ENVVAR（要求非空）==';
END

IF EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS
           WHERE TABLE_NAME = N'ASSISTANT_PROVIDER' AND COLUMN_NAME = N'API_KEY_ENV_VAR'
             AND IS_NULLABLE = N'NO')
BEGIN
    ALTER TABLE dbo.ASSISTANT_PROVIDER ALTER COLUMN [API_KEY_ENV_VAR] NVARCHAR(100) NULL;
    PRINT N'== 已放宽 API_KEY_ENV_VAR 为可空（NULL = 该端点不需要凭据）==';
END

IF NOT EXISTS (SELECT 1 FROM sys.check_constraints
               WHERE name = N'CK_ASSISTANT_PROVIDER_ENVVAR'
                 AND parent_object_id = OBJECT_ID(N'dbo.ASSISTANT_PROVIDER'))
BEGIN
    -- NULL 可以（自建端点），空串不行：空串是脏数据，看起来"配了"其实没有
    ALTER TABLE dbo.ASSISTANT_PROVIDER WITH CHECK
        ADD CONSTRAINT [CK_ASSISTANT_PROVIDER_ENVVAR]
            CHECK ([API_KEY_ENV_VAR] IS NULL OR LEN(LTRIM(RTRIM([API_KEY_ENV_VAR]))) > 0);
    PRINT N'== 新增 CK_ASSISTANT_PROVIDER_ENVVAR（NULL 或非空串）==';
END

/* ---------- 5. 收口断言 ---------- */
IF NOT EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS
               WHERE TABLE_NAME = N'ASSISTANT_MODEL' AND COLUMN_NAME = N'KIND' AND IS_NULLABLE = N'NO')
    THROW 51073, N'ASSISTANT_MODEL.KIND 未就位或可为空，迁移中止。', 1;

IF NOT EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS
               WHERE TABLE_NAME = N'ASSISTANT_MODEL' AND COLUMN_NAME = N'DIMENSION')
    THROW 51074, N'ASSISTANT_MODEL.DIMENSION 未就位，迁移中止。', 1;

IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = N'UX_ASSISTANT_MODEL_ACTIVE_KIND'
                 AND object_id = OBJECT_ID(N'dbo.ASSISTANT_MODEL') AND is_unique = 1
                 AND filter_definition LIKE N'%IS_ACTIVE%')
    THROW 51075, N'按用途唯一的"当前模型"索引未就位，迁移中止。', 1;

IF EXISTS (SELECT 1 FROM sys.indexes
           WHERE name = N'UX_ASSISTANT_MODEL_ACTIVE' AND object_id = OBJECT_ID(N'dbo.ASSISTANT_MODEL'))
    THROW 51076, N'旧的"全表至多一条"索引仍在，迁移中止（否则嵌入模型永远无法与对话模型同时启用）。', 1;

IF EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS
           WHERE TABLE_NAME = N'ASSISTANT_PROVIDER' AND COLUMN_NAME = N'API_KEY_ENV_VAR'
             AND IS_NULLABLE = N'NO')
    THROW 51077, N'供应商的 API_KEY_ENV_VAR 仍不可为空，迁移中止（自建端点表达不出来）。', 1;

-- 不得为已有数据造成"同用途多条当前"：索引已经拦住了，这里给一句人话解释失败原因
-- （同样走动态 SQL：引用的是本脚本刚加的列）
DECLARE @DuplicateActive INT;
EXEC sys.sp_executesql
    N'SELECT @n = COUNT(*) FROM (
          SELECT [KIND] FROM dbo.ASSISTANT_MODEL WHERE [IS_ACTIVE] = 1
          GROUP BY [KIND] HAVING COUNT(*) > 1) d;',
    N'@n INT OUTPUT',
    @n = @DuplicateActive OUTPUT;
IF @DuplicateActive > 0
    THROW 51078, N'同一用途下存在多条当前模型，迁移中止。', 1;

-- 密钥列绝不该存在（照 291 的口径）：后人想加列时先撞到这里
IF EXISTS (SELECT 1 FROM sys.columns
           WHERE object_id = OBJECT_ID(N'dbo.ASSISTANT_MODEL')
             AND name IN (N'API_KEY', N'APIKEY', N'SECRET', N'TOKEN', N'KEY_TEXT'))
    THROW 51079, N'ASSISTANT_MODEL 里出现了疑似密钥列——密钥不入库（ADR-030 §3），迁移中止。', 1;

COMMIT TRANSACTION;

PRINT N'== 收口：模型表已按用途区分，"当前模型"按用途各一条；供应商可表达无密钥端点 ==';
