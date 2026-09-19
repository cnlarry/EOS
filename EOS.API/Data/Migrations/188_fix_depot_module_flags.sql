-- ============================================================================
-- EOS.ERP migration 189: 按同域模块补齐库位主档 / 库存策略的标志位
-- ----------------------------------------------------------------------------
-- 症状：GET /api/v1/admin/menus 整页 500 —— MenuAdminRepository.ReadModule 用
-- SqlDataReader.GetBoolean 读标志位列，遇到 NULL 直接抛 SqlNullValueException。
--
-- 根因：新建模块行时只给了 M_IDX / 父节点 / 名称 / 主表 / 路由，而 GROUP1..GROUP5、
-- DETAIL_NO_SAVE、SEARCH_1、SEARCH_2、IF_COPY 这些列没有默认值约束，留成 NULL 就炸。
--
-- 处置：**逐列从同域兄弟模块 110306 仓库资料复制全部标志位**，不硬编码取值——
-- 库位主档与库存策略和仓库资料同属仓库域、管理深度同层，标志位本就应当一致。
--
-- 幂等：已是目标取值即跳过。
-- 回滚：把 110309 / 110310 的标志位还原为 NULL。
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
    THROW 51800, @GUARD_MESSAGE, 1;

IF NOT EXISTS (SELECT 1 FROM dbo.MODULES WHERE M_IDX = 110306)
    THROW 51801, N'参照模块 110306 仓库资料不存在，迁移中止。', 1;

UPDATE m
   SET m.M_TAG = r.M_TAG,
       m.GROUP1 = r.GROUP1, m.GROUP2 = r.GROUP2, m.GROUP3 = r.GROUP3, m.GROUP4 = r.GROUP4, m.GROUP5 = r.GROUP5,
       m.CONFIRM_TAG = r.CONFIRM_TAG,
       m.DETAIL_NO_SAVE = r.DETAIL_NO_SAVE,
       m.AUTO_APPROVE = r.AUTO_APPROVE,
       m.ERROR_NO_SAVE = r.ERROR_NO_SAVE,
       m.SEARCH_1 = r.SEARCH_1, m.SEARCH_2 = r.SEARCH_2,
       m.IF_COPY = r.IF_COPY,
       m.EFFECT_ENGINE_TAG = r.EFFECT_ENGINE_TAG,
       m.M_ALIAS = ISNULL(m.M_ALIAS, N''),
       m.LAST_UPDATE_BY = N'migration',
       m.LAST_UPDATE_DATE = GETDATE()
  FROM dbo.MODULES m
 CROSS JOIN (SELECT * FROM dbo.MODULES WHERE M_IDX = 110306) r
 WHERE m.M_IDX IN (110309, 110310);

PRINT N'== 已按 110306 补齐标志位 ' + CONVERT(NVARCHAR(10), @@ROWCOUNT) + N' 行 ==';

/* ---------- 收口断言：新模块不得有任何 NULL 的 bit 列 ---------- */
IF EXISTS (
    SELECT 1 FROM dbo.MODULES
    WHERE M_IDX IN (110309, 110310)
      AND (M_TAG IS NULL OR GROUP1 IS NULL OR GROUP2 IS NULL OR GROUP3 IS NULL OR GROUP4 IS NULL OR GROUP5 IS NULL
        OR CONFIRM_TAG IS NULL OR DETAIL_NO_SAVE IS NULL OR AUTO_APPROVE IS NULL OR ERROR_NO_SAVE IS NULL
        OR SEARCH_1 IS NULL OR SEARCH_2 IS NULL OR IF_COPY IS NULL OR EFFECT_ENGINE_TAG IS NULL))
    THROW 51802, N'新模块仍存在 NULL 的标志位，菜单读取会抛异常，迁移中止。', 1;

/* 与参照模块逐个标志位一致 */
IF EXISTS (
    SELECT 1 FROM dbo.MODULES m CROSS JOIN (SELECT * FROM dbo.MODULES WHERE M_IDX = 110306) r
    WHERE m.M_IDX IN (110309, 110310)
      AND (m.M_TAG <> r.M_TAG OR m.GROUP1 <> r.GROUP1 OR m.GROUP2 <> r.GROUP2 OR m.GROUP3 <> r.GROUP3
        OR m.GROUP4 <> r.GROUP4 OR m.GROUP5 <> r.GROUP5 OR m.CONFIRM_TAG <> r.CONFIRM_TAG
        OR m.DETAIL_NO_SAVE <> r.DETAIL_NO_SAVE OR m.AUTO_APPROVE <> r.AUTO_APPROVE
        OR m.ERROR_NO_SAVE <> r.ERROR_NO_SAVE OR m.SEARCH_1 <> r.SEARCH_1 OR m.SEARCH_2 <> r.SEARCH_2
        OR m.IF_COPY <> r.IF_COPY OR m.EFFECT_ENGINE_TAG <> r.EFFECT_ENGINE_TAG))
    THROW 51803, N'新模块的标志位与参照模块不一致，迁移中止。', 1;

PRINT N'== 收口：110309 / 110310 的标志位与 110306 逐列一致，且无 NULL ==';
