-- ============================================================================
-- EOS.ERP migration 108: 退役保存侧遗留钩子过程（清 MODULES.AFTERSAVE_SP 并下线过程本体）
-- ----------------------------------------------------------------------------
-- 背景：现代保存链**从不执行** MODULES.AFTERSAVE_SP——WorkbenchDefinitionBuilder 三级回落
-- （静态规则表 → DomainRuleMap 的 C# 领域规则 → CatalogAfterSaveMap 的校验目录承接；三者皆无
-- 且钩子非空则标 SprocPendingPorting=true 直接拒存），故这 84 个模块的钩子字段与 63 个过程
-- 只是配置面遗留（与 107 的批核过程同性质）。
-- 本批同时收口定义装配的一处耦合：原实现仅在 `hasSproc || hasAutoBillNo` 分支里查
-- DomainRuleMap / CatalogAfterSaveMap，钩子字段退役后"有 C# 规则、但既无钩子又不自动编号"的
-- 模块会静默丢规则；已改为「钩子 / 自动编号 / 领域规则 / 目录承接」四者之一即装配
-- （WorkbenchDefinitionBuilder，回归守卫见 WorkbenchDefinitionPublishRebuildTests 的 110103 用例）。
-- 处理（与 107 同形）：① 清空这批模块的 MODULES.AFTERSAVE_SP；② 就地修正已发布快照的
--    `AfterSaveSproc` 为 null；③ DROP 这批过程本体（源码保留在 EOS.Database SSDT 快照供考古）。
-- 依赖守卫：任何本批名单外的库对象引用这些过程即中止；清空引用后再复核一次残留。
-- 幂等：以"当前 AFTERSAVE_SP 指向且在册"为准取名单，重复执行第二次为空操作。
-- ============================================================================

SET NOCOUNT ON;

/* WORKBENCH_DEFINITION_SNAPSHOT 上有筛选唯一索引，更新必须带正确的 SET 选项。 */
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET ANSI_PADDING ON;
SET ANSI_WARNINGS ON;
SET CONCAT_NULL_YIELDS_NULL ON;
SET ARITHABORT ON;

DECLARE @GUARD_MESSAGE NVARCHAR(400) = N'本脚本只能在 EOS.ERP 数据库内执行，当前库为 ' + DB_NAME() + N'。';
IF DB_NAME() <> N'EOS.ERP'
    THROW 50000, @GUARD_MESSAGE, 1;

IF OBJECT_ID(N'tempdb..#RetiredAfterSave') IS NOT NULL DROP TABLE #RetiredAfterSave;
SELECT DISTINCT LTRIM(RTRIM(M.AFTERSAVE_SP)) AS SPROC
INTO #RetiredAfterSave
FROM dbo.MODULES M
WHERE LTRIM(RTRIM(ISNULL(M.AFTERSAVE_SP, N''))) <> N''
  AND OBJECT_ID(N'dbo.' + LTRIM(RTRIM(M.AFTERSAVE_SP)), N'P') IS NOT NULL;

DECLARE @TARGETS INT = (SELECT COUNT(*) FROM #RetiredAfterSave);

IF EXISTS (
    SELECT 1
    FROM sys.sql_expression_dependencies d
    JOIN sys.objects o ON o.object_id = d.referencing_id
    JOIN #RetiredAfterSave R ON R.SPROC = d.referenced_entity_name
    WHERE o.type IN (N'P', N'FN', N'IF', N'TF', N'TR', N'V')
      AND o.name <> R.SPROC
      AND d.referenced_class = 1
      AND d.referenced_schema_name = N'dbo'
)
    THROW 50001, N'仍有本批名单外对象引用待退役保存后钩子过程，迁移中止（先核实引用方）。', 1;

/* ① 已发布快照与 MODULES 保持一致：保存后钩子置空。 */
UPDATE S
   SET S.DEFINITION_JSON = REPLACE(S.DEFINITION_JSON,
                                   N'"AfterSaveSproc":"' + LTRIM(RTRIM(M.AFTERSAVE_SP)) + N'"',
                                   N'"AfterSaveSproc":null')
FROM dbo.WORKBENCH_DEFINITION_SNAPSHOT S
JOIN dbo.MODULES M ON M.M_IDX = S.MODULE_ID
WHERE S.IS_CURRENT = 1
  AND LTRIM(RTRIM(ISNULL(M.AFTERSAVE_SP, N''))) <> N''
  AND S.DEFINITION_JSON LIKE N'%"AfterSaveSproc":"' + LTRIM(RTRIM(M.AFTERSAVE_SP)) + N'"%';

UPDATE M
   SET M.AFTERSAVE_SP = NULL,
       M.LAST_UPDATE_BY = N'DbUp',
       M.LAST_UPDATE_DATE = GETDATE()
FROM dbo.MODULES M
JOIN #RetiredAfterSave R ON R.SPROC = LTRIM(RTRIM(M.AFTERSAVE_SP));

/* 复核：任何残留引用都会让保存回落到即将删除的过程。 */
IF EXISTS (
    SELECT 1 FROM dbo.MODULES M JOIN #RetiredAfterSave R ON R.SPROC = LTRIM(RTRIM(ISNULL(M.AFTERSAVE_SP, N'')))
)
    THROW 50002, N'仍有模块引用本批待退役保存后钩子过程，迁移中止。', 1;

/* ③ 下线过程本体。 */
DECLARE @SPROC NVARCHAR(128), @STMT NVARCHAR(300), @DROPPED INT = 0;
DECLARE cur_retire_after CURSOR LOCAL FAST_FORWARD FOR SELECT SPROC FROM #RetiredAfterSave ORDER BY SPROC;
OPEN cur_retire_after;
FETCH NEXT FROM cur_retire_after INTO @SPROC;
WHILE @@FETCH_STATUS = 0
BEGIN
    IF OBJECT_ID(N'dbo.' + @SPROC, N'P') IS NOT NULL
    BEGIN
        SET @STMT = N'DROP PROCEDURE dbo.' + QUOTENAME(@SPROC) + N';';
        EXEC sys.sp_executesql @STMT;
        SET @DROPPED = @DROPPED + 1;
    END
    FETCH NEXT FROM cur_retire_after INTO @SPROC;
END
CLOSE cur_retire_after;
DEALLOCATE cur_retire_after;

IF OBJECT_ID(N'tempdb..#RetiredAfterSave') IS NOT NULL DROP TABLE #RetiredAfterSave;

PRINT N'== 保存侧遗留钩子过程退役完成：名单 ' + CAST(@TARGETS AS NVARCHAR(10))
    + N' 个过程，实际 DROP ' + CAST(@DROPPED AS NVARCHAR(10)) + N' 个 ==';
