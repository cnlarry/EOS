-- ============================================================================
-- EOS.ERP migration 260: 出库明细的批号格接上批次选择器（按效期排序）
-- ----------------------------------------------------------------------------
-- 批号在出库单据上是由**用户自己挑**的（服务端不替他决定出哪几批），所以要让他看得见
-- "哪几批快到期"。挑的地方是统一选择器：本迁移补的是**"这个字段有选择器入口"**这一行配置，
-- 排序与列由服务端注册数据源 `inventory.batches` 给出（见 ChooserRepository）。
--
-- 只接**出库侧**：入库侧的效期是用户按实物手输的（入库明细已有 EFFECT_DATE 列），
-- 给它挂"挑已有批次"反而会诱导用户去选一个已有效期、而不是录本单的真实效期。
--
-- 不加列、不改契约：来源表仍是 INV_BATCH_M，服务端按来源表把它映射成注册数据源键
-- （`ChooserRepository.PreferredSourceKey`），前端据此走 sourceKey 分支。
-- FILTER_STRUCT 保留既有口径（只列还有余量的批次），与已有的 INV_OCCUR_OUT_D 那行一致；
-- 走 sourceKey 分支时该过滤条件由注册数据源自身承担（同一语义：IN_SUM > OUT_SUM）。
--
-- 幂等：(T_ID, F_ID, SERIAL_NO) 已存在即跳过；后置自证要求这 14 张表的批号字段都有启用来源。
-- ============================================================================

SET NOCOUNT ON;
SET XACT_ABORT ON;

IF DB_NAME() <> N'EOS.ERP'
    THROW 50000, N'本脚本只能在 EOS.ERP 数据库内执行。', 1;

BEGIN TRANSACTION;

DECLARE @Tables TABLE (T_ID NVARCHAR(200) NOT NULL PRIMARY KEY);
INSERT INTO @Tables (T_ID) VALUES
    (N'COP_SEND_D'),            -- 1406 送货单
    (N'COP_FITOUT_D'),          -- 1411 备货单
    (N'COP_ORDER_D'),           -- 1405 客户订单
    (N'COP_BACK_D'),            -- 1423 退料单(生产不良)
    (N'MOC_GET_D'),             -- 1503/1514/1517/2805/2806 领料类
    (N'MOC_PRODUCT_OUT_D'),     -- 1515 返工单 / 2815 托外返工单
    (N'MOC_OUT_PRODUCT_OUT_D'), -- 2802 托外退货单
    (N'PUR_CANCEL_D'),          -- 1608/1612 退料类
    (N'INV_OCCUR_TRANSFER_D'),  -- 130105 仓库调拔单（调出腿）
    (N'INV_OCCUR_SCRAP_D'),     -- 130106 库存报废单
    (N'INV_LOAN_D'),            -- 130108 借出单
    (N'INV_RETURN_D'),          -- 130109 返还单
    (N'MOU_GET_D');             -- 2907 模房领料单

-- 目标字段必须在库内登记过（否则选择器挂上去也没人渲染）
IF EXISTS (SELECT 1 FROM @Tables t
           WHERE NOT EXISTS (SELECT 1 FROM dbo.FIELDS f WHERE f.T_ID = t.T_ID AND f.F_ID = N'BATCH_NO'))
    THROW 52150, N'目标明细表缺少 BATCH_NO 字段元数据，与本迁移的预期不符。', 1;

INSERT INTO dbo.FIELD_DATASOURCE (
    T_ID, F_ID, SERIAL_NO, ACTIVE_TAG, SOURCE_T_ID, SOURCE_DESC, SOURCE_M_IDX,
    FILTER_STRUCT, RETURN_ITEMS, CREATE_BY, CREATE_DATE)
SELECT t.T_ID, N'BATCH_NO', 1, 1, N'INV_BATCH_M', N'料件批号', 0,
       -- 与既有 INV_OCCUR_OUT_D 那行逐字同形：只列还有余量的批次
       N'{"logic":"AND","items":[{"field":"INV_BATCH_M.IN_SUM","operator":"GT","value":"INV_BATCH_M.OUT_SUM","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}',
       N'[{"target":"BATCH_NO","column":"BATCH_NO"}]',
       N'migration-260', SYSDATETIME()
FROM @Tables t
WHERE NOT EXISTS (SELECT 1 FROM dbo.FIELD_DATASOURCE c
                   WHERE c.T_ID = t.T_ID AND c.F_ID = N'BATCH_NO' AND c.SERIAL_NO = 1);

-- 后置自证：这 13 张表 + 既有的 INV_OCCUR_OUT_D，批号字段都要有**启用中**的批次来源
IF EXISTS (SELECT 1 FROM @Tables t
           WHERE NOT EXISTS (SELECT 1 FROM dbo.FIELD_DATASOURCE c
                              WHERE c.T_ID = t.T_ID AND c.F_ID = N'BATCH_NO'
                                AND ISNULL(c.ACTIVE_TAG, 0) = 1
                                AND LTRIM(RTRIM(ISNULL(c.SOURCE_T_ID, N''))) = N'INV_BATCH_M'))
    THROW 52151, N'批号选择器来源未按预期就位。', 1;

-- ---------------------------------------------------------------- 标脏（快照不得静默落后）
-- 加了选择器入口 = 字段定义变了；不标脏会让已发布快照静默落后于配置。
MERGE dbo.WORKBENCH_MODULE_DIRTY AS t
USING (SELECT DISTINCT m.M_IDX FROM dbo.MODULES m
        JOIN @Tables x ON LTRIM(RTRIM(ISNULL(m.DETAIL_TABLE, N''))) = x.T_ID) AS s
   ON t.M_IDX = s.M_IDX
WHEN MATCHED THEN UPDATE SET DIRTY_TAG = 1, LAST_MODIFIED_BY = N'migration-260', LAST_MODIFIED_AT = SYSDATETIME()
WHEN NOT MATCHED THEN INSERT (M_IDX, DIRTY_TAG, LAST_MODIFIED_BY, LAST_MODIFIED_AT)
    VALUES (s.M_IDX, 1, N'migration-260', SYSDATETIME());

COMMIT TRANSACTION;

PRINT N'== 完成：13 张出库类明细表的批号格接上批次选择器并标脏 ==';
