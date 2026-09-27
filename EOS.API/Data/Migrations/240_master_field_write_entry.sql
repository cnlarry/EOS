-- ============================================================================
-- EOS.ERP migration 241: 批次 / 库存主档的受控写入口（D2 / WS-17）
-- ----------------------------------------------------------------------------
-- 背景：模块 `1302 料件批号资料`（主表 `INV_BATCH_M`）与 `1303 料件库存资料`（主表 `INV_PRO_DEPOT`）
-- **不在统一表单白名单内，这是故意的**——`check-inventory-read-hosts.ps1` 的写路径断言要求
-- "主表是余额 / 流水 / 批次账的模块不得进白名单"，否则通用表单会绕过移动引擎直接改账。
-- 后果是两条被登记的"人工改错入口"**实际不存在**（表单端点对该模块 404）。
--
-- 本迁移**不开白名单**（那等于拆掉一道安全门），而是挂上**一扇窄门**：
--   · 单据动作 `master-field-write`（代码在 `MasterFieldWriteHandler`）：字段白名单 + 引擎维护列拒写
--     + 按钮级授权（fail-closed）+ 全程审计，且不触发 `inventory-move`、不写 `INV_PRO_DEPOT_LOG`；
--   · 动作行 `EVENT_CODE = 'MANUAL'` = 用户点按钮才跑（不参与任何单据事件的效果链）；
--   · `CONFIRM_TAG = 1` = 先探路后确认：第一次点击只回"将会改成什么"，什么都不写。
--
-- 顺带解决台帐问题 5：`USEABLE_QTY` / `LAST_CHECK_DATE` 等**引擎维护列**的 `FIELDS.IS_READONLY`
-- 一直是 0（可编辑），今天靠"模块不在白名单"兜底——**白名单是会改的配置，字段只读才是元数据事实**。
-- 本迁移把这些列的只读位订正为 1。注意这是**第二道闸**：第一道是处理器里的维护列拒写表
-- （即使有人改了白名单、即使配置把维护列声明成参数，服务端一样拒）。
--
-- 幂等：动作行按 (MODULE_ID, EVENT_CODE, EFFECT_KEY) 判重；授权行按 (G_IDX, M_IDX, BUTTON_KEY) 判重；
-- 只读位按"当前不是 1 才改"处理。
-- ============================================================================

/* ---------- ① 动作行：料件批号资料上的「修改人工字段」按钮 ---------- */
IF NOT EXISTS (SELECT 1 FROM dbo.MODULE_BUSINESS_ACTION
                WHERE MODULE_ID = 1302 AND RTRIM(EVENT_CODE) = N'MANUAL' AND RTRIM(EFFECT_KEY) = N'master-field-write')
BEGIN
    INSERT INTO dbo.MODULE_BUSINESS_ACTION
        (MODULE_ID, EVENT_CODE, SEQ, EFFECT_KEY, EFFECT_NAME, ENABLED, FAIL_MODE,
         CONDITION_STRUCT, PARAM_STRUCT, REVERSE_STRUCT, REMARK, SOURCE_REF,
         CREATE_PERSON, CREATE_DATE, LABEL, CONFIRM_TAG)
        VALUES (1302, N'MANUAL', 1, N'master-field-write', N'修改人工字段', 1, N'BLOCK',
                NULL,
                N'{"fields":[{"key":"effectDate","label":"有效日期","type":"date"},'
                + N'{"key":"batchDate","label":"批号启用日期","type":"date"},'
                + N'{"key":"againCheckDate","label":"复检日期","type":"date"}]}',
                NULL,
                N' D2（WS-17）：批次主档的受控写入口——只允许改人工语义列，引擎维护列服务端拒，不产生库存流水',
                N'', N'ADR020', GETDATE(), N'修改人工字段', 1);
    PRINT N'== 新增动作行：1302 master-field-write（MANUAL / 二次确认）==';
END
ELSE
    PRINT N'== 动作行 1302/master-field-write 已存在，跳过 ==';

/* ---------- ② 按钮授权：fail-closed，默认没人能按；按"能浏览 1302 的组"发 ---------- */
-- 与模块/报表权限同一套组成员关系（SYSDG_USER）；没有任何组能浏览 1302 时退回管理员组。
DECLARE @granted INT = 0;

INSERT INTO dbo.SYSDH_BUTTON (G_IDX, M_IDX, BUTTON_KEY, ALLOW_TAG, REMARK, CREATE_PERSON, CREATE_DATE)
SELECT DISTINCT h.G_IDX, 1302, N'master-field-write', 1,
       N'WS-17：能浏览料件批号资料的组可修改其人工字段', N'ADR020', GETDATE()
  FROM dbo.SYSDH h
 WHERE h.M_IDX = 1302
   AND NOT EXISTS (SELECT 1 FROM dbo.SYSDH_BUTTON b
                    WHERE b.M_IDX = 1302 AND RTRIM(b.BUTTON_KEY) = N'master-field-write' AND b.G_IDX = h.G_IDX);
SET @granted = @@ROWCOUNT;

IF @granted = 0
   AND NOT EXISTS (SELECT 1 FROM dbo.SYSDH_BUTTON
                    WHERE M_IDX = 1302 AND RTRIM(BUTTON_KEY) = N'master-field-write')
BEGIN
    INSERT INTO dbo.SYSDH_BUTTON (G_IDX, M_IDX, BUTTON_KEY, ALLOW_TAG, REMARK, CREATE_PERSON, CREATE_DATE)
        VALUES (N'1', 1302, N'master-field-write', 1,
                N'WS-17：兜底授权（库内没有能浏览 1302 的组记录）', N'ADR020', GETDATE());
    PRINT N'== 按钮授权：库内无能浏览 1302 的组记录，已兜底授权给组 1 ==';
END
ELSE
    PRINT CONCAT(N'== 按钮授权：本次新增 ', @granted, N' 个组 ==');

/* ---------- ③ 引擎维护列的只读位（元数据事实，不依赖白名单兜底） ---------- */
UPDATE dbo.FIELDS
   SET IS_READONLY = 1,
       LAST_UPDATE_BY = N'ADR020',
       LAST_UPDATE_DATE = GETDATE()
 WHERE RTRIM(T_ID) = N'INV_PRO_DEPOT'
   AND RTRIM(F_ID) IN (N'QTY', N'COST_PRICE', N'COST_AMOUNT', N'INIT_QTY', N'USEABLE_QTY', N'LAST_CHECK_DATE')
   AND ISNULL(IS_READONLY, 0) = 0;

/* ---------- 核对：三件都真的就位，否则迁移白跑 ---------- */
DECLARE @action INT = (SELECT COUNT(*) FROM dbo.MODULE_BUSINESS_ACTION
                        WHERE MODULE_ID = 1302 AND RTRIM(EVENT_CODE) = N'MANUAL'
                          AND RTRIM(EFFECT_KEY) = N'master-field-write' AND ENABLED = 1);
DECLARE @auth INT = (SELECT COUNT(*) FROM dbo.SYSDH_BUTTON
                      WHERE M_IDX = 1302 AND RTRIM(BUTTON_KEY) = N'master-field-write' AND ALLOW_TAG = 1);
DECLARE @writable INT = (SELECT COUNT(*) FROM dbo.FIELDS
                          WHERE RTRIM(T_ID) = N'INV_PRO_DEPOT'
                            AND RTRIM(F_ID) IN (N'QTY', N'COST_PRICE', N'COST_AMOUNT', N'INIT_QTY', N'USEABLE_QTY', N'LAST_CHECK_DATE')
                            AND ISNULL(IS_READONLY, 0) = 1);
DECLARE @mustreadonly INT = (SELECT COUNT(*) FROM dbo.FIELDS
                              WHERE RTRIM(T_ID) = N'INV_PRO_DEPOT'
                                AND RTRIM(F_ID) IN (N'QTY', N'COST_PRICE', N'COST_AMOUNT', N'INIT_QTY', N'USEABLE_QTY', N'LAST_CHECK_DATE'));

IF @action <> 1 THROW 52120, N'1302 的 master-field-write 动作行未就位，迁移中止。', 1;
IF @auth = 0 THROW 52121, N'没有任何组被授权按这个按钮——按钮会以"配了但没人能按"的形式存在，迁移中止。', 1;
IF @writable <> @mustreadonly THROW 52122, N'引擎维护列仍有可编辑的，迁移中止。', 1;

PRINT CONCAT(N'== 就位：动作行 1 条 / 授权组 ', @auth, N' 个 / 维护列只读 ', @writable, N' 列 ==');
