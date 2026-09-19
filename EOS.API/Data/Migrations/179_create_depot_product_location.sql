-- ============================================================================
-- EOS.ERP migration 180: 新增物料默认货位 DEPOT_PRODUCT_LOCATION
-- ----------------------------------------------------------------------------
-- 方向是"物料 → 位置"而不是"位置 → 物料"：拣货时要回答的是"这个货在哪"。
-- 一个物料在一个库别可以有主货位 + 多个溢出位，故为 1:N，并用 IS_PRIMARY / SEQ_NO
-- 区分主次与顺序。固定存放与混合存放两种方式都依赖这张表给出默认落位。
--
-- 位置以 (DEPOT_ID, LOCATION_NO) 复合外键指向库位主档，保证不会引用不存在的位置。
--
-- 幂等：表已存在时只做断言，不重复建表。
-- 回滚：DROP TABLE（纯配置数据，删除不损失业务流水）。
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
    THROW 50900, @GUARD_MESSAGE, 1;

IF OBJECT_ID(N'dbo.DEPOT_LOCATION', N'U') IS NULL
    THROW 50901, N'dbo.DEPOT_LOCATION 不存在，请先执行建表迁移，迁移中止。', 1;

IF OBJECT_ID(N'dbo.DEPOT_PRODUCT_LOCATION', N'U') IS NOT NULL
BEGIN
    PRINT N'== dbo.DEPOT_PRODUCT_LOCATION 已存在（幂等跳过建表）==';
END
ELSE
BEGIN
    CREATE TABLE dbo.DEPOT_PRODUCT_LOCATION
    (
        DEPOT_ID    NCHAR (10)    NOT NULL,
        PRO_NO      NCHAR (30)    NOT NULL,
        LOCATION_NO NVARCHAR (30) NOT NULL,
        IS_PRIMARY  BIT           NOT NULL CONSTRAINT DF_DPL_IS_PRIMARY DEFAULT (0),
        SEQ_NO      INT           NULL,
        CONSTRAINT PK_DEPOT_PRODUCT_LOCATION PRIMARY KEY CLUSTERED (DEPOT_ID ASC, PRO_NO ASC, LOCATION_NO ASC),
        CONSTRAINT FK_DPL_LOCATION FOREIGN KEY (DEPOT_ID, LOCATION_NO)
            REFERENCES dbo.DEPOT_LOCATION (DEPOT_ID, LOCATION_NO)
    );

    PRINT N'== dbo.DEPOT_PRODUCT_LOCATION 已建立（PK / 1 FK）==';
END

/* ---------- 列说明 ---------- */
DECLARE @descriptions TABLE (COLUMN_NAME SYSNAME, DESCRIPTION NVARCHAR(400));
INSERT INTO @descriptions (COLUMN_NAME, DESCRIPTION) VALUES
    (N'DEPOT_ID',    N'所属库别'),
    (N'PRO_NO',      N'料号'),
    (N'LOCATION_NO', N'库内位置编号'),
    (N'IS_PRIMARY',  N'是否主货位：1 主货位 / 0 溢出位'),
    (N'SEQ_NO',      N'同一物料的拣货顺序');

DECLARE @columnName SYSNAME, @description NVARCHAR(400);
DECLARE description_cursor CURSOR LOCAL FAST_FORWARD FOR SELECT COLUMN_NAME, DESCRIPTION FROM @descriptions;
OPEN description_cursor;
FETCH NEXT FROM description_cursor INTO @columnName, @description;
WHILE @@FETCH_STATUS = 0
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM sys.extended_properties ep
        WHERE ep.major_id = OBJECT_ID(N'dbo.DEPOT_PRODUCT_LOCATION')
          AND ep.minor_id = COLUMNPROPERTY(OBJECT_ID(N'dbo.DEPOT_PRODUCT_LOCATION'), @columnName, 'ColumnId')
          AND ep.name = N'MS_Description')
    BEGIN
        EXEC sp_addextendedproperty
            @name = N'MS_Description', @value = @description,
            @level0type = N'SCHEMA', @level0name = N'dbo',
            @level1type = N'TABLE',  @level1name = N'DEPOT_PRODUCT_LOCATION',
            @level2type = N'COLUMN', @level2name = @columnName;
    END
    FETCH NEXT FROM description_cursor INTO @columnName, @description;
END
CLOSE description_cursor;
DEALLOCATE description_cursor;

/* ---------- 收口断言 ---------- */
IF OBJECT_ID(N'dbo.DEPOT_PRODUCT_LOCATION', N'U') IS NULL
    THROW 50902, N'dbo.DEPOT_PRODUCT_LOCATION 未建立，迁移中止。', 1;

DECLARE @pk INT = (SELECT COUNT(*) FROM sys.key_constraints WHERE parent_object_id = OBJECT_ID(N'dbo.DEPOT_PRODUCT_LOCATION') AND type = N'PK');
DECLARE @fk INT = (SELECT COUNT(*) FROM sys.foreign_keys WHERE parent_object_id = OBJECT_ID(N'dbo.DEPOT_PRODUCT_LOCATION'));

IF @pk <> 1 OR @fk <> 1
    THROW 50903, N'DEPOT_PRODUCT_LOCATION 的约束构成不符合预期（期望 PK=1 / FK=1），迁移中止。', 1;

IF NOT EXISTS (
    SELECT 1 FROM sys.default_constraints dc
    JOIN sys.columns c ON c.object_id = dc.parent_object_id AND c.column_id = dc.parent_column_id
    WHERE dc.parent_object_id = OBJECT_ID(N'dbo.DEPOT_PRODUCT_LOCATION')
      AND c.name = N'IS_PRIMARY' AND dc.definition LIKE N'%(0)%')
    THROW 50904, N'DEPOT_PRODUCT_LOCATION.IS_PRIMARY 缺少默认值 0，迁移中止。', 1;

PRINT N'== 收口：DEPOT_PRODUCT_LOCATION PK=1 / FK=1，IS_PRIMARY 默认 0 ==';
