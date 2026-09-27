-- ============================================================================
-- EOS.ERP migration 258: 库存策略新增「过期批次」档位（EXPIRY_MODE）
-- ----------------------------------------------------------------------------
-- 语义：出库时碰到**已过期**的批次怎么办 —— 0 不管 / 1 告警 / 2 拒绝。
-- 默认取 2（拒绝）：效期一旦录进来，"过期还能照常出库"是最不该出现的默认档位。
--
-- 为什么并入 `DEPOT_STOCK_POLICY` 而不是另立一张配置表：库存策略的求值口径是"**库别行整行覆盖，
-- 否则回落部署级默认**"，另立一张表就会多出第二套继承与审计，两套口径迟早分叉；而本档位的
-- 消费点（过账引擎）与其余档位同在 `#INV_MOVE_POLICY` 一次求值。
--
-- 作用范围与其余维度一致：库别行覆盖部署级默认行（`DEPOT_ID = '*'`）。
-- 判据本身（怎么算过期、判哪一侧）在过账引擎，不在本迁移。
--
-- 幂等：列存在即跳过；默认行缺失即 THROW（策略求值依赖它）。
-- ============================================================================

SET NOCOUNT ON;
SET XACT_ABORT ON;

IF DB_NAME() <> N'EOS.ERP'
    THROW 50000, N'本脚本只能在 EOS.ERP 数据库内执行。', 1;

BEGIN TRANSACTION;

IF COL_LENGTH('dbo.DEPOT_STOCK_POLICY', 'EXPIRY_MODE') IS NULL
BEGIN
    ALTER TABLE dbo.DEPOT_STOCK_POLICY
        ADD EXPIRY_MODE INT NOT NULL
            CONSTRAINT DF_DEPOT_STOCK_POLICY_EXPIRY_MODE DEFAULT (2);
    PRINT N'== 新增列 DEPOT_STOCK_POLICY.EXPIRY_MODE（INT，默认 2 = 拒绝）==';
END
ELSE
    PRINT N'== 列 EXPIRY_MODE 已存在，跳过 ==';

-- 取值域约束走**动态 SQL**：整批是一次性解析的，同一批次里直接写 EXPIRY_MODE 会在解析期报
-- "列名无效"（该列是本批刚 ADD 的）。约束本身仍落在同一条事务里。
IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N'CK_DEPOT_STOCK_POLICY_EXPIRY_MODE')
    EXEC (N'ALTER TABLE dbo.DEPOT_STOCK_POLICY
        ADD CONSTRAINT CK_DEPOT_STOCK_POLICY_EXPIRY_MODE CHECK (EXPIRY_MODE IN (0, 1, 2));');

IF NOT EXISTS (SELECT 1 FROM sys.extended_properties
                WHERE major_id = OBJECT_ID(N'dbo.DEPOT_STOCK_POLICY')
                  AND minor_id = COLUMNPROPERTY(OBJECT_ID(N'dbo.DEPOT_STOCK_POLICY'), N'EXPIRY_MODE', 'ColumnId')
                  AND name = N'MS_Description')
    EXEC sys.sp_addextendedproperty
        @name = N'MS_Description',
        @value = N'出库碰到已过期批次时的行为：0=不管；1=放行但告警；2=拒绝（按下单时该批次的 EFFECT_DATE 判，空效期视为不受管控）。库别行覆盖部署级默认行。',
        @level0type = N'SCHEMA', @level0name = N'dbo',
        @level1type = N'TABLE', @level1name = N'DEPOT_STOCK_POLICY',
        @level2type = N'COLUMN', @level2name = N'EXPIRY_MODE';

-- 核对：部署级默认行必须在（策略求值与过账判据都从它回落）
IF NOT EXISTS (SELECT 1 FROM dbo.DEPOT_STOCK_POLICY WHERE DEPOT_ID = N'*')
    THROW 52140, N'缺少部署级默认行 DEPOT_STOCK_POLICY(''*'')，过期批次档位无处可放，迁移中止。', 1;

-- 后置自证：列可空性/默认值/检查约束三件套到位。
IF EXISTS (SELECT 1 FROM sys.columns c
            WHERE c.object_id = OBJECT_ID(N'dbo.DEPOT_STOCK_POLICY') AND c.name = N'EXPIRY_MODE'
              AND (c.is_nullable = 1 OR c.system_type_id <> TYPE_ID(N'int')))
    THROW 52141, N'EXPIRY_MODE 未落成「INT 非空」，与本迁移的预期不符。', 1;

IF NOT EXISTS (SELECT 1 FROM sys.default_constraints d
                JOIN sys.columns c ON c.object_id = d.parent_object_id AND c.column_id = d.parent_column_id
               WHERE d.parent_object_id = OBJECT_ID(N'dbo.DEPOT_STOCK_POLICY') AND c.name = N'EXPIRY_MODE')
    THROW 52142, N'EXPIRY_MODE 缺少默认值约束。', 1;

IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N'CK_DEPOT_STOCK_POLICY_EXPIRY_MODE')
    THROW 52143, N'EXPIRY_MODE 缺少取值检查约束。', 1;

-- 取值打印走动态 SQL：整批一次性解析，同批内直接引用刚 ADD 的列会在解析期报"列名无效"。
EXEC (N'DECLARE @mode INT = (SELECT EXPIRY_MODE FROM dbo.DEPOT_STOCK_POLICY WHERE DEPOT_ID = N''*'');
PRINT CONCAT(N''== 就位：部署级 EXPIRY_MODE = '', @mode, N''（0=不管 / 1=告警 / 2=拒绝）=='');');

COMMIT TRANSACTION;
