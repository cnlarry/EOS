-- ============================================================================
-- EOS.ERP migration 229: 引擎维护的库存类列一律只读（主档 / 批次账）
-- ----------------------------------------------------------------------------
-- 背景：库存数量有唯一的写入归属——余额与流水由移动引擎写，批次账的累计由移动引擎写，
-- 产品主档上的库存类统计由 MRP 重算回写。但字段元数据里这些列**可编辑**，于是统一表单
-- 能把它们当普通字段提交：
--   · `INV_BATCH_M.IN_SUM / OUT_SUM` 是引擎维护的累计，而引擎判"批号够不够"用的正是
--     `IN_SUM - OUT_SUM`（InventoryMoveHandler）⇒ 手改这两个数可以直接伪造批号可用量；
--   · `PRODUCT.MRP_QTY / NOT_*_QTY / IN_BUY_QTY / QTY` 由 MrpRecalcService 全表重算覆写，
--     手填的值下次重算即被冲掉，在那之前只会误导报表与预警。
--
-- 服务端口径：`IsReadonly` 且非必填、无选择器回填的字段，提交即被拒（`READONLY_FIELD`）。
-- 因此把元数据置只读 = 真正关闭这条手工写路径，不只是前端不渲染。
--
-- 幂等：只更新当前 `IS_READONLY = 0` 的行。
-- 回滚：按 F_REMARK 里的标记把这些行改回 `IS_READONLY = 0`。
-- ============================================================================

SET NOCOUNT ON;

SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET ANSI_PADDING ON;
SET ANSI_WARNINGS ON;
SET CONCAT_NULL_YIELDS_NULL ON;
SET ARITHABORT ON;

DECLARE @GUARD_MESSAGE NVARCHAR(400) = N'本脚本只能在 EOS.ERP 数据库内执行，当前库为 ' + DB_NAME() + N'。';
IF DB_NAME() <> N'EOS.ERP'
    THROW 51900, @GUARD_MESSAGE, 1;

/* ---------- 目标范围：引擎维护 / 派生列 ---------- */
DECLARE @targets TABLE (T_ID SYSNAME NOT NULL, F_ID SYSNAME NOT NULL, REASON NVARCHAR(120) NOT NULL,
                        PRIMARY KEY (T_ID, F_ID));

INSERT INTO @targets (T_ID, F_ID, REASON) VALUES
    -- 产品/料件主档：库存量与 MRP 派生列（MrpRecalcService 重算回写）
    (N'PRODUCT', N'QTY',           N'库存量＝MRP 库别余额合计（重算回写）'),
    (N'PRODUCT', N'MRP_QTY',       N'可用库存＝MRP 重算结果'),
    (N'PRODUCT', N'NOT_SEND_QTY',  N'预计送货＝订单未送量（重算回写）'),
    (N'PRODUCT', N'NOT_IN_QTY',    N'预计入库＝制令未完工量（重算回写）'),
    (N'PRODUCT', N'NOT_GET_QTY',   N'预计领料＝制令未领量（重算回写）'),
    (N'PRODUCT', N'IN_BUY_QTY',    N'采购在途＝请购未采购＋采购未收货（重算回写）'),
    -- 派生列：既有实现遗留的库存类计算结果，当前无写入者，同样不接受手工填写
    (N'PRODUCT', N'MRP_QTY_ABS',   N'需采购数量（派生，无写入者）'),
    (N'PRODUCT', N'SAFETY_MRP_QTY',N'欠安全库存（派生，无写入者）'),
    (N'PRODUCT', N'AMOUNT',        N'库存金额（派生，无写入者）'),
    -- 批次账：累计收发与最近收发/检查日期由移动引擎维护
    (N'INV_BATCH_M', N'IN_SUM',            N'入库数量累计（移动引擎维护）'),
    (N'INV_BATCH_M', N'OUT_SUM',           N'出库数量累计（移动引擎维护）'),
    (N'INV_BATCH_M', N'LATELY_IN_DATE',    N'最近入库日期（移动引擎维护）'),
    (N'INV_BATCH_M', N'LATELY_OUT_DATE',   N'最近出库日期（移动引擎维护）'),
    (N'INV_BATCH_M', N'LATELY_CHECK_DATE', N'最近检查日期（移动引擎维护）');

/* ---------- 前置：目标列必须都已经有字段元数据行 ---------- */
DECLARE @missing INT = (
    SELECT COUNT(*) FROM @targets g
    WHERE NOT EXISTS (
        SELECT 1 FROM dbo.FIELDS f
        WHERE f.T_ID = g.T_ID AND LTRIM(RTRIM(f.F_ID)) = g.F_ID));

IF @missing > 0
    THROW 51901, N'存在没有字段元数据行的目标列，迁移中止（先补 FIELDS 行再置只读）。', 1;

/* ---------- 置只读 ---------- */
DECLARE @marker NVARCHAR(40) = N'[229 引擎维护列置只读]';

UPDATE f
SET f.IS_READONLY = 1,
    f.F_REMARK = CASE WHEN ISNULL(f.F_REMARK, N'') LIKE N'%' + @marker + N'%'
                      THEN f.F_REMARK
                      ELSE ISNULL(f.F_REMARK, N'') + N' ' + @marker END,
    f.LAST_UPDATE_BY = N'migration',
    f.LAST_UPDATE_DATE = GETDATE()
FROM dbo.FIELDS f
JOIN @targets g ON g.T_ID = f.T_ID AND LTRIM(RTRIM(f.F_ID)) = g.F_ID
WHERE ISNULL(f.IS_READONLY, 0) = 0;

DECLARE @changedText NVARCHAR(10) = CONVERT(NVARCHAR(10), @@ROWCOUNT);
PRINT N'== 已置只读 ' + @changedText + N' 列 ==';

/* ---------- 收口断言：目标列必须全部只读 ---------- */
IF EXISTS (
    SELECT 1 FROM dbo.FIELDS f
    JOIN @targets g ON g.T_ID = f.T_ID AND LTRIM(RTRIM(f.F_ID)) = g.F_ID
    WHERE ISNULL(f.IS_READONLY, 0) = 0)
    THROW 51902, N'仍有引擎维护列可编辑，迁移中止。', 1;

/* ---------- 反向守卫：人维护的列不得被顺手锁掉 ---------- */
DECLARE @editable TABLE (T_ID SYSNAME NOT NULL, F_ID SYSNAME NOT NULL, PRIMARY KEY (T_ID, F_ID));

INSERT INTO @editable (T_ID, F_ID) VALUES
    (N'PRODUCT', N'SAFETY_QTY'),        -- 安全存量：计划参数
    (N'PRODUCT', N'ADDING_QTY'),        -- 补货点：计划参数
    (N'PRODUCT', N'MIN_PRODUCE_QTY'),   -- 最小生产数量：生产参数
    (N'INV_BATCH_M', N'EFFECT_DATE'),   -- 有效日期：由入库单据录入
    (N'INV_BATCH_M', N'BATCH_DATE'),    -- 批号启用日期
    (N'INV_BATCH_M', N'AGAIN_CHECK_DATE'), -- 复检日期：质检计划，人为设定
    (N'INV_BATCH_M', N'REMARK');        -- 备注

IF EXISTS (
    SELECT 1 FROM dbo.FIELDS f
    JOIN @editable e ON e.T_ID = f.T_ID AND LTRIM(RTRIM(f.F_ID)) = e.F_ID
    WHERE ISNULL(f.IS_READONLY, 0) = 1)
    THROW 51903, N'人维护的列被置成了只读，迁移中止（只读范围只覆盖引擎维护列）。', 1;

DECLARE @targetCount NVARCHAR(10) = CONVERT(NVARCHAR(10), (SELECT COUNT(*) FROM @targets));

PRINT N'== 收口：引擎维护列 ' + @targetCount + N' 列全部只读，人维护列保持可编辑 ==';
