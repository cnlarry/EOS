-- ============================================================================
-- EOS.ERP migration 297: 助手参数第二批（处境 / 诊断 / 动作阈值 / 配置写准入，共 36 条）
-- ----------------------------------------------------------------------------
--  来源：ADR-030 §10 批 C ——「把四个 IConfiguration 节收口进参数表」。
--
--  收口之前，助手有四组参数住在 **appsettings 的四个节**上：
--    · AssistantSituation     19 项（处境预算与上报上限）
--    · AssistantDiagnosis      8 项（诊断输出上限）+ 1 项写死在代码里（归因文案条数）
--    · AssistantActionLimits   4 项（动作阈值）
--    · AssistantConfigWrite    4 项（配置写准入）
--  而 appsettings.json 里**这些节根本不存在**（实测全文零命中），于是这 35 项+1 项
--  "既不在库里、也不在文件里"，只存在于代码默认值中——连部署时想改都只能靠环境变量。
--  本次把它们并入 dbo.SYSSS（OWNER_MODULE = 3105），与首批同源：声明在参数目录、
--  取值在库里、解释在 AssistantParameterResolver。ADR-030 §8 那句"助手配置一律由管理面持有"
--  到此才真正成立。
--
--  取值不需要搬迁：这些节从来没有被写过（配置里没有节、appsettings 里也没有），
--  所以全部只写**定义**（DEFAULT_VALUE = 代码默认值），PARAM_VALUE 一律留空。
--  默认值取自各 options 类的属性初始值（目录里引用同一批对象），这里不再另立数字。
--
--  红线（预演 / 幂等 / 越权上限 / 批核族）**不在本次落库范围**：它们没有可写参数形态，
--  登记在 AssistantParameterCatalog.RedLineKeys 里供门禁识别。
--
--  幂等：建行按 (OWNER_MODULE, PARAM_KEY) 判存在；可重复执行。
-- ============================================================================
SET NOCOUNT ON;
SET XACT_ABORT ON;

IF DB_NAME() <> N'EOS.ERP'
    THROW 50000, N'本脚本只能在 EOS.ERP 数据库内执行。', 1;

IF NOT EXISTS (SELECT 1 FROM dbo.MODULES WHERE M_IDX = 3105)
    THROW 50400, N'模块 3105 助手设置不存在，迁移中止（先跑迁移 293）。', 1;

BEGIN TRANSACTION;

DECLARE @def TABLE (
    PARAM_KEY     NVARCHAR(64),
    VALUE_TYPE    NVARCHAR(16),
    DEFAULT_VALUE NVARCHAR(4000),
    GROUP_CODE    NVARCHAR(32),
    GROUP_LABEL   NVARCHAR(50),
    GROUP_SEQ     INT,
    SEQ_NO        INT,
    DESC_TEXT     NVARCHAR(300)
);

INSERT INTO @def (PARAM_KEY, VALUE_TYPE, DEFAULT_VALUE, GROUP_CODE, GROUP_LABEL, GROUP_SEQ, SEQ_NO, DESC_TEXT)
VALUES
    -- ---- 域 CAPABILITY（组间顺序 40）：配置写准入，四类各自独立 ----
    (N'CONFIG_WRITE_FIELDS', N'bit', N'1', N'CAPABILITY', N'能力面', 40, 10,
     N'可写字段元数据（显示名 / 可见 / 只读 / 必填等）'),
    (N'CONFIG_WRITE_DATASOURCES', N'bit', N'0', N'CAPABILITY', N'能力面', 40, 20,
     N'可写字段的数据来源（源表 / 过滤结构 / 回填映射）；默认关闭——四类风险与验证程度不同，不互相带走'),
    (N'CONFIG_WRITE_BUTTONS', N'bit', N'0', N'CAPABILITY', N'能力面', 40, 30,
     N'可写自定义按钮；不含按钮授权（按钮授权是权限，不是配置）；默认关闭'),
    (N'CONFIG_WRITE_EFFECTS', N'bit', N'0', N'CAPABILITY', N'能力面', 40, 40,
     N'可写效果键与公式行；默认关闭'),

    -- ---- 域 GOVERNANCE（组间顺序 50）：可代理动作的阈值（排在既有 7 项之后）----
    (N'ACTION_MAX_ROWS', N'int', N'50', N'GOVERNANCE', N'成本与熔断', 50, 80,
     N'一次动作请求最多处理的行数：不设上限等于允许一句「全删了」'),
    (N'ACTION_MAX_APPROVAL_RECORDS', N'int', N'25', N'GOVERNANCE', N'成本与熔断', 50, 90,
     N'一次「操作请求卡」最多列出的单据数（只准备请求、不执行处置）'),
    (N'ACTION_MAX_AUDIT_KEYS', N'int', N'20', N'GOVERNANCE', N'成本与熔断', 50, 100,
     N'审计里保留的资源键条数上限——审计是检索入口，不是数据出口'),
    (N'ACTION_MAX_CONFIG_CLONE_OBJECTS', N'int', N'100', N'GOVERNANCE', N'成本与熔断', 50, 110,
     N'一次「照 A 配 B」最多搬运的对象数'),

    -- ---- 域 SITUATION（组间顺序 60）：处境预算与上报上限 ----
    (N'SIT_RESIDENT_TOKEN_LIMIT', N'int', N'300', N'SITUATION', N'处境', 60, 10,
     N'常驻处境（身份 + 待办）合计的 token 预算，超限按上限硬截断并记 Warning'),
    (N'SIT_IDENTITY_TOKEN_LIMIT', N'int', N'200', N'SITUATION', N'处境', 60, 20,
     N'身份段（我是谁、能看什么）的 token 预算'),
    (N'SIT_PENDING_TOKEN_LIMIT', N'int', N'80', N'SITUATION', N'处境', 60, 30,
     N'待办段（我手上压着什么）的 token 预算'),
    (N'SIT_MAX_FILTERS', N'int', N'10', N'SITUATION', N'处境', 60, 40,
     N'前端上报的筛选条件条数上限，超限截断并记 Warning'),
    (N'SIT_MAX_SELECTION', N'int', N'20', N'SITUATION', N'处境', 60, 50,
     N'前端上报的选中行主键条数上限'),
    (N'SIT_MAX_DIRTY_FIELDS', N'int', N'20', N'SITUATION', N'处境', 60, 60,
     N'前端上报的未保存字段条数上限'),
    (N'SIT_MAX_VALUE_LENGTH', N'int', N'120', N'SITUATION', N'处境', 60, 70,
     N'上报文本（筛选值、主键、字段值）的字符上限'),
    (N'SIT_MAX_NOTICE_SUMMARY_LENGTH', N'int', N'160', N'SITUATION', N'处境', 60, 80,
     N'前端上报的被拒摘要的字符上限'),
    (N'SIT_OVERDUE_DAYS', N'int', N'7', N'SITUATION', N'处境', 60, 90,
     N'建立日期早于该天数仍未批核即视为滞留'),
    (N'SIT_OVERDUE_MAX_AGE_DAYS', N'int', N'365', N'SITUATION', N'处境', 60, 100,
     N'超过该年龄的历史单据不进摘要——没有上界时按默认排序会取到多年前的遗留单'),
    (N'SIT_DIGEST_MAX_ITEMS', N'int', N'5', N'SITUATION', N'处境', 60, 110,
     N'打开助手即见（零模型调用）的摘要最多列几条'),
    (N'SIT_BLOCKED_NOW_SCAN_RECORDS', N'int', N'10', N'SITUATION', N'处境', 60, 120,
     N'「此刻办不下去」逐单扫描的每模块记录数上限，与年龄上界一起把这条链路的总代价限住'),
    (N'SIT_BLOCKED_NOW_MAX_AGE_DAYS', N'int', N'30', N'SITUATION', N'处境', 60, 130,
     N'「此刻办不下去」扫描的年龄上界：只看近期单据——既是「此刻」的语义，也把代价限住'),
    (N'SIT_BLOCKED_NOW_PROBE_RULES', N'int', N'4', N'SITUATION', N'处境', 60, 140,
     N'「此刻办不下去」每模块最多判定的校验判据条数（逐单求值的成本上界）'),
    (N'SIT_DIGEST_MODULE_SCAN_LIMIT', N'int', N'8', N'SITUATION', N'处境', 60, 150,
     N'摘要扫描的候选模块数上限（按本人最近活动取候选）'),
    (N'SIT_ACTIVITY_WINDOW_DAYS', N'int', N'30', N'SITUATION', N'处境', 60, 160,
     N'取「本人最近活动模块」的时间窗（天）'),
    (N'SIT_RECENT_FAILURE_DAYS', N'int', N'7', N'SITUATION', N'处境', 60, 170,
     N'取「最近被拒事件」的时间窗（天）'),
    (N'SIT_RECENT_FAILURE_LIMIT', N'int', N'3', N'SITUATION', N'处境', 60, 180,
     N'取「最近被拒事件」的条数上限'),
    (N'SIT_DIGEST_TEXT_LENGTH', N'int', N'120', N'SITUATION', N'处境', 60, 190,
     N'摘要条目文案的字符上限'),

    -- ---- 域 DIAGNOSIS（组间顺序 70）：诊断输出上限 ----
    (N'DIAG_MAX_VALIDATION_RULES', N'int', N'12', N'DIAGNOSIS', N'诊断', 70, 10,
     N'诊断里列出的校验规则条数上限，超限截断并记入 caveat'),
    (N'DIAG_MAX_FIELD_GUARDS', N'int', N'8', N'DIAGNOSIS', N'诊断', 70, 20,
     N'诊断里列出的字段保护条数上限'),
    (N'DIAG_MAX_PROVENANCE', N'int', N'12', N'DIAGNOSIS', N'诊断', 70, 30,
     N'诊断里列出的值来源（这个字段的值是怎么来的）条数上限'),
    (N'DIAG_MAX_EFFECTS', N'int', N'12', N'DIAGNOSIS', N'诊断', 70, 40,
     N'诊断里列出的效果影响面条数上限'),
    (N'DIAG_MAX_BLOCKERS', N'int', N'8', N'DIAGNOSIS', N'诊断', 70, 50,
     N'诊断里列出的阻塞原因条数上限'),
    (N'DIAG_LAST_FAILURE_DAYS', N'int', N'30', N'DIAGNOSIS', N'诊断', 70, 60,
     N'取「最近一次对该记录的失败」的时间窗（天）'),
    (N'DIAG_LAST_FAILURE_LIMIT', N'int', N'1', N'DIAGNOSIS', N'诊断', 70, 70,
     N'取最近失败审计的条数上限（只取最新的一条附上）'),
    (N'DIAG_MAX_TEXT_LENGTH', N'int', N'160', N'DIAGNOSIS', N'诊断', 70, 80,
     N'单条原因 / 文案的字符上限'),
    (N'DIAG_ACTION_SUMMARY_LIMIT', N'int', N'3', N'DIAGNOSIS', N'诊断', 70, 90,
     N'「为什么办不下去」的归因条数上限：它原先写死在代码里，一个配置入口都没有');

INSERT INTO dbo.SYSSS (
    OWNER_MODULE, PARAM_KEY, PARAM_VALUE, VALUE_TYPE, DEFAULT_VALUE,
    GROUP_CODE, GROUP_LABEL, GROUP_SEQ, SEQ_NO, DESC_TEXT, EFFECT_SCOPE, REMARK,
    CREATE_PERSON, CREATE_DATE)
SELECT
    3105, d.PARAM_KEY, NULL, d.VALUE_TYPE, d.DEFAULT_VALUE,
    d.GROUP_CODE, d.GROUP_LABEL, d.GROUP_SEQ, d.SEQ_NO, d.DESC_TEXT, N'immediate', NULL,
    N'mig-297', SYSDATETIME()
FROM @def d
WHERE NOT EXISTS (
    SELECT 1 FROM dbo.SYSSS x WHERE x.OWNER_MODULE = 3105 AND x.PARAM_KEY = d.PARAM_KEY);

PRINT N'== 助手参数第二批建行：' + CONVERT(NVARCHAR(10), @@ROWCOUNT) + N' 条（表内已有则跳过）==';

/* ---------- 断言 ---------- */
IF EXISTS (
    SELECT 1 FROM dbo.SYSSS
    WHERE OWNER_MODULE = 3105
      AND (VALUE_TYPE NOT IN (N'bit', N'int', N'decimal', N'string')
        OR EFFECT_SCOPE <> N'immediate'
        OR LTRIM(RTRIM(ISNULL(GROUP_CODE, N''))) = N''
        OR LTRIM(RTRIM(ISNULL(GROUP_LABEL, N''))) = N''
        OR LTRIM(RTRIM(ISNULL(DESC_TEXT, N''))) = N''
        OR ISNULL(GROUP_SEQ, 0) = 0))
    THROW 50401, N'助手参数定义不完整（类型 / 生效范围 / 分组 / 说明 / 分组顺序）。', 1;

-- 两批合计 45 条（首批 9 + 本批 36）
IF NOT EXISTS (SELECT 1 FROM dbo.SYSSS WHERE OWNER_MODULE = 3105 HAVING COUNT(*) = 45)
    THROW 50402, N'助手参数条数不是 45（首批 9 + 本批 36），迁移中止。', 1;

-- 本批每条都必须有列默认值：它们全是 bit / int，默认值写进列才是数据
IF EXISTS (SELECT 1 FROM @def d
           JOIN dbo.SYSSS s ON s.OWNER_MODULE = 3105 AND s.PARAM_KEY = d.PARAM_KEY
           WHERE s.DEFAULT_VALUE IS NULL)
    THROW 50403, N'本批参数存在空的 DEFAULT_VALUE——标量型参数的默认值必须写在列里。', 1;

-- 红线键不得落库（ADR-030 §6.4）：它们没有可配置形态
IF EXISTS (SELECT 1 FROM dbo.SYSSS
           WHERE OWNER_MODULE = 3105
             AND (PARAM_KEY LIKE N'%DRYRUN%' OR PARAM_KEY LIKE N'%IDEMPOTEN%'
               OR PARAM_KEY LIKE N'%UNAUTHORIZED%' OR PARAM_KEY LIKE N'%APPROVAL_FAMILY%'))
    THROW 50404, N'参数表里出现了红线键——红线没有可配置形态（ADR-030 §6.4）。', 1;

COMMIT TRANSACTION;

PRINT N'== 收口：助手参数第二批已并入 dbo.SYSSS（3105 合计 45 条）==';
