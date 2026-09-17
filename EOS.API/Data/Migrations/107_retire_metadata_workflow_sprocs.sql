-- ============================================================================
-- EOS.ERP migration 107: 退役元数据面遗留批核过程（清 MODULES.UPDATE_SP 并下线过程本体）
-- ----------------------------------------------------------------------------
-- 背景：批核生效链已全部由效果引擎接管（`EFFECT_ENGINE_TAG=1`），静态白名单
-- （`ModuleBusinessMap.WorkflowSproc`）在迁移 105/106 后已为空，但 50 个模块的
-- `MODULES.UPDATE_SP` 仍指向 40 个在册 `P_WF_*` 过程：运行期不可达（引擎分支先于遗留桥），
-- 属"白名单只减不增"的配置面未清项（见 docs/plans/ADR-012两级覆盖率报告.md §八）。
-- 处理（与 105/106 同形）：① 清空这批模块的 `MODULES.UPDATE_SP`；
-- ② 就地修正已发布快照：`WorkflowSproc` 置空，且对**没有流程定义**的模块把 `HasWorkflow` 置 false
--    （有 WFFORM 流程的模块保留 true，其批核入口走送审）；
-- ③ DROP 这批过程本体（源码保留在 EOS.Database SSDT 快照供考古）。
-- 依赖守卫：任何本批名单外的库对象引用这些过程即中止；清空引用后再复核一次残留。
-- 幂等：以"当前 UPDATE_SP 指向且在册"为准取名单，重复执行第二次为空操作。
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

IF OBJECT_ID(N'tempdb..#RetiredWF') IS NOT NULL DROP TABLE #RetiredWF;
SELECT DISTINCT LTRIM(RTRIM(M.UPDATE_SP)) AS SPROC
INTO #RetiredWF
FROM dbo.MODULES M
WHERE LTRIM(RTRIM(ISNULL(M.UPDATE_SP, N''))) <> N''
  AND OBJECT_ID(N'dbo.' + LTRIM(RTRIM(M.UPDATE_SP)), N'P') IS NOT NULL;

DECLARE @TARGETS INT = (SELECT COUNT(*) FROM #RetiredWF);

IF EXISTS (
    SELECT 1
    FROM sys.sql_expression_dependencies d
    JOIN sys.objects o ON o.object_id = d.referencing_id
    JOIN #RetiredWF R ON R.SPROC = d.referenced_entity_name
    WHERE o.type IN (N'P', N'FN', N'IF', N'TF', N'TR', N'V')
      AND o.name <> R.SPROC
      AND d.referenced_class = 1
      AND d.referenced_schema_name = N'dbo'
)
    THROW 50001, N'仍有本批名单外对象引用待退役批核过程，迁移中止（先核实引用方）。', 1;

/* ① 已发布快照与 MODULES 保持一致：批核过程置空。 */
UPDATE S
   SET S.DEFINITION_JSON = REPLACE(S.DEFINITION_JSON,
                                   N'"WorkflowSproc":"' + LTRIM(RTRIM(M.UPDATE_SP)) + N'"',
                                   N'"WorkflowSproc":null')
FROM dbo.WORKBENCH_DEFINITION_SNAPSHOT S
JOIN dbo.MODULES M ON M.M_IDX = S.MODULE_ID
WHERE S.IS_CURRENT = 1
  AND LTRIM(RTRIM(ISNULL(M.UPDATE_SP, N''))) <> N''
  AND S.DEFINITION_JSON LIKE N'%"WorkflowSproc":"' + LTRIM(RTRIM(M.UPDATE_SP)) + N'"%';

/* 无流程定义的模块：HasWorkflow 置 false（有 WFFORM 流程的模块保留，其批核走送审）。 */
UPDATE S
   SET S.DEFINITION_JSON = REPLACE(S.DEFINITION_JSON, N'"HasWorkflow":true', N'"HasWorkflow":false')
FROM dbo.WORKBENCH_DEFINITION_SNAPSHOT S
JOIN dbo.MODULES M ON M.M_IDX = S.MODULE_ID
WHERE S.IS_CURRENT = 1
  AND LTRIM(RTRIM(ISNULL(M.UPDATE_SP, N''))) <> N''
  AND S.DEFINITION_JSON LIKE N'%"HasWorkflow":true%'
  AND NOT EXISTS (SELECT 1 FROM dbo.WFFORM wf WITH (NOLOCK) WHERE wf.WF_M_IDX = M.M_IDX);

UPDATE M
   SET M.UPDATE_SP = NULL,
       M.LAST_UPDATE_BY = N'DbUp',
       M.LAST_UPDATE_DATE = GETDATE()
FROM dbo.MODULES M
JOIN #RetiredWF R ON R.SPROC = LTRIM(RTRIM(M.UPDATE_SP));

/* 复核：任何残留引用都会让批核回落到即将删除的过程。 */
IF EXISTS (
    SELECT 1 FROM dbo.MODULES M JOIN #RetiredWF R ON R.SPROC = LTRIM(RTRIM(ISNULL(M.UPDATE_SP, N'')))
)
    THROW 50002, N'仍有模块引用本批待退役批核过程，迁移中止。', 1;

/* ③ 下线过程本体。 */
DECLARE @SPROC NVARCHAR(128), @STMT NVARCHAR(300), @DROPPED INT = 0;
DECLARE cur_retire CURSOR LOCAL FAST_FORWARD FOR SELECT SPROC FROM #RetiredWF ORDER BY SPROC;
OPEN cur_retire;
FETCH NEXT FROM cur_retire INTO @SPROC;
WHILE @@FETCH_STATUS = 0
BEGIN
    IF OBJECT_ID(N'dbo.' + @SPROC, N'P') IS NOT NULL
    BEGIN
        SET @STMT = N'DROP PROCEDURE dbo.' + QUOTENAME(@SPROC) + N';';
        EXEC sys.sp_executesql @STMT;
        SET @DROPPED = @DROPPED + 1;
    END
    FETCH NEXT FROM cur_retire INTO @SPROC;
END
CLOSE cur_retire;
DEALLOCATE cur_retire;

IF OBJECT_ID(N'tempdb..#RetiredWF') IS NOT NULL DROP TABLE #RetiredWF;

PRINT N'== 元数据面遗留批核过程退役完成：名单 ' + CAST(@TARGETS AS NVARCHAR(10))
    + N' 个过程，实际 DROP ' + CAST(@DROPPED AS NVARCHAR(10)) + N' 个 ==';
