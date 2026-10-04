-- ============================================================================
-- EOS.ERP migration 311: 口径「营业额」定稿（sales_amount 的对外名称与说明写准）
-- ----------------------------------------------------------------------------
--  背景：业务侧高频词是「营业额」，而库里这条口径一直叫「销售额」。`enum_metrics` 按
--  名称 / 标识 / 说明做关键字匹配，叫法对不上，助手就只能猜（实测它猜了含税那条——
--  方向对了，但没有依据，换个问法就可能猜错，且答案里看不出它猜的是什么口径）。
--
--  业务口径（2026-10-04 业务方拍板）：**营业额 = 1405 销售订单（明细表 COP_ORDER_D）的
--  含税金额合计，只统计已批核（有效）单据**。这与既有 sales_amount 的语义完全一致
--  （决策台账 #48：`SUM(COP_ORDER_D.AMOUNT_TAX)` 且主单 `COP_ORDER_M.CONFIRM_TAG=1`），
--  因此本迁移**不新增第二条口径**（同一语义并存两条，改一处忘一处必然漂移）：
--  口径标识、定义、行过滤、维度全部不动，只把对外名称与说明定准。
--
--  改动：
--    · sales_amount    → 名称「营业额」；说明写清三要素（来源 1405 / 含税 / 仅已批核单据），
--                        并把俗称「销售额（含税）」留在说明里——既有的关键字检索与历史叫法仍能命中；
--    · sales_amount_ex → 说明同步写清（未税口径，只统计已批核单据），避免两条被混用。
--
--  幂等：按当前值判差异再更新，可重复执行。
-- ============================================================================
SET NOCOUNT ON;
SET XACT_ABORT ON;

IF DB_NAME() <> N'EOS.ERP'
    THROW 50000, N'本脚本只能在 EOS.ERP 数据库内执行。', 1;

IF OBJECT_ID(N'dbo.REPORT_METRIC', N'U') IS NULL
    THROW 50311, N'dbo.REPORT_METRIC 不存在，迁移中止（先跑迁移 027）。', 1;

BEGIN TRANSACTION;

UPDATE dbo.REPORT_METRIC
SET [METRIC_NAME] = N'营业额',
    [DESCRIPTION] = N'1405 销售订单（明细 COP_ORDER_D）含税金额合计，只统计已批核（有效）单据；俗称销售额（含税）',
    [CONFIRMED_BY] = N'admin',
    [CONFIRMED_DATE] = SYSDATETIME(),
    [LAST_UPDATE_BY] = N'admin',
    [LAST_UPDATE_DATE] = SYSDATETIME()
WHERE [METRIC_ID] = N'sales_amount'
  AND ([METRIC_NAME] <> N'营业额'
       OR ISNULL([DESCRIPTION], N'') <> N'1405 销售订单（明细 COP_ORDER_D）含税金额合计，只统计已批核（有效）单据；俗称销售额（含税）');

UPDATE dbo.REPORT_METRIC
SET [DESCRIPTION] = N'1405 销售订单（明细 COP_ORDER_D）未税金额合计，只统计已批核（有效）单据；俗称销售额（未税）',
    [LAST_UPDATE_BY] = N'admin',
    [LAST_UPDATE_DATE] = SYSDATETIME()
WHERE [METRIC_ID] = N'sales_amount_ex'
  AND ISNULL([DESCRIPTION], N'') <> N'1405 销售订单（明细 COP_ORDER_D）未税金额合计，只统计已批核（有效）单据；俗称销售额（未税）';

COMMIT;
GO
