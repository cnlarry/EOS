-- ============================================================================
-- EOS.ERP migration 055: MODULE_BUSINESS_ACTION_OP.SOURCE_TERMS_STRUCT
-- ----------------------------------------------------------------------------
-- 公式行表扩展：源支持"字段加减组合"（闭式，仅 +/−，禁止乘除/函数）。
--   新增 SOURCE_TERMS_STRUCT NVARCHAR(MAX) NULL：
--     与 SOURCE_FIELD 二选一；JSON 形如
--       [{"field":"QTY","coef":1},{"field":"SPARE_QTY","coef":1},{"field":"FINISHED_SEND_QTY","coef":-1}]
--     表示 SUM(QTY + SPARE_QTY - FINISHED_SEND_QTY)；coef 仅允许 1 / -1。
-- 命名全大写；用 sys.extended_properties 守卫扩展属性（幂等可重复执行）。
-- ============================================================================

SET NOCOUNT ON;

DECLARE @GUARD_MESSAGE NVARCHAR(400) = N'本脚本只能在 EOS.ERP 数据库内执行，当前库为 ' + DB_NAME() + N'。';
IF DB_NAME() <> N'EOS.ERP'
    THROW 50000, @GUARD_MESSAGE, 1;

PRINT N'== 开始扩展 MODULE_BUSINESS_ACTION_OP（SOURCE_TERMS_STRUCT） ==';

IF COL_LENGTH(N'dbo.MODULE_BUSINESS_ACTION_OP', N'SOURCE_TERMS_STRUCT') IS NULL
BEGIN
    ALTER TABLE dbo.MODULE_BUSINESS_ACTION_OP ADD SOURCE_TERMS_STRUCT NVARCHAR(MAX) NULL;
END

IF NOT EXISTS (
    SELECT 1
    FROM sys.extended_properties ep
    WHERE ep.major_id = OBJECT_ID(N'dbo.MODULE_BUSINESS_ACTION_OP')
      AND ep.minor_id = COLUMNPROPERTY(OBJECT_ID(N'dbo.MODULE_BUSINESS_ACTION_OP'), N'SOURCE_TERMS_STRUCT', N'ColumnId')
      AND ep.name = N'MS_Description'
)
BEGIN
    EXEC sys.sp_addextendedproperty @name=N'MS_Description',
        @value=N'源加减项 JSON（闭式，仅字段 +/−，coef 只允许 1/-1）；与 SOURCE_FIELD 二选一，如 SUM(QTY+SPARE_QTY-FINISHED_SEND_QTY)。',
        @level0type=N'SCHEMA', @level0name=N'dbo',
        @level1type=N'TABLE', @level1name=N'MODULE_BUSINESS_ACTION_OP',
        @level2type=N'COLUMN', @level2name=N'SOURCE_TERMS_STRUCT';
END

PRINT N'== MODULE_BUSINESS_ACTION_OP.SOURCE_TERMS_STRUCT 扩展完成 ==';
