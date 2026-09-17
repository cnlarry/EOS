-- ============================================================================
-- EOS.ERP migration 106: 下线采购单批核旧存储过程 P_WF_PUR_PURCHASE（效果引擎已接管）
-- ----------------------------------------------------------------------------
-- 1606 采购单的批核/解批已由效果链（APPROVE_EFFECT 8 步 / DEAPPROVE 反向）接管，双路对拍
-- 通过（`shadow-1606-20260917-019/020`：批核 3 处、解批 2 处差异全部为已登记口径差——
-- ADR §16.6 信用额度方向与 ADR §11.3「旧 NULL 累加失效」按 coalesce 修正）。
-- 从库中下线，源码保留于 EOS.Database SSDT 项目（dbo/Stored Procedures/P_WF_PUR_PURCHASE.sql）供考古。
-- 同时清理 MODULES.UPDATE_SP 与已发布快照里的 WorkflowSproc：旧过程删除后若仍保留引用，
-- 引擎对该事件无动作时会回落到不存在的过程而报错（与 072/074/105 同一处理）。
-- 本迁移之后，批核侧遗留过程白名单（ModuleBusinessMap.WorkflowSproc）为空。
-- 幂等：OBJECT_ID 守卫 + EXISTS 条件；动态守卫：任何非本目标对象若引用即中止。
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

IF EXISTS (
    SELECT 1
    FROM sys.sql_expression_dependencies d
    JOIN sys.objects o ON o.object_id = d.referencing_id
    WHERE o.type IN (N'P', N'FN', N'IF', N'TF', N'TR', N'V')
      AND o.name <> N'P_WF_PUR_PURCHASE'
      AND d.referenced_class = 1
      AND d.referenced_schema_name = N'dbo'
      AND d.referenced_entity_name = N'P_WF_PUR_PURCHASE'
)
    THROW 50001, N'仍有本目标名单外对象引用 P_WF_PUR_PURCHASE，迁移中止（先核实引用方）。', 1;

IF OBJECT_ID(N'tempdb..#RetiredSproc') IS NOT NULL DROP TABLE #RetiredSproc;
SELECT M.M_IDX AS MODULE_ID, LTRIM(RTRIM(M.UPDATE_SP)) AS SPROC
INTO #RetiredSproc
FROM dbo.MODULES M
WHERE LTRIM(RTRIM(ISNULL(M.UPDATE_SP, N''))) = N'P_WF_PUR_PURCHASE';

DECLARE @CLEANED INT = (SELECT COUNT(*) FROM #RetiredSproc);

/* 已发布快照里的批核过程名与 MODULES 保持一致置空，避免运行时仍回落到旧过程。 */
UPDATE S
   SET S.DEFINITION_JSON = REPLACE(S.DEFINITION_JSON,
                                   N'"WorkflowSproc":"' + R.SPROC + N'"',
                                   N'"WorkflowSproc":null')
FROM dbo.WORKBENCH_DEFINITION_SNAPSHOT S
JOIN #RetiredSproc R ON R.MODULE_ID = S.MODULE_ID
WHERE S.IS_CURRENT = 1
  AND S.DEFINITION_JSON LIKE N'%"WorkflowSproc":"' + R.SPROC + N'"%';

UPDATE M
   SET M.UPDATE_SP = NULL,
       M.LAST_UPDATE_BY = N'DbUp',
       M.LAST_UPDATE_DATE = GETDATE()
FROM dbo.MODULES M
JOIN #RetiredSproc R ON R.MODULE_ID = M.M_IDX;

/* 引用清空后再复核一次：任何残留引用都会让批核回落到即将删除的过程。 */
IF EXISTS (
    SELECT 1 FROM dbo.MODULES M
    WHERE LTRIM(RTRIM(ISNULL(M.UPDATE_SP, N''))) = N'P_WF_PUR_PURCHASE'
)
    THROW 50002, N'仍有模块引用 P_WF_PUR_PURCHASE，迁移中止。', 1;

IF OBJECT_ID(N'dbo.P_WF_PUR_PURCHASE', N'P') IS NOT NULL DROP PROCEDURE dbo.P_WF_PUR_PURCHASE;

IF OBJECT_ID(N'tempdb..#RetiredSproc') IS NOT NULL DROP TABLE #RetiredSproc;

PRINT N'== 采购单批核旧存储过程下线完成，模块引用清理 ' + CAST(@CLEANED AS NVARCHAR(10)) + N' 条 ==';
