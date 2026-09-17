-- ============================================================================
-- EOS.ERP migration 114: 同形族 C# 领域规则退役（判据已入校验目录）
-- ----------------------------------------------------------------------------
-- `cop-back`(1423) / `cop-fitin`(1412) / `inv-loan`(130108) / `inv-return`(130109) /
-- `moc-product-in`(1505,1519,2816) 五族在 C# 侧只有一条判据——「批管品必填批号」，
-- 已按迁移 113 以 SAVE 期 `line-require`（跨表 condition + 逐行诊断）忠实移植，
-- 故从 `DomainRuleMap` 与 `DomainRuleService` 分派中删除这批族，并把模块登记为
-- 「保存后行为已由校验目录承接」（`CatalogAfterSaveMap`）。
-- 本迁移就地修正已发布快照里的族名（与 105/107/110/112 同法），使运行期不再分派到已删族；
-- 新规则进入运行期需重发布这些模块（发布走新版参数校验）。
-- 幂等：仅当当前快照仍含这些族名时改写；改写后复核，并守卫"退役后每模块必须有启用的 SAVE 期规则"。
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
    THROW 50000, @GUARD_MESSAGE, 1;

IF OBJECT_ID(N'tempdb..#PortedFamily') IS NOT NULL DROP TABLE #PortedFamily;
CREATE TABLE #PortedFamily (MODULE_ID INT PRIMARY KEY, FAMILY NVARCHAR(40) NOT NULL);
INSERT INTO #PortedFamily (MODULE_ID, FAMILY) VALUES
    (1423, N'cop-back'), (1412, N'cop-fitin'), (130108, N'inv-loan'), (130109, N'inv-return'),
    (1505, N'moc-product-in'), (1519, N'moc-product-in'), (2816, N'moc-product-in');

UPDATE S
   SET S.DEFINITION_JSON = REPLACE(S.DEFINITION_JSON,
                                   N'"DomainRule":"' + F.FAMILY + N'"',
                                   N'"DomainRule":null')
FROM dbo.WORKBENCH_DEFINITION_SNAPSHOT S
JOIN #PortedFamily F ON F.MODULE_ID = S.MODULE_ID
WHERE S.IS_CURRENT = 1
  AND S.DEFINITION_JSON LIKE N'%"DomainRule":"' + F.FAMILY + N'"%';

IF EXISTS (
    SELECT 1 FROM dbo.WORKBENCH_DEFINITION_SNAPSHOT S
    JOIN #PortedFamily F ON F.MODULE_ID = S.MODULE_ID
    WHERE S.IS_CURRENT = 1 AND S.DEFINITION_JSON LIKE N'%"DomainRule":"' + F.FAMILY + N'"%'
)
    THROW 50001, N'当前快照仍残留已退役的族名，迁移中止。', 1;

IF EXISTS (
    SELECT 1 FROM #PortedFamily F
    WHERE NOT EXISTS (
        SELECT 1 FROM dbo.MODULE_VALIDATION_RULE R
        WHERE R.MODULE_ID = F.MODULE_ID AND R.STAGE = N'SAVE' AND R.ENABLED = 1)
)
    THROW 50002, N'有模块在退役 C# 后没有启用的 SAVE 期目录规则，迁移中止（保存期判据会消失）。', 1;

IF OBJECT_ID(N'tempdb..#PortedFamily') IS NOT NULL DROP TABLE #PortedFamily;

PRINT N'== 同形五族 C# 退役完成（快照族名已置空，7 个模块均有启用的 SAVE 期目录规则）==';
