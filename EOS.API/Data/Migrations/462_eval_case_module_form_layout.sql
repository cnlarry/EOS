-- ============================================================================
-- EOS.ERP migration 462：评估用例模块（3106）的版式行落地
-- ----------------------------------------------------------------------------
--  3106 的版式此前一直是"按元数据推导"（MODULE_FORM_LAYOUT 无行）。发布快照后，
--  check-form-layout 的"持快照的模块必须有版式行"会红——这条判据要求快照持有者
--  有可核对的版式事实，而不是每次现推。
--
--  这里把推导结果**落成行**：页签 1（空标题、4 列）+ 每个可见字段一行，
--  顺序按 VERIFY_INDEX、再按字段号；生命周期列不进版式（ADR-042）。
--  值与既有模块一致：SPAN=1 / ROW_SPAN=1 / NEW_LINE=0 / CELL_ROLE=0 / IS_HIDDEN=0。
-- ============================================================================
SET NOCOUNT ON;
SET XACT_ABORT ON;

IF DB_NAME() <> N'EOS.ERP'
    THROW 51970, N'本脚本只允许在 EOS.ERP 数据库上执行。', 1;

IF OBJECT_ID(N'dbo.MODULE_FORM_LAYOUT', N'U') IS NULL OR OBJECT_ID(N'dbo.MODULE_FORM_TAB', N'U') IS NULL
    THROW 51971, N'版式表不存在（迁移 319/322 未执行），迁移终止。', 1;

IF NOT EXISTS (SELECT 1 FROM dbo.MODULES WHERE M_IDX = 3106)
    THROW 51972, N'模块 3106 不存在（迁移 458 未执行），迁移终止。', 1;

IF NOT EXISTS (SELECT 1 FROM dbo.MODULE_FORM_LAYOUT WHERE M_IDX = 3106)
BEGIN
    IF NOT EXISTS (SELECT 1 FROM dbo.MODULE_FORM_TAB WHERE M_IDX = 3106)
        INSERT INTO dbo.MODULE_FORM_TAB (M_IDX, TAB_NO, TAB_TITLE, LAYOUT_COLUMNS)
        VALUES (3106, 1, N'', 4);

    INSERT INTO dbo.MODULE_FORM_LAYOUT
        (M_IDX, T_ID, F_ID, TAB_NO, ORDER_NO, SPAN, ROW_SPAN, NEW_LINE, SECTION_ID, CELL_GROUP, CELL_ROLE,
         IS_HIDDEN, MODIFIED_BY, MODIFIED_TIME)
    SELECT 3106, N'ASSISTANT_EVAL_CASE', f.F_ID, 1,
           ROW_NUMBER() OVER (ORDER BY COALESCE(f.VERIFY_INDEX, 999), f.F_ID),
           1, 1, 0, NULL, NULL, 0, 0, N'462', SYSUTCDATETIME()
    FROM dbo.FIELDS f
    WHERE f.T_ID = N'ASSISTANT_EVAL_CASE'
      AND COALESCE(f.IS_VISIBLE, 1) = 1
      AND COALESCE(f.IS_VIRTUAL, 0) = 0
      AND f.F_ID NOT IN (N'APPROVED', N'APPROVED_BY', N'APPROVED_TIME',
                         N'FINISHED', N'FINISHED_BY', N'FINISHED_TIME',
                         N'CREATED_BY', N'CREATED_TIME', N'MODIFIED_BY', N'MODIFIED_TIME');

    PRINT N'== 3106 版式行已落库 ==';
END
ELSE
    PRINT N'== 3106 已有版式行，跳过 ==';
