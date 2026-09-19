-- ============================================================================
-- EOS.ERP migration 198: 盘点单的库位 / 批号 / 盘点范围列入默认字段
-- ----------------------------------------------------------------------------
-- 库位维度加列时，字段元数据只设了 IS_VISIBLE=1 / IS_QUERY=1，没有设 IS_DEFAULT_FIELDS。
-- 工作台构建字段清单时（WorkbenchDefinitionBuilder.ReadFields）对非主键列的口径是
-- "用户没有自定义列配置时，只取 IS_DEFAULT_FIELDS=1 的列"，因此这些列虽然"可见"，
-- 却不会出现在默认明细列与表单里——只有用户自己去"选择列"才看得到。
--
-- 对盘点单来说这是功能性问题而不是显示偏好：按库区生成的明细行带着库位与批号，
-- 看不见就等于按库位盘点不成立。本迁移只为盘点单把三列置为默认字段：
--   · INV_CHECK_STOCK_M.LOCATION_ROOT_NO（盘点范围，本迁移前甚至不进单头字段清单）
--   · INV_CHECK_STOCK_D.LOCATION_NO / BATCH_NO
--
-- 其余 23 张明细表的同名列**不在本迁移范围内**：把它们加进各自模块的默认列会同时改变
-- 23 个模块的默认列表与表单版式，属于显示口径决策，另行处置。
--
-- 幂等：按 (T_ID, F_ID) 定点更新，重复执行为 0 行。
-- 回滚：将三行的 IS_DEFAULT_FIELDS 置回 0。
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
    THROW 52300, @GUARD_MESSAGE, 1;

/* ---------- 前置守卫：三列必须都已登记字段元数据 ---------- */
DECLARE @missing NVARCHAR(400) = (
    SELECT STRING_AGG(t.TBL + N'.' + t.COL, N', ')
    FROM (VALUES (N'INV_CHECK_STOCK_M', N'LOCATION_ROOT_NO'),
                 (N'INV_CHECK_STOCK_D', N'LOCATION_NO'),
                 (N'INV_CHECK_STOCK_D', N'BATCH_NO')) AS t(TBL, COL)
    WHERE NOT EXISTS (
        SELECT 1 FROM dbo.FIELDS f WHERE f.T_ID = t.TBL AND LTRIM(RTRIM(f.F_ID)) = t.COL));

IF @missing IS NOT NULL
BEGIN
    DECLARE @missingMessage NVARCHAR(400) = N'以下列尚无字段元数据，迁移中止：' + @missing + N'。';
    THROW 52301, @missingMessage, 1;
END

/* ---------- 定点更新 ---------- */
UPDATE f
   SET f.IS_DEFAULT_FIELDS = 1,
       f.LAST_UPDATE_BY = N'DbUp',
       f.LAST_UPDATE_DATE = SYSDATETIME()
  FROM dbo.FIELDS f
  JOIN (VALUES (N'INV_CHECK_STOCK_M', N'LOCATION_ROOT_NO'),
               (N'INV_CHECK_STOCK_D', N'LOCATION_NO'),
               (N'INV_CHECK_STOCK_D', N'BATCH_NO')) AS t(TBL, COL)
    ON f.T_ID = t.TBL AND LTRIM(RTRIM(f.F_ID)) = t.COL
 WHERE COALESCE(f.IS_DEFAULT_FIELDS, 0) <> 1;

PRINT N'== 已把 ' + CONVERT(NVARCHAR(10), @@ROWCOUNT) + N' 个盘点单字段列入默认字段 ==';

/* ---------- 标记待发布：运行时读的是已发布快照 ---------- */
MERGE dbo.WORKBENCH_MODULE_DIRTY AS T
USING (SELECT 130101 AS MODULE_ID) AS S
   ON T.MODULE_ID = S.MODULE_ID
WHEN MATCHED THEN UPDATE SET DIRTY_TAG = 1, LAST_MODIFIED_BY = N'DbUp', LAST_MODIFIED_AT = SYSDATETIME()
WHEN NOT MATCHED THEN INSERT (MODULE_ID, DIRTY_TAG, LAST_MODIFIED_BY, LAST_MODIFIED_AT)
    VALUES (S.MODULE_ID, 1, N'DbUp', SYSDATETIME());

/* ---------- 收口断言 ---------- */
IF EXISTS (
    SELECT 1 FROM dbo.FIELDS f
    JOIN (VALUES (N'INV_CHECK_STOCK_M', N'LOCATION_ROOT_NO'),
                 (N'INV_CHECK_STOCK_D', N'LOCATION_NO'),
                 (N'INV_CHECK_STOCK_D', N'BATCH_NO')) AS t(TBL, COL)
      ON f.T_ID = t.TBL AND LTRIM(RTRIM(f.F_ID)) = t.COL
    WHERE COALESCE(f.IS_DEFAULT_FIELDS, 0) <> 1 OR COALESCE(f.IS_VISIBLE, 1) <> 1)
    THROW 52302, N'盘点单库位 / 批号 / 盘点范围列未成为默认可见字段，迁移中止。', 1;

IF NOT EXISTS (SELECT 1 FROM dbo.WORKBENCH_MODULE_DIRTY WHERE MODULE_ID = 130101 AND DIRTY_TAG = 1)
    THROW 52303, N'模块 130101 未标记为待发布，迁移中止。', 1;

PRINT N'== 盘点单库位 / 批号 / 盘点范围已列入默认字段，模块待发布 ==';
