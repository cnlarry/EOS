-- ============================================================================
-- EOS.ERP migration 188: 补齐库位主档 / 库存策略模块的菜单标记
-- ----------------------------------------------------------------------------
-- 菜单构建会读取 MODULES.M_TAG；同域兄弟模块（110301/110303/110306…）该列均为 1，
-- 新建模块留 NULL 会让 GET /api/v1/admin/menus 整页 500。M_ALIAS 同批补齐为空串，
-- 与兄弟模块的既有取值一致。
--
-- 建表迁移 186 已同步修正，本迁移用于收敛此前已落库的行。
-- 幂等：已是目标取值即跳过。
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
    THROW 51700, @GUARD_MESSAGE, 1;

UPDATE dbo.MODULES
   SET M_TAG = 1,
       M_ALIAS = ISNULL(M_ALIAS, N''),
       LAST_UPDATE_BY = N'migration',
       LAST_UPDATE_DATE = GETDATE()
 WHERE M_IDX IN (110309, 110310)
   AND (M_TAG IS NULL OR M_ALIAS IS NULL);

PRINT N'== 已补齐菜单标记 ' + CONVERT(NVARCHAR(10), @@ROWCOUNT) + N' 行 ==';

/* ---------- 收口断言：新模块的菜单标记必须与同域兄弟一致 ---------- */
IF EXISTS (
    SELECT 1 FROM dbo.MODULES m
    WHERE m.M_IDX IN (110309, 110310)
      AND (m.M_TAG IS NULL
        OR m.M_TAG <> (SELECT TOP 1 r.M_TAG FROM dbo.MODULES r WHERE r.M_IDX = 110306)
        OR m.M_ALIAS IS NULL))
    THROW 51701, N'新模块的菜单标记仍与同域兄弟不一致，迁移中止。', 1;

PRINT N'== 收口：110309 / 110310 的 M_TAG / M_ALIAS 与 110306 一致 ==';
