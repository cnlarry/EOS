-- ============================================================================
-- EOS.ERP migration 339: host module 14999 (今日需交货) as a workbench view
-- ----------------------------------------------------------------------------
-- Database: EOS.ERP (the single business database of the new system)
--
-- Context: 14999 is a read-only cross-table list whose master table is COP_ORDER_D.
-- Its row-level scope rows (SYSDD, users lesson/long) were written for the retired
-- /detail-query surface and reference a joined table by name:
--     CLIENT.SALES_ID='Lesson'   /   CLIENT.SALES_ID='YW2-08'
-- DataFilterParser accepts a qualified reference only when the qualifier equals the
-- master table (see DataFilterParser.TryResolveField), so for those two accounts the
-- list is refused with 403 — not narrowed, but unavailable.
--
-- Migration 318 deliberately left 14999 out of scope: COP_ORDER_D owns no salesperson
-- column, and its SALES_ID is a *virtual* field (CLIENT.SALES_ID, one of 40
-- cross-table/computed virtual columns). Virtual fields are excluded from the filter
-- whitelist (ReadFilterFieldKeys takes IS_VIRTUAL = 0 only), so the column needs a
-- carrier — a view that exposes it as an ordinary column.
--
-- This migration applies 318's carrier shape to 14999:
--   ① the view materialises the whole field surface — the 54 physical columns of
--      COP_ORDER_D plus its 40 virtual columns, each spelled exactly as
--      FIELDS.VIRTUAL_EXP has it (the join face is the one TABLES.QUERY_RELATION
--      already declared for COP_ORDER_D) — so SALES_ID becomes a real column;
--   ② the module points MASTER_TABLE at the view and its list predicate moves inside
--      the view, exactly as 14996 / 14998 / 170297 do; FILTER is emptied because the
--      predicate now lives in the view and SORT_FIELDS uses the view-qualified column
--      (NormalizeSort accepts a qualifier only for the master table);
--   ③ FIELDS / SYSQL_DEFAULT carry the column metadata of the view. Every row is
--      written with IS_VIRTUAL = 0 and no VIRTUAL_EXP: the columns are real now, and a
--      virtual field would make the runtime join the source tables a second time.
--      IS_PK is cleared as well — a view has no primary key (same as 318);
--   ④ SYSQL_FIELDS and FIELD_DATASOURCE rows are deliberately left on COP_ORDER_D,
--      as in 318: the view is a new T_ID, while 1404 / 1405 keep using COP_ORDER_D
--      as their detail table and must keep reading their own metadata;
--   ⑤ the row-level scope that named a joined table is rewritten to the view's own
--      SALES_ID column;
--   ⑥ column widths saved from 14999's list now land on the view's own T_ID instead of
--      COP_ORDER_D. Before this change such a save rewrote FIELDS.DISPLAY_LENGTH of
--      COP_ORDER_D — which is also 1405's detail table — so adjusting this list
--      silently changed the order form's field widths (observed 2026-10-08: a single
--      column-width save rewrote 71 rows and left 1405's snapshot stale; see L83).
--
-- 14999 is in no module list (neither UnifiedFormEditor:EnabledModuleIds nor
-- ReadOnlyModuleIds) and has no published snapshot, so its definition is rebuilt at
-- runtime — no republish step is needed for this change to take effect.
--
-- Idempotent: the view is dropped and recreated; metadata writes are re-runnable.
-- All objects uppercase.
-- ============================================================================

SET NOCOUNT ON;

/* Some metadata tables carry filtered indexes / indexed views: DELETE and UPDATE need
   the correct SET options. */
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET ANSI_PADDING ON;
SET ANSI_WARNINGS ON;

DECLARE @GUARD_MESSAGE NVARCHAR(400) = N'本脚本只能在 EOS.ERP 数据库内执行，当前库为 ' + DB_NAME() + N'。';
IF DB_NAME() <> N'EOS.ERP'
    THROW 50000, @GUARD_MESSAGE, 1;

IF OBJECT_ID('dbo.COP_ORDER_D', 'U') IS NULL
   OR OBJECT_ID('dbo.COP_ORDER_M', 'U') IS NULL
   OR OBJECT_ID('dbo.CLIENT', 'U') IS NULL
   OR OBJECT_ID('dbo.SYSDN', 'U') IS NULL
   OR OBJECT_ID('dbo.PRODUCT', 'U') IS NULL
   OR OBJECT_ID('dbo.COLOR', 'U') IS NULL
   OR OBJECT_ID('dbo.TAX', 'U') IS NULL
   OR OBJECT_ID('dbo.MOU_PRO_M', 'U') IS NULL
    THROW 50003, N'14999 视图依赖的源表不齐，拒绝执行。', 1;
GO

-- ---------------------------------------------------------------------------
-- 1. V_COP_ORDER_D_UNDELIVERED (module 14999): order lines still due for delivery.
--    The predicate ("not closed, not fully delivered, due date reached") is sealed
--    here; SALES_ID is exposed as a master-table column so a row-level filter can be
--    written against the customer's owning salesperson. Both were declared on the
--    module / on FIELDS before this migration, so the surface is unchanged.
-- ---------------------------------------------------------------------------
IF OBJECT_ID('dbo.V_COP_ORDER_D_UNDELIVERED', 'V') IS NOT NULL
    DROP VIEW dbo.V_COP_ORDER_D_UNDELIVERED;
GO
CREATE VIEW dbo.V_COP_ORDER_D_UNDELIVERED
AS
SELECT COP_ORDER_D.*,
       -- cross-table columns (COP_ORDER_M / CLIENT / SYSDN / PRODUCT / COLOR / TAX / MOU_PRO_M)
       product.BUSINESS_TAG AS BUSINESS_TAG,
       CLIENT.CLIENT_NAME AS CLIENT_NAME,
       COLOR.COLOR_NAME AS COLOR_NAME,
       PRODUCT.CUBAGE AS CUBAGE,
       PRODUCT.QTY AS CUR_DEPOT_QTY,
       PRODUCT.LINE_ID AS LINE_ID,
       MOU_PRO_M.MOULD_IDS AS MOULD_IDS,
       PRODUCT.MRP_QTY AS MRP_QTY,
       CASE WHEN PRODUCT.MRP_QTY < 0 AND -PRODUCT.MRP_QTY <= COP_ORDER_D.QTY THEN ROUND(-PRODUCT.MRP_QTY, 0)
            ELSE CASE WHEN PRODUCT.MRP_QTY < 0 AND -PRODUCT.MRP_QTY > COP_ORDER_D.QTY THEN COP_ORDER_D.QTY ELSE 0 END
       END AS MRP_QTY_ABS,
       PRODUCT.P_LENGTH AS P_LENGTH,
       PRODUCT.P_WIDTH AS P_WIDTH,
       product.pro_name AS PRO_NAME,
       PRODUCT.PRO_SPEC AS PRO_SPEC,
       CLIENT.SALES_ID AS SALES_ID,
       SYSDN.EMP_NAME AS SALES_NAME,
       CASE WHEN PRODUCT.UNIT_PCS = 0 THEN COP_ORDER_D.QTY
            ELSE CEILING(COP_ORDER_D.QTY / PRODUCT.UNIT_PCS)
       END AS SET_PCS,
       PRODUCT.SUTTLE AS SUTTLE,
       TAX.TAX_NAME AS TAX_NAME,
       PRODUCT.UNIT_PCS AS UNIT_PCS,
       COP_ORDER_M.CLIENT_ID AS CLIENT_ID,
       COP_ORDER_M.DEPOT_ID AS DEPOT_ID,
       COP_ORDER_M.ORDER_DATE AS ORDER_DATE,
       -- computed columns over this row
       COP_ORDER_D.FINISHED_FITOUT_QTY - COP_ORDER_D.FINISHED_SEND_QTY AS CAN_FITIN_QTY,
       COP_ORDER_D.FINISHED_FITOUT_SPARE_QTY - COP_ORDER_D.FINISHED_SPARE_QTY AS CAN_FITIN_SPARE_QTY,
       COP_ORDER_D.QTY - COP_ORDER_D.FINISHED_SEND_QTY AS CAN_SEND_QTY,
       COP_ORDER_D.SPARE_QTY - COP_ORDER_D.FINISHED_SPARE_QTY AS CAN_SEND_SPARE_QTY,
       COP_ORDER_D.PLAN_QTY - COP_ORDER_D.DO_PLAN_QTY AS NOT_DO_PLAN_QTY,
       COP_ORDER_D.PLAN_SPARE_QTY - COP_ORDER_D.DO_PLAN_SPARE_QTY AS NOT_DO_PLAN_SPARE_QTY,
       COP_ORDER_D.AMOUNT - COP_ORDER_D.FINISHED_AMOUNT AS NOT_FINISHED_AMOUNT,
       COP_ORDER_D.FINISHED_PRODUCE_QTY - COP_ORDER_D.FINISHED_FITOUT_QTY AS NOT_FINISHED_WORK_QTY,
       COP_ORDER_D.QTY - COP_ORDER_D.FINISHED_FITOUT_QTY AS NOT_FITOUT_QTY,
       COP_ORDER_D.SPARE_QTY - COP_ORDER_D.FINISHED_FITOUT_SPARE_QTY AS NOT_FITOUT_SPARE_QTY,
       COP_ORDER_D.QTY - COP_ORDER_D.FINISHED_SEND_QTY AS NOT_SEND_QTY,
       COP_ORDER_D.SPARE_QTY - COP_ORDER_D.FINISHED_SPARE_QTY AS NOT_SEND_SPARE_QTY,
       COP_ORDER_D.SERIAL_NO AS ORDER_SERIAL_NO,
       COP_ORDER_D.QTY - COP_ORDER_D.FINISHED_PRODUCE_QTY AS UNFINISHED_PRODUCE_QTY,
       COP_ORDER_D.SPARE_QTY - COP_ORDER_D.FINISHED_PRODUCE_SPARE_QTY AS UNFINISHED_PRODUCE_SPARE_QTY,
       COP_ORDER_D.AMOUNT_TAX - COP_ORDER_D.FINISHED_RECEIPT_AMOUNT AS UNFINISHED_RECEIPT_AMOUNT,
       COP_ORDER_D.PLAN_QTY - COP_ORDER_D.FINISHED_PLAN_QTY AS UNPLAN_QTY,
       COP_ORDER_D.PLAN_SPARE_QTY - COP_ORDER_D.FINISHED_PLAN_SPARE_QTY AS UNPLAN_SPARE_QTY
FROM dbo.COP_ORDER_D WITH (NOLOCK)
LEFT JOIN dbo.COP_ORDER_M WITH (NOLOCK)
       ON COP_ORDER_D.ORDER_TYPE = COP_ORDER_M.ORDER_TYPE AND COP_ORDER_D.ORDER_NO = COP_ORDER_M.ORDER_NO
LEFT JOIN dbo.CLIENT WITH (NOLOCK) ON COP_ORDER_M.CLIENT_ID = CLIENT.CLIENT_ID
LEFT JOIN dbo.SYSDN WITH (NOLOCK) ON CLIENT.SALES_ID = SYSDN.EMP_ID
LEFT JOIN dbo.PRODUCT WITH (NOLOCK) ON COP_ORDER_D.PRO_NO = PRODUCT.PRO_NO
LEFT JOIN dbo.COLOR WITH (NOLOCK) ON PRODUCT.COLOR_ID = COLOR.COLOR_ID
LEFT JOIN dbo.TAX WITH (NOLOCK) ON COP_ORDER_D.TAX_ID = TAX.TAX_ID
LEFT JOIN dbo.MOU_PRO_M WITH (NOLOCK) ON COP_ORDER_D.PRO_NO = MOU_PRO_M.PRO_NO
WHERE COP_ORDER_D.FINISHED_TAG = 0
  AND COP_ORDER_D.QTY - COP_ORDER_D.FINISHED_SEND_QTY > 0
  AND COP_ORDER_D.PRE_SEND_DATE <= GETDATE();
GO

BEGIN TRANSACTION;

DECLARE @n INT;

-- ---------------------------------------------------------------------------
-- 2. TABLES: register the view (14993 / 14996 / 14998 / 170297 are registered the same way).
-- ---------------------------------------------------------------------------
DELETE FROM dbo.TABLES WHERE T_ID = N'V_COP_ORDER_D_UNDELIVERED';
INSERT INTO dbo.TABLES (T_ID, T_DESC, T_TYPE, T_KIND, QUERY_RELATION, LAST_UPDATE_BY, LAST_UPDATE_DATE)
VALUES (N'V_COP_ORDER_D_UNDELIVERED', N'今日需交货', N'VIEW', N'P',
        N'V_COP_ORDER_D_UNDELIVERED WITH (NOLOCK)', N'EOS-MIG', GETDATE());
SET @n = @@ROWCOUNT;
PRINT N'[339] TABLES 视图登记：' + CAST(@n AS NVARCHAR(10)) + N' 行';

-- ---------------------------------------------------------------------------
-- 3. MODULES: point 14999 at the view; the predicate moved inside the view.
-- ---------------------------------------------------------------------------
UPDATE dbo.MODULES
SET MASTER_TABLE = N'V_COP_ORDER_D_UNDELIVERED',
    FILTER = NULL,
    SORT_FIELDS = N'V_COP_ORDER_D_UNDELIVERED.PRE_SEND_DATE desc',
    LAST_UPDATE_BY = N'EOS-MIG',
    LAST_UPDATE_DATE = GETDATE()
WHERE M_IDX = 14999;
SET @n = @@ROWCOUNT;
PRINT N'[339] MODULES 14999 改挂工作台视图：' + CAST(@n AS NVARCHAR(10)) + N' 行';

-- ---------------------------------------------------------------------------
-- 4. FIELDS: column metadata of the view, copied from COP_ORDER_D (labels, widths and
--    formats stay one system-wide; the view column carries the same name). IS_VIRTUAL
--    and IS_PK are cleared — the columns are real and a view has no primary key.
-- ---------------------------------------------------------------------------
DELETE FROM dbo.FIELDS WHERE T_ID = N'V_COP_ORDER_D_UNDELIVERED';
INSERT INTO dbo.FIELDS (T_ID, F_ID, F_DESC, F_TYPE, DISPLAY_LENGTH, DISPLAY_FORMAT, HEADER_ALIGN, ITEM_ALIGN,
                        IS_VERIFY, VERIFY_INDEX, IS_PK, IS_READONLY, IS_VISIBLE, IS_VIRTUAL, IS_AUTOINC,
                        IS_QUERY, IS_COST, IS_SECRECY, CAN_COPY, IS_DEFAULT_FIELDS, LAST_UPDATE_BY, LAST_UPDATE_DATE)
SELECT N'V_COP_ORDER_D_UNDELIVERED', F_ID, F_DESC, F_TYPE, DISPLAY_LENGTH, DISPLAY_FORMAT, HEADER_ALIGN, ITEM_ALIGN,
       IS_VERIFY, VERIFY_INDEX, 0, IS_READONLY, IS_VISIBLE, 0, IS_AUTOINC,
       IS_QUERY, IS_COST, IS_SECRECY, CAN_COPY, IS_DEFAULT_FIELDS, N'EOS-MIG', GETDATE()
FROM dbo.FIELDS
WHERE T_ID = N'COP_ORDER_D';
SET @n = @@ROWCOUNT;
PRINT N'[339] FIELDS 字段元数据：' + CAST(@n AS NVARCHAR(10)) + N' 行';

-- ---------------------------------------------------------------------------
-- 5. SYSQL_DEFAULT: default column list of the view.
-- ---------------------------------------------------------------------------
DELETE FROM dbo.SYSQL_DEFAULT WHERE T_ID = N'V_COP_ORDER_D_UNDELIVERED';
INSERT INTO dbo.SYSQL_DEFAULT (T_ID, T_ID_R, F_IDX, F_ID)
SELECT N'V_COP_ORDER_D_UNDELIVERED', N'V_COP_ORDER_D_UNDELIVERED', F_IDX, F_ID
FROM dbo.SYSQL_DEFAULT
WHERE T_ID = N'COP_ORDER_D';
SET @n = @@ROWCOUNT;
PRINT N'[339] SYSQL_DEFAULT 默认列：' + CAST(@n AS NVARCHAR(10)) + N' 行';

-- ---------------------------------------------------------------------------
-- 6. Row-level scope that named a joined table is rewritten to the view's own SALES_ID
--    column. Guarded by construction: only runs when the view really exposes SALES_ID.
-- ---------------------------------------------------------------------------
IF EXISTS (SELECT 1
           FROM sys.columns c
           JOIN sys.objects o ON o.object_id = c.object_id AND o.type = 'V'
           JOIN sys.schemas s ON o.schema_id = s.schema_id
           WHERE s.name = N'dbo' AND o.name = N'V_COP_ORDER_D_UNDELIVERED' AND c.name = N'SALES_ID')
BEGIN
    UPDATE dbo.SYSDD
    SET DATA_FILTER = REPLACE(DATA_FILTER, N'CLIENT.', N'')
    WHERE M_IDX = 14999
      AND DATA_FILTER LIKE N'CLIENT.%';
    SET @n = @@ROWCOUNT;
    PRINT N'[339] SYSDD 数据范围前缀改写：' + CAST(@n AS NVARCHAR(10)) + N' 行';

    UPDATE dbo.SYSDH
    SET DATA_FILTER = REPLACE(DATA_FILTER, N'CLIENT.', N'')
    WHERE M_IDX = 14999
      AND DATA_FILTER LIKE N'CLIENT.%';
    SET @n = @@ROWCOUNT;
    PRINT N'[339] SYSDH 数据范围前缀改写：' + CAST(@n AS NVARCHAR(10)) + N' 行';
END
ELSE
    PRINT N'[339] 视图未暴露 SALES_ID，跳过数据范围改写。';

COMMIT TRANSACTION;
