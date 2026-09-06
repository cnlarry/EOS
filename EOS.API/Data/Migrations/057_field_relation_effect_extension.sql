-- ============================================================================
-- EOS.ERP migration 058: FIELD_RELATION 效果侧扩展（单据关系方案 A）
-- ----------------------------------------------------------------------------
-- 在读侧单列边基础上增加“效果边组”语义：
--   RELATION_ID   效果边组标识（复合键定位同组多行共享）
--   RELATION_NAME 边组语义名（供 2301/引擎展示）
--   RELATION_KIND READ=读侧既有边 / EFFECT=效果/校验定位边
--   SOURCE_SCOPE  本单侧角色：MASTER / DETAIL / TABLE（TABLE=已登记上下文表）
--   KEY_ORDINAL   同组键序（与 MATCH_STRUCT 数组顺序一致）
-- 既有读侧行保持四元组主键，RELATION_KIND 回填 READ；
-- EFFECT 边由登记工具幂等写入，配置保存 lint 引用已登记边。
-- 命名全大写；幂等：COL_LENGTH / sys.indexes 守卫。
-- ============================================================================

SET NOCOUNT ON;

DECLARE @GUARD_MESSAGE NVARCHAR(400) = N'本脚本只能在 EOS.ERP 数据库内执行，当前库为 ' + DB_NAME() + N'。';
IF DB_NAME() <> N'EOS.ERP'
    THROW 50000, @GUARD_MESSAGE, 1;

PRINT N'== 开始扩展 FIELD_RELATION（效果侧边组） ==';

IF COL_LENGTH(N'dbo.FIELD_RELATION', N'RELATION_ID') IS NULL
    ALTER TABLE dbo.FIELD_RELATION ADD RELATION_ID BIGINT NULL;

IF COL_LENGTH(N'dbo.FIELD_RELATION', N'RELATION_NAME') IS NULL
    ALTER TABLE dbo.FIELD_RELATION ADD RELATION_NAME NVARCHAR(100) NULL;

IF COL_LENGTH(N'dbo.FIELD_RELATION', N'RELATION_KIND') IS NULL
    ALTER TABLE dbo.FIELD_RELATION ADD RELATION_KIND NVARCHAR(10) NOT NULL
        CONSTRAINT DF_FIELD_RELATION_KIND DEFAULT (N'READ');

IF COL_LENGTH(N'dbo.FIELD_RELATION', N'SOURCE_SCOPE') IS NULL
    ALTER TABLE dbo.FIELD_RELATION ADD SOURCE_SCOPE NVARCHAR(10) NULL;

IF COL_LENGTH(N'dbo.FIELD_RELATION', N'KEY_ORDINAL') IS NULL
    ALTER TABLE dbo.FIELD_RELATION ADD KEY_ORDINAL INT NULL;

GO

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes i WHERE i.object_id = OBJECT_ID(N'dbo.FIELD_RELATION')
      AND i.name = N'UX_FIELD_RELATION_EFFECT_GROUP'
)
BEGIN
    CREATE UNIQUE INDEX UX_FIELD_RELATION_EFFECT_GROUP
        ON dbo.FIELD_RELATION (RELATION_ID, KEY_ORDINAL)
        WHERE RELATION_KIND = N'EFFECT';
END

GO

DECLARE @TARGET TABLE (COL_NAME NVARCHAR(128), COL_DESC NVARCHAR(400));
INSERT INTO @TARGET (COL_NAME, COL_DESC) VALUES
    (N'RELATION_ID', N'效果边组标识：同一边组的多行（复合键）共享同一 ID。'),
    (N'RELATION_NAME', N'边组语义名（如 收料明细→采购行）。'),
    (N'RELATION_KIND', N'边种类：READ=语义层读侧既有边；EFFECT=效果/校验定位边。'),
    (N'SOURCE_SCOPE', N'效果边本单侧角色：MASTER / DETAIL / TABLE。'),
    (N'KEY_ORDINAL', N'效果边组内键序（1 起，与定位键数组顺序一致）。');

DECLARE @col NVARCHAR(128), @desc NVARCHAR(400);
DECLARE cur CURSOR LOCAL FAST_FORWARD FOR SELECT COL_NAME, COL_DESC FROM @TARGET;
OPEN cur;
FETCH NEXT FROM cur INTO @col, @desc;
WHILE @@FETCH_STATUS = 0
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM sys.extended_properties ep
        WHERE ep.major_id = OBJECT_ID(N'dbo.FIELD_RELATION')
          AND ep.minor_id = COLUMNPROPERTY(OBJECT_ID(N'dbo.FIELD_RELATION'), @col, N'ColumnId')
          AND ep.name = N'MS_Description'
    )
        EXEC sys.sp_addextendedproperty @name=N'MS_Description', @value=@desc,
            @level0type=N'SCHEMA', @level0name=N'dbo',
            @level1type=N'TABLE', @level1name=N'FIELD_RELATION',
            @level2type=N'COLUMN', @level2name=@col;
    FETCH NEXT FROM cur INTO @col, @desc;
END
CLOSE cur;
DEALLOCATE cur;

GO

PRINT N'== FIELD_RELATION 效果侧扩展完成 ==';
