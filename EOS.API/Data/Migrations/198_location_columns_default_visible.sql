-- ============================================================================
-- EOS.ERP migration 199: 库位 / 批号列进入默认显示列（用户 2026-09-19 拍板 #124 A）
-- ----------------------------------------------------------------------------
-- 背景：库位维度给 24 张单据明细表与若干库存表加了列，字段元数据只设了
-- IS_VISIBLE=1 / IS_QUERY=1，**没有设 IS_DEFAULT_FIELDS**。工作台构建字段清单的口径是
-- "用户没有自定义列配置时，只取 IS_DEFAULT_FIELDS=1 的非主键列"
-- （WorkbenchDefinitionBuilder.ReadFields），因此这些列虽然"可见"，却不会出现在
-- 默认明细列与表单里——只有用户自己点「选择列」才看得到。
--
-- 这 29 张表清一色是库存与收发货过账单据（出入库 / 调拨 / 报废 / 盘点 / 领退料 /
-- 生产入出库 / 收料 / 采购退料 / 发货退货备货 / 托外领退 / 库存查询表），
-- **它们都会真的记库位**，默认不显示等于功能做了却找不到。故一次性置为默认列。
--
-- 目标列由**列名白名单 × 表名白名单**圈定：
--   · 列：LOCATION_NO / IN_LOCATION_NO / OUT_LOCATION_NO / BAD_LOCATION_NO / BATCH_NO
--   · **不含 LOCATION_PATH**（服务端维护的物化路径快照列，一律不展示、不查询）
--   · **不含 LOCATION_ROOT_NO**（盘点范围，属盘点单概念，已在迁移 198 单独处理）
--
-- 不做的事（刻意）：
--   · 不改 FORM_ORDER / FORM_TAB_NO —— 那是表单版式口径，本次只改"默认显示与否"；
--   · 不动没有已发布快照的模块 —— 它们不在工作台上运行，标记脏反而制造噪声。
--
-- 幂等：只更新尚未置位的行；重复执行 0 行。
-- 回滚：把本迁移置位的行按 T_ID+F_ID 白名单还原为 IS_DEFAULT_FIELDS=0。
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
    THROW 52400, @GUARD_MESSAGE, 1;

/* ---------- 白名单 ---------- */
DECLARE @columns TABLE (F SYSNAME NOT NULL PRIMARY KEY);
INSERT INTO @columns (F) VALUES
    (N'LOCATION_NO'), (N'IN_LOCATION_NO'), (N'OUT_LOCATION_NO'), (N'BAD_LOCATION_NO'), (N'BATCH_NO');

DECLARE @tables TABLE (T SYSNAME NOT NULL PRIMARY KEY);
INSERT INTO @tables (T) VALUES
    -- 销售 / 发货退货备货
    (N'COP_SEND_D'), (N'COP_RETURN_D'), (N'COP_FITOUT_D'), (N'COP_FITIN_D'), (N'COP_BACK_D'),
    (N'COP_TRANSFER_D'), (N'COP_TRANSBACK_D'),
    -- 生产
    (N'MOC_GET_D'), (N'MOC_BACK_D'), (N'MOC_PRODUCT_IN_D'), (N'MOC_PRODUCT_OUT_D'),
    (N'MOC_OUT_PRODUCT_IN_D'), (N'MOC_OUT_PRODUCT_OUT_D'), (N'MOU_GET_D'), (N'MOU_GET2_D'),
    -- 采购
    (N'PUR_RECEIVE_D'), (N'PUR_CANCEL_D'),
    -- 库存单据
    (N'INV_OCCUR_INIT_D'), (N'INV_OCCUR_IN_D'), (N'INV_OCCUR_OUT_D'), (N'INV_OCCUR_TRANSFER_D'),
    (N'INV_OCCUR_SCRAP_D'), (N'INV_OCCUR_ADJUST_D'), (N'INV_LOAN_D'), (N'INV_RETURN_D'),
    -- 库存主档 / 查询表 / 库位主档
    (N'INV_PRO_DEPOT'), (N'INV_PRO_MONTH_D'), (N'INV_DEPOT_LOG'), (N'DEPOT_LOCATION');

/* ---------- 前置守卫：白名单本身必须站得住 ----------
   注意不能要求"表 × 列"的组合全部存在：这 29 张表并不是每张都同时有五个列位
   （例如退货单有 LOCATION_NO 与 BAD_LOCATION_NO、却没有 IN_LOCATION_NO）。
   因此守两件事：表都在、且每个列名至少命中一个列位。 */
DECLARE @missingTable NVARCHAR(400) = (
    SELECT STRING_AGG(t.T, N', ') FROM @tables t
    WHERE OBJECT_ID(N'dbo.' + t.T, N'U') IS NULL);

IF @missingTable IS NOT NULL
BEGIN
    DECLARE @missingTableMessage NVARCHAR(400) = N'白名单里的表不存在，迁移中止：' + @missingTable + N'。';
    THROW 52401, @missingTableMessage, 1;
END

DECLARE @unusedColumns NVARCHAR(400) = (
    SELECT STRING_AGG(c.F, N', ') FROM @columns c
    WHERE NOT EXISTS (SELECT 1 FROM dbo.FIELDS f
                      JOIN @tables t ON LTRIM(RTRIM(f.T_ID)) = t.T
                      WHERE LTRIM(RTRIM(f.F_ID)) = c.F));

IF @unusedColumns IS NOT NULL
BEGIN
    DECLARE @unusedColumnMessage NVARCHAR(400) =
        N'白名单里的列名在目标表上一个列位都没有，迁移中止：' + @unusedColumns + N'。';
    THROW 52402, @unusedColumnMessage, 1;
END

/* ---------- 置为默认列（只动尚未置位的） ---------- */
UPDATE f
   SET f.IS_DEFAULT_FIELDS = 1,
       f.LAST_UPDATE_BY = N'DbUp',
       f.LAST_UPDATE_DATE = SYSDATETIME()
  FROM dbo.FIELDS f
  JOIN @tables t ON LTRIM(RTRIM(f.T_ID)) = t.T
  JOIN @columns c ON LTRIM(RTRIM(f.F_ID)) = c.F
 WHERE COALESCE(f.IS_VISIBLE, 1) = 1 AND COALESCE(f.IS_DEFAULT_FIELDS, 0) <> 1;

DECLARE @updated INT = @@ROWCOUNT;
PRINT N'== 已把 ' + CONVERT(NVARCHAR(10), @updated) + N' 个库位 / 批号列位置为默认显示列 ==';

/* ---------- 标记待发布：只针对已有当前快照的受影响模块 ---------- */
MERGE dbo.WORKBENCH_MODULE_DIRTY AS D
USING (SELECT DISTINCT M.M_IDX AS MODULE_ID
       FROM dbo.MODULES M
       JOIN dbo.WORKBENCH_DEFINITION_SNAPSHOT S ON S.MODULE_ID = M.M_IDX AND S.IS_CURRENT = 1
       WHERE LTRIM(RTRIM(ISNULL(M.MASTER_TABLE, ''))) IN (SELECT T FROM @tables)
          OR LTRIM(RTRIM(ISNULL(M.DETAIL_TABLE, ''))) IN (SELECT T FROM @tables)) AS S
   ON D.MODULE_ID = S.MODULE_ID
WHEN MATCHED THEN UPDATE SET D.DIRTY_TAG = 1, D.LAST_MODIFIED_BY = N'DbUp', D.LAST_MODIFIED_AT = SYSDATETIME()
WHEN NOT MATCHED THEN INSERT (MODULE_ID, DIRTY_TAG, LAST_MODIFIED_BY, LAST_MODIFIED_AT)
    VALUES (S.MODULE_ID, 1, N'DbUp', SYSDATETIME());

PRINT N'== 已标记 ' + CONVERT(NVARCHAR(10), @@ROWCOUNT) + N' 个受影响模块待重发布 ==';

/* ---------- 收口断言 ---------- */
IF EXISTS (
    SELECT 1 FROM dbo.FIELDS f
    JOIN @tables t ON LTRIM(RTRIM(f.T_ID)) = t.T
    JOIN @columns c ON LTRIM(RTRIM(f.F_ID)) = c.F
    WHERE COALESCE(f.IS_DEFAULT_FIELDS, 0) <> 1 OR COALESCE(f.IS_VISIBLE, 1) <> 1)
    THROW 52402, N'白名单里仍有未成为默认可见列的列位，迁移中止。', 1;

/* 物化路径列是服务端快照列：不得因本次改动而被顺带设为可见或默认列 */
IF EXISTS (SELECT 1 FROM dbo.FIELDS
           WHERE LTRIM(RTRIM(F_ID)) = N'LOCATION_PATH'
             AND (COALESCE(IS_VISIBLE, 1) = 1 OR COALESCE(IS_DEFAULT_FIELDS, 0) = 1))
    THROW 52403, N'位置路径列被错误地设为可见或默认列，迁移中止。', 1;

IF NOT EXISTS (SELECT 1 FROM dbo.WORKBENCH_MODULE_DIRTY WHERE DIRTY_TAG = 1)
    THROW 52404, N'没有任何模块被标记为待发布，迁移中止。', 1;

PRINT N'== 库位 / 批号列已进入默认显示列，受影响模块待重发布 ==';
