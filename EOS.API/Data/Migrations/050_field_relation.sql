-- ============================================================================
-- EOS.ERP migration 051: FIELD_RELATION — registered cross-table field relations
-- ----------------------------------------------------------------------------
-- The semantic layer's field-relation facts: which *_ID columns point to the
-- same entity across tables (the factual basis for cross-table connections).
-- Developer-owned, versioned via DbUp migrations; the assistant consumes them
-- read-only and never generates or modifies relations.
--
-- Every seeded row was verified against sys.columns before registration; the
-- runtime validator and data-integrity regression re-check physical existence.
-- Naming convention: all uppercase. Idempotent: IF NOT EXISTS guards.
-- ============================================================================

SET NOCOUNT ON;

DECLARE @GUARD_MESSAGE NVARCHAR(400) = N'本脚本只能在 EOS.ERP 数据库内执行，当前库为 ' + DB_NAME() + N'。';
IF DB_NAME() <> N'EOS.ERP'
    THROW 50000, @GUARD_MESSAGE, 1;

BEGIN TRANSACTION;

IF OBJECT_ID(N'dbo.FIELD_RELATION') IS NULL
BEGIN
    CREATE TABLE dbo.FIELD_RELATION (
        [FROM_TABLE] NVARCHAR(64) NOT NULL,
        [FROM_COLUMN] NVARCHAR(64) NOT NULL,
        [TO_TABLE] NVARCHAR(64) NOT NULL,
        [TO_COLUMN] NVARCHAR(64) NOT NULL,
        [DESCRIPTION] NVARCHAR(200) NULL,
        [CREATE_PERSON] NCHAR(40) NULL,
        [CREATE_DATE] DATETIME2 NOT NULL CONSTRAINT [DF_FIELD_RELATION_CREATED] DEFAULT (SYSDATETIME()),
        CONSTRAINT [PK_FIELD_RELATION] PRIMARY KEY CLUSTERED
            ([FROM_TABLE], [FROM_COLUMN], [TO_TABLE], [TO_COLUMN])
    );
END

IF NOT EXISTS (SELECT 1 FROM dbo.FIELD_RELATION WITH (NOLOCK))
BEGIN
    INSERT INTO dbo.FIELD_RELATION ([FROM_TABLE], [FROM_COLUMN], [TO_TABLE], [TO_COLUMN], [DESCRIPTION], [CREATE_PERSON]) VALUES
        (N'COP_ORDER_D',     N'PRO_NO',      N'PRODUCT',  N'PRO_NO',      N'订单明细的产品',        N'admin'),
        (N'INV_PRO_DEPOT',   N'PRO_NO',      N'PRODUCT',  N'PRO_NO',      N'库存记录的产品',        N'admin'),
        (N'COP_ORDER_M',     N'CLIENT_ID',   N'CLIENT',   N'CLIENT_ID',   N'订单的客户',            N'admin'),
        (N'COP_RECEIPT_M',   N'CLIENT_ID',   N'CLIENT',   N'CLIENT_ID',   N'收款单的客户',          N'admin'),
        (N'PUR_PURCHASE_M',  N'SUPPLIER_ID', N'SUPPLIER', N'SUPPLIER_ID', N'采购单的供应商',        N'admin'),
        (N'PUR_DUE_M',       N'SUPPLIER_ID', N'SUPPLIER', N'SUPPLIER_ID', N'应付账款的供应商',      N'admin'),
        (N'INV_PRO_DEPOT',   N'DEPOT_ID',    N'DEPOT',    N'DEPOT_ID',    N'库存记录的仓库',        N'admin');
END

COMMIT TRANSACTION;
