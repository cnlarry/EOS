-- ============================================================================
-- EOS.ERP migration 215: 订正「用户填写却未标必填」的明细列
-- ----------------------------------------------------------------------------
-- 现象（2026-09-22 真机复现）：模块 1602 厂商计价表新建时，明细「折扣%」（SUPPLIER_PRICE_D.REBATE）
-- 留空——界面上的正常填法——保存返回 500，日志 `不能将值 NULL 插入列 'REBATE'`。
--
-- 根因：这些列**是明细表主键的一部分、库内 NOT NULL、无默认值**，但 FIELDS.IS_VERIFY=0
-- （未标必填）⇒ 前端认为可不填、服务端必填校验也不拦，提交空串转成 NULL 后撞库约束。
-- 元数据与物理结构口径不一致，用户看到的是 500 而不是"这一格不能为空"。
--
-- 处置：把「用户可填写（可见、非只读）却库内非空」的明细主键列标为必填。
-- 主表键列与 SERIAL_NO 不在此列（它们由服务端从主表带入/按行分配，属服务端持有）。
--
-- 覆盖面（2026-09-22 真库筛查，已剔除服务端持有列）：3 张明细表 / 11 列。
-- 幂等：只更新 IS_VERIFY<>1 的行；重复执行不再变更。
-- 回滚：把这 11 行 IS_VERIFY 置回 0（回滚后 1602 等模块的该场景会重新变成 500）。
-- ============================================================================

SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @GUARD_MESSAGE NVARCHAR(400) = N'本脚本只能在 EOS.ERP 数据库内执行，当前库为 ' + DB_NAME() + N'。';
IF DB_NAME() <> N'EOS.ERP'
    THROW 50000, @GUARD_MESSAGE, 1;

DECLARE @Targets TABLE (T_ID NVARCHAR(100) NOT NULL, F_ID NVARCHAR(100) NOT NULL);
INSERT INTO @Targets (T_ID, F_ID) VALUES
    (N'SUPPLIER_PRICE_D', N'PRO_NO'),        -- 品号
    (N'SUPPLIER_PRICE_D', N'CURR_ID'),       -- 币别
    (N'SUPPLIER_PRICE_D', N'REBATE'),        -- 折扣%
    (N'SUPPLIER_PRICE_D', N'TAX_ID'),        -- 税别
    (N'SUPPLIER_PRICE_D', N'TAX_TYPE'),      -- 税类型
    (N'CLIENT_PRICE_D',   N'REBATE'),
    (N'CLIENT_PRICE_D',   N'TAX_ID'),
    (N'CLIENT_PRICE_D',   N'TAX_TYPE'),
    (N'INV_BATCH_D',      N'BATCH_DATE'),    -- 批号日期
    (N'INV_BATCH_D',      N'DEPOT_ID'),      -- 库别
    (N'INV_BATCH_D',      N'EFFECT_DEPOT');  -- 生效库别

-- 前提守卫：目标列必须真实存在且库内非空，否则说明库结构已变，本迁移的前提不再成立
IF EXISTS (
    SELECT 1 FROM @Targets t
    WHERE NOT EXISTS (
        SELECT 1 FROM sys.columns c
        WHERE c.object_id = OBJECT_ID(N'dbo.' + t.T_ID) AND c.name = t.F_ID AND c.is_nullable = 0))
    THROW 51000, N'必填订正：目标列不存在或库内可空，前提不成立。', 1;

IF EXISTS (
    SELECT 1 FROM @Targets t
    WHERE NOT EXISTS (SELECT 1 FROM dbo.FIELDS f WHERE f.T_ID = t.T_ID AND f.F_ID = t.F_ID))
    THROW 51000, N'必填订正：目标列没有字段元数据行。', 1;

UPDATE f
SET f.IS_VERIFY = 1,
    f.LAST_UPDATE_BY = N'EOS-MIG',
    f.LAST_UPDATE_DATE = GETDATE()
FROM dbo.FIELDS f
JOIN @Targets t ON t.T_ID = f.T_ID AND t.F_ID = f.F_ID
WHERE ISNULL(f.IS_VERIFY, 0) <> 1;

-- 收口断言：目标列全部已标必填
IF EXISTS (
    SELECT 1 FROM @Targets t
    JOIN dbo.FIELDS f ON f.T_ID = t.T_ID AND f.F_ID = t.F_ID
    WHERE ISNULL(f.IS_VERIFY, 0) <> 1)
    THROW 51000, N'必填订正：仍有目标列未标必填。', 1;

PRINT N'必填口径已订正：3 张明细表 / 11 列（SUPPLIER_PRICE_D / CLIENT_PRICE_D / INV_BATCH_D）。';
GO
