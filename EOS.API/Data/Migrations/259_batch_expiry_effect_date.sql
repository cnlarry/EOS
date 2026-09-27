-- ============================================================================
-- EOS.ERP migration 257: 批次效期录入（入库单据明细加 EFFECT_DATE）
-- ----------------------------------------------------------------------------
-- 效期的唯一真源是批次账 `INV_BATCH_M.EFFECT_DATE`（列早已存在）。本迁移只补"它从哪来"：
-- 给**首次入库**的入库单据明细加一列 `EFFECT_DATE`（可空），并把它接进录入链路——
-- ① 物理列；② FIELDS 元数据（否则"列存在但表单里看不到"）；③ 版式行（紧随批号）；
-- ④ 过账动作参数（否则引擎的行集里根本取不到这一列）。
--
-- 加列范围（6 张表 / 9 个模块）按"该批次是否**首次进账**"判定，逐表理由见
-- docs/plans/A5-批次效期-施工清单.md ：
--   PUR_RECEIVE_D        1607 收料单                       采购收料
--   MOC_PRODUCT_IN_D     1505 入库单 / 1519 入库单管理(外) / 2816 托外入库单   生产与委外完工入库
--   MOC_OUT_PRODUCT_IN_D 2801 托外进货单管理               委外来料收货
--   INV_OCCUR_IN_D       2817 品检收料入库 / 130103 其它入库单   品检放行后实物入库
--   INV_OCCUR_INIT_D     130102 期初开帐单                  期初建账
--   INV_OCCUR_ADJUST_D   130107 库存调整单                  盘盈 / 调整入库
-- **回流入库**（退货 / 备货返仓 / 生产退料 / 领料退回 / 借出返还 / 调拨 / 报废反冲）一律不加列：
-- 它们的批次早已存在、效期在首次入库时就已确定，再开一个录入点只会在填值不同时把整单拒掉。
--
-- 语义：`EFFECT_DATE IS NULL` = 该批次不受效期管控（不参与按效期排序、不被过期拦截）。
-- 落库时机与冲突处置在过账引擎（InventoryMoveHandler），不在本迁移。
--
-- 幂等：列/元数据/版式/动作参数各自"已存在即跳过"；任一环节与预期不符即 THROW（不静默跳过）。
-- ============================================================================

SET NOCOUNT ON;
SET XACT_ABORT ON;

IF DB_NAME() <> N'EOS.ERP'
    THROW 50000, N'本脚本只能在 EOS.ERP 数据库内执行。', 1;

BEGIN TRANSACTION;

DECLARE @Tables TABLE (T_ID NVARCHAR(200) NOT NULL, Purpose NVARCHAR(200) NOT NULL);
INSERT INTO @Tables (T_ID, Purpose) VALUES
    (N'PUR_RECEIVE_D',        N'1607 收料单：采购收料'),
    (N'MOC_PRODUCT_IN_D',     N'1505/1519/2816 完工入库：生产与委外'),
    (N'MOC_OUT_PRODUCT_IN_D', N'2801 托外进货单：委外来料收货'),
    (N'INV_OCCUR_IN_D',       N'2817/130103 品检收料入库与其他入库'),
    (N'INV_OCCUR_INIT_D',     N'130102 期初开帐单：上线建账'),
    (N'INV_OCCUR_ADJUST_D',   N'130107 库存调整单：盘盈与调整入库');

-- 「模块 × 明细表」版式落点（9 对）
DECLARE @Layouts TABLE (M_IDX INT NOT NULL, T_ID NVARCHAR(200) NOT NULL);
INSERT INTO @Layouts (M_IDX, T_ID) VALUES
    (1607, N'PUR_RECEIVE_D'),
    (1505, N'MOC_PRODUCT_IN_D'),
    (1519, N'MOC_PRODUCT_IN_D'),
    (2816, N'MOC_PRODUCT_IN_D'),
    (2801, N'MOC_OUT_PRODUCT_IN_D'),
    (2817, N'INV_OCCUR_IN_D'),
    (130103, N'INV_OCCUR_IN_D'),
    (130102, N'INV_OCCUR_INIT_D'),
    (130107, N'INV_OCCUR_ADJUST_D');

-- 过账动作（9 条 inventory-move，方向 IN）——参数里必须带得上 EFFECT_DATE
DECLARE @ActionModules TABLE (M_IDX INT NOT NULL PRIMARY KEY);
INSERT INTO @ActionModules (M_IDX) VALUES (1607), (1505), (1519), (2816), (2801), (2817), (130103), (130102), (130107);

-- ---------------------------------------------------------------- ① 物理列
DECLARE @table NVARCHAR(200), @tablePurpose NVARCHAR(200);
DECLARE tableCursor CURSOR LOCAL FAST_FORWARD FOR SELECT T_ID, Purpose FROM @Tables;
OPEN tableCursor;
FETCH NEXT FROM tableCursor INTO @table, @tablePurpose;
WHILE @@FETCH_STATUS = 0
BEGIN
    IF OBJECT_ID(N'dbo.' + @table, N'U') IS NULL
        THROW 52100, N'目标明细表不存在，与本迁移的预期不符。', 1;

    IF COL_LENGTH(N'dbo.' + @table, N'EFFECT_DATE') IS NULL
    BEGIN
        EXEC (N'ALTER TABLE dbo.' + @table + N' ADD EFFECT_DATE DATETIME NULL;');
        PRINT N'== 新增列 ' + @table + N'.EFFECT_DATE（DATETIME，可空）==';
    END
    ELSE
        PRINT N'== 列 ' + @table + N'.EFFECT_DATE 已存在，跳过 ==';

    -- 列语义写进库里：看结构的人不必去翻计划文档
    IF NOT EXISTS (SELECT 1 FROM sys.extended_properties
                    WHERE major_id = OBJECT_ID(N'dbo.' + @table)
                      AND minor_id = COLUMNPROPERTY(OBJECT_ID(N'dbo.' + @table), N'EFFECT_DATE', 'ColumnId')
                      AND name = N'MS_Description')
        EXEC sys.sp_addextendedproperty
            @name = N'MS_Description',
            @value = N'批次效期（有效日期）：入库时录入，首次建批次账时写入 INV_BATCH_M.EFFECT_DATE。留空表示该批次不受效期管控。受控写入口见模块 1302。',
            @level0type = N'SCHEMA', @level0name = N'dbo',
            @level1type = N'TABLE', @level1name = @table,
            @level2type = N'COLUMN', @level2name = N'EFFECT_DATE';

    FETCH NEXT FROM tableCursor INTO @table, @tablePurpose;
END
CLOSE tableCursor;
DEALLOCATE tableCursor;

-- ---------------------------------------------------------------- ② FIELDS 元数据
-- 形态照 INV_BATCH_M.EFFECT_DATE（同为 datetime 的批次日期列），**但 IS_VERIFY=0**：
-- 效期可空是口径本身（NULL = 不受管控），"必填"是库别策略档位 3 的运行期判据，不是字段级约束。
INSERT INTO dbo.FIELDS (
    T_ID, F_ID, F_DESC, F_TYPE, DISPLAY_LENGTH, HEADER_ALIGN, ITEM_ALIGN, DISPLAY_FORMAT,
    IS_VERIFY, VERIFY_INDEX, IS_PK, IS_READONLY, IS_VISIBLE, IS_VIRTUAL, IS_AUTOINC, IS_QUERY,
    IS_COST, IS_SECRECY, CAN_COPY, IS_DEFAULT_FIELDS, F_REMARK, LAST_UPDATE_BY, LAST_UPDATE_DATE)
SELECT t.T_ID, N'EFFECT_DATE', N'有效日期', N'datetime', 80, N'center', N'left', N'yyyy-MM-dd',
       0, 0, 0, 0, 1, 0, 0, 1,
       0, 0, 1, 1, N'批次效期：入库时录入；留空表示该批次不受效期管控。', N'migration-257', SYSDATETIME()
FROM @Tables t
WHERE NOT EXISTS (SELECT 1 FROM dbo.FIELDS f WHERE f.T_ID = t.T_ID AND f.F_ID = N'EFFECT_DATE');

-- 后置自证：6 行齐备、且都不是占位标签（占位文本非空，读取侧的"空则回落字段代号"兜不住）
IF EXISTS (SELECT 1 FROM @Tables t
           WHERE NOT EXISTS (SELECT 1 FROM dbo.FIELDS f
                              WHERE f.T_ID = t.T_ID AND f.F_ID = N'EFFECT_DATE'
                                AND LTRIM(RTRIM(ISNULL(f.F_DESC, N''))) NOT IN (N'', N'NULL', N'&nbsp;')
                                AND ISNULL(f.IS_VISIBLE, 0) = 1
                                AND ISNULL(f.IS_READONLY, 0) = 0
                                AND ISNULL(f.IS_DEFAULT_FIELDS, 0) = 1))
    THROW 52101, N'FIELDS 元数据未按预期就位（缺行、占位标签或可见性/默认列不符）。', 1;

-- ---------------------------------------------------------------- ③ 版式行（紧随批号）
-- 只处理还没有 EFFECT_DATE 版式行的对：重复执行时既不重复平移 ORDER_NO，也不重复插行。
DELETE p FROM @Layouts p
WHERE EXISTS (SELECT 1 FROM dbo.MODULE_FORM_LAYOUT l
               WHERE l.M_IDX = p.M_IDX AND l.T_ID = p.T_ID AND l.F_ID = N'EFFECT_DATE');

-- 锚点必须是批号那一行：效期紧挨批号显示，位置由它决定而不是凭 ORDER_NO 猜
IF EXISTS (SELECT 1 FROM @Layouts p
           WHERE NOT EXISTS (SELECT 1 FROM dbo.MODULE_FORM_LAYOUT l
                              WHERE l.M_IDX = p.M_IDX AND l.T_ID = p.T_ID AND l.F_ID = N'BATCH_NO'))
    THROW 52102, N'目标「模块 × 明细表」缺少批号版式行，效期的落点无锚可依。', 1;

-- 腾位置：锚点之后的整列后移一位。该表只有主键 (M_IDX, T_ID, F_ID)，ORDER_NO 无唯一索引，
-- 因此不需要"倒序更新"避让瞬态冲突。
UPDATE l
SET l.ORDER_NO = l.ORDER_NO + 1
FROM dbo.MODULE_FORM_LAYOUT l
JOIN @Layouts p ON p.M_IDX = l.M_IDX AND p.T_ID = l.T_ID
JOIN dbo.MODULE_FORM_LAYOUT a ON a.M_IDX = p.M_IDX AND a.T_ID = p.T_ID AND a.F_ID = N'BATCH_NO'
WHERE l.ORDER_NO > a.ORDER_NO;

INSERT INTO dbo.MODULE_FORM_LAYOUT (
    M_IDX, T_ID, F_ID, TAB_NO, ORDER_NO, SPAN, ROW_SPAN, NEW_LINE,
    SECTION_ID, CELL_GROUP, CELL_ROLE, IS_HIDDEN, UPDATED_BY, UPDATED_AT)
SELECT a.M_IDX, a.T_ID, N'EFFECT_DATE', a.TAB_NO, a.ORDER_NO + 1, 1, 1, 0,
       a.SECTION_ID, NULL, 0, 0, N'migration-257', SYSDATETIME()
FROM dbo.MODULE_FORM_LAYOUT a
JOIN @Layouts p ON p.M_IDX = a.M_IDX AND p.T_ID = a.T_ID
WHERE a.F_ID = N'BATCH_NO';

-- 后置自证：9 对都就位，且紧挨批号
IF EXISTS (SELECT 1 FROM @Layouts p
           WHERE NOT EXISTS (
               SELECT 1 FROM dbo.MODULE_FORM_LAYOUT e
               JOIN dbo.MODULE_FORM_LAYOUT b ON b.M_IDX = e.M_IDX AND b.T_ID = e.T_ID AND b.F_ID = N'BATCH_NO'
               WHERE e.M_IDX = p.M_IDX AND e.T_ID = p.T_ID AND e.F_ID = N'EFFECT_DATE'
                 AND e.ORDER_NO = b.ORDER_NO + 1))
    THROW 52103, N'效期版式行未按预期落在批号之后。', 1;

-- ---------------------------------------------------------------- ④ 过账动作参数
-- 前置自证：这 9 个模块各恰有一条启用的入库动作，且 detail 锚点齐备（否则下面的 UPDATE 会静默少改）
IF EXISTS (SELECT 1 FROM @ActionModules m
           WHERE (SELECT COUNT(*) FROM dbo.MODULE_BUSINESS_ACTION a
                   WHERE a.M_IDX = m.M_IDX AND a.EFFECT_KEY = N'inventory-move' AND a.ENABLED = 1
                     AND CHARINDEX(N'"direction":"IN"', ISNULL(a.PARAM_STRUCT, N'')) > 0) <> 1)
    THROW 52104, N'目标模块的入库动作不是恰好一条，无法安全追加过账参数。', 1;

IF EXISTS (SELECT 1 FROM @ActionModules m
           WHERE NOT EXISTS (SELECT 1 FROM dbo.MODULE_BUSINESS_ACTION a
                              WHERE a.M_IDX = m.M_IDX AND a.EFFECT_KEY = N'inventory-move' AND a.ENABLED = 1
                                AND CHARINDEX(N'"BATCH_NO"]', ISNULL(a.PARAM_STRUCT, N'')) > 0))
    THROW 52107, N'目标入库动作的 fieldMap.detail 没有 BATCH_NO 锚点，无法安全追加效期。', 1;

-- 行集的目标列由 fieldMap.detail 决定；不登记这一列，引擎即使拿到单据也取不到效期。
-- 只追加 detail 数组的末项（这 9 条动作的 detail 末项都是 "BATCH_NO"），其余键一字不碰。
UPDATE a
SET a.PARAM_STRUCT = REPLACE(a.PARAM_STRUCT, N'"BATCH_NO"]', N'"BATCH_NO","EFFECT_DATE"]'),
    a.LAST_UPDATE_BY = N'migration-257',
    a.LAST_UPDATE_DATE = SYSDATETIME()
FROM dbo.MODULE_BUSINESS_ACTION a
JOIN @ActionModules m ON m.M_IDX = a.M_IDX
WHERE a.EFFECT_KEY = N'inventory-move'
  AND a.ENABLED = 1
  AND CHARINDEX(N'"EFFECT_DATE"', ISNULL(a.PARAM_STRUCT, N'')) = 0
  AND CHARINDEX(N'"BATCH_NO"]', ISNULL(a.PARAM_STRUCT, N'')) > 0;

-- 后置自证：9 条动作的 detail 都带上了效期
IF EXISTS (SELECT 1 FROM @ActionModules m
           WHERE NOT EXISTS (SELECT 1 FROM dbo.MODULE_BUSINESS_ACTION a
                              WHERE a.M_IDX = m.M_IDX AND a.EFFECT_KEY = N'inventory-move' AND a.ENABLED = 1
                                AND a.PARAM_STRUCT LIKE N'%"EFFECT_DATE"%'))
    THROW 52105, N'过账动作参数未带上 EFFECT_DATE。', 1;

-- ---------------------------------------------------------------- ⑤ 标脏（快照不得静默落后）
MERGE dbo.WORKBENCH_MODULE_DIRTY AS t
USING (SELECT DISTINCT M_IDX FROM @Layouts) AS s ON t.M_IDX = s.M_IDX
WHEN MATCHED THEN UPDATE SET DIRTY_TAG = 1, LAST_MODIFIED_BY = N'migration-257', LAST_MODIFIED_AT = SYSDATETIME()
WHEN NOT MATCHED THEN INSERT (M_IDX, DIRTY_TAG, LAST_MODIFIED_BY, LAST_MODIFIED_AT)
    VALUES (s.M_IDX, 1, N'migration-257', SYSDATETIME());

IF EXISTS (SELECT 1 FROM @Layouts p
           WHERE NOT EXISTS (SELECT 1 FROM dbo.WORKBENCH_MODULE_DIRTY d
                              WHERE d.M_IDX = p.M_IDX AND ISNULL(d.DIRTY_TAG, 0) = 1))
    THROW 52106, N'受影响模块未全部标记为待发布。', 1;

COMMIT TRANSACTION;

PRINT N'== 完成：6 张入库明细表加 EFFECT_DATE；9 个模块的 FIELDS/版式/过账参数就位并标脏 ==';
