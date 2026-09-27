-- ============================================================================
-- EOS.ERP migration 246: 库存预留与释放（D7-⑥ / WS-22）
-- ----------------------------------------------------------------------------
-- 三块配置，缺一不可：
--   ① 模块 `1303 料件库存资料` 上的两个按钮：`inventory-reserve`（按来源占料）与
--      `inventory-release`（手工释放兜底）——与冻结/解冻同一套格子身份（主表物理主键即四键）。
--   ② 两个按钮的**按钮级授权**（fail-closed，同 241/244/245 的规则：能浏览 1303 的组即可按）。
--   ③ 来源模块 `1502 制令单` 的 `ENDCASE`（结案）上挂 `inventory-release-by-source`：
--      **来源结案即释放该单据的预留**（D7-⑥「挂 生命周期」）。该效果**无参数**——
--      来源模块号取当前模块、来源单号取主键里唯一 `_NO` 列的值，所以配置侧只需这一行。
--
-- 预留必须带来源（`SOURCE_TYPE` = 字符串形式的模块号、`SOURCE_NO` = 单号），
-- 这条写入约定由 WS-20 定义（可用量服务据此做"来源结案 ⇒ 不计入"的惰性判定）。
--
-- 幂等：动作行按 (MODULE_ID, EVENT_CODE, EFFECT_KEY) 判重；授权行按 (G_IDX, M_IDX, BUTTON_KEY) 判重；
-- 效果行按 (MODULE_ID, EVENT_CODE, EFFECT_KEY) 判重。
-- 出口断言：两条动作 + 两条授权 + 一条 ENDCASE 效果，缺一即失败。
-- ============================================================================

DECLARE @stockModule INT = 1303;
DECLARE @sourceModule INT = 1502;

/* ---------- ① 预留与手工释放 ---------- */
IF NOT EXISTS (SELECT 1 FROM dbo.MODULE_BUSINESS_ACTION
                WHERE MODULE_ID = @stockModule AND RTRIM(EVENT_CODE) = N'MANUAL' AND RTRIM(EFFECT_KEY) = N'inventory-reserve')
BEGIN
    INSERT INTO dbo.MODULE_BUSINESS_ACTION
        (MODULE_ID, EVENT_CODE, SEQ, EFFECT_KEY, EFFECT_NAME, ENABLED, FAIL_MODE,
         CONDITION_STRUCT, PARAM_STRUCT, REVERSE_STRUCT, REMARK, SOURCE_REF,
         CREATE_PERSON, CREATE_DATE, LABEL, CONFIRM_TAG)
        VALUES (@stockModule, N'MANUAL', 3, N'inventory-reserve', N'库存预留', 1, N'BLOCK',
                NULL,
                N'{"fields":[{"key":"quantity","label":"预留数量","type":"number","required":true},'
                + N'{"key":"sourceType","label":"来源类型（模块号）","type":"string","required":true,"maxLength":20},'
                + N'{"key":"sourceNo","label":"来源单号","type":"string","required":true,"maxLength":40},'
                + N'{"key":"reason","label":"原因","type":"string","maxLength":200}]}',
                NULL,
                N' D7-⑥（WS-22）：按来源单据占料；来源结案/取消时由 inventory-release-by-source 自动释放',
                N'', N'ADR020', GETDATE(), N'库存预留', 1);
    PRINT N'== 新增动作行：1303 inventory-reserve ==';
END
ELSE
    PRINT N'== 动作行 1303/inventory-reserve 已存在，跳过 ==';

IF NOT EXISTS (SELECT 1 FROM dbo.MODULE_BUSINESS_ACTION
                WHERE MODULE_ID = @stockModule AND RTRIM(EVENT_CODE) = N'MANUAL' AND RTRIM(EFFECT_KEY) = N'inventory-release')
BEGIN
    INSERT INTO dbo.MODULE_BUSINESS_ACTION
        (MODULE_ID, EVENT_CODE, SEQ, EFFECT_KEY, EFFECT_NAME, ENABLED, FAIL_MODE,
         CONDITION_STRUCT, PARAM_STRUCT, REVERSE_STRUCT, REMARK, SOURCE_REF,
         CREATE_PERSON, CREATE_DATE, LABEL, CONFIRM_TAG)
        VALUES (@stockModule, N'MANUAL', 4, N'inventory-release', N'手工释放预留', 1, N'BLOCK',
                NULL,
                N'{"fields":[{"key":"quantity","label":"释放数量","type":"number","required":true},'
                + N'{"key":"reason","label":"原因","type":"string","maxLength":200}]}',
                NULL,
                N' D7-⑥（WS-22）：来源单据还没结案、或来源丢失时的手工释放兜底；按先建先解',
                N'', N'ADR020', GETDATE(), N'手工释放预留', 1);
    PRINT N'== 新增动作行：1303 inventory-release ==';
END
ELSE
    PRINT N'== 动作行 1303/inventory-release 已存在，跳过 ==';

/* ---------- ② 两个按钮的授权 ---------- */
DECLARE @granted INT = 0;
INSERT INTO dbo.SYSDH_BUTTON (G_IDX, M_IDX, BUTTON_KEY, ALLOW_TAG, REMARK, CREATE_PERSON, CREATE_DATE)
SELECT DISTINCT h.G_IDX, @stockModule, k.BUTTON_KEY, 1,
       N'WS-22：能浏览料件库存资料的组可预留/释放', N'ADR020', GETDATE()
  FROM dbo.SYSDH h
  CROSS JOIN (SELECT N'inventory-reserve' AS BUTTON_KEY UNION ALL SELECT N'inventory-release') k
 WHERE h.M_IDX = @stockModule
   AND NOT EXISTS (SELECT 1 FROM dbo.SYSDH_BUTTON b
                    WHERE b.M_IDX = @stockModule AND RTRIM(b.BUTTON_KEY) = k.BUTTON_KEY AND b.G_IDX = h.G_IDX);
SET @granted = @@ROWCOUNT;
PRINT CONCAT(N'== 按钮授权：本次新增 ', @granted, N' 行 ==');

/* ---------- ③ 来源结案 ⇒ 释放预留（挂生命周期） ---------- */
IF NOT EXISTS (SELECT 1 FROM dbo.MODULE_BUSINESS_ACTION
                WHERE MODULE_ID = @sourceModule AND RTRIM(EVENT_CODE) = N'ENDCASE'
                  AND RTRIM(EFFECT_KEY) = N'inventory-release-by-source')
BEGIN
    INSERT INTO dbo.MODULE_BUSINESS_ACTION
        (MODULE_ID, EVENT_CODE, SEQ, EFFECT_KEY, EFFECT_NAME, ENABLED, FAIL_MODE,
         CONDITION_STRUCT, PARAM_STRUCT, REVERSE_STRUCT, REMARK, SOURCE_REF,
         CREATE_PERSON, CREATE_DATE, LABEL, CONFIRM_TAG)
        VALUES (@sourceModule, N'ENDCASE', 7, N'inventory-release-by-source', N'结案释放预留', 1, N'BLOCK',
                NULL, NULL, NULL,
                N' D7-⑥（WS-22）：制令单结案时释放它名下的有效预留，并按可用量口径重算受影响格子的 USEABLE_QTY；无参数（身份来自框架）',
                N'', N'ADR020', GETDATE(), N'结案释放预留', 0);
    PRINT N'== 新增效果行：1502 ENDCASE inventory-release-by-source ==';
END
ELSE
    PRINT N'== 效果行 1502/ENDCASE 已存在，跳过 ==';

/* ---------- ④ 出口断言 ---------- */
DECLARE @actions INT = (SELECT COUNT(*) FROM dbo.MODULE_BUSINESS_ACTION
                         WHERE MODULE_ID = @stockModule AND RTRIM(EVENT_CODE) = N'MANUAL'
                           AND RTRIM(EFFECT_KEY) IN (N'inventory-reserve', N'inventory-release') AND ENABLED = 1);
DECLARE @auth INT = (SELECT COUNT(*) FROM dbo.SYSDH_BUTTON
                      WHERE M_IDX = @stockModule AND RTRIM(BUTTON_KEY) IN (N'inventory-reserve', N'inventory-release')
                        AND ALLOW_TAG = 1);
DECLARE @hook INT = (SELECT COUNT(*) FROM dbo.MODULE_BUSINESS_ACTION
                      WHERE MODULE_ID = @sourceModule AND RTRIM(EVENT_CODE) = N'ENDCASE'
                        AND RTRIM(EFFECT_KEY) = N'inventory-release-by-source' AND ENABLED = 1);
IF @actions <> 2 THROW 52260, N'预留/释放动作行未就位两条，迁移中止。', 1;
IF @auth < 2 THROW 52261, N'预留或释放按钮无人被授权（配了却没人能按），迁移中止。', 1;
IF @hook <> 1 THROW 52262, N'来源模块的结案释放钩子未就位，迁移中止（没有它，来源结案后账户与列都会停旧值）。', 1;
PRINT CONCAT(N'== 就位：动作 ', @actions, N' 条 / 授权 ', @auth, N' 行 / 结案钩子 ', @hook, N' 条 ==');
