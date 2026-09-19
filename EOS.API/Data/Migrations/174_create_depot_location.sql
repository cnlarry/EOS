-- ============================================================================
-- EOS.ERP migration 175: 新增库位主档 DEPOT_LOCATION
-- ----------------------------------------------------------------------------
-- 库存位置采用"自引用树 + 编码"：层级由 PARENT_NO 承载（层数因客户而异），
-- LOCATION_PATH 是冗余的物化路径，用于按库区盘点 / 汇总，避免递归查询。
-- 位置号在库内唯一（不同库别可以有各自的 A-01-02）。
--
-- 只描述库存域内的物理位置，不建模工位 / 设备位 / 档案位 / 资产位。
--
-- 列规约：建立组（CREATE_PERSON / CREATE_DATE）与公司别（CI）按既有主档表口径
-- 取 NOT NULL + DEFAULT；经办人 / 修改日期组（LAST_UPDATE_*）保持可空
-- （NULL 表示事件尚未发生）。可空性口径由 scripts/check-lifecycle-columns.ps1 巡检。
--
-- 幂等：表已存在时只做断言，不重复建表。
-- 回滚：DROP TABLE（仅当尚无引用该表的对象时才安全）。
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
    THROW 50400, @GUARD_MESSAGE, 1;

/* ---------- 前置：所属仓库主档必须存在且列型一致（外键前提） ---------- */
IF OBJECT_ID(N'dbo.DEPOT', N'U') IS NULL
    THROW 50401, N'主档表 dbo.DEPOT 不存在，迁移中止。', 1;

IF NOT EXISTS (
    SELECT 1 FROM sys.columns c
    WHERE c.object_id = OBJECT_ID(N'dbo.DEPOT') AND c.name = N'DEPOT_ID'
      AND TYPE_NAME(c.user_type_id) = N'nchar' AND c.max_length = 20)
    THROW 50402, N'dbo.DEPOT.DEPOT_ID 不是 NCHAR(10)，与 DEPOT_LOCATION 不同型，无法建立外键，迁移中止。', 1;

IF OBJECT_ID(N'dbo.DEPOT_LOCATION', N'U') IS NOT NULL
BEGIN
    PRINT N'== dbo.DEPOT_LOCATION 已存在（幂等跳过建表）==';
END
ELSE
BEGIN
    CREATE TABLE dbo.DEPOT_LOCATION
    (
        DEPOT_ID         NCHAR (10)     NOT NULL,
        LOCATION_NO      NVARCHAR (30)  NOT NULL,
        PARENT_NO        NVARCHAR (30)  NULL,
        LOCATION_PATH    NVARCHAR (300) NOT NULL,
        LOCATION_TYPE    NVARCHAR (10)  NOT NULL,
        LOCATION_NAME    NVARCHAR (100) NULL,
        STORAGE_TYPE     NVARCHAR (10)  NULL,
        SEQ_NO           INT            NULL,
        STATUS           NCHAR (1)      NOT NULL CONSTRAINT DF_DEPOT_LOCATION_STATUS       DEFAULT (N'A'),
        CREATE_PERSON    NCHAR (20)     NOT NULL CONSTRAINT DF_DEPOT_LOCATION_CREATE_PERSON DEFAULT (N''),
        CREATE_DATE      DATETIME       NOT NULL CONSTRAINT DF_DEPOT_LOCATION_CREATE_DATE   DEFAULT (GETDATE()),
        LAST_UPDATE_BY   NCHAR (20)     NULL,
        LAST_UPDATE_DATE DATETIME       NULL,
        OWNER            NCHAR (10)     NULL,
        OWNER_G          NCHAR (10)     NULL,
        CI               NCHAR (10)     NOT NULL CONSTRAINT DF_DEPOT_LOCATION_CI            DEFAULT (N'DEFAULT'),
        CONSTRAINT PK_DEPOT_LOCATION PRIMARY KEY CLUSTERED (DEPOT_ID ASC, LOCATION_NO ASC),
        CONSTRAINT FK_DEPOT_LOCATION_DEPOT FOREIGN KEY (DEPOT_ID) REFERENCES dbo.DEPOT (DEPOT_ID),
        CONSTRAINT FK_DEPOT_LOCATION_PARENT FOREIGN KEY (DEPOT_ID, PARENT_NO)
            REFERENCES dbo.DEPOT_LOCATION (DEPOT_ID, LOCATION_NO),
        CONSTRAINT CK_DEPOT_LOCATION_TYPE
            CHECK (LOCATION_TYPE IN (N'ZONE', N'RACK', N'BIN', N'PALLET', N'STAGE', N'QC', N'SCRAP', N'TRANSIT')),
        CONSTRAINT CK_DEPOT_LOCATION_STORAGE_TYPE
            CHECK (STORAGE_TYPE IS NULL OR STORAGE_TYPE IN (N'BULK', N'PICK')),
        CONSTRAINT CK_DEPOT_LOCATION_STATUS
            CHECK (STATUS IN (N'A', N'L', N'I'))
    );

    CREATE NONCLUSTERED INDEX IX_DEPOT_LOCATION_PARENT
        ON dbo.DEPOT_LOCATION (DEPOT_ID ASC, PARENT_NO ASC);

    CREATE NONCLUSTERED INDEX IX_DEPOT_LOCATION_PATH
        ON dbo.DEPOT_LOCATION (DEPOT_ID ASC, LOCATION_PATH ASC);

    PRINT N'== dbo.DEPOT_LOCATION 已建立（PK / 2 FK / 3 CHECK / 2 非聚集索引）==';
END

/* ---------- 列说明 ---------- */
DECLARE @descriptions TABLE (COLUMN_NAME SYSNAME, DESCRIPTION NVARCHAR(400));
INSERT INTO @descriptions (COLUMN_NAME, DESCRIPTION) VALUES
    (N'DEPOT_ID',         N'所属库别'),
    (N'LOCATION_NO',      N'库内位置编号（库内唯一）'),
    (N'PARENT_NO',        N'上级位置编号（同库内；空为第一层）'),
    (N'LOCATION_PATH',    N'位置物化路径，如 /A/A-01/A-01-02'),
    (N'LOCATION_TYPE',    N'位置类型：ZONE 库区 / RACK 货架 / BIN 货位 / PALLET 托盘 / STAGE 暂存 / QC 待检 / SCRAP 报废 / TRANSIT 在途'),
    (N'LOCATION_NAME',    N'位置名称'),
    (N'STORAGE_TYPE',     N'存放用途：BULK 整托存储位 / PICK 拆零拣货位；空为不区分'),
    (N'SEQ_NO',           N'同层排序，决定拣货 / 上架行走顺序'),
    (N'STATUS',           N'状态：A 可用 / L 锁定 / I 停用'),
    (N'CREATE_PERSON',    N'建立人'),
    (N'CREATE_DATE',      N'建立日期'),
    (N'LAST_UPDATE_BY',   N'最后修改人'),
    (N'LAST_UPDATE_DATE', N'最后修改日期'),
    (N'OWNER',            N'归属账号'),
    (N'OWNER_G',          N'归属组'),
    (N'CI',               N'公司别');

DECLARE @columnName SYSNAME, @description NVARCHAR(400);
DECLARE description_cursor CURSOR LOCAL FAST_FORWARD FOR SELECT COLUMN_NAME, DESCRIPTION FROM @descriptions;
OPEN description_cursor;
FETCH NEXT FROM description_cursor INTO @columnName, @description;
WHILE @@FETCH_STATUS = 0
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM sys.extended_properties ep
        WHERE ep.major_id = OBJECT_ID(N'dbo.DEPOT_LOCATION') AND ep.minor_id = COLUMNPROPERTY(OBJECT_ID(N'dbo.DEPOT_LOCATION'), @columnName, 'ColumnId')
          AND ep.name = N'MS_Description')
    BEGIN
        EXEC sp_addextendedproperty
            @name = N'MS_Description', @value = @description,
            @level0type = N'SCHEMA', @level0name = N'dbo',
            @level1type = N'TABLE',  @level1name = N'DEPOT_LOCATION',
            @level2type = N'COLUMN', @level2name = @columnName;
    END
    FETCH NEXT FROM description_cursor INTO @columnName, @description;
END
CLOSE description_cursor;
DEALLOCATE description_cursor;

/* ---------- 收口断言 ---------- */
IF OBJECT_ID(N'dbo.DEPOT_LOCATION', N'U') IS NULL
    THROW 50403, N'dbo.DEPOT_LOCATION 未建立，迁移中止。', 1;

DECLARE @pk INT = (SELECT COUNT(*) FROM sys.key_constraints WHERE parent_object_id = OBJECT_ID(N'dbo.DEPOT_LOCATION') AND type = N'PK');
DECLARE @fk INT = (SELECT COUNT(*) FROM sys.foreign_keys WHERE parent_object_id = OBJECT_ID(N'dbo.DEPOT_LOCATION'));
DECLARE @ck INT = (SELECT COUNT(*) FROM sys.check_constraints WHERE parent_object_id = OBJECT_ID(N'dbo.DEPOT_LOCATION'));
DECLARE @ix INT = (SELECT COUNT(*) FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.DEPOT_LOCATION') AND type = 2);

IF @pk <> 1 OR @fk <> 2 OR @ck <> 3 OR @ix <> 2
    THROW 50404, N'DEPOT_LOCATION 的约束构成不符合预期（期望 PK=1 / FK=2 / CHECK=3 / 非聚集索引=2），迁移中止。', 1;

/* 建立组与公司别必须 NOT NULL 且有默认值（与现有主档表口径一致） */
IF EXISTS (
    SELECT 1 FROM sys.columns c
    LEFT JOIN sys.default_constraints dc ON dc.parent_object_id = c.object_id AND dc.parent_column_id = c.column_id
    WHERE c.object_id = OBJECT_ID(N'dbo.DEPOT_LOCATION')
      AND c.name IN (N'CREATE_PERSON', N'CREATE_DATE', N'CI')
      AND (c.is_nullable = 1 OR dc.object_id IS NULL))
    THROW 50405, N'DEPOT_LOCATION 的建立组 / 公司别列不是 NOT NULL + DEFAULT，迁移中止。', 1;

PRINT N'== 收口：DEPOT_LOCATION PK=1 / FK=2 / CHECK=3 / 非聚集索引=2，建立组与公司别为 NOT NULL + DEFAULT ==';
