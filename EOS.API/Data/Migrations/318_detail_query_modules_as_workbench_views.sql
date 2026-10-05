-- ============================================================================
-- EOS.ERP migration 318: host the three cross-table detail queries as workbench views
-- ----------------------------------------------------------------------------
-- Database: EOS.ERP (the single business database of the new system)
--
-- Context: 14996 (待生产订单明细), 14998 (客户逾期未对帐) and 170297 (厂商逾期未对账)
-- were the only modules hosted by the dedicated read-only page /detail-query/{moduleId}
-- (DetailQueryController). They could not be hosted by the generic workbench because
-- MODULES.FILTER referenced rows of *other* tables (COP_ORDER_M.ORDER_DATE,
-- PRODUCT.MAIN_SOURCE, COP_SEND_M.SEND_DATE, PUR_RECEIVE_M.RECEIVE_DATE), and
-- DataFilterParser accepts a qualified reference only when the qualifier equals the
-- master table: the workbench would answer 403 instead of returning rows.
--
-- Module 14993 (应收未收(汇总), master table V_COP_ACCOUNT_M) already shows the shape
-- used for exactly this situation: the cross-table predicate is sealed into a view;
-- the module points MASTER_TABLE at the view, leaves MODULES.FILTER empty, and the
-- generic single-table workbench is then sufficient. This migration applies that shape
-- to the three modules above so that the /detail-query surface can be retired.
--
-- Consequences:
--   ① the modules gain the standard list surface (search, sorting, column selection,
--      export, print, field-level permission) and — unlike the retired page — they now
--      honour row-level scope (EXEC_TAG / DATA_FILTER);
--   ② a row-level filter may only reference the *master table's own* columns (see
--      DataFilterParser.TryResolveField). The user data filters written for the old page
--      referenced a joined table by name (CLIENT.SALES_ID=...), so on any module whose
--      master table is not CLIENT the whole list is refused with 403 — not narrowed, but
--      unavailable. The capability has exactly two carrier shapes, both used here:
--        (a) the master table already owns the column — 1404 / 1405 / 14997 carry a real
--            SALES_ID, so the condition only has to drop its qualifier;
--        (b) it does not own it — expose it through a view; 14998's view exposes
--            CLIENT.SALES_ID (the customer's owning salesperson) as SALES_ID.
--      14999 is deliberately out of scope: COP_ORDER_D owns no salesperson column and its
--      SALES_ID is a *virtual* field (CLIENT.SALES_ID, one of 40 cross-table virtual
--      columns). Virtual fields are excluded from the filter whitelist
--      (ReadFilterFieldKeys takes IS_VIRTUAL=0 only), so shape (b) is the only way and it
--      needs its own view — a separate change, not a line in this one;
--   ③ the three views are registered in TABLES with T_TYPE='VIEW';
--   ④ FIELDS / SYSQL_DEFAULT carry the column metadata of each view.
-- Idempotent: views are dropped and recreated; metadata writes are re-runnable.
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
   OR OBJECT_ID('dbo.PRODUCT', 'U') IS NULL
   OR OBJECT_ID('dbo.BOM_STRU_M', 'U') IS NULL
   OR OBJECT_ID('dbo.COP_SEND_D', 'U') IS NULL
   OR OBJECT_ID('dbo.COP_SEND_M', 'U') IS NULL
   OR OBJECT_ID('dbo.PUR_RECEIVE_D', 'U') IS NULL
   OR OBJECT_ID('dbo.PUR_RECEIVE_M', 'U') IS NULL
   OR OBJECT_ID('dbo.CLIENT', 'U') IS NULL
    THROW 50003, N'三个明细视图依赖的源表不齐，拒绝执行。', 1;
GO

-- ---------------------------------------------------------------------------
-- 1. V_COP_ORDER_D_UNPRODUCED (module 14996): unproduced customer-order lines.
--    Predicate and derived column are sealed here; the fixed lower date bound is the
--    same 2014-10-01 cut-off the retired page used.
-- ---------------------------------------------------------------------------
IF OBJECT_ID('dbo.V_COP_ORDER_D_UNPRODUCED', 'V') IS NOT NULL
    DROP VIEW dbo.V_COP_ORDER_D_UNPRODUCED;
GO
CREATE VIEW dbo.V_COP_ORDER_D_UNPRODUCED
AS
SELECT m.ORDER_TYPE,
       m.ORDER_NO,
       d.SERIAL_NO,
       d.PRO_NO,
       p.PRO_NAME,
       d.PLAN_QTY,
       d.FINISHED_PLAN_QTY,
       d.PLAN_QTY - d.FINISHED_PLAN_QTY AS UNFINISHED_QTY,
       m.ORDER_DATE,
       m.CLIENT_ID
FROM dbo.COP_ORDER_D d WITH (NOLOCK)
INNER JOIN dbo.COP_ORDER_M m WITH (NOLOCK)
        ON m.ORDER_TYPE = d.ORDER_TYPE AND m.ORDER_NO = d.ORDER_NO
INNER JOIN dbo.PRODUCT p WITH (NOLOCK) ON p.PRO_NO = d.PRO_NO
WHERE d.FINISHED_TAG = 0
  AND d.PLAN_QTY > d.FINISHED_PLAN_QTY
  AND p.MAIN_SOURCE = '2'
  AND m.ORDER_DATE >= '2014-10-01'
  AND EXISTS (SELECT 1 FROM dbo.BOM_STRU_M b WITH (NOLOCK) WHERE b.PRO_NO = d.PRO_NO);
GO

-- ---------------------------------------------------------------------------
-- 2. V_COP_SEND_D_UNRECONCILED (module 14998): unreconciled customer deliveries.
--    SALES_ID is exposed here as a master-table column so that a filter can be written
--    against the customer's owning salesperson — the column only exists on CLIENT, so
--    without this carrier shape the condition could not be expressed at all.
-- ---------------------------------------------------------------------------
IF OBJECT_ID('dbo.V_COP_SEND_D_UNRECONCILED', 'V') IS NOT NULL
    DROP VIEW dbo.V_COP_SEND_D_UNRECONCILED;
GO
CREATE VIEW dbo.V_COP_SEND_D_UNRECONCILED
AS
SELECT m.SEND_TYPE,
       m.SEND_NO,
       d.SERIAL_NO,
       m.SEND_DATE,
       m.CLIENT_ID,
       m.CLIENT_NAME,
       c.SALES_ID,
       d.PRO_NO,
       d.QTY,
       d.FINISHED_QTY,
       d.QTY - d.FINISHED_QTY AS UNFINISHED_QTY,
       d.REMARK
FROM dbo.COP_SEND_D d WITH (NOLOCK)
INNER JOIN dbo.COP_SEND_M m WITH (NOLOCK)
        ON m.SEND_TYPE = d.SEND_TYPE AND m.SEND_NO = d.SEND_NO
LEFT JOIN dbo.CLIENT c WITH (NOLOCK) ON c.CLIENT_ID = m.CLIENT_ID
WHERE d.FINISHED_TAG = 0
  AND m.FINISHED_TAG = 0
  AND d.QTY - d.FINISHED_QTY > 0
  AND m.SEND_DATE < CONVERT(varchar(7), DATEADD(month, -1, GETDATE()), 120) + '-26';
GO

-- ---------------------------------------------------------------------------
-- 3. V_PUR_RECEIVE_D_UNRECONCILED (module 170297): unreconciled supplier receipts.
-- ---------------------------------------------------------------------------
IF OBJECT_ID('dbo.V_PUR_RECEIVE_D_UNRECONCILED', 'V') IS NOT NULL
    DROP VIEW dbo.V_PUR_RECEIVE_D_UNRECONCILED;
GO
CREATE VIEW dbo.V_PUR_RECEIVE_D_UNRECONCILED
AS
SELECT m.RECEIVE_TYPE,
       m.RECEIVE_NO,
       d.SERIAL_NO,
       m.RECEIVE_DATE,
       m.SUPPLIER_ID,
       d.PRO_NO,
       d.QTY,
       d.FINISHED_QTY,
       d.QTY - d.FINISHED_QTY AS UNFINISHED_QTY,
       d.REMARK
FROM dbo.PUR_RECEIVE_D d WITH (NOLOCK)
INNER JOIN dbo.PUR_RECEIVE_M m WITH (NOLOCK)
        ON m.RECEIVE_TYPE = d.RECEIVE_TYPE AND m.RECEIVE_NO = d.RECEIVE_NO
WHERE d.FINISHED_TAG = 0
  AND m.FINISHED_TAG = 0
  AND d.QTY - d.FINISHED_QTY > 0
  AND m.RECEIVE_DATE < CONVERT(varchar(7), DATEADD(month, -1, GETDATE()), 120) + '-26';
GO

BEGIN TRANSACTION;

DECLARE @n INT;

-- ---------------------------------------------------------------------------
-- 4. TABLES: register the three views (14993's view is registered the same way).
-- ---------------------------------------------------------------------------
DELETE FROM dbo.TABLES WHERE T_ID IN (N'V_COP_ORDER_D_UNPRODUCED', N'V_COP_SEND_D_UNRECONCILED', N'V_PUR_RECEIVE_D_UNRECONCILED');

INSERT INTO dbo.TABLES (T_ID, T_DESC, T_TYPE, T_KIND, QUERY_RELATION, LAST_UPDATE_BY, LAST_UPDATE_DATE)
VALUES (N'V_COP_ORDER_D_UNPRODUCED',     N'待生产订单明细', N'VIEW', N'P', N'V_COP_ORDER_D_UNPRODUCED WITH (NOLOCK)',     N'EOS-MIG', GETDATE()),
       (N'V_COP_SEND_D_UNRECONCILED',    N'客户逾期未对帐', N'VIEW', N'P', N'V_COP_SEND_D_UNRECONCILED WITH (NOLOCK)',    N'EOS-MIG', GETDATE()),
       (N'V_PUR_RECEIVE_D_UNRECONCILED', N'厂商逾期未对账', N'VIEW', N'P', N'V_PUR_RECEIVE_D_UNRECONCILED WITH (NOLOCK)', N'EOS-MIG', GETDATE());
SET @n = @@ROWCOUNT;
PRINT N'[318] TABLES 视图登记：' + CAST(@n AS NVARCHAR(10)) + N' 行';

-- ---------------------------------------------------------------------------
-- 5. MODULES: point the three modules at their view and clear the cross-table filter.
--    FILTER is emptied because the predicate now lives inside the view; SORT_FIELDS uses
--    the view-qualified column (NormalizeSort accepts a qualifier only for the master
--    table, and a view carries no primary key for the fallback sort).
-- ---------------------------------------------------------------------------
UPDATE dbo.MODULES
SET M_URL = N'/workbench',
    MASTER_TABLE = N'V_COP_ORDER_D_UNPRODUCED',
    FILTER = NULL,
    SORT_FIELDS = N'V_COP_ORDER_D_UNPRODUCED.ORDER_DATE desc',
    LAST_UPDATE_BY = N'EOS-MIG',
    LAST_UPDATE_DATE = GETDATE()
WHERE M_IDX = 14996;
SET @n = @@ROWCOUNT;
PRINT N'[318] MODULES 14996 改挂工作台：' + CAST(@n AS NVARCHAR(10)) + N' 行';

UPDATE dbo.MODULES
SET M_URL = N'/workbench',
    MASTER_TABLE = N'V_COP_SEND_D_UNRECONCILED',
    FILTER = NULL,
    SORT_FIELDS = N'V_COP_SEND_D_UNRECONCILED.SEND_DATE desc',
    LAST_UPDATE_BY = N'EOS-MIG',
    LAST_UPDATE_DATE = GETDATE()
WHERE M_IDX = 14998;
SET @n = @@ROWCOUNT;
PRINT N'[318] MODULES 14998 改挂工作台：' + CAST(@n AS NVARCHAR(10)) + N' 行';

UPDATE dbo.MODULES
SET M_URL = N'/workbench',
    MASTER_TABLE = N'V_PUR_RECEIVE_D_UNRECONCILED',
    FILTER = NULL,
    SORT_FIELDS = N'V_PUR_RECEIVE_D_UNRECONCILED.RECEIVE_DATE desc',
    LAST_UPDATE_BY = N'EOS-MIG',
    LAST_UPDATE_DATE = GETDATE()
WHERE M_IDX = 170297;
SET @n = @@ROWCOUNT;
PRINT N'[318] MODULES 170297 改挂工作台：' + CAST(@n AS NVARCHAR(10)) + N' 行';

-- ---------------------------------------------------------------------------
-- 6. FIELDS: column metadata of the three views. Display names, widths and formats are
--    taken from the source fields so a business column keeps one label system-wide;
--    VERIFY_INDEX fixes the display order (the view has no primary key).
-- ---------------------------------------------------------------------------
DELETE FROM dbo.FIELDS WHERE T_ID IN (N'V_COP_ORDER_D_UNPRODUCED', N'V_COP_SEND_D_UNRECONCILED', N'V_PUR_RECEIVE_D_UNRECONCILED');

INSERT INTO dbo.FIELDS (T_ID, F_ID, F_DESC, F_TYPE, DISPLAY_LENGTH, DISPLAY_FORMAT, HEADER_ALIGN, ITEM_ALIGN,
                        IS_VERIFY, VERIFY_INDEX, IS_PK, IS_READONLY, IS_VISIBLE, IS_VIRTUAL, IS_AUTOINC,
                        IS_QUERY, IS_COST, IS_SECRECY, CAN_COPY, IS_DEFAULT_FIELDS, LAST_UPDATE_BY, LAST_UPDATE_DATE)
VALUES
-- V_COP_ORDER_D_UNPRODUCED
(N'V_COP_ORDER_D_UNPRODUCED', N'ORDER_TYPE',        N'订单别',       N'nvarchar',  67, NULL,         N'center', N'left',  0,  1, 0, 1, 1, 0, 0, 0, 0, 0, 0, 1, N'EOS-MIG', GETDATE()),
(N'V_COP_ORDER_D_UNPRODUCED', N'ORDER_NO',          N'订单号',       N'nvarchar',  86, NULL,         N'center', N'left',  0,  2, 0, 1, 1, 0, 0, 1, 0, 0, 0, 1, N'EOS-MIG', GETDATE()),
(N'V_COP_ORDER_D_UNPRODUCED', N'SERIAL_NO',         N'项次',         N'int',       54, NULL,         N'center', N'center',0,  3, 0, 1, 1, 0, 0, 1, 0, 0, 0, 1, N'EOS-MIG', GETDATE()),
(N'V_COP_ORDER_D_UNPRODUCED', N'PRO_NO',            N'料号',         N'nvarchar', 127, NULL,         N'center', N'left',  0,  4, 0, 1, 1, 0, 0, 1, 0, 0, 0, 1, N'EOS-MIG', GETDATE()),
(N'V_COP_ORDER_D_UNPRODUCED', N'PRO_NAME',          N'品名',         N'nvarchar', 118, NULL,         N'center', N'left',  0,  5, 0, 1, 1, 0, 0, 1, 0, 0, 0, 1, N'EOS-MIG', GETDATE()),
(N'V_COP_ORDER_D_UNPRODUCED', N'PLAN_QTY',          N'计划生产数量', N'float',    104, NULL,         N'center', NULL,     0,  6, 0, 1, 1, 0, 0, 1, 0, 0, 0, 1, N'EOS-MIG', GETDATE()),
(N'V_COP_ORDER_D_UNPRODUCED', N'FINISHED_PLAN_QTY', N'已下生产数量', N'float',    104, NULL,         N'center', NULL,     0,  7, 0, 1, 1, 0, 0, 1, 0, 0, 0, 1, N'EOS-MIG', GETDATE()),
(N'V_COP_ORDER_D_UNPRODUCED', N'UNFINISHED_QTY',    N'未生产数量',   N'float',    104, N'0.##',      N'center', N'right', 0,  8, 0, 1, 1, 0, 0, 1, 0, 0, 0, 1, N'EOS-MIG', GETDATE()),
(N'V_COP_ORDER_D_UNPRODUCED', N'ORDER_DATE',        N'订单日期',     N'date',      79, N'yyyy-MM-dd',N'center', N'left',  0,  9, 0, 1, 1, 0, 0, 1, 0, 0, 0, 1, N'EOS-MIG', GETDATE()),
(N'V_COP_ORDER_D_UNPRODUCED', N'CLIENT_ID',         N'客户',         N'nvarchar',  77, NULL,         N'center', N'left',  0, 10, 0, 1, 1, 0, 0, 1, 0, 0, 0, 1, N'EOS-MIG', GETDATE()),
-- V_COP_SEND_D_UNRECONCILED
(N'V_COP_SEND_D_UNRECONCILED', N'SEND_TYPE',     N'送货单别',   N'nvarchar',  79, NULL,         N'center', N'left',  0,  1, 0, 1, 1, 0, 0, 1, 0, 0, 0, 1, N'EOS-MIG', GETDATE()),
(N'V_COP_SEND_D_UNRECONCILED', N'SEND_NO',       N'送货单号',   N'nvarchar',  79, NULL,         N'center', N'left',  0,  2, 0, 1, 1, 0, 0, 1, 0, 0, 0, 1, N'EOS-MIG', GETDATE()),
(N'V_COP_SEND_D_UNRECONCILED', N'SERIAL_NO',     N'项次',       N'int',       54, NULL,         N'center', N'left',  0,  3, 0, 1, 1, 0, 0, 0, 0, 0, 0, 1, N'EOS-MIG', GETDATE()),
(N'V_COP_SEND_D_UNRECONCILED', N'SEND_DATE',     N'送货日期',   N'date',      79, N'yyyy-MM-dd',N'center', N'left',  0,  4, 0, 1, 1, 0, 0, 1, 0, 0, 0, 1, N'EOS-MIG', GETDATE()),
(N'V_COP_SEND_D_UNRECONCILED', N'CLIENT_ID',     N'客户',       N'nvarchar',  77, NULL,         N'center', N'left',  0,  5, 0, 1, 1, 0, 0, 1, 0, 0, 0, 1, N'EOS-MIG', GETDATE()),
(N'V_COP_SEND_D_UNRECONCILED', N'CLIENT_NAME',   N'客户名',     N'nvarchar',  67, NULL,         N'center', NULL,     0,  6, 0, 1, 1, 0, 0, 1, 0, 0, 0, 1, N'EOS-MIG', GETDATE()),
(N'V_COP_SEND_D_UNRECONCILED', N'SALES_ID',      N'业务员',     N'nvarchar',  67, NULL,         N'center', N'left',  0,  7, 0, 1, 1, 0, 0, 1, 0, 0, 0, 1, N'EOS-MIG', GETDATE()),
(N'V_COP_SEND_D_UNRECONCILED', N'PRO_NO',        N'料号',       N'nvarchar', 148, NULL,         N'center', N'left',  0,  8, 0, 1, 1, 0, 0, 1, 0, 0, 0, 1, N'EOS-MIG', GETDATE()),
(N'V_COP_SEND_D_UNRECONCILED', N'QTY',           N'数量',       N'float',     70, NULL,         N'center', N'right', 0,  9, 0, 1, 1, 0, 0, 1, 0, 0, 0, 1, N'EOS-MIG', GETDATE()),
(N'V_COP_SEND_D_UNRECONCILED', N'FINISHED_QTY',  N'对帐数量',   N'float',     79, NULL,         N'center', NULL,     0, 10, 0, 1, 1, 0, 0, 1, 0, 0, 0, 1, N'EOS-MIG', GETDATE()),
(N'V_COP_SEND_D_UNRECONCILED', N'UNFINISHED_QTY',N'未对帐数量', N'float',     79, N'0.##',      N'center', N'right', 0, 11, 0, 1, 1, 0, 0, 1, 0, 0, 0, 1, N'EOS-MIG', GETDATE()),
(N'V_COP_SEND_D_UNRECONCILED', N'REMARK',        N'备注',       N'nvarchar', 100, NULL,         NULL,      N'left',  0, 12, 0, 1, 1, 0, 0, 0, 0, 0, 0, 1, N'EOS-MIG', GETDATE()),
-- V_PUR_RECEIVE_D_UNRECONCILED
(N'V_PUR_RECEIVE_D_UNRECONCILED', N'RECEIVE_TYPE',  N'收料单别',   N'nvarchar',  40, NULL,         N'center', N'left',  0,  1, 0, 1, 1, 0, 0, 1, 0, 0, 0, 1, N'EOS-MIG', GETDATE()),
(N'V_PUR_RECEIVE_D_UNRECONCILED', N'RECEIVE_NO',    N'收料单号',   N'nvarchar', 100, NULL,         NULL,      N'left',  0,  2, 0, 1, 1, 0, 0, 1, 0, 0, 0, 1, N'EOS-MIG', GETDATE()),
(N'V_PUR_RECEIVE_D_UNRECONCILED', N'SERIAL_NO',     N'项次',       N'int',       30, NULL,         N'center', N'left',  0,  3, 0, 1, 1, 0, 0, 0, 0, 0, 0, 1, N'EOS-MIG', GETDATE()),
(N'V_PUR_RECEIVE_D_UNRECONCILED', N'RECEIVE_DATE',  N'收料日期',   N'date',      80, N'yyyy-MM-dd',N'center', N'left',  0,  4, 0, 1, 1, 0, 0, 1, 0, 0, 0, 1, N'EOS-MIG', GETDATE()),
(N'V_PUR_RECEIVE_D_UNRECONCILED', N'SUPPLIER_ID',   N'厂商',       N'nvarchar', 100, NULL,         N'center', N'left',  0,  5, 0, 1, 1, 0, 0, 1, 0, 0, 0, 1, N'EOS-MIG', GETDATE()),
(N'V_PUR_RECEIVE_D_UNRECONCILED', N'PRO_NO',        N'料号',       N'nvarchar', 150, NULL,         N'center', N'left',  0,  6, 0, 1, 1, 0, 0, 1, 0, 0, 0, 1, N'EOS-MIG', GETDATE()),
(N'V_PUR_RECEIVE_D_UNRECONCILED', N'QTY',           N'入库数',     N'float',     70, NULL,         NULL,      N'right', 0,  7, 0, 1, 1, 0, 0, 0, 0, 0, 0, 1, N'EOS-MIG', GETDATE()),
(N'V_PUR_RECEIVE_D_UNRECONCILED', N'FINISHED_QTY',  N'对帐数量',   N'float',    100, NULL,         N'center', NULL,     0,  8, 0, 1, 1, 0, 0, 1, 0, 0, 0, 1, N'EOS-MIG', GETDATE()),
(N'V_PUR_RECEIVE_D_UNRECONCILED', N'UNFINISHED_QTY',N'未对帐数量', N'float',    100, N'0.##',      N'center', N'right', 0,  9, 0, 1, 1, 0, 0, 1, 0, 0, 0, 1, N'EOS-MIG', GETDATE()),
(N'V_PUR_RECEIVE_D_UNRECONCILED', N'REMARK',        N'备注',       N'nvarchar', 100, NULL,         NULL,      N'left',  0, 10, 0, 1, 1, 0, 0, 0, 0, 0, 0, 1, N'EOS-MIG', GETDATE());
SET @n = @@ROWCOUNT;
PRINT N'[318] FIELDS 字段元数据：' + CAST(@n AS NVARCHAR(10)) + N' 行';

-- ---------------------------------------------------------------------------
-- 7. SYSQL_DEFAULT: default column list of the three views (same shape as 14993's view).
-- ---------------------------------------------------------------------------
DELETE FROM dbo.SYSQL_DEFAULT WHERE T_ID IN (N'V_COP_ORDER_D_UNPRODUCED', N'V_COP_SEND_D_UNRECONCILED', N'V_PUR_RECEIVE_D_UNRECONCILED');

INSERT INTO dbo.SYSQL_DEFAULT (T_ID, T_ID_R, F_IDX, F_ID)
VALUES
(N'V_COP_ORDER_D_UNPRODUCED', N'V_COP_ORDER_D_UNPRODUCED', 1,  N'ORDER_TYPE'),
(N'V_COP_ORDER_D_UNPRODUCED', N'V_COP_ORDER_D_UNPRODUCED', 2,  N'ORDER_NO'),
(N'V_COP_ORDER_D_UNPRODUCED', N'V_COP_ORDER_D_UNPRODUCED', 3,  N'SERIAL_NO'),
(N'V_COP_ORDER_D_UNPRODUCED', N'V_COP_ORDER_D_UNPRODUCED', 4,  N'PRO_NO'),
(N'V_COP_ORDER_D_UNPRODUCED', N'V_COP_ORDER_D_UNPRODUCED', 5,  N'PRO_NAME'),
(N'V_COP_ORDER_D_UNPRODUCED', N'V_COP_ORDER_D_UNPRODUCED', 6,  N'PLAN_QTY'),
(N'V_COP_ORDER_D_UNPRODUCED', N'V_COP_ORDER_D_UNPRODUCED', 7,  N'FINISHED_PLAN_QTY'),
(N'V_COP_ORDER_D_UNPRODUCED', N'V_COP_ORDER_D_UNPRODUCED', 8,  N'UNFINISHED_QTY'),
(N'V_COP_ORDER_D_UNPRODUCED', N'V_COP_ORDER_D_UNPRODUCED', 9,  N'ORDER_DATE'),
(N'V_COP_ORDER_D_UNPRODUCED', N'V_COP_ORDER_D_UNPRODUCED', 10, N'CLIENT_ID'),
(N'V_COP_SEND_D_UNRECONCILED', N'V_COP_SEND_D_UNRECONCILED', 1,  N'SEND_TYPE'),
(N'V_COP_SEND_D_UNRECONCILED', N'V_COP_SEND_D_UNRECONCILED', 2,  N'SEND_NO'),
(N'V_COP_SEND_D_UNRECONCILED', N'V_COP_SEND_D_UNRECONCILED', 3,  N'SERIAL_NO'),
(N'V_COP_SEND_D_UNRECONCILED', N'V_COP_SEND_D_UNRECONCILED', 4,  N'SEND_DATE'),
(N'V_COP_SEND_D_UNRECONCILED', N'V_COP_SEND_D_UNRECONCILED', 5,  N'CLIENT_ID'),
(N'V_COP_SEND_D_UNRECONCILED', N'V_COP_SEND_D_UNRECONCILED', 6,  N'CLIENT_NAME'),
(N'V_COP_SEND_D_UNRECONCILED', N'V_COP_SEND_D_UNRECONCILED', 7,  N'SALES_ID'),
(N'V_COP_SEND_D_UNRECONCILED', N'V_COP_SEND_D_UNRECONCILED', 8,  N'PRO_NO'),
(N'V_COP_SEND_D_UNRECONCILED', N'V_COP_SEND_D_UNRECONCILED', 9,  N'QTY'),
(N'V_COP_SEND_D_UNRECONCILED', N'V_COP_SEND_D_UNRECONCILED', 10, N'FINISHED_QTY'),
(N'V_COP_SEND_D_UNRECONCILED', N'V_COP_SEND_D_UNRECONCILED', 11, N'UNFINISHED_QTY'),
(N'V_COP_SEND_D_UNRECONCILED', N'V_COP_SEND_D_UNRECONCILED', 12, N'REMARK'),
(N'V_PUR_RECEIVE_D_UNRECONCILED', N'V_PUR_RECEIVE_D_UNRECONCILED', 1,  N'RECEIVE_TYPE'),
(N'V_PUR_RECEIVE_D_UNRECONCILED', N'V_PUR_RECEIVE_D_UNRECONCILED', 2,  N'RECEIVE_NO'),
(N'V_PUR_RECEIVE_D_UNRECONCILED', N'V_PUR_RECEIVE_D_UNRECONCILED', 3,  N'SERIAL_NO'),
(N'V_PUR_RECEIVE_D_UNRECONCILED', N'V_PUR_RECEIVE_D_UNRECONCILED', 4,  N'RECEIVE_DATE'),
(N'V_PUR_RECEIVE_D_UNRECONCILED', N'V_PUR_RECEIVE_D_UNRECONCILED', 5,  N'SUPPLIER_ID'),
(N'V_PUR_RECEIVE_D_UNRECONCILED', N'V_PUR_RECEIVE_D_UNRECONCILED', 6,  N'PRO_NO'),
(N'V_PUR_RECEIVE_D_UNRECONCILED', N'V_PUR_RECEIVE_D_UNRECONCILED', 7,  N'QTY'),
(N'V_PUR_RECEIVE_D_UNRECONCILED', N'V_PUR_RECEIVE_D_UNRECONCILED', 8,  N'FINISHED_QTY'),
(N'V_PUR_RECEIVE_D_UNRECONCILED', N'V_PUR_RECEIVE_D_UNRECONCILED', 9,  N'UNFINISHED_QTY'),
(N'V_PUR_RECEIVE_D_UNRECONCILED', N'V_PUR_RECEIVE_D_UNRECONCILED', 10, N'REMARK');
SET @n = @@ROWCOUNT;
PRINT N'[318] SYSQL_DEFAULT 默认列：' + CAST(@n AS NVARCHAR(10)) + N' 行';

-- ---------------------------------------------------------------------------
-- 8. Row-level scope that named a joined table (CLIENT.SALES_ID=...) is rewritten to the
--    module's own SALES_ID column. The target set is derived, not hard-coded: only modules
--    whose master table really exposes a SALES_ID column qualify, which is exactly the
--    agreed "a salesperson sees the documents they made" shape. 14999 is therefore excluded
--    by construction (COP_ORDER_D has no physical SALES_ID) and keeps its current rows,
--    still pending its own view.
-- ---------------------------------------------------------------------------
DECLARE @ScopeModules TABLE (M INT PRIMARY KEY);
INSERT INTO @ScopeModules (M)
SELECT m.M_IDX
FROM dbo.MODULES m WITH (NOLOCK)
WHERE m.M_IDX IN (1404, 1405, 14997, 14996, 14998, 170297)
  AND EXISTS (SELECT 1
              FROM sys.columns c
              JOIN sys.objects o ON o.object_id = c.object_id AND o.type IN ('U', 'V')
              JOIN sys.schemas s ON o.schema_id = s.schema_id
              WHERE s.name = N'dbo' AND o.name = m.MASTER_TABLE AND c.name = N'SALES_ID');

DECLARE @ScopeList NVARCHAR(200) = (SELECT STRING_AGG(CAST(M AS NVARCHAR(20)), N',') FROM @ScopeModules);
PRINT N'[318] 数据范围改写目标模块（主表确有 SALES_ID 列）：' + ISNULL(@ScopeList, N'（无）');

IF OBJECT_ID('dbo.SYSDD', 'U') IS NOT NULL
BEGIN
    UPDATE dbo.SYSDD
    SET DATA_FILTER = REPLACE(DATA_FILTER, N'CLIENT.', N'')
    WHERE M_IDX IN (SELECT M FROM @ScopeModules)
      AND DATA_FILTER LIKE N'CLIENT.%';
    SET @n = @@ROWCOUNT;
    PRINT N'[318] SYSDD 数据范围前缀改写：' + CAST(@n AS NVARCHAR(10)) + N' 行';
END

IF OBJECT_ID('dbo.SYSDH', 'U') IS NOT NULL
BEGIN
    UPDATE dbo.SYSDH
    SET DATA_FILTER = REPLACE(DATA_FILTER, N'CLIENT.', N'')
    WHERE M_IDX IN (SELECT M FROM @ScopeModules)
      AND DATA_FILTER LIKE N'CLIENT.%';
    SET @n = @@ROWCOUNT;
    PRINT N'[318] SYSDH 数据范围前缀改写：' + CAST(@n AS NVARCHAR(10)) + N' 行';
END

COMMIT TRANSACTION;

PRINT N'[318] 明细查询三模块改挂统一工作台视图完成；/detail-query 承载面已无模块。';
