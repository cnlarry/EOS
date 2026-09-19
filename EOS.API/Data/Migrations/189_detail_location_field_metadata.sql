-- ============================================================================
-- EOS.ERP migration 190: 既有表上新增的库位 / 批次列补字段元数据
-- ----------------------------------------------------------------------------
-- 库位维度加在 24 张单据明细表、盘点单头、月结明细与两张库存表上。表结构有了不等于
-- 能用：**没有 FIELDS 行，列在列表与表单里就不可见、也不可配置**，四键功能只落地一半。
--
-- 本迁移**按表名 + 列名白名单驱动**，类型与文案从库内元数据推导，不手抄逐列取值：
--   · 目标列由列名白名单圈定（位置列四态 + 批号 + 盘点范围 + 位置路径）；
--   · F_TYPE 取该列的真实类型；
--   · F_DESC 由列名映射得到中文标签；
--   · 只在**尚无 FIELDS 行**时插入，因此不会覆盖已有的手工配置。
--
-- 口径：明细列是草稿态可填字段 ⇒ IS_VISIBLE=1 / IS_QUERY=1 / IS_READONLY=0；
-- 位置路径是服务端维护的快照列 ⇒ 不展示、不查询、只读。
--
-- 幂等：按 (T_ID, F_ID) 判存在性。
-- 回滚：DELETE 本迁移写入的行（按 F_REMARK 标记识别）。
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

/* ---------- 目标范围 ---------- */
DECLARE @targets TABLE (TBL SYSNAME NOT NULL, COL SYSNAME NOT NULL, PRIMARY KEY (TBL, COL));

INSERT INTO @targets (TBL, COL)
SELECT t.name, c.name
FROM sys.tables t
JOIN sys.columns c ON c.object_id = t.object_id
WHERE SCHEMA_NAME(t.schema_id) = N'dbo'
  AND c.name IN (N'LOCATION_NO', N'IN_LOCATION_NO', N'OUT_LOCATION_NO', N'BAD_LOCATION_NO',
                 N'LOCATION_ROOT_NO', N'LOCATION_PATH', N'BATCH_NO')
  AND t.name IN (
      -- P1-07：24 张单据明细表
      N'INV_OCCUR_INIT_D', N'INV_OCCUR_IN_D', N'INV_OCCUR_OUT_D', N'INV_OCCUR_TRANSFER_D',
      N'INV_OCCUR_SCRAP_D', N'INV_OCCUR_ADJUST_D', N'INV_LOAN_D', N'INV_RETURN_D', N'INV_CHECK_STOCK_D',
      N'COP_SEND_D', N'COP_RETURN_D', N'COP_FITOUT_D', N'COP_FITIN_D', N'COP_BACK_D',
      N'MOC_GET_D', N'MOC_BACK_D', N'MOC_PRODUCT_IN_D', N'MOC_PRODUCT_OUT_D',
      N'PUR_RECEIVE_D', N'PUR_CANCEL_D',
      N'MOC_OUT_PRODUCT_IN_D', N'MOC_OUT_PRODUCT_OUT_D',
      N'MOU_GET_D', N'MOU_GET2_D',
      -- P1-08 / P1-11 / P1-04 / P1-05
      N'INV_CHECK_STOCK_M', N'INV_PRO_MONTH_D', N'INV_PRO_DEPOT', N'INV_DEPOT_LOG');

IF NOT EXISTS (SELECT 1 FROM @targets)
    THROW 51901, N'未匹配到任何目标列，迁移中止（表名或列名白名单可能有误）。', 1;

/* ---------- 插入缺失的字段元数据 ---------- */
INSERT INTO dbo.FIELDS
    (T_ID, F_ID, F_DESC, F_TYPE, IS_PK, IS_VISIBLE, IS_QUERY, IS_READONLY, DISPLAY_LENGTH, F_REMARK,
     FORM_ORDER, LAST_UPDATE_BY, LAST_UPDATE_DATE)
SELECT g.TBL,
       g.COL,
       CASE g.COL
           WHEN N'LOCATION_NO'      THEN N'库位'
           WHEN N'IN_LOCATION_NO'   THEN N'入库库位'
           WHEN N'OUT_LOCATION_NO'  THEN N'出库库位'
           WHEN N'BAD_LOCATION_NO'  THEN N'不良品库位'
           WHEN N'LOCATION_ROOT_NO' THEN N'盘点范围'
           WHEN N'LOCATION_PATH'    THEN N'位置路径'
           WHEN N'BATCH_NO'         THEN N'批号'
       END,
       TYPE_NAME(c.user_type_id),
       CASE WHEN pk.column_id IS NULL THEN 0 ELSE 1 END,
       CASE WHEN g.COL = N'LOCATION_PATH' THEN 0 ELSE 1 END,
       CASE WHEN g.COL = N'LOCATION_PATH' THEN 0 ELSE 1 END,
       CASE WHEN g.COL = N'LOCATION_PATH' THEN 1 ELSE 0 END,
       CASE g.COL
           WHEN N'LOCATION_NO'      THEN 94
           WHEN N'IN_LOCATION_NO'   THEN 94
           WHEN N'OUT_LOCATION_NO'  THEN 94
           WHEN N'BAD_LOCATION_NO'  THEN 94
           WHEN N'LOCATION_ROOT_NO' THEN 94
           WHEN N'LOCATION_PATH'    THEN 160
           WHEN N'BATCH_NO'         THEN 94
       END,
       N'由 P1 库位维度迁移新增',
       COALESCE((SELECT MAX(FORM_ORDER) FROM dbo.FIELDS f WHERE f.T_ID = g.TBL), 0) + 100,
       N'migration',
       GETDATE()
FROM @targets g
JOIN sys.tables t ON t.name = g.TBL AND SCHEMA_NAME(t.schema_id) = N'dbo'
JOIN sys.columns c ON c.object_id = t.object_id AND c.name = g.COL
LEFT JOIN (
    SELECT ic.object_id, ic.column_id
    FROM sys.index_columns ic
    JOIN sys.indexes i ON i.object_id = ic.object_id AND i.index_id = ic.index_id
    WHERE i.is_primary_key = 1 AND ic.is_included_column = 0
) pk ON pk.object_id = c.object_id AND pk.column_id = c.column_id
WHERE NOT EXISTS (
    SELECT 1 FROM dbo.FIELDS f
    WHERE f.T_ID = g.TBL AND LTRIM(RTRIM(f.F_ID)) = g.COL);

PRINT N'== 已写入字段元数据 ' + CONVERT(NVARCHAR(10), @@ROWCOUNT) + N' 行 ==';

/* ---------- 收口断言：白名单里的每一列都必须有 FIELDS 行 ---------- */
DECLARE @missing INT = (
    SELECT COUNT(*) FROM @targets g
    WHERE NOT EXISTS (SELECT 1 FROM dbo.FIELDS f WHERE f.T_ID = g.TBL AND LTRIM(RTRIM(f.F_ID)) = g.COL));

IF @missing > 0
    THROW 51902, N'仍存在没有字段元数据的目标列，迁移中止。', 1;

/* 位置路径列不得出现在查询或展示中（它是服务端维护的快照） */
IF EXISTS (SELECT 1 FROM dbo.FIELDS WHERE LTRIM(RTRIM(F_ID)) = N'LOCATION_PATH' AND (IS_VISIBLE = 1 OR IS_QUERY = 1))
    THROW 51903, N'位置路径列被错误地设为可见或可查询，迁移中止。', 1;

/* 批号列必须按输入字段对待（可填、可查询） */
IF EXISTS (
    SELECT 1 FROM dbo.FIELDS
    WHERE LTRIM(RTRIM(F_ID)) = N'BATCH_NO' AND F_REMARK = N'由 P1 库位维度迁移新增' AND IS_QUERY = 0)
    THROW 51904, N'新增的批号列未设为可查询，迁移中止。', 1;

DECLARE @total INT = (SELECT COUNT(*) FROM @targets);
DECLARE @registered INT = (
    SELECT COUNT(*) FROM @targets g
    WHERE EXISTS (SELECT 1 FROM dbo.FIELDS f WHERE f.T_ID = g.TBL AND LTRIM(RTRIM(f.F_ID)) = g.COL));
DECLARE @registeredText NVARCHAR(10) = CONVERT(NVARCHAR(10), @registered);
DECLARE @totalText NVARCHAR(10) = CONVERT(NVARCHAR(10), @total);

PRINT N'== 收口：目标列 ' + @totalText + N' 个，已登记字段元数据 ' + @registeredText + N' 个 ==';
