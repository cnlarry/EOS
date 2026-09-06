-- ============================================================================
-- EOS.ERP migration 054: MODULE_BUSINESS_ACTION_OP.SOURCE_TABLE
-- ----------------------------------------------------------------------------
-- 公式行表扩展：源范围支持模块已登记上下文表（MORE 类，如
-- PUR_PURCHASE_MORE / PUR_APPLY_MORE / MOC_GET_MORE）。
--   新增 SOURCE_TABLE NVARCHAR(64) NULL：
--     SOURCE_SCOPE = 'TABLE' 时必填，指向该模块注册表中登记的上下文表；
--     来源行由本单键限定（本单键过滤 + 分组字段见 MATCH_STRUCT 的闭式 JSON）。
-- 命名全大写；用 sys.extended_properties 守卫扩展属性（幂等可重复执行）。
-- ============================================================================

SET NOCOUNT ON;

DECLARE @GUARD_MESSAGE NVARCHAR(400) = N'本脚本只能在 EOS.ERP 数据库内执行，当前库为 ' + DB_NAME() + N'。';
IF DB_NAME() <> N'EOS.ERP'
    THROW 50000, @GUARD_MESSAGE, 1;

PRINT N'== 开始扩展 MODULE_BUSINESS_ACTION_OP（SOURCE_TABLE） ==';

IF COL_LENGTH(N'dbo.MODULE_BUSINESS_ACTION_OP', N'SOURCE_TABLE') IS NULL
BEGIN
    ALTER TABLE dbo.MODULE_BUSINESS_ACTION_OP ADD SOURCE_TABLE NVARCHAR(64) NULL;
END

IF NOT EXISTS (
    SELECT 1
    FROM sys.extended_properties ep
    WHERE ep.major_id = OBJECT_ID(N'dbo.MODULE_BUSINESS_ACTION_OP')
      AND ep.minor_id = COLUMNPROPERTY(OBJECT_ID(N'dbo.MODULE_BUSINESS_ACTION_OP'), N'SOURCE_TABLE', N'ColumnId')
      AND ep.name = N'MS_Description'
)
BEGIN
    EXEC sys.sp_addextendedproperty @name=N'MS_Description',
        @value=N'源表（SOURCE_SCOPE=TABLE 时必填；必须是模块已登记的上下文表，如 MORE 类表；来源行由本单键限定，分组字段见 MATCH_STRUCT）。',
        @level0type=N'SCHEMA', @level0name=N'dbo',
        @level1type=N'TABLE', @level1name=N'MODULE_BUSINESS_ACTION_OP',
        @level2type=N'COLUMN', @level2name=N'SOURCE_TABLE';
END

PRINT N'== MODULE_BUSINESS_ACTION_OP.SOURCE_TABLE 扩展完成 ==';
