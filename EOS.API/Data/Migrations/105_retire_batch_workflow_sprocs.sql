-- ============================================================================
-- EOS.ERP migration 105: 下线已收口的批核旧存储过程（效果引擎/自动批核已接管）
-- ----------------------------------------------------------------------------
-- 本批 5 个过程对应的模块，批核/解批生效链均已不经过遗留过程，且影子对拍通过：
--   - P_WF_COP_ORDER  ：1405 客户订单（批核/解批差异已按新语义登记，效果引擎接管）
--   - P_WF_COP_RECEIPT：170102 收款单（账务链效果引擎接管，解批差异已登记）
--   - P_WF_PUR_PAY    ：170202 付款单（同上）
--   - P_WF_SFC_DAILY  ：180401 生产记录单（过程体为空操作，批核仅由调用方翻转 CONFIRM_TAG）
--   - P_WF_PRODUCT    ：1201 产品/料件基本资料、1311 安全库存预警（批核副作用为版次递增，
--                       受 SYSSS.PRO_EDITION_TAG 门控；开关关闭时批核/解批均为纯状态翻转，
--                       故同时把 1201 的 EFFECT_ENGINE_TAG 置 1，由效果引擎空链承载该翻转）
-- 从库中下线，源码保留于 EOS.Database SSDT 项目（dbo/Stored Procedures/ 对应 .sql）供考古。
-- 同时清理 MODULES.UPDATE_SP 与已发布快照里的 WorkflowSproc：旧过程删除后若仍保留引用，
-- 引擎对该事件无动作时会回落到不存在的过程而报错（与 072/074 同一处理）。
-- 幂等：OBJECT_ID 守卫 + EXISTS 条件；动态守卫：任何非本批对象若引用即中止。
-- 守卫用 sys.sql_expression_dependencies 精确查依赖（毫秒级），不用 sys.sql_modules.definition
-- LIKE 全表扫（本库 20 秒+，曾致 DbUp 启动超时）。
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
      AND o.name NOT IN (N'P_WF_COP_ORDER', N'P_WF_COP_RECEIPT', N'P_WF_PUR_PAY', N'P_WF_SFC_DAILY', N'P_WF_PRODUCT')
      AND d.referenced_class = 1
      AND d.referenced_schema_name = N'dbo'
      AND d.referenced_entity_name IN (N'P_WF_COP_ORDER', N'P_WF_COP_RECEIPT', N'P_WF_PUR_PAY', N'P_WF_SFC_DAILY', N'P_WF_PRODUCT')
)
    THROW 50001, N'仍有本批名单外对象引用本批旧批核过程，迁移中止（先核实引用方）。', 1;

/* 批核能力转由效果引擎承载：空效果链下批核/解批即纯状态翻转。 */
UPDATE dbo.MODULES
   SET EFFECT_ENGINE_TAG = 1,
       LAST_UPDATE_BY = N'DbUp',
       LAST_UPDATE_DATE = GETDATE()
 WHERE M_IDX = 1201
   AND ISNULL(EFFECT_ENGINE_TAG, 0) = 0;

IF OBJECT_ID(N'tempdb..#RetiredSproc') IS NOT NULL DROP TABLE #RetiredSproc;
SELECT M.M_IDX AS MODULE_ID, LTRIM(RTRIM(M.UPDATE_SP)) AS SPROC
INTO #RetiredSproc
FROM dbo.MODULES M
WHERE LTRIM(RTRIM(ISNULL(M.UPDATE_SP, N''))) IN
      (N'P_WF_COP_ORDER', N'P_WF_COP_RECEIPT', N'P_WF_PUR_PAY', N'P_WF_SFC_DAILY', N'P_WF_PRODUCT');

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

/* 1201 同步开启引擎标记，否则退役批核过程后该模块会失去批核入口。 */
UPDATE S
   SET S.DEFINITION_JSON = REPLACE(S.DEFINITION_JSON,
                                   N'"EffectEngineEnabled":false',
                                   N'"EffectEngineEnabled":true')
FROM dbo.WORKBENCH_DEFINITION_SNAPSHOT S
WHERE S.MODULE_ID = 1201
  AND S.IS_CURRENT = 1
  AND S.DEFINITION_JSON LIKE N'%"EffectEngineEnabled":false%';

UPDATE M
   SET M.UPDATE_SP = NULL,
       M.LAST_UPDATE_BY = N'DbUp',
       M.LAST_UPDATE_DATE = GETDATE()
FROM dbo.MODULES M
JOIN #RetiredSproc R ON R.MODULE_ID = M.M_IDX;

/* 引用清空后再复核一次：任何残留引用都会让批核回落到即将删除的过程。 */
IF EXISTS (
    SELECT 1 FROM dbo.MODULES M
    WHERE LTRIM(RTRIM(ISNULL(M.UPDATE_SP, N''))) IN
          (N'P_WF_COP_ORDER', N'P_WF_COP_RECEIPT', N'P_WF_PUR_PAY', N'P_WF_SFC_DAILY', N'P_WF_PRODUCT')
)
    THROW 50002, N'仍有模块引用本批旧批核过程，迁移中止。', 1;

IF OBJECT_ID(N'dbo.P_WF_COP_ORDER', N'P') IS NOT NULL DROP PROCEDURE dbo.P_WF_COP_ORDER;
IF OBJECT_ID(N'dbo.P_WF_COP_RECEIPT', N'P') IS NOT NULL DROP PROCEDURE dbo.P_WF_COP_RECEIPT;
IF OBJECT_ID(N'dbo.P_WF_PUR_PAY', N'P') IS NOT NULL DROP PROCEDURE dbo.P_WF_PUR_PAY;
IF OBJECT_ID(N'dbo.P_WF_SFC_DAILY', N'P') IS NOT NULL DROP PROCEDURE dbo.P_WF_SFC_DAILY;
IF OBJECT_ID(N'dbo.P_WF_PRODUCT', N'P') IS NOT NULL DROP PROCEDURE dbo.P_WF_PRODUCT;

IF OBJECT_ID(N'tempdb..#RetiredSproc') IS NOT NULL DROP TABLE #RetiredSproc;

PRINT N'== 批核旧存储过程下线完成（5 个），模块引用清理 ' + CAST(@CLEANED AS NVARCHAR(10)) + N' 条；1201 批核入口转由效果引擎空链承载 ==';
