-- ============================================================================
-- EOS.ERP migration 195: DEPOT_LOCATION.LOCATION_PATH 补默认值
-- ----------------------------------------------------------------------------
-- LOCATION_PATH 是**服务端派生列**（物化路径），由保存期动作 location-path-recalc 按上级重算；
-- 而效果链的 SAVE 阶段跑在记录插入**之后**，因此插入那一刻该列必须有一个可接受的值——
-- 它是 NOT NULL 且原本没有默认值，于是工作台新建库位会直接以
-- "不能将值 NULL 插入列 'LOCATION_PATH'" 报 500。
--
-- 补 DEFAULT (N'')：插入先落空串，同一事务内的保存期动作随即写入真实路径，
-- 提交时不会是空串。空串语义是"路径尚未生成"，只可能出现在事务中间态或直连改库的残留，
-- 两者都不会被当作合法路径使用（按路径前缀的查询匹配不到空串）。
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
    THROW 52200, @GUARD_MESSAGE, 1;

IF OBJECT_ID(N'dbo.DEPOT_LOCATION', N'U') IS NULL
    THROW 52201, N'表 dbo.DEPOT_LOCATION 不存在，迁移中止。', 1;

IF NOT EXISTS (SELECT 1 FROM sys.columns c
               WHERE c.object_id = OBJECT_ID(N'dbo.DEPOT_LOCATION') AND c.name = N'LOCATION_PATH')
    THROW 52202, N'列 dbo.DEPOT_LOCATION.LOCATION_PATH 不存在，迁移中止。', 1;

IF NOT EXISTS (SELECT 1 FROM sys.default_constraints d
               JOIN sys.columns c ON c.object_id = d.parent_object_id AND c.column_id = d.parent_column_id
               WHERE c.object_id = OBJECT_ID(N'dbo.DEPOT_LOCATION') AND c.name = N'LOCATION_PATH')
BEGIN
    ALTER TABLE dbo.DEPOT_LOCATION
        ADD CONSTRAINT DF_DEPOT_LOCATION_PATH DEFAULT (N'') FOR LOCATION_PATH;
    PRINT N'== 已为 DEPOT_LOCATION.LOCATION_PATH 补默认值 N'''' ==';
END
ELSE
    PRINT N'== LOCATION_PATH 已有默认值（幂等跳过）==';

/* ---------- 收口断言 ---------- */
IF NOT EXISTS (SELECT 1 FROM sys.default_constraints d
               JOIN sys.columns c ON c.object_id = d.parent_object_id AND c.column_id = d.parent_column_id
               WHERE c.object_id = OBJECT_ID(N'dbo.DEPOT_LOCATION') AND c.name = N'LOCATION_PATH')
    THROW 52203, N'LOCATION_PATH 仍缺少默认值，迁移中止。', 1;

-- 既有数据不得因补默认值出现空路径（哨兵行应为 '/-'）
IF EXISTS (SELECT 1 FROM dbo.DEPOT_LOCATION WHERE LTRIM(RTRIM(ISNULL(LOCATION_PATH, N''))) = N'')
    THROW 52204, N'存在路径为空的库位行，请先修复后再迁移，迁移中止。', 1;

PRINT N'== LOCATION_PATH 默认值就位 ==';
