-- ============================================================================
-- EOS.ERP migration 110: 退役空实现的模具领域规则族（2903 mou-apply / 2904 mou-accept）
-- ----------------------------------------------------------------------------
-- 背景：`DomainRuleMap` 里 2903 模具申请 / 2904 模具承认 两族在 C# 侧都指向
-- `MouDomainRules.MouldNoopAfterSaveAsync`——无条件返回成功、既无校验也无写入，属历史占位。
-- 处置：从 `DomainRuleMap` 与 `DomainRuleService` 分派中删除该族（连带删除空实现方法）。
-- 本迁移就地修正已发布快照里的族名，使运行期不再分派到已删族（与 105/107 同法；快照内容与
-- 重发布一致 ⇒ 按决策 #109 复用版本，既不升版本也不使对拍证据降级）。
-- 幂等：仅当当前快照仍含该族名时改写；改写后复核。
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

IF OBJECT_ID(N'tempdb..#NoopFamily') IS NOT NULL DROP TABLE #NoopFamily;
CREATE TABLE #NoopFamily (MODULE_ID INT PRIMARY KEY, FAMILY NVARCHAR(40) NOT NULL);
INSERT INTO #NoopFamily (MODULE_ID, FAMILY) VALUES (2903, N'mou-apply'), (2904, N'mou-accept');

UPDATE S
   SET S.DEFINITION_JSON = REPLACE(S.DEFINITION_JSON,
                                   N'"DomainRule":"' + F.FAMILY + N'"',
                                   N'"DomainRule":null')
FROM dbo.WORKBENCH_DEFINITION_SNAPSHOT S
JOIN #NoopFamily F ON F.MODULE_ID = S.MODULE_ID
WHERE S.IS_CURRENT = 1
  AND S.DEFINITION_JSON LIKE N'%"DomainRule":"' + F.FAMILY + N'"%';

IF EXISTS (
    SELECT 1 FROM dbo.WORKBENCH_DEFINITION_SNAPSHOT S
    JOIN #NoopFamily F ON F.MODULE_ID = S.MODULE_ID
    WHERE S.IS_CURRENT = 1 AND S.DEFINITION_JSON LIKE N'%"DomainRule":"' + F.FAMILY + N'"%'
)
    THROW 50001, N'当前快照仍残留已退役的模具空实现族名，迁移中止。', 1;

IF OBJECT_ID(N'tempdb..#NoopFamily') IS NOT NULL DROP TABLE #NoopFamily;

PRINT N'== 2903/2904 空实现领域规则族退役完成（快照族名已置空）==';
