-- ============================================================================
-- EOS.ERP migration 245: 库存冻结 / 解冻的按钮（ADR-020 §9.7 D7-⑦ / §10 WS-21）
-- ----------------------------------------------------------------------------
-- 挂载位置：模块 `1303 料件库存资料`，主表 `INV_PRO_DEPOT`
-- ——它的物理主键正好是 `(PRO_NO, DEPOT_ID, LOCATION_NO, BATCH_NO)`，
-- 也就是说**库存格子就是余额行**：动作对哪一格生效由框架给的主键值决定，不需要另造格子身份。
--
-- 授权按 D7-⑦：走 **ADR-018 按钮级授权**（fail-closed 名单），**不挂配置权**——
-- 冻结是品检 / 客服发起的业务动作，不该要求 `CanSetup`。规则与迁移 241（1302 受控写入口）、
-- 244（1304 快照按钮）同一条：**能浏览该模块的组**即可按；库内没有该模块的组记录时退回管理员组 `1`。
--
-- `CONFIRM_TAG = 1`：先探路后确认——冻结量是手输的数字，第一次点击只回"可用量会从多少变到多少"。
--
-- 幂等：动作行按 (MODULE_ID, EVENT_CODE, EFFECT_KEY) 判重；授权行按 (G_IDX, M_IDX, BUTTON_KEY) 判重。
-- 出口断言：两条动作各就位一条，且**每条至少有一个组被授权**（配了却没人能按属于缺陷）。
-- ============================================================================

DECLARE @module INT = 1303;

/* ---------- ① 两条动作行 ---------- */
IF NOT EXISTS (SELECT 1 FROM dbo.MODULE_BUSINESS_ACTION
                WHERE MODULE_ID = @module AND RTRIM(EVENT_CODE) = N'MANUAL' AND RTRIM(EFFECT_KEY) = N'inventory-freeze')
BEGIN
    INSERT INTO dbo.MODULE_BUSINESS_ACTION
        (MODULE_ID, EVENT_CODE, SEQ, EFFECT_KEY, EFFECT_NAME, ENABLED, FAIL_MODE,
         CONDITION_STRUCT, PARAM_STRUCT, REVERSE_STRUCT, REMARK, SOURCE_REF,
         CREATE_PERSON, CREATE_DATE, LABEL, CONFIRM_TAG)
        VALUES (@module, N'MANUAL', 1, N'inventory-freeze', N'库存冻结', 1, N'BLOCK',
                NULL,
                N'{"fields":[{"key":"quantity","label":"冻结数量","type":"number","required":true},'
                + N'{"key":"reason","label":"原因","type":"string","maxLength":200}]}',
                NULL,
                N'ADR-020 §9.7 D7（WS-21）：对一格库存（料号/库别/库位/批次）登记占用并同步可用量；不设"不得超过可用量"的上限（质量扣货必须表达得出来）',
                N'ADR-020 §9.7', N'ADR020', GETDATE(), N'库存冻结', 1);
    PRINT N'== 新增动作行：1303 inventory-freeze ==';
END
ELSE
    PRINT N'== 动作行 1303/inventory-freeze 已存在，跳过 ==';

IF NOT EXISTS (SELECT 1 FROM dbo.MODULE_BUSINESS_ACTION
                WHERE MODULE_ID = @module AND RTRIM(EVENT_CODE) = N'MANUAL' AND RTRIM(EFFECT_KEY) = N'inventory-unfreeze')
BEGIN
    INSERT INTO dbo.MODULE_BUSINESS_ACTION
        (MODULE_ID, EVENT_CODE, SEQ, EFFECT_KEY, EFFECT_NAME, ENABLED, FAIL_MODE,
         CONDITION_STRUCT, PARAM_STRUCT, REVERSE_STRUCT, REMARK, SOURCE_REF,
         CREATE_PERSON, CREATE_DATE, LABEL, CONFIRM_TAG)
        VALUES (@module, N'MANUAL', 2, N'inventory-unfreeze', N'库存解冻', 1, N'BLOCK',
                NULL,
                N'{"fields":[{"key":"quantity","label":"解冻数量","type":"number","required":true},'
                + N'{"key":"reason","label":"原因","type":"string","maxLength":200}]}',
                NULL,
                N'ADR-020 §9.7 D7-⑥（WS-21）：手工释放兜底——按先建先解释放该格占用，解冻量超过当前冻结合计即拒',
                N'ADR-020 §9.7', N'ADR020', GETDATE(), N'库存解冻', 1);
    PRINT N'== 新增动作行：1303 inventory-unfreeze ==';
END
ELSE
    PRINT N'== 动作行 1303/inventory-unfreeze 已存在，跳过 ==';

/* ---------- ② 按钮授权（fail-closed：默认没人能按，按"能浏览 1303 的组"发） ---------- */
DECLARE @granted INT = 0;
INSERT INTO dbo.SYSDH_BUTTON (G_IDX, M_IDX, BUTTON_KEY, ALLOW_TAG, REMARK, CREATE_PERSON, CREATE_DATE)
SELECT DISTINCT h.G_IDX, @module, k.BUTTON_KEY, 1,
       N'ADR-020 WS-21：能浏览料件库存资料的组可冻结/解冻', N'ADR020', GETDATE()
  FROM dbo.SYSDH h
  CROSS JOIN (SELECT N'inventory-freeze' AS BUTTON_KEY UNION ALL SELECT N'inventory-unfreeze') k
 WHERE h.M_IDX = @module
   AND NOT EXISTS (SELECT 1 FROM dbo.SYSDH_BUTTON b
                    WHERE b.M_IDX = @module AND RTRIM(b.BUTTON_KEY) = k.BUTTON_KEY AND b.G_IDX = h.G_IDX);
SET @granted = @@ROWCOUNT;

IF NOT EXISTS (SELECT 1 FROM dbo.SYSDH_BUTTON WHERE M_IDX = @module AND RTRIM(BUTTON_KEY) = N'inventory-freeze' AND ALLOW_TAG = 1)
BEGIN
    INSERT INTO dbo.SYSDH_BUTTON (G_IDX, M_IDX, BUTTON_KEY, ALLOW_TAG, REMARK, CREATE_PERSON, CREATE_DATE)
        VALUES (N'1', @module, N'inventory-freeze', 1, N'ADR-020 WS-21：兜底授权（库内没有能浏览 1303 的组记录）', N'ADR020', GETDATE());
    PRINT N'== 冻结按钮：兜底授权给组 1 ==';
END

IF NOT EXISTS (SELECT 1 FROM dbo.SYSDH_BUTTON WHERE M_IDX = @module AND RTRIM(BUTTON_KEY) = N'inventory-unfreeze' AND ALLOW_TAG = 1)
BEGIN
    INSERT INTO dbo.SYSDH_BUTTON (G_IDX, M_IDX, BUTTON_KEY, ALLOW_TAG, REMARK, CREATE_PERSON, CREATE_DATE)
        VALUES (N'1', @module, N'inventory-unfreeze', 1, N'ADR-020 WS-21：兜底授权（库内没有能浏览 1303 的组记录）', N'ADR020', GETDATE());
    PRINT N'== 解冻按钮：兜底授权给组 1 ==';
END

PRINT CONCAT(N'== 授权：本次新增 ', @granted, N' 行 ==');

/* ---------- ③ 出口断言 ---------- */
DECLARE @actions INT = (SELECT COUNT(*) FROM dbo.MODULE_BUSINESS_ACTION
                         WHERE MODULE_ID = @module AND RTRIM(EVENT_CODE) = N'MANUAL'
                           AND RTRIM(EFFECT_KEY) IN (N'inventory-freeze', N'inventory-unfreeze') AND ENABLED = 1);
DECLARE @auth INT = (SELECT COUNT(*) FROM dbo.SYSDH_BUTTON
                      WHERE M_IDX = @module AND RTRIM(BUTTON_KEY) IN (N'inventory-freeze', N'inventory-unfreeze')
                        AND ALLOW_TAG = 1);
IF @actions <> 2 THROW 52250, N'冻结/解冻动作行未就位两条，迁移中止。', 1;
IF @auth < 2 THROW 52251, N'冻结或解冻按钮没有任何组被授权（配了却没人能按），迁移中止。', 1;
PRINT CONCAT(N'== 就位：动作行 ', @actions, N' 条 / 授权行 ', @auth, N' 条 ==');
