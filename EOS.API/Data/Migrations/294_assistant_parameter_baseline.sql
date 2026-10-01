-- ============================================================================
-- EOS.ERP migration 294: 助手参数并入系统参数表（dbo.SYSSS，OWNER_MODULE = 3105）
-- ----------------------------------------------------------------------------
--  来源：ADR-030 §5 / §6（本版把"助手管理面"重写为"参数框架与运行机制"）。
--
--  这一条把助手的全局策略参数从**助手自己的表**（dbo.ASSISTANT_SETTING，迁移 293）
--  搬进**平台唯一的参数表**，理由有二：
--    ① 平台已经有一整套参数机制（类型、默认值、分组、生效范围、归属模块、审计、门禁
--       check-system-params.ps1）。再维护一张几乎同构的表 = 造第二处真源；
--    ② 参数的**声明**在代码（AssistantParameterCatalog），表里只放**取值**与定义快照。
--       于是"界面显示 5 元、代码其实是 8 元"这类漂移由门禁拦下（§9 断言 2）。
--
--  本次只落 ADR-030 §5.3 的**首批 9 项**（域 PROMPT / GOVERNANCE / MEMORY）。
--  §5.3 的其余域（CHAT / TOOL_LIMIT / CAPABILITY / SITUATION / DIAGNOSIS / KB）
--  随各自的**消费改造**分批落库 —— 一次把 106 项全部建行，会让绝大多数参数在"没人读"的
--  状态下出现在管理界面上，而那正是本次要根除的形态（§10：目录+库行+消费改造+门禁四件套）。
--
--  键名由帕斯卡改为**全大写 + 下划线**（SystemPrompt → SYSTEM_PROMPT）：SYSSS 现存 103 行的键
--  全是这个形状，同一张表两种风格会让门禁与检索都要写两套（§6.3）。
--
--  两处**取值单位/形态**必须换算，不能照搬：
--    · ReserveMicroYuanPerRequest（微元，如 50000）→ RESERVE_YUAN_PER_REQUEST（元，0.05）
--      —— 微元是内部记账单位，不该出现在管理界面上；
--    · EnableAutoDistill（'true'/'false'）→ MEM_ENABLE_AUTO_DISTILL（bit '1'/'0'）
--      —— SYSSS 的 bit 归一化形态。
--
--  写 DEFAULT_VALUE 的口径（§6.3）：**标量型写、string 型留 NULL**。长文本默认值留在代码里，
--  写进列只会多一份要人工同步的副本，它一旦跟不上代码，界面给出的默认值就是错的。
--
--  幂等：建行按 (OWNER_MODULE, PARAM_KEY) 判存在；DROP 前先断言搬迁已成功。可重复执行。
-- ============================================================================
SET NOCOUNT ON;
SET XACT_ABORT ON;

IF DB_NAME() <> N'EOS.ERP'
    THROW 50000, N'本脚本只能在 EOS.ERP 数据库内执行。', 1;

IF NOT EXISTS (SELECT 1 FROM dbo.MODULES WHERE M_IDX = 3105)
    THROW 50100, N'模块 3105 助手设置不存在，迁移中止（先跑迁移 293）。', 1;

IF OBJECT_ID(N'dbo.ASSISTANT_SETTING', N'U') IS NULL
    THROW 50101, N'缺少 dbo.ASSISTANT_SETTING（迁移 293 未执行），迁移中止。', 1;

BEGIN TRANSACTION;

/* ---------- 1. 定义：本次落库的 9 项（与 AssistantParameterCatalog.All 一一对应） ---------- */
DECLARE @def TABLE (
    PARAM_KEY     NVARCHAR(64),
    OLD_KEY       NVARCHAR(60),
    VALUE_TYPE    NVARCHAR(16),
    DEFAULT_VALUE NVARCHAR(4000),
    GROUP_CODE    NVARCHAR(32),
    GROUP_LABEL   NVARCHAR(50),
    GROUP_SEQ     INT,
    SEQ_NO        INT,
    DESC_TEXT     NVARCHAR(300)
);

INSERT INTO @def (PARAM_KEY, OLD_KEY, VALUE_TYPE, DEFAULT_VALUE, GROUP_CODE, GROUP_LABEL, GROUP_SEQ, SEQ_NO, DESC_TEXT)
VALUES
    -- 域 PROMPT（组间顺序 10）：string 型的默认值留在代码里，故 DEFAULT_VALUE 为 NULL
    (N'SYSTEM_PROMPT', N'SystemPrompt', N'string', NULL,
     N'PROMPT', N'提示词', 10, 10,
     N'每轮对话注入的指令。输出格式那一段是与前端渲染面的契约（回答按 Markdown 渲染），改之前先看手册 60 篇'),

    -- 域 GOVERNANCE（组间顺序 50）
    (N'GLOBAL_DAILY_CAP_YUAN', N'GlobalDailyCapYuan', N'decimal', N'50',
     N'GOVERNANCE', N'成本与熔断', 50, 10,
     N'全体用户当日合计上限（元），超过即拒绝新请求；必须大于 0——设成 0 会让所有人立刻被拒'),
    (N'USER_DAILY_CAP_YUAN', N'UserDailyCapYuan', N'decimal', N'5',
     N'GOVERNANCE', N'成本与熔断', 50, 20,
     N'单个用户当日上限（元），必须大于 0；这是唯一允许按用户放宽的参数——给别人调高限额是管理决定'),
    (N'MAX_CONSECUTIVE_FAILURES', N'MaxConsecutiveFailures', N'int', N'3',
     N'GOVERNANCE', N'成本与熔断', 50, 30,
     N'同一用户连续技术失败达到该次数即熔断冷却，成功一次即清零；权限拒绝与用户取消不计入'),
    (N'COOLDOWN_SECONDS', N'CooldownSeconds', N'int', N'60',
     N'GOVERNANCE', N'成本与熔断', 50, 40,
     N'触发熔断后的冷却时长（秒）'),
    (N'RESERVE_YUAN_PER_REQUEST', N'ReserveMicroYuanPerRequest', N'decimal', N'0.05',
     N'GOVERNANCE', N'成本与熔断', 50, 50,
     N'单轮模型调用的预留额（元），实际预留 = 本值 ×（工具轮上限 + 1）；单位是元，不是内部记账用的微元'),
    (N'INPUT_PER_MILLION_YUAN', N'InputPerMillionYuan', N'decimal', N'1',
     N'GOVERNANCE', N'成本与熔断', 50, 60,
     N'模型行没填输入单价时用它（元/百万 token）；必须大于 0——0 元会让日上限永远不触发'),
    (N'OUTPUT_PER_MILLION_YUAN', N'OutputPerMillionYuan', N'decimal', N'2',
     N'GOVERNANCE', N'成本与熔断', 50, 70,
     N'模型行没填输出单价时用它（元/百万 token）'),

    -- 域 MEMORY（组间顺序 80）
    (N'MEM_ENABLE_AUTO_DISTILL', N'EnableAutoDistill', N'bit', N'1',
     N'MEMORY', N'记忆', 80, 10,
     N'会话结束后异步提炼候选记忆（待用户确认）；关掉可以省一次模型调用');

/* ---------- 2. 建行：取值自 ASSISTANT_SETTING 搬过来，缺行则留空（= 用默认值） ---------- */
INSERT INTO dbo.SYSSS (
    OWNER_MODULE, PARAM_KEY, PARAM_VALUE, VALUE_TYPE, DEFAULT_VALUE,
    GROUP_CODE, GROUP_LABEL, GROUP_SEQ, SEQ_NO, DESC_TEXT, EFFECT_SCOPE, REMARK,
    CREATE_PERSON, CREATE_DATE)
SELECT
    3105, d.PARAM_KEY,
    CASE
        -- 旧表**没有这一行** = 从未覆盖过 ⇒ 新表也留空（= 用默认值）。
        -- 这一条必须在任何形态换算之前判：下面的 CASE 一旦把"没有值"卷进 ELSE 分支，
        -- 就会把"从未覆盖（默认开）"静默写成"明确关掉"——参数值看着像事实，实际是凭空造的。
        WHEN s.PARAM_KEY IS NULL THEN NULL
        -- 微元 → 元：旧值是不可解析的文本时原样搬过去，让解析器如实报"这项没生效"，
        -- 而不是悄悄换成 NULL（那会表现成"这条参数从来没被改过"）
        WHEN d.PARAM_KEY = N'RESERVE_YUAN_PER_REQUEST' THEN
            CASE WHEN TRY_CONVERT(decimal(38, 10), s.PARAM_VALUE) IS NULL THEN s.PARAM_VALUE
                 ELSE FORMAT(TRY_CONVERT(decimal(38, 10), s.PARAM_VALUE) / 1000000.0, N'0.######', N'en-US')
            END
        -- bool → bit 的归一化形态（有行但值为空同样按"未覆盖"处理，不再落成 0）
        WHEN d.PARAM_KEY = N'MEM_ENABLE_AUTO_DISTILL' THEN
            CASE WHEN s.PARAM_VALUE IS NULL THEN NULL
                 WHEN s.PARAM_VALUE IN (N'true', N'1', N'是') THEN N'1'
                 ELSE N'0' END
        ELSE s.PARAM_VALUE
    END,
    d.VALUE_TYPE, d.DEFAULT_VALUE,
    d.GROUP_CODE, d.GROUP_LABEL, d.GROUP_SEQ, d.SEQ_NO, d.DESC_TEXT, N'immediate', NULL,
    N'mig-294', SYSDATETIME()
FROM @def d
LEFT JOIN dbo.ASSISTANT_SETTING s ON s.PARAM_KEY = d.OLD_KEY
WHERE NOT EXISTS (
    SELECT 1 FROM dbo.SYSSS x WHERE x.OWNER_MODULE = 3105 AND x.PARAM_KEY = d.PARAM_KEY);

PRINT N'== 助手参数建行：' + CONVERT(NVARCHAR(10), @@ROWCOUNT) + N' 条（表内已有则跳过）==';

/* ---------- 3. 断言：定义完整（与门禁 check-system-params 的判据同源） ---------- */
IF EXISTS (
    SELECT 1 FROM dbo.SYSSS
    WHERE OWNER_MODULE = 3105
      AND (VALUE_TYPE NOT IN (N'bit', N'int', N'decimal', N'string')
        OR EFFECT_SCOPE <> N'immediate'
        OR LTRIM(RTRIM(ISNULL(GROUP_CODE, N''))) = N''
        OR LTRIM(RTRIM(ISNULL(GROUP_LABEL, N''))) = N''
        OR LTRIM(RTRIM(ISNULL(DESC_TEXT, N''))) = N''
        OR ISNULL(GROUP_SEQ, 0) = 0))
    THROW 50102, N'助手参数定义不完整（类型 / 生效范围 / 分组 / 说明 / 分组顺序）。', 1;

-- 助手参数**没有"需重启"档**：出现一个就说明它本该是部署配置，不该在这里
IF EXISTS (SELECT 1 FROM dbo.SYSSS WHERE OWNER_MODULE = 3105 AND EFFECT_SCOPE <> N'immediate')
    THROW 50103, N'助手参数里出现了 EFFECT_SCOPE = restart —— 该参数应留在部署配置里。', 1;

IF NOT EXISTS (SELECT 1 FROM dbo.SYSSS WHERE OWNER_MODULE = 3105 HAVING COUNT(*) = 9)
    THROW 50104, N'助手参数条数不是 9，迁移中止。', 1;

-- 密钥绝不入库（ADR-030 §3）：参数的类型里没有任何一种能表达凭据
IF EXISTS (
    SELECT 1 FROM dbo.SYSSS
    WHERE OWNER_MODULE = 3105
      AND (PARAM_KEY LIKE N'%API_KEY%' OR PARAM_KEY LIKE N'%SECRET%'
        OR PARAM_KEY LIKE N'%TOKEN%' OR PARAM_KEY LIKE N'%PASSWORD%'))
    THROW 50105, N'助手参数里出现了疑似凭据的键名——密钥不入库（ADR-030 §3），迁移中止。', 1;

/* ---------- 4. 退役旧表：搬迁已在同一事务内断言成功，这里不存在"两头都没有"的窗口 ---------- */
DROP TABLE dbo.ASSISTANT_SETTING;
PRINT N'== 已退役 dbo.ASSISTANT_SETTING（取值已并入 dbo.SYSSS 的 3105）==';

COMMIT TRANSACTION;

/* ---------- 5. 收口断言（事务之外复核） ---------- */
IF OBJECT_ID(N'dbo.ASSISTANT_SETTING', N'U') IS NOT NULL
    THROW 50106, N'dbo.ASSISTANT_SETTING 未退役，迁移未收口。', 1;

IF NOT EXISTS (SELECT 1 FROM dbo.SYSSS WHERE OWNER_MODULE = 3105 HAVING COUNT(*) = 9)
    THROW 50107, N'助手参数未落库，迁移未收口。', 1;

PRINT N'== 收口：助手参数已并入 dbo.SYSSS（OWNER_MODULE = 3105，首批 9 项）==';
