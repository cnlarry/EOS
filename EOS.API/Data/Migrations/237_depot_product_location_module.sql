-- ============================================================================
-- EOS.ERP migration 238: 物料主货位的维护入口（模块 + 字段元数据）
-- ----------------------------------------------------------------------------
-- ADR-020 §10 WS-14（D1b）：`DEPOT_PRODUCT_LOCATION` 建表在迁移 180，但**只有表**——
-- 0 行 / 0 元数据 / 0 模块 / 0 引用，能力台帐把它登记为"完全空壳"。后果不是"暂时用不上"，
-- 而是：`STORAGE_MODE = FIXED` 的库别在引擎里**认不到任何主货位**（表里没人能往里写），
-- 于是"固定存放"实际等于"落哨兵"，配置里写着 FIXED 而行为与不配一样。
--
-- 本迁移补的是**入口**（能维护），不是行为：引擎侧读主货位在 WS-15 已接
-- （`DepotLocationService.GetPrimaryLocationsAsync`）。两条合起来，"FIXED ⇒ 落主货位"才是活的。
--
-- 配置形态照仓库既有主档（模板 = 模块 110309 库位主档 / 110306 仓库资料 / 1206 品号）：
--   · 模块挂 `1103 仓库管理` 之下（M_ROOT_IDX = 11），URL 走 `/workbench`（统一表单，非报表）；
--   · 三个主键列 `IS_PK = 1`（与表上的 PK (DEPOT_ID, PRO_NO, LOCATION_NO) 一致——
--     统一表单的 MasterPkOrder 就是从 IS_PK 推出来的）；
--   · 库别 / 品号 / 库位给选择器（`BROWSE_M_IDX`）：新表单的 chooser 按模块号取数，
--     故填模块号即可；`BROWSE_URL` 只对旧 aspx 有意义，库位那一格留空（旧页面没有对应入口，
--     与其编一个不存在的地址，不如不给）。
--
-- 为什么不把 `IS_PRIMARY` 做成只读：主货位**就是**靠它标出来的，一个物料在一个库别可以有
-- 主货位 + 多个溢出位（ADR-014 §3.11），可读可写是本表存在的意义。
--
-- 幂等：模块与字段都按"不存在才插"处理，可重复执行。
-- ============================================================================

IF NOT EXISTS (SELECT 1 FROM dbo.MODULES WHERE M_IDX = 110311)
BEGIN
    INSERT INTO dbo.MODULES (M_IDX, M_DESC, M_URL, M_P_IDX, M_ROOT_IDX, SORT_IDX, M_TAG, MASTER_TABLE)
        VALUES (110311, N'物料主货位', N'/workbench', 1103, 11, 81, 1, N'DEPOT_PRODUCT_LOCATION');
    PRINT N'== 新增模块 110311 物料主货位 ==';
END
ELSE
    PRINT N'== 模块 110311 已存在，跳过 ==';

IF NOT EXISTS (SELECT 1 FROM dbo.FIELDS WHERE RTRIM(T_ID) = N'DEPOT_PRODUCT_LOCATION')
BEGIN
    INSERT INTO dbo.FIELDS
        (T_ID, F_ID, F_DESC, F_TYPE, BROWSE_M_IDX, BROWSE_URL, ONLY_CHOOSE,
         IS_PK, IS_READONLY, IS_VISIBLE, IS_QUERY, IS_DEFAULT_FIELDS, FORM_ORDER)
        VALUES
        (N'DEPOT_PRODUCT_LOCATION', N'DEPOT_ID', N'库别', N'nchar', 110306,
         N'~/INV/Depot.aspx?IDX=DEPOT_ID={DEPOT_ID}', 1, 1, 0, 1, 1, 1, 1),
        (N'DEPOT_PRODUCT_LOCATION', N'PRO_NO', N'品号', N'nchar', 1206,
         N'~/BOM/Product.aspx?IDX=PRO_NO={PRO_NO}', 1, 1, 0, 1, 1, 1, 2),
        (N'DEPOT_PRODUCT_LOCATION', N'LOCATION_NO', N'库位编号', N'nvarchar', 110309,
         NULL, 1, 1, 0, 1, 1, 1, 3),
        (N'DEPOT_PRODUCT_LOCATION', N'IS_PRIMARY', N'主货位', N'bit', NULL,
         NULL, 0, 0, 0, 1, 0, 1, 4),
        (N'DEPOT_PRODUCT_LOCATION', N'SEQ_NO', N'排序', N'int', NULL,
         NULL, 0, 0, 0, 1, 0, 1, 5);
    PRINT N'== 新增 DEPOT_PRODUCT_LOCATION 的 5 个字段元数据 ==';
END
ELSE
    PRINT N'== DEPOT_PRODUCT_LOCATION 字段元数据已存在，跳过 ==';

/* 核对：模块、主键列、选择器都就位，否则迁移白跑 */
DECLARE @fields INT = (SELECT COUNT(*) FROM dbo.FIELDS WHERE RTRIM(T_ID) = N'DEPOT_PRODUCT_LOCATION');
DECLARE @pk INT = (SELECT COUNT(*) FROM dbo.FIELDS WHERE RTRIM(T_ID) = N'DEPOT_PRODUCT_LOCATION' AND IS_PK = 1);
DECLARE @choosers INT = (SELECT COUNT(*) FROM dbo.FIELDS
                          WHERE RTRIM(T_ID) = N'DEPOT_PRODUCT_LOCATION' AND BROWSE_M_IDX IS NOT NULL);
IF NOT EXISTS (SELECT 1 FROM dbo.MODULES WHERE M_IDX = 110311 AND RTRIM(MASTER_TABLE) = N'DEPOT_PRODUCT_LOCATION')
    THROW 52110, N'模块 110311 未指向 DEPOT_PRODUCT_LOCATION，迁移中止。', 1;
IF @fields <> 5 OR @pk <> 3 OR @choosers <> 3
    THROW 52111, N'物料主货位的字段元数据不完整（字段/主键/选择器数量不符），迁移中止。', 1;

PRINT N'== 就位：模块 110311 物料主货位（5 个字段 / 3 个主键列 / 3 个选择器）==';
