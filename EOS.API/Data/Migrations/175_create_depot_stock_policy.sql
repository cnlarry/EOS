-- ============================================================================
-- EOS.ERP migration 176: 新增库存策略参数组 DEPOT_STOCK_POLICY（含部署级默认行）
-- ----------------------------------------------------------------------------
-- 库存管理"管到多细"由客户自行配置：位置 / 存放 / 批次 / 容量 / 混品号 / 混批次
-- 六个维度各自独立，不打包成档位。参数放在独立表而不是仓库主档上，是为了：
--   · 可继承——DEPOT_ID = N'*' 为部署级默认，库别无自己的行时继承它；
--   · 可扩展——将来支持库区级覆盖只需在主键上加一维，不必动 DEPOT；
--   · 职责分离——DEPOT 是仓库主档，策略是管理深度决策，两者的权限与审计对象不同。
--
-- DEPOT_ID 用变长 NVARCHAR(10) 且**不建外键**：N'*' 不是合法库别，因此没有
-- "必须与 DEPOT 同型"的理由；变长同时消除定长填充带来的比较歧义。
--
-- 求值口径：库别行存在则整行覆盖，否则回落部署级默认行；两跳求值须收口到一处。
--
-- 幂等：表已存在时只做断言与默认行补插，不重复建表。
-- 回滚：DROP TABLE（策略为纯配置，删除不损失业务数据）。
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
    THROW 50500, @GUARD_MESSAGE, 1;

IF OBJECT_ID(N'dbo.DEPOT_STOCK_POLICY', N'U') IS NOT NULL
BEGIN
    PRINT N'== dbo.DEPOT_STOCK_POLICY 已存在（幂等跳过建表）==';
END
ELSE
BEGIN
    CREATE TABLE dbo.DEPOT_STOCK_POLICY
    (
        DEPOT_ID         NVARCHAR (10) NOT NULL,
        LOCATION_MODE    INT           NOT NULL CONSTRAINT DF_DSP_LOCATION_MODE DEFAULT (0),
        STORAGE_MODE     NVARCHAR (10) NOT NULL CONSTRAINT DF_DSP_STORAGE_MODE  DEFAULT (N'FIXED'),
        BATCH_MODE       INT           NOT NULL CONSTRAINT DF_DSP_BATCH_MODE    DEFAULT (0),
        CAPACITY_MODE    INT           NOT NULL CONSTRAINT DF_DSP_CAPACITY_MODE DEFAULT (0),
        MIX_PRODUCT      BIT           NOT NULL CONSTRAINT DF_DSP_MIX_PRODUCT   DEFAULT (1),
        MIX_BATCH        BIT           NOT NULL CONSTRAINT DF_DSP_MIX_BATCH     DEFAULT (1),
        LAST_UPDATE_BY   NCHAR (20)    NULL,
        LAST_UPDATE_DATE DATETIME      NULL,
        CI               NCHAR (10)    NOT NULL CONSTRAINT DF_DSP_CI            DEFAULT (N'DEFAULT'),
        OWNER            NCHAR (10)    NULL,
        OWNER_G          NCHAR (10)    NULL,
        CONSTRAINT PK_DEPOT_STOCK_POLICY PRIMARY KEY CLUSTERED (DEPOT_ID ASC),
        CONSTRAINT CK_DSP_LOCATION_MODE CHECK (LOCATION_MODE BETWEEN 0 AND 3),
        CONSTRAINT CK_DSP_BATCH_MODE    CHECK (BATCH_MODE BETWEEN 0 AND 3),
        CONSTRAINT CK_DSP_CAPACITY_MODE CHECK (CAPACITY_MODE BETWEEN 0 AND 2),
        CONSTRAINT CK_DSP_STORAGE_MODE  CHECK (STORAGE_MODE IN (N'FIXED', N'RANDOM', N'MIXED'))
    );

    PRINT N'== dbo.DEPOT_STOCK_POLICY 已建立（PK / 4 CHECK）==';
END

/* ---------- 部署级默认行：六维全取最松值，即"与不启用这些维度时完全等价" ---------- */
IF NOT EXISTS (SELECT 1 FROM dbo.DEPOT_STOCK_POLICY WHERE DEPOT_ID = N'*')
BEGIN
    INSERT INTO dbo.DEPOT_STOCK_POLICY
        (DEPOT_ID, LOCATION_MODE, STORAGE_MODE, BATCH_MODE, CAPACITY_MODE, MIX_PRODUCT, MIX_BATCH)
    VALUES (N'*', 0, N'FIXED', 0, 0, 1, 1);

    PRINT N'== 已写入部署级默认行（* ：0 / FIXED / 0 / 0 / 1 / 1）==';
END
ELSE
BEGIN
    PRINT N'== 部署级默认行已存在（幂等跳过）==';
END

/* ---------- 列说明 ---------- */
DECLARE @descriptions TABLE (COLUMN_NAME SYSNAME, DESCRIPTION NVARCHAR(400));
INSERT INTO @descriptions (COLUMN_NAME, DESCRIPTION) VALUES
    (N'DEPOT_ID',         N'作用域：具体库别代号，或 * 表示部署级默认'),
    (N'LOCATION_MODE',    N'位置管理深度：0 不管 / 1 可填 / 2 建议 / 3 强制'),
    (N'STORAGE_MODE',     N'存放方式：FIXED 固定 / RANDOM 随机 / MIXED 混合'),
    (N'BATCH_MODE',       N'批次管理：0 不管（非批管料件批号归零）/ 1 记录 / 2 必填 / 3 必填+效期'),
    (N'CAPACITY_MODE',    N'容量校验：0 不校验 / 1 告警 / 2 强制'),
    (N'MIX_PRODUCT',      N'是否允许同一位置混放不同料号：1 允许 / 0 禁止'),
    (N'MIX_BATCH',        N'是否允许同一位置混放不同批次：1 允许 / 0 禁止'),
    (N'LAST_UPDATE_BY',   N'最后修改人'),
    (N'LAST_UPDATE_DATE', N'最后修改日期'),
    (N'CI',               N'公司别'),
    (N'OWNER',            N'归属账号'),
    (N'OWNER_G',          N'归属组');

DECLARE @columnName SYSNAME, @description NVARCHAR(400);
DECLARE description_cursor CURSOR LOCAL FAST_FORWARD FOR SELECT COLUMN_NAME, DESCRIPTION FROM @descriptions;
OPEN description_cursor;
FETCH NEXT FROM description_cursor INTO @columnName, @description;
WHILE @@FETCH_STATUS = 0
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM sys.extended_properties ep
        WHERE ep.major_id = OBJECT_ID(N'dbo.DEPOT_STOCK_POLICY')
          AND ep.minor_id = COLUMNPROPERTY(OBJECT_ID(N'dbo.DEPOT_STOCK_POLICY'), @columnName, 'ColumnId')
          AND ep.name = N'MS_Description')
    BEGIN
        EXEC sp_addextendedproperty
            @name = N'MS_Description', @value = @description,
            @level0type = N'SCHEMA', @level0name = N'dbo',
            @level1type = N'TABLE',  @level1name = N'DEPOT_STOCK_POLICY',
            @level2type = N'COLUMN', @level2name = @columnName;
    END
    FETCH NEXT FROM description_cursor INTO @columnName, @description;
END
CLOSE description_cursor;
DEALLOCATE description_cursor;

/* ---------- 收口断言 ---------- */
DECLARE @pk INT = (SELECT COUNT(*) FROM sys.key_constraints WHERE parent_object_id = OBJECT_ID(N'dbo.DEPOT_STOCK_POLICY') AND type = N'PK');
DECLARE @ck INT = (SELECT COUNT(*) FROM sys.check_constraints WHERE parent_object_id = OBJECT_ID(N'dbo.DEPOT_STOCK_POLICY'));
DECLARE @fk INT = (SELECT COUNT(*) FROM sys.foreign_keys WHERE parent_object_id = OBJECT_ID(N'dbo.DEPOT_STOCK_POLICY'));

IF @pk <> 1 OR @ck <> 4
    THROW 50501, N'DEPOT_STOCK_POLICY 的约束构成不符合预期（期望 PK=1 / CHECK=4），迁移中止。', 1;

/* 该表刻意不建外键：* 不是合法库别 */
IF @fk <> 0
    THROW 50502, N'DEPOT_STOCK_POLICY 不应有外键，迁移中止。', 1;

IF NOT EXISTS (
    SELECT 1 FROM dbo.DEPOT_STOCK_POLICY
    WHERE DEPOT_ID = N'*' AND LOCATION_MODE = 0 AND STORAGE_MODE = N'FIXED'
      AND BATCH_MODE = 0 AND CAPACITY_MODE = 0 AND MIX_PRODUCT = 1 AND MIX_BATCH = 1)
    THROW 50503, N'部署级默认行缺失或六维取值不是 0 / FIXED / 0 / 0 / 1 / 1，迁移中止。', 1;

/* 有 OWNER 列的表其 CI 必须是 NOT NULL + DEFAULT */
IF EXISTS (
    SELECT 1 FROM sys.columns c
    LEFT JOIN sys.default_constraints dc ON dc.parent_object_id = c.object_id AND dc.parent_column_id = c.column_id
    WHERE c.object_id = OBJECT_ID(N'dbo.DEPOT_STOCK_POLICY') AND c.name = N'CI'
      AND (c.is_nullable = 1 OR dc.object_id IS NULL))
    THROW 50504, N'DEPOT_STOCK_POLICY.CI 不是 NOT NULL + DEFAULT，迁移中止。', 1;

PRINT N'== 收口：DEPOT_STOCK_POLICY PK=1 / CHECK=4 / FK=0，部署级默认行六维 = 0 / FIXED / 0 / 0 / 1 / 1 ==';
