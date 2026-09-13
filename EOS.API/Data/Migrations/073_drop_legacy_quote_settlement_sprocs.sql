-- ============================================================================
-- EOS.ERP migration 074: 下线报价族与对账族已收口模块批核旧存储过程（效果引擎已接管）
-- ----------------------------------------------------------------------------
-- 报价族与对账族的批核/解批已由效果引擎（APPROVE_EFFECT/DEAPPROVE）接管并通过影子对拍
-- （批核全 PASS；解批差异已按新语义拍板为「差异不归一」）：
--   - P_WF_COP_QUOTE  ：1404 客户报价单（客户计价表同步 + 询价单回写）
--   - P_WF_PUR_QUOTE  ：1604 厂商报价单（厂商计价表同步 + 报价参数重算）
--   - P_WF_COP_ACCOUNT：170101 客户对账单（送货/退货数量金额与结案）
--   - P_WF_PUR_DUE    ：170201 厂商对账单（收料/退料数量金额与结案）
-- 从库中下线，源码保留于 EOS.Database SSDT 项目（dbo/Stored Procedures/ 对应 .sql），
-- 移出 Build 组供考古。
-- 同时清理 MODULES.UPDATE_SP 与已发布快照里的 WorkflowSproc：旧过程删除后若仍保留引用，
-- 引擎对该事件无动作时会回落到不存在的过程而报错（与 072 同一处理）。
-- 幂等：OBJECT_ID 守卫 + EXISTS 条件；动态守卫：任何非本批对象若引用即中止。
-- 守卫用 sys.sql_expression_dependencies 精确查依赖（毫秒级），不用
-- sys.sql_modules.definition LIKE 全表扫（本库 20 秒+，曾致 DbUp 启动超时）。
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
      AND o.name NOT IN (N'P_WF_COP_QUOTE', N'P_WF_PUR_QUOTE', N'P_WF_COP_ACCOUNT', N'P_WF_PUR_DUE')
      AND d.referenced_class = 1
      AND d.referenced_schema_name = N'dbo'
      AND d.referenced_entity_name IN (N'P_WF_COP_QUOTE', N'P_WF_PUR_QUOTE', N'P_WF_COP_ACCOUNT', N'P_WF_PUR_DUE')
)
    THROW 50001, N'仍有本批名单外对象引用本批旧报价/对账批核过程，迁移中止（先核实引用方）。', 1;

IF OBJECT_ID(N'dbo.P_WF_COP_QUOTE', N'P') IS NOT NULL DROP PROCEDURE dbo.P_WF_COP_QUOTE;
IF OBJECT_ID(N'dbo.P_WF_PUR_QUOTE', N'P') IS NOT NULL DROP PROCEDURE dbo.P_WF_PUR_QUOTE;
IF OBJECT_ID(N'dbo.P_WF_COP_ACCOUNT', N'P') IS NOT NULL DROP PROCEDURE dbo.P_WF_COP_ACCOUNT;
IF OBJECT_ID(N'dbo.P_WF_PUR_DUE', N'P') IS NOT NULL DROP PROCEDURE dbo.P_WF_PUR_DUE;

/* 清理指向本批过程的模块引用，并同步置空已发布快照里的 WorkflowSproc。 */
IF OBJECT_ID(N'tempdb..#RetiredSproc') IS NOT NULL DROP TABLE #RetiredSproc;
SELECT M.M_IDX AS MODULE_ID, LTRIM(RTRIM(M.UPDATE_SP)) AS SPROC
INTO #RetiredSproc
FROM dbo.MODULES M
WHERE LTRIM(RTRIM(ISNULL(M.UPDATE_SP, N''))) IN (N'P_WF_COP_QUOTE', N'P_WF_PUR_QUOTE', N'P_WF_COP_ACCOUNT', N'P_WF_PUR_DUE');

DECLARE @CLEANED INT = (SELECT COUNT(*) FROM #RetiredSproc);

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

IF OBJECT_ID(N'tempdb..#RetiredSproc') IS NOT NULL DROP TABLE #RetiredSproc;

PRINT N'== 报价/对账批核旧存储过程（4 个）下线完成，模块引用清理 ' + CAST(@CLEANED AS NVARCHAR(10)) + N' 条 ==';
