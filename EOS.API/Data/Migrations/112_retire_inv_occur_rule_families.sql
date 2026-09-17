-- ============================================================================
-- EOS.ERP migration 112: 库存异动族 C# 领域规则退役（判据已入校验目录）
-- ----------------------------------------------------------------------------
-- 背景：`inv-init` / `inv-in` / `inv-out` / `inv-transfer` / `inv-scrap` / `inv-adjust`
-- 六族在 C# 侧只有一条判据——「批管品必填批号」（`InvDomainRules.InvOccurValidateAsync`）。
-- 该判据已按迁移 111 以 `line-require`（SAVE 期，跨表 condition 触发器 + 逐行诊断）忠实移植，
-- 故从 `DomainRuleMap` 与 `DomainRuleService` 分派中删除这批族，并把对应模块登记为
-- 「保存后行为已由校验目录承接」（`CatalogAfterSaveMap`）。
-- 本迁移就地修正已发布快照里的族名，使运行期不再分派到已删族（与 105/107/110 同法）。
-- 注意：新规则要进入**运行期**需重发布这些模块（发布用新二进制，才能通过新版参数校验）。
-- 幂等：仅当当前快照仍含这些族名时改写；改写后复核。
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
    (130102, N'inv-init'), (130103, N'inv-in'), (130104, N'inv-out'), (130105, N'inv-transfer'),
    (130106, N'inv-scrap'), (130107, N'inv-adjust'), (130110, N'inv-out'),
    (2817, N'inv-in'), (2818, N'inv-out'), (3901, N'inv-out');

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
    THROW 50001, N'当前快照仍残留已退役的库存异动族名，迁移中止。', 1;

/* 这些模块必须有启用的 SAVE 期目录规则，否则保存期判据就真的消失了。 */
IF EXISTS (
    SELECT 1 FROM #PortedFamily F
    WHERE NOT EXISTS (
        SELECT 1 FROM dbo.MODULE_VALIDATION_RULE R
        WHERE R.MODULE_ID = F.MODULE_ID AND R.STAGE = N'SAVE' AND R.ENABLED = 1)
)
    THROW 50002, N'有模块在退役 C# 后没有启用的 SAVE 期目录规则，迁移中止（保存期判据会消失）。', 1;

IF OBJECT_ID(N'tempdb..#PortedFamily') IS NOT NULL DROP TABLE #PortedFamily;

PRINT N'== 库存异动六族 C# 退役完成（快照族名已置空，10 个模块均有启用的 SAVE 期目录规则）==';
