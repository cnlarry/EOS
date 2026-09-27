-- ============================================================================
-- EOS.ERP migration 263: 清掉发不出去的模块脏标记（迁移 260 标脏范围过宽）
-- ----------------------------------------------------------------------------
-- `WORKBENCH_MODULE_DIRTY` 的语义是"该模块的工作台定义改过、待重发布"。发布的前置是模块
-- **运行期可达**（`WorkbenchDefinitionValidator` 的 `runtime_whitelist` 判据：`M_URL` 是
-- 工作台承载页，或在统一表单名单内），不可达的模块一律被校验拒绝、脏行永远清不掉，
-- 于是「已编辑未发布」列表里常驻一批假积压。
--
-- 来源：迁移 260 按"哪些模块的 `DETAIL_TABLE` 命中这批出库明细表"标脏，把与工作台共用同一
-- 明细表的 `/reports`、`/search-center` 明细页也一起标了——它们既不在统一表单名单内，
-- `M_URL` 也不是工作台承载页（实测 2026-09-27 发布这批模块时被 `runtime_whitelist` 全数拒绝）。
--
-- 处置：只清这 13 行（`LAST_MODIFIED_BY='migration-260'` 且确实不可达）。**可达模块的脏标记
-- 一行不动**；若清单里出现可达模块说明预期不成立，直接中止而不是继续删。
-- 其余历史脏行（其它标记来源、含 `/reports` 明细页 144 行等）不在本迁移范围，另议。
--
-- 幂等：先删后自证；重复执行删 0 行、自证仍成立。
-- ============================================================================

SET NOCOUNT ON;
SET XACT_ABORT ON;

IF DB_NAME() <> N'EOS.ERP'
    THROW 50000, N'本脚本只能在 EOS.ERP 数据库内执行。', 1;

BEGIN TRANSACTION;

DECLARE @Unpublishable TABLE (M_IDX INT NOT NULL PRIMARY KEY);
INSERT INTO @Unpublishable (M_IDX) VALUES
    (2504), (139805), (139806), (149805), (149807), (149808), (149813),
    (149814), (149816), (159803), (169808), (289802), (299806);

-- 前置自证：清单里不得出现工作台承载页模块（可达就不该删，宁可中止）
IF EXISTS (SELECT 1 FROM dbo.WORKBENCH_MODULE_DIRTY d
           JOIN @Unpublishable u ON u.M_IDX = d.M_IDX
           JOIN dbo.MODULES m ON m.M_IDX = d.M_IDX
           WHERE LTRIM(RTRIM(ISNULL(m.M_URL, N''))) LIKE N'/workbench%')
    THROW 52180, N'清单中出现工作台承载页模块（可达、脏标记有意义），本迁移的预期不成立。', 1;

-- 可达脏行数取证：删除前后必须保持不变
DECLARE @ReachableBefore INT = (
    SELECT COUNT(*) FROM dbo.WORKBENCH_MODULE_DIRTY d
    JOIN dbo.MODULES m ON m.M_IDX = d.M_IDX
    WHERE LTRIM(RTRIM(ISNULL(m.M_URL, N''))) LIKE N'/workbench%');

DECLARE @Removed INT;

DELETE d
FROM dbo.WORKBENCH_MODULE_DIRTY d
JOIN @Unpublishable u ON u.M_IDX = d.M_IDX;

SET @Removed = @@ROWCOUNT;
PRINT N'== 已清除不可达模块的脏标记行数：' + CONVERT(NVARCHAR(10), @Removed) + N' ==';

-- 后置自证 ① 清单里的行不再存在
IF EXISTS (SELECT 1 FROM dbo.WORKBENCH_MODULE_DIRTY d JOIN @Unpublishable u ON u.M_IDX = d.M_IDX)
    THROW 52181, N'清单中的脏标记未清干净。', 1;

-- 后置自证 ② 可达脏行一行未动
IF (SELECT COUNT(*) FROM dbo.WORKBENCH_MODULE_DIRTY d
    JOIN dbo.MODULES m ON m.M_IDX = d.M_IDX
    WHERE LTRIM(RTRIM(ISNULL(m.M_URL, N''))) LIKE N'/workbench%') <> @ReachableBefore
    THROW 52182, N'可达模块的脏标记数量发生变化，删除范围越界。', 1;

COMMIT TRANSACTION;

PRINT N'== 完成：不可达模块的脏标记已清除，可达模块的待发布积压保持原样 ==';
