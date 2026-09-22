-- ============================================================================
-- EOS.ERP migration 217: 打开工资表两模块的效果引擎开关
-- ----------------------------------------------------------------------------
-- 迁移 216 为四个工资表模块配了「保存期写判别字段」的 set-state 动作，但真机验证只有
-- 180310 / 1803101 生效（新建 200、判别字段写对），180309 / 1803091 仍被自己的模块过滤
-- 拒收（400 RECORD_OUT_OF_MODULE_FILTER）。原因：**这两个模块 EFFECT_ENGINE_TAG=0**，
-- 保存期效果链根本不执行——动作配了也不会跑。
--
-- 同族四个模块里 180310 / 1803101 开着引擎，180309 / 1803091 关着；而这两个模块
-- **早已配置了启用状态的 duplicate-check 校验规则**（RULE_ID 134 / 138），因引擎关闭
-- 一直处于"配了不跑"的状态。打开开关同时解决两件事：判别字段写入 + 既有校验生效。
--
-- 安全性：这两个模块当前**没有任何业务动作**（216 之前 ACTIONS 计数为 0），打开引擎后
-- 新增执行的只有 216 的判别字段动作与既有的金额/重复校验，不引入别处未验证的行为。
-- 迁移 200 修复的 14 个模块全部是 EFFECT_ENGINE_TAG=1——同一口径。
--
-- 幂等：只更新当前为 0 的行。回滚：把这两行置回 0 并清脏标记。
-- ============================================================================

SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @GUARD_MESSAGE NVARCHAR(400) = N'本脚本只能在 EOS.ERP 数据库内执行，当前库为 ' + DB_NAME() + N'。';
IF DB_NAME() <> N'EOS.ERP'
    THROW 50000, @GUARD_MESSAGE, 1;

DECLARE @Targets TABLE (MODULE_ID INT NOT NULL PRIMARY KEY);
INSERT INTO @Targets (MODULE_ID) VALUES (180309), (1803091);

/* 前置守卫：模块存在且主表是本迁移讨论的工资表主表 */
IF EXISTS (
    SELECT 1 FROM @Targets t
    WHERE NOT EXISTS (SELECT 1 FROM dbo.MODULES m
                      WHERE m.M_IDX = t.MODULE_ID AND LTRIM(RTRIM(ISNULL(m.MASTER_TABLE, N''))) = N'HR_WAGE_M'))
    THROW 52701, N'目标模块不存在或主表不是 HR_WAGE_M，迁移中止。', 1;

/* 前置守卫：判别字段动作（迁移 216）必须已在位——否则打开引擎也建不出单 */
IF EXISTS (
    SELECT 1 FROM @Targets t
    WHERE NOT EXISTS (SELECT 1 FROM dbo.MODULE_BUSINESS_ACTION a
                      WHERE a.MODULE_ID = t.MODULE_ID AND a.EVENT_CODE = N'SAVE'
                        AND a.EFFECT_KEY = N'set-state' AND a.ENABLED = 1))
    THROW 52702, N'判别字段动作不在位（应先执行迁移 216），迁移中止。', 1;

UPDATE m
SET m.EFFECT_ENGINE_TAG = 1,
    m.LAST_UPDATE_BY = N'DbUp',
    m.LAST_UPDATE_DATE = SYSDATETIME()
FROM dbo.MODULES m
JOIN @Targets t ON t.MODULE_ID = m.M_IDX
WHERE ISNULL(m.EFFECT_ENGINE_TAG, 0) = 0;

IF EXISTS (SELECT 1 FROM @Targets t JOIN dbo.MODULES m ON m.M_IDX = t.MODULE_ID
           WHERE ISNULL(m.EFFECT_ENGINE_TAG, 0) <> 1)
    THROW 52703, N'效果引擎开关未打开成功，迁移中止。', 1;

/* 标记待发布：运行时读的是已发布快照 */
MERGE dbo.WORKBENCH_MODULE_DIRTY AS D
USING (SELECT MODULE_ID FROM @Targets) AS S
   ON D.MODULE_ID = S.MODULE_ID
WHEN MATCHED THEN UPDATE SET D.DIRTY_TAG = 1, D.LAST_MODIFIED_BY = N'DbUp', D.LAST_MODIFIED_AT = SYSDATETIME()
WHEN NOT MATCHED THEN INSERT (MODULE_ID, DIRTY_TAG, LAST_MODIFIED_BY, LAST_MODIFIED_AT)
    VALUES (S.MODULE_ID, 1, N'DbUp', SYSDATETIME());

PRINT N'== 已打开 180309 / 1803091 的效果引擎开关，模块待重发布 ==';
GO
