-- ============================================================================
-- EOS.ERP migration 471：判官告警线（ADR-044 §5 J3）
-- ----------------------------------------------------------------------------
--  J3 = "阈值告警：口径标注率下降 / 越界承诺 > 0 / 无法判定率上升"，只告警、不阻断。
--  本迁移落**可参数化的两条**（口径标注率下限、无法判定率上限）与**最小样本数**：
--
--    · 越界承诺数（D 的 FAIL）**不参数化**：它的目标恒为 0，是一条红线。
--      给它一个"上限"参数等于给"把红线调没"开口子——与动作面红线（预演恒开 / 幂等恒开 /
--      越权上限 0）同一口径，红线只能被触发，不能被配置。
--    · 最小样本数：3 条里 1 条不通过就报"标注率 67%"的话，告警会立刻退化成噪声。
--
--  默认值取自代码默认对象（AssistantJudgeLimitsOptions），迁移只落行，不写第二份解释。
--  幂等：按 (OWNER_MODULE, PARAM_KEY) 判存在；可重复执行。
-- ============================================================================
SET NOCOUNT ON;
SET XACT_ABORT ON;

IF DB_NAME() <> N'EOS.ERP'
    THROW 51980, N'本脚本只允许在 EOS.ERP 数据库上执行。', 1;

IF NOT EXISTS (SELECT 1 FROM dbo.MODULES WHERE M_IDX = 3105)
    THROW 51981, N'模块 3105 助手设置不存在，迁移终止。', 1;

IF NOT EXISTS (SELECT 1 FROM dbo.SYS_PARAMETER WHERE OWNER_MODULE = 3105 AND GROUP_CODE = N'JUDGE')
    THROW 51982, N'判官参数（域 JUDGE）尚未落库（迁移 470 未执行），迁移终止。', 1;

BEGIN TRANSACTION;

DECLARE @items TABLE (
    SEQ_NO INT, PARAM_KEY NVARCHAR(64), GROUP_CODE NVARCHAR(32), GROUP_LABEL NVARCHAR(64),
    GROUP_SEQ INT, VALUE_TYPE NVARCHAR(16), DEFAULT_VALUE NVARCHAR(200), DESC_TEXT NVARCHAR(400));

INSERT INTO @items VALUES
    (130, N'JUDGE_ALERT_CITATION_MIN_PCT', N'JUDGE', N'判官', 100, N'int', N'80',
     N'口径标注率告警下限（%）：A 条款通过率低于它即在看板告警。这是告警线不是合格线——低于它要查条款或模型，不要调低它。'),
    (140, N'JUDGE_ALERT_UNSURE_MAX_PCT', N'JUDGE', N'判官', 100, N'int', N'40',
     N'无法判定率告警上限（%）：UNSURE 占比高于它即告警，通常意味着条款写不清或在拿不准时乱判。'),
    (150, N'JUDGE_ALERT_MIN_SAMPLES', N'JUDGE', N'判官', 100, N'int', N'20',
     N'告警生效的最小样本数（条）：窗口内判过的回答少于它就不告警，否则告警会被当成噪声忽略。');

INSERT INTO dbo.SYS_PARAMETER (
    OWNER_MODULE, PARAM_KEY, PARAM_VALUE, VALUE_TYPE, DEFAULT_VALUE,
    GROUP_CODE, GROUP_LABEL, GROUP_SEQ, SEQ_NO, DESC_TEXT, EFFECT_SCOPE, REMARK,
    CREATED_BY, CREATED_TIME)
SELECT
    3105, i.PARAM_KEY, NULL, i.VALUE_TYPE, i.DEFAULT_VALUE,
    i.GROUP_CODE, i.GROUP_LABEL, i.GROUP_SEQ, i.SEQ_NO, i.DESC_TEXT, N'immediate', NULL,
    N'mig-471', SYSDATETIME()
FROM @items i
WHERE NOT EXISTS (
    SELECT 1 FROM dbo.SYS_PARAMETER x WHERE x.OWNER_MODULE = 3105 AND x.PARAM_KEY = i.PARAM_KEY);

PRINT N'== 判官告警线参数建行：' + CONVERT(NVARCHAR(10), @@ROWCOUNT) + N' 条（表内已有则跳过）==';

/* ---------- 断言 ---------- */
IF EXISTS (SELECT 1 FROM @items i
           WHERE NOT EXISTS (SELECT 1 FROM dbo.SYS_PARAMETER s
                             WHERE s.OWNER_MODULE = 3105 AND s.PARAM_KEY = i.PARAM_KEY))
    THROW 51983, N'有判官告警线参数未落库，迁移终止。', 1;

IF EXISTS (SELECT 1 FROM dbo.SYS_PARAMETER
           WHERE OWNER_MODULE = 3105 AND PARAM_KEY LIKE N'JUDGE[_]ALERT[_]%'
             AND (GROUP_CODE <> N'JUDGE' OR VALUE_TYPE <> N'int' OR EFFECT_SCOPE <> N'immediate'
                  OR TRY_CONVERT(INT, DEFAULT_VALUE) IS NULL
                  OR TRY_CONVERT(INT, DEFAULT_VALUE) NOT BETWEEN 0 AND 100))
    THROW 51984,
        N'判官告警线参数的类型/取值不符（应为 JUDGE 域、int、0–100、immediate），迁移终止。', 1;

IF EXISTS (SELECT GROUP_CODE, SEQ_NO FROM dbo.SYS_PARAMETER
           WHERE OWNER_MODULE = 3105 AND GROUP_CODE = N'JUDGE'
           GROUP BY GROUP_CODE, SEQ_NO HAVING COUNT(*) > 1)
    THROW 51985, N'判官参数的组内序号重复，迁移终止。', 1;

IF NOT EXISTS (SELECT 1 FROM dbo.SYS_PARAMETER
               WHERE OWNER_MODULE = 3105 AND GROUP_CODE = N'JUDGE' HAVING COUNT(*) >= 15)
    THROW 51986, N'判官参数不足 15 条（12 + 3 条告警线），迁移终止。', 1;

COMMIT TRANSACTION;

PRINT N'== 收口：判官告警线（3 条）已并入 dbo.SYS_PARAMETER（3105，域 JUDGE 合计 15 条）==';
