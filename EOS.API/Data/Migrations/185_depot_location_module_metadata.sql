-- ============================================================================
-- EOS.ERP migration 186: 库位主档与库存策略的模块、菜单与字段元数据
-- ----------------------------------------------------------------------------
-- 库位主档（DEPOT_LOCATION）与库存策略（DEPOT_STOCK_POLICY）都是 P1 新建的表，
-- 但只有库内对象还不够：没有 MODULES 行就没有入口、没有权限门，没有 FIELDS 行
-- 则在列表与表单里一个字段都看不到。
--
-- 模块编号取 110309 / 110310（1103xx 仓库资料域已用到 110308），父节点与根节点
-- 沿用该域的既有取值，使它们与"仓库资料"并列出现在同一菜单分组下。
--
-- 字段元数据按 DEPOT 的既有口径补齐：主键列 IS_PK=1、可查询列 IS_QUERY=1、
-- 服务端持有列 IS_READONLY=1。位置路径是冗余快照列，不对用户展示也不参与查询。
--
-- 幂等：按主键逐行 MERGE，可重复执行。
-- 回滚：DELETE 本迁移写入的 MODULES / FIELDS 行。
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
    THROW 51500, @GUARD_MESSAGE, 1;

IF OBJECT_ID(N'dbo.DEPOT_LOCATION', N'U') IS NULL OR OBJECT_ID(N'dbo.DEPOT_STOCK_POLICY', N'U') IS NULL
    THROW 51501, N'库位主档或策略表不存在，请先执行 P1 建表迁移，迁移中止。', 1;

/* ---------- ① 模块行 ---------- */
-- M_TAG / M_ALIAS 必须与同域兄弟模块同口径：菜单构建读取 M_TAG，留 NULL 会让整页 500。
MERGE dbo.MODULES AS target
USING (VALUES
    (110309, 1103, 11, 65, N'库位主档', N'DEPOT_LOCATION', N'/workbench', N''),
    (110310, 1103, 11, 66, N'库存策略', N'DEPOT_STOCK_POLICY', N'/workbench', N'')
) AS source (M_IDX, M_P_IDX, M_ROOT_IDX, SORT_IDX, M_DESC, MASTER_TABLE, M_URL, M_ALIAS)
ON target.M_IDX = source.M_IDX
WHEN MATCHED THEN UPDATE SET
    M_P_IDX = source.M_P_IDX, M_ROOT_IDX = source.M_ROOT_IDX, SORT_IDX = source.SORT_IDX,
    M_DESC = source.M_DESC, MASTER_TABLE = source.MASTER_TABLE, M_URL = source.M_URL,
    M_ALIAS = source.M_ALIAS, M_TAG = 1,
    LAST_UPDATE_BY = N'migration', LAST_UPDATE_DATE = GETDATE()
WHEN NOT MATCHED THEN INSERT (M_IDX, M_P_IDX, M_ROOT_IDX, SORT_IDX, M_DESC, MASTER_TABLE, M_URL, M_ALIAS, M_TAG, CREATE_PERSON, CREATE_DATE)
    VALUES (source.M_IDX, source.M_P_IDX, source.M_ROOT_IDX, source.SORT_IDX, source.M_DESC,
            source.MASTER_TABLE, source.M_URL, source.M_ALIAS, 1, N'migration', GETDATE());

PRINT N'== 已注册模块 110309 库位主档 / 110310 库存策略 ==';

/* ---------- ①b 标志位按同域兄弟模块复制 ----------
   GROUP1..GROUP5 / DETAIL_NO_SAVE / SEARCH_1 / SEARCH_2 / IF_COPY 这些列没有默认值约束，
   留 NULL 会让菜单读取（SqlDataReader.GetBoolean）当场抛异常。逐列复制 110306 的取值，
   不硬编码：两个新模块与仓库资料同域同层，标志位本就应当一致。 */
IF EXISTS (SELECT 1 FROM dbo.MODULES WHERE M_IDX = 110306)
BEGIN
    UPDATE m
       SET m.M_TAG = r.M_TAG,
           m.GROUP1 = r.GROUP1, m.GROUP2 = r.GROUP2, m.GROUP3 = r.GROUP3, m.GROUP4 = r.GROUP4, m.GROUP5 = r.GROUP5,
           m.CONFIRM_TAG = r.CONFIRM_TAG, m.DETAIL_NO_SAVE = r.DETAIL_NO_SAVE, m.AUTO_APPROVE = r.AUTO_APPROVE,
           m.ERROR_NO_SAVE = r.ERROR_NO_SAVE, m.SEARCH_1 = r.SEARCH_1, m.SEARCH_2 = r.SEARCH_2,
           m.IF_COPY = r.IF_COPY, m.EFFECT_ENGINE_TAG = r.EFFECT_ENGINE_TAG
      FROM dbo.MODULES m
     CROSS JOIN (SELECT * FROM dbo.MODULES WHERE M_IDX = 110306) r
     WHERE m.M_IDX IN (110309, 110310);
END

/* ---------- ② 字段元数据 ---------- */
DECLARE @fields TABLE (
    T_ID NVARCHAR(60), F_ID NVARCHAR(60), F_DESC NVARCHAR(200), F_TYPE NVARCHAR(20),
    IS_PK BIT, IS_VISIBLE BIT, IS_QUERY BIT, IS_READONLY BIT, DISPLAY_LENGTH INT, F_REMARK NVARCHAR(400));

INSERT INTO @fields (T_ID, F_ID, F_DESC, F_TYPE, IS_PK, IS_VISIBLE, IS_QUERY, IS_READONLY, DISPLAY_LENGTH, F_REMARK) VALUES
    -- 库位主档
    (N'DEPOT_LOCATION', N'DEPOT_ID',         N'库别',     N'nchar',    1, 1, 1, 0,  54, N'所属库别'),
    (N'DEPOT_LOCATION', N'LOCATION_NO',      N'库位编号', N'nvarchar', 1, 1, 1, 0,  94, N'库内位置编号，库内唯一'),
    (N'DEPOT_LOCATION', N'PARENT_NO',        N'上级库位', N'nvarchar', 0, 1, 1, 0,  94, N'上级位置编号，空为第一层'),
    (N'DEPOT_LOCATION', N'LOCATION_PATH',    N'位置路径', N'nvarchar', 0, 0, 0, 1, 160, N'物化路径，服务端维护，不展示不查询'),
    (N'DEPOT_LOCATION', N'LOCATION_TYPE',    N'位置类型', N'nvarchar', 0, 1, 1, 0,  67, N'ZONE 库区 / RACK 货架 / BIN 货位 / PALLET 托盘 / STAGE 暂存 / QC 待检 / SCRAP 报废 / TRANSIT 在途'),
    (N'DEPOT_LOCATION', N'LOCATION_NAME',    N'位置名称', N'nvarchar', 0, 1, 1, 0,  94, NULL),
    (N'DEPOT_LOCATION', N'STORAGE_TYPE',     N'存放用途', N'nvarchar', 0, 1, 1, 0,  67, N'BULK 整托存储位 / PICK 拆零拣货位；空为不区分'),
    (N'DEPOT_LOCATION', N'SEQ_NO',           N'排序',     N'int',      0, 1, 0, 0,  54, N'同层排序'),
    (N'DEPOT_LOCATION', N'STATUS',           N'状态',     N'nchar',    0, 1, 1, 0,  67, N'A 可用 / L 锁定 / I 停用'),
    (N'DEPOT_LOCATION', N'CREATE_PERSON',    N'建立人',   N'nchar',    0, 1, 0, 1,  67, NULL),
    (N'DEPOT_LOCATION', N'CREATE_DATE',      N'建立日期', N'datetime', 0, 1, 0, 1, 141, NULL),
    (N'DEPOT_LOCATION', N'LAST_UPDATE_BY',   N'修改人',   N'nchar',    0, 1, 0, 1,  67, NULL),
    (N'DEPOT_LOCATION', N'LAST_UPDATE_DATE', N'修改日期', N'datetime', 0, 1, 0, 1,  79, NULL),
    (N'DEPOT_LOCATION', N'OWNER',            N'所有者',   N'nchar',    0, 1, 0, 0,  67, NULL),
    (N'DEPOT_LOCATION', N'OWNER_G',          N'所有者组', N'nchar',    0, 1, 0, 0,  79, NULL),
    (N'DEPOT_LOCATION', N'CI',               N'公司',     N'nchar',    0, 1, 0, 0,  94, NULL),
    -- 库存策略
    (N'DEPOT_STOCK_POLICY', N'DEPOT_ID',               N'作用域',     N'nvarchar', 1, 1, 1, 0, 94,  N'具体库别代号，或 * 表示部署级默认'),
    (N'DEPOT_STOCK_POLICY', N'LOCATION_MODE',          N'位置管理',   N'int',      0, 1, 1, 0, 106, N'0 不管 / 1 可填 / 2 建议 / 3 强制'),
    (N'DEPOT_STOCK_POLICY', N'STORAGE_MODE',           N'存放方式',   N'nvarchar', 0, 1, 1, 0, 94,  N'FIXED 固定 / RANDOM 随机 / MIXED 混合'),
    (N'DEPOT_STOCK_POLICY', N'BATCH_MODE',             N'批次管理',   N'int',      0, 1, 1, 0, 106, N'0 不管 / 1 记录 / 2 必填 / 3 必填+效期（未实现）'),
    (N'DEPOT_STOCK_POLICY', N'CAPACITY_MODE',          N'容量校验',   N'int',      0, 1, 1, 0, 106, N'0 不校验 / 1 告警 / 2 强制（未实现）'),
    (N'DEPOT_STOCK_POLICY', N'MIX_PRODUCT',            N'允许混品号', N'bit',      0, 1, 1, 0, 106, N'1 允许 / 0 禁止'),
    (N'DEPOT_STOCK_POLICY', N'MIX_BATCH',              N'允许混批次', N'bit',      0, 1, 1, 0, 106, N'1 允许 / 0 禁止'),
    (N'DEPOT_STOCK_POLICY', N'MONTH_CLOSE_BY_BATCH',   N'月结按批次', N'bit',      0, 1, 1, 1, 106, N'仅部署级生效，库别不可覆盖'),
    (N'DEPOT_STOCK_POLICY', N'MONTH_CLOSE_BY_LOCATION',N'月结按库位', N'bit',      0, 1, 1, 1, 106, N'仅部署级生效，库别不可覆盖'),
    (N'DEPOT_STOCK_POLICY', N'LAST_UPDATE_BY',         N'修改人',     N'nchar',    0, 1, 0, 1, 67,  NULL),
    (N'DEPOT_STOCK_POLICY', N'LAST_UPDATE_DATE',       N'修改日期',   N'datetime', 0, 1, 0, 1, 79,  NULL),
    (N'DEPOT_STOCK_POLICY', N'CI',                     N'公司',       N'nchar',    0, 1, 0, 0, 94,  NULL),
    (N'DEPOT_STOCK_POLICY', N'OWNER',                  N'所有者',     N'nchar',    0, 1, 0, 0, 67,  NULL),
    (N'DEPOT_STOCK_POLICY', N'OWNER_G',                N'所有者组',   N'nchar',    0, 1, 0, 0, 79,  NULL);

MERGE dbo.FIELDS AS target
USING (SELECT f.*, ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS ORD FROM @fields f) AS source
   ON target.T_ID = source.T_ID AND target.F_ID = source.F_ID
WHEN MATCHED THEN UPDATE SET
    F_DESC = source.F_DESC, F_TYPE = source.F_TYPE, IS_PK = source.IS_PK, IS_VISIBLE = source.IS_VISIBLE,
    IS_QUERY = source.IS_QUERY, IS_READONLY = source.IS_READONLY, DISPLAY_LENGTH = source.DISPLAY_LENGTH,
    F_REMARK = source.F_REMARK, FORM_ORDER = source.ORD,
    LAST_UPDATE_BY = N'migration', LAST_UPDATE_DATE = GETDATE()
WHEN NOT MATCHED THEN INSERT
    (T_ID, F_ID, F_DESC, F_TYPE, IS_PK, IS_VISIBLE, IS_QUERY, IS_READONLY, DISPLAY_LENGTH, F_REMARK, FORM_ORDER, LAST_UPDATE_BY, LAST_UPDATE_DATE)
    VALUES (source.T_ID, source.F_ID, source.F_DESC, source.F_TYPE, source.IS_PK, source.IS_VISIBLE,
            source.IS_QUERY, source.IS_READONLY, source.DISPLAY_LENGTH, source.F_REMARK, source.ORD,
            N'migration', GETDATE());

PRINT N'== 已写入字段元数据 ' + CONVERT(NVARCHAR(10), @@ROWCOUNT) + N' 行 ==';

/* ---------- 收口断言 ---------- */
IF (SELECT COUNT(*) FROM dbo.MODULES WHERE M_IDX IN (110309, 110310)) <> 2
    THROW 51502, N'模块行未注册齐，迁移中止。', 1;

IF EXISTS (SELECT 1 FROM dbo.MODULES WHERE M_IDX IN (110309, 110310) AND (MASTER_TABLE IS NULL OR M_URL IS NULL))
    THROW 51503, N'模块行缺少主表或路由，迁移中止。', 1;

DECLARE @expected INT = (SELECT COUNT(*) FROM @fields);
DECLARE @actual INT = (SELECT COUNT(*) FROM dbo.FIELDS WHERE T_ID IN (N'DEPOT_LOCATION', N'DEPOT_STOCK_POLICY'));

IF @actual < @expected
    THROW 51504, N'字段元数据行数少于预期，迁移中止。', 1;

/* 每张新主档至少要有主键列元数据，否则列表无法定位行 */
IF NOT EXISTS (SELECT 1 FROM dbo.FIELDS WHERE T_ID = N'DEPOT_LOCATION' AND F_ID = N'LOCATION_NO' AND IS_PK = 1)
   OR NOT EXISTS (SELECT 1 FROM dbo.FIELDS WHERE T_ID = N'DEPOT_STOCK_POLICY' AND F_ID = N'DEPOT_ID' AND IS_PK = 1)
    THROW 51505, N'新主档缺少主键列元数据，迁移中止。', 1;

DECLARE @actualText NVARCHAR(10) = CONVERT(NVARCHAR(10), @actual);
PRINT N'== 收口：模块 2 个（110309 库位主档 / 110310 库存策略），字段元数据 ' + @actualText + N' 行 ==';
