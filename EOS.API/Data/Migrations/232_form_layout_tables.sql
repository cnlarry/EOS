-- ============================================================================
-- EOS.ERP migration 233: 模块级表单版式表（MODULE_FORM_LAYOUT / MODULE_FORM_TAB）
-- ----------------------------------------------------------------------------
-- 表单的"形态"（页签 / 顺序 / 占位 / 复合格 / 分节 / 表单内隐藏）今天配在**字段**上
-- （FIELDS.FORM_*），而形态是**模块**的属性：同一张物理表被多个模块共用时（实测 26 张，
-- MOC_PRODUCE_M 被 6 个模块共用），这些模块的版式只能一模一样，且改一个会连带改到别人。
-- 故把形态搬到模块级，键为 (M_IDX, T_ID, F_ID)：
--
--   M_IDX  —— 模块号；T_ID —— 字段所属表（模块主表或明细表）；F_ID —— 字段代号。
--   T_ID 不可省：主表与明细表存在大量同名字段（实测 909 组），只按 (M_IDX, F_ID) 会撞车。
--
-- 存行为而不是整份 LAYOUT_JSON 的理由：配置面要按 (模块, 表, 字段) 做查询、校验、门禁与
-- 审计差异——"哪些模块排了这个字段""主从是否成对""必填字段有没有被藏起来"，用 JSON 都要
-- 先解析。行式存 → 保存期编译成 JSON 进 WORKBENCH_DEFINITION_SNAPSHOT.DEFINITION_JSON。
--
-- 定制粒度是 (模块, 表)：某模块某表一旦有行，该表在表单上的字段集**完全**由本表决定，
-- 未列出的字段视为"未加入表单"，不再回落 FIELDS.FORM_*。若改判"模块只要有任何一行就全定制"，
-- 只有明细行的模块会把主表字段整批判成"未加入表单"而全部消失。
--
-- 明细只消费 ORDER_NO（列顺序）与 IS_HIDDEN（列显隐），其余版式列对明细行无意义但仍是
-- NOT NULL——写入时显式落默认值（明细无页签、无格、无复合格）。
--
-- 分节与复合格是两个字段（现状 FORM_CELL_GROUP 一列两种语义，分节还得靠"同组 ≥2 个主字段"
-- 去猜）：CELL_GROUP/CELL_ROLE 表达复合格 [主][选择钮][从]，SECTION_ID 表达分节。
--
-- 幂等：表 / 扩展属性均按存在性判断，可重复执行。
-- 回滚：DROP TABLE dbo.MODULE_FORM_TAB; DROP TABLE dbo.MODULE_FORM_LAYOUT;
-- 说明：本迁移只建表，不写任何版式行——零配置模块由运行时按既有字段级配置推导默认版式。
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

BEGIN TRANSACTION;

-- 1. MODULE_FORM_LAYOUT：一行 = 一个字段在某模块表单上的位置与占位
IF OBJECT_ID(N'dbo.MODULE_FORM_LAYOUT', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.MODULE_FORM_LAYOUT (
        [M_IDX]       INT            NOT NULL,
        [T_ID]        NVARCHAR(100)  NOT NULL,
        [F_ID]        NVARCHAR(100)  NOT NULL,
        [TAB_NO]      INT            NOT NULL CONSTRAINT [DF_MODULE_FORM_LAYOUT_TAB_NO]   DEFAULT (1),
        [ORDER_NO]    INT            NOT NULL CONSTRAINT [DF_MODULE_FORM_LAYOUT_ORDER_NO] DEFAULT (1),
        [SPAN]        TINYINT        NOT NULL CONSTRAINT [DF_MODULE_FORM_LAYOUT_SPAN]     DEFAULT (1),
        [ROW_SPAN]    TINYINT        NOT NULL CONSTRAINT [DF_MODULE_FORM_LAYOUT_ROW_SPAN] DEFAULT (1),
        [NEW_LINE]    BIT            NOT NULL CONSTRAINT [DF_MODULE_FORM_LAYOUT_NEW_LINE] DEFAULT (0),
        [SECTION_ID]  NVARCHAR(50)   NULL,
        [CELL_GROUP]  NVARCHAR(50)   NULL,
        [CELL_ROLE]   TINYINT        NOT NULL CONSTRAINT [DF_MODULE_FORM_LAYOUT_CELL_ROLE] DEFAULT (0),
        [IS_HIDDEN]   BIT            NOT NULL CONSTRAINT [DF_MODULE_FORM_LAYOUT_HIDDEN]    DEFAULT (0),
        [UPDATED_BY]  NVARCHAR(50)   NULL,
        [UPDATED_AT]  DATETIME2(3)   NOT NULL CONSTRAINT [DF_MODULE_FORM_LAYOUT_UPDATED]   DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT [PK_MODULE_FORM_LAYOUT] PRIMARY KEY ([M_IDX], [T_ID], [F_ID])
    );
    PRINT N'== 已建表 dbo.MODULE_FORM_LAYOUT ==';
END
ELSE
    PRINT N'== dbo.MODULE_FORM_LAYOUT 已存在，跳过 ==';

-- 2. MODULE_FORM_TAB：页签（TAB_NO=1 为常驻"默认"页签，不可删除）
IF OBJECT_ID(N'dbo.MODULE_FORM_TAB', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.MODULE_FORM_TAB (
        [M_IDX]     INT           NOT NULL,
        [TAB_NO]    INT           NOT NULL,
        [TAB_TITLE] NVARCHAR(50)  NOT NULL CONSTRAINT [DF_MODULE_FORM_TAB_TITLE] DEFAULT (N''),
        CONSTRAINT [PK_MODULE_FORM_TAB] PRIMARY KEY ([M_IDX], [TAB_NO])
    );
    PRINT N'== 已建表 dbo.MODULE_FORM_TAB ==';
END
ELSE
    PRINT N'== dbo.MODULE_FORM_TAB 已存在，跳过 ==';

-- 3. 扩展属性（表 / 列说明，便于 schema 审阅）
DECLARE @descriptions TABLE (
    TABLE_NAME  SYSNAME NOT NULL,
    LEVEL2_TYPE SYSNAME NULL,
    LEVEL2_NAME SYSNAME NULL,
    DESCRIPTION NVARCHAR(400) NOT NULL);

INSERT INTO @descriptions (TABLE_NAME, LEVEL2_TYPE, LEVEL2_NAME, DESCRIPTION) VALUES
    (N'MODULE_FORM_LAYOUT', NULL,     NULL,      N'模块级表单版式：一行 = 一个字段在某模块表单上的位置与占位（键 M_IDX+T_ID+F_ID）'),
    (N'MODULE_FORM_LAYOUT', N'COLUMN', N'T_ID',       N'字段所属表：该模块的主表或明细表（主/明细存在大量同名字段，故不可省）'),
    (N'MODULE_FORM_LAYOUT', N'COLUMN', N'TAB_NO',     N'页签号，默认 1；明细行无页签，固定写 1'),
    (N'MODULE_FORM_LAYOUT', N'COLUMN', N'ORDER_NO',   N'页签内的排列序号（主表）/ 明细列顺序（明细表）'),
    (N'MODULE_FORM_LAYOUT', N'COLUMN', N'SPAN',       N'列跨度 1..MODULES.FORM_COLUMNS；明细行忽略，固定写 1'),
    (N'MODULE_FORM_LAYOUT', N'COLUMN', N'ROW_SPAN',   N'行跨度 1..3；明细行忽略，固定写 1'),
    (N'MODULE_FORM_LAYOUT', N'COLUMN', N'NEW_LINE',   N'强制换行；明细行忽略，固定写 0'),
    (N'MODULE_FORM_LAYOUT', N'COLUMN', N'SECTION_ID', N'所属分节（与复合格解耦）；明细行忽略，固定写 NULL'),
    (N'MODULE_FORM_LAYOUT', N'COLUMN', N'CELL_GROUP', N'复合格组名：同组的主字段与从字段渲染在同一格；明细行忽略，固定写 NULL'),
    (N'MODULE_FORM_LAYOUT', N'COLUMN', N'CELL_ROLE',  N'复合格角色：0 普通 / 1 主字段 / 2 从字段；明细行忽略，固定写 0'),
    (N'MODULE_FORM_LAYOUT', N'COLUMN', N'IS_HIDDEN',  N'表单（主表）或明细列内不显示；与 FIELDS.IS_VISIBLE（跨子系统可见性）不是一回事'),
    (N'MODULE_FORM_TAB',    NULL,     NULL,      N'模块级表单页签：TAB_NO 即顺序，TAB_NO=1 为常驻默认页签，空标题显示为"默认"');

DECLARE @level2Type SYSNAME, @level2Name SYSNAME, @description NVARCHAR(400), @level2TableName SYSNAME;
DECLARE description_cursor CURSOR LOCAL FAST_FORWARD FOR
    SELECT TABLE_NAME, LEVEL2_TYPE, LEVEL2_NAME, DESCRIPTION FROM @descriptions;
OPEN description_cursor;
FETCH NEXT FROM description_cursor INTO @level2TableName, @level2Type, @level2Name, @description;
WHILE @@FETCH_STATUS = 0
BEGIN
    IF EXISTS (
        SELECT 1 FROM sys.extended_properties ep
        WHERE ep.major_id = OBJECT_ID(N'dbo.' + @level2TableName)
          AND ep.minor_id = CASE WHEN @level2Type IS NULL THEN 0
                                 ELSE COLUMNPROPERTY(OBJECT_ID(N'dbo.' + @level2TableName), @level2Name, 'ColumnId') END
          AND ep.name = N'MS_Description')
    BEGIN
        EXEC sp_updateextendedproperty
            @name = N'MS_Description', @value = @description,
            @level0type = N'SCHEMA', @level0name = N'dbo',
            @level1type = N'TABLE',  @level1name = @level2TableName,
            @level2type = @level2Type, @level2name = @level2Name;
    END
    ELSE
    BEGIN
        EXEC sp_addextendedproperty
            @name = N'MS_Description', @value = @description,
            @level0type = N'SCHEMA', @level0name = N'dbo',
            @level1type = N'TABLE',  @level1name = @level2TableName,
            @level2type = @level2Type, @level2name = @level2Name;
    END
    FETCH NEXT FROM description_cursor INTO @level2TableName, @level2Type, @level2Name, @description;
END
CLOSE description_cursor;
DEALLOCATE description_cursor;

-- 4. 核对断言：两表与主键必须就位
IF OBJECT_ID(N'dbo.MODULE_FORM_LAYOUT', N'U') IS NULL
    THROW 52301, N'表 dbo.MODULE_FORM_LAYOUT 未就位，迁移中止。', 1;
IF OBJECT_ID(N'dbo.MODULE_FORM_TAB', N'U') IS NULL
    THROW 52302, N'表 dbo.MODULE_FORM_TAB 未就位，迁移中止。', 1;

IF NOT EXISTS (SELECT 1 FROM sys.indexes i
               WHERE i.object_id = OBJECT_ID(N'dbo.MODULE_FORM_LAYOUT') AND i.is_primary_key = 1
                 AND (SELECT COUNT(*) FROM sys.index_columns ic
                      WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.is_included_column = 0) = 3)
    THROW 52303, N'MODULE_FORM_LAYOUT 的主键不是三列 (M_IDX, T_ID, F_ID)，迁移中止。', 1;

IF EXISTS (
    SELECT 1 FROM (VALUES (N'M_IDX'), (N'T_ID'), (N'F_ID')) AS expected(COLUMN_NAME)
    WHERE NOT EXISTS (
        SELECT 1 FROM sys.index_columns ic
        JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
        WHERE ic.object_id = OBJECT_ID(N'dbo.MODULE_FORM_LAYOUT')
          AND ic.index_id = (SELECT index_id FROM sys.indexes
                             WHERE object_id = OBJECT_ID(N'dbo.MODULE_FORM_LAYOUT') AND is_primary_key = 1)
          AND ic.is_included_column = 0 AND c.name = expected.COLUMN_NAME))
    THROW 52304, N'MODULE_FORM_LAYOUT 主键缺列，迁移中止。', 1;

IF EXISTS (
    SELECT 1 FROM (VALUES (N'M_IDX'), (N'T_ID'), (N'F_ID'), (N'TAB_NO'), (N'ORDER_NO'), (N'SPAN'), (N'ROW_SPAN'),
                         (N'NEW_LINE'), (N'SECTION_ID'), (N'CELL_GROUP'), (N'CELL_ROLE'), (N'IS_HIDDEN'),
                         (N'UPDATED_BY'), (N'UPDATED_AT')) AS expected(COLUMN_NAME)
    WHERE NOT EXISTS (
        SELECT 1 FROM sys.columns c
        WHERE c.object_id = OBJECT_ID(N'dbo.MODULE_FORM_LAYOUT') AND c.name = expected.COLUMN_NAME))
    THROW 52305, N'MODULE_FORM_LAYOUT 缺列，迁移中止。', 1;

-- 版式表为空是**预期状态**：零配置模块走运行时推导
IF EXISTS (SELECT 1 FROM dbo.MODULE_FORM_LAYOUT)
    THROW 52306, N'MODULE_FORM_LAYOUT 预期为空表，迁移中止。', 1;

IF NOT EXISTS (SELECT 1 FROM sys.extended_properties
               WHERE major_id = OBJECT_ID(N'dbo.MODULE_FORM_LAYOUT') AND minor_id = 0 AND name = N'MS_Description')
    THROW 52307, N'MODULE_FORM_LAYOUT 表说明缺失，迁移中止。', 1;

-- 5. 自证
SELECT N'MODULE_FORM_LAYOUT' AS OBJECT_NAME, COUNT(*) AS CNT FROM sys.tables WHERE name = N'MODULE_FORM_LAYOUT';
SELECT N'MODULE_FORM_TAB' AS OBJECT_NAME, COUNT(*) AS CNT FROM sys.tables WHERE name = N'MODULE_FORM_TAB';
SELECT N'MODULE_FORM_LAYOUT_COLUMNS' AS OBJECT_NAME, COUNT(*) AS CNT
FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.MODULE_FORM_LAYOUT');
SELECT N'MODULE_FORM_LAYOUT_ROWS' AS OBJECT_NAME, COUNT(*) AS CNT FROM dbo.MODULE_FORM_LAYOUT;
SELECT N'MODULE_FORM_TAB_ROWS' AS OBJECT_NAME, COUNT(*) AS CNT FROM dbo.MODULE_FORM_TAB;

COMMIT TRANSACTION;
