-- ============================================================================
-- EOS.ERP migration 221: 库存策略（110310）的自定义按钮「哨兵存量归位」
-- ----------------------------------------------------------------------------
-- 既有实现把这一步挂在"保存策略"里：位置档位升档时若还有货记在『未指定位置』上，
-- 得先带着 relocateTo 再存一次才能由系统代搬。本迁移把它独立成一个**带参数的操作**：
--     EFFECT_KEY   = 'relocate-sentinel'（处理器见 DocumentActions/Handlers/RelocateSentinelHandler.cs）
--     CONFIRM_TAG  = 1（先返回"将会发生什么"，确认后才真做）
--     PARAM_STRUCT = {"fields":[{"key":"relocateTo",…}]}（目标库位由用户填，服务端按同一声明复核）
--
-- 为什么要把 MODI_URL 补上：单据动作的入口闸门要求"模块能新增或能编辑"
-- （HasAdd/HasEdit），110310 是自定义配置页（不在统一表单白名单里），此前 NEW_URL/MODI_URL 皆空，
-- 端点会直接 404。这里把 MODI_URL 指向它真实的承载页——这条列的含义就是"本模块的编辑页在哪"。
--
-- 幂等：动作按 (MODULE_ID, EVENT_CODE='MANUAL', SEQ) 合并；MODI_URL 按 MODULE_ID 合并；
--       脏标记按 MODULE_ID 合并。
-- 回滚：删除本迁移写入的 MANUAL 行、 MODI_URL 留原值、清脏标记。
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

DECLARE @Module INT = 110310;
DECLARE @ActionKey NVARCHAR(50) = N'relocate-sentinel';
DECLARE @Source NVARCHAR(100) = N'document-action';
DECLARE @PolicyPage NVARCHAR(200) = N'/admin/depot-stock-policy';

IF COL_LENGTH(N'dbo.MODULE_BUSINESS_ACTION', N'LABEL') IS NULL
    OR COL_LENGTH(N'dbo.MODULE_BUSINESS_ACTION', N'CONFIRM_TAG') IS NULL
    OR COL_LENGTH(N'dbo.MODULE_BUSINESS_ACTION', N'PARAM_STRUCT') IS NULL
    THROW 52210, N'MODULE_BUSINESS_ACTION 缺少 LABEL / CONFIRM_TAG / PARAM_STRUCT 列，请先执行迁移 218。', 1;

IF COL_LENGTH(N'dbo.MODULES', N'MODI_URL') IS NULL
    THROW 52211, N'MODULES 缺少 MODI_URL 列，无法登记本模块的编辑承载页，迁移中止。', 1;

IF NOT EXISTS (SELECT 1 FROM dbo.MODULES WHERE M_IDX = @Module
                 AND LTRIM(RTRIM(ISNULL(MASTER_TABLE, N''))) = N'DEPOT_STOCK_POLICY')
    THROW 52212, N'模块 110310 形态不符（应为 DEPOT_STOCK_POLICY），迁移中止。', 1;

IF NOT EXISTS (SELECT 1 FROM dbo.INV_PRO_DEPOT WHERE 1 = 1)  -- 库存余额表必须存在（归位改的就是它）
    THROW 52213, N'INV_PRO_DEPOT 不可读，无法执行哨兵行归位，迁移中止。', 1;

IF EXISTS (SELECT 1 FROM dbo.MODULE_BUSINESS_ACTION
           WHERE MODULE_ID = @Module AND EVENT_CODE = N'MANUAL' AND SEQ = 1
             AND ISNULL(SOURCE_REF, N'') <> @Source)
    THROW 52214, N'模块 110310 的 MANUAL 顺序号 1 已被其它按钮占用，迁移中止。', 1;

IF EXISTS (SELECT 1 FROM dbo.MODULES
           WHERE M_IDX = @Module
             AND LTRIM(RTRIM(ISNULL(MODI_URL, N''))) NOT IN (N'', @PolicyPage))
    THROW 52215, N'模块 110310 的 MODI_URL 已指向其它页面，迁移不覆盖，请先人工确认。', 1;

DECLARE @Params NVARCHAR(MAX) =
    N'{"fields":[{"key":"relocateTo","label":"目标库位","type":"string","required":true,"maxLength":50}]}';

UPDATE dbo.MODULES
   SET MODI_URL = @PolicyPage,
       LAST_UPDATE_BY = N'DbUp',
       LAST_UPDATE_DATE = SYSDATETIME()
 WHERE M_IDX = @Module;

MERGE dbo.MODULE_BUSINESS_ACTION AS T
USING (SELECT @Module AS MODULE_ID, 1 AS SEQ) AS S
   ON T.MODULE_ID = S.MODULE_ID AND T.EVENT_CODE = N'MANUAL' AND T.SEQ = S.SEQ
WHEN MATCHED THEN
    UPDATE SET T.EFFECT_KEY = @ActionKey, T.EFFECT_NAME = N'哨兵存量归位',
               T.LABEL = N'哨兵存量归位', T.CONFIRM_TAG = 1,
               T.ENABLED = 1, T.FAIL_MODE = N'BLOCK',
               T.CONDITION_STRUCT = NULL, T.PARAM_STRUCT = @Params, T.REVERSE_STRUCT = NULL,
               T.REMARK = N'用户在库存策略上点击：把记在『未指定位置』上的存量整批改记到指定库位（库别总量不变）',
               T.SOURCE_REF = @Source,
               T.LAST_UPDATE_BY = N'DbUp', T.LAST_UPDATE_DATE = SYSDATETIME()
WHEN NOT MATCHED THEN
    INSERT (MODULE_ID, EVENT_CODE, SEQ, EFFECT_KEY, EFFECT_NAME, LABEL, CONFIRM_TAG,
            ENABLED, FAIL_MODE, CONDITION_STRUCT, PARAM_STRUCT, REVERSE_STRUCT, REMARK, SOURCE_REF,
            CREATE_PERSON, CREATE_DATE, LAST_UPDATE_BY, LAST_UPDATE_DATE)
    VALUES (S.MODULE_ID, N'MANUAL', S.SEQ, @ActionKey, N'哨兵存量归位', N'哨兵存量归位', 1,
            1, N'BLOCK', NULL, @Params, NULL,
            N'用户在库存策略上点击：把记在『未指定位置』上的存量整批改记到指定库位（库别总量不变）', @Source,
            N'DbUp', SYSDATETIME(), N'DbUp', SYSDATETIME());

MERGE dbo.WORKBENCH_MODULE_DIRTY AS D
USING (SELECT @Module AS MODULE_ID) AS S
   ON D.MODULE_ID = S.MODULE_ID
WHEN MATCHED THEN UPDATE SET D.DIRTY_TAG = 1, D.LAST_MODIFIED_BY = N'DbUp', D.LAST_MODIFIED_AT = SYSDATETIME()
WHEN NOT MATCHED THEN INSERT (MODULE_ID, DIRTY_TAG, LAST_MODIFIED_BY, LAST_MODIFIED_AT)
    VALUES (S.MODULE_ID, 1, N'DbUp', SYSDATETIME());

IF NOT EXISTS (SELECT 1 FROM dbo.MODULE_BUSINESS_ACTION a
               WHERE a.MODULE_ID = @Module AND a.EVENT_CODE = N'MANUAL' AND a.SEQ = 1
                 AND a.EFFECT_KEY = @ActionKey AND a.ENABLED = 1 AND a.CONFIRM_TAG = 1
                 AND a.LABEL = N'哨兵存量归位' AND a.FAIL_MODE = N'BLOCK'
                 AND CHARINDEX(N'"key":"relocateTo"', ISNULL(a.PARAM_STRUCT, N'')) > 0)
    THROW 52216, N'「哨兵存量归位」按钮未配置成功，迁移中止。', 1;

IF NOT EXISTS (SELECT 1 FROM dbo.MODULES m WHERE m.M_IDX = @Module
                 AND LTRIM(RTRIM(ISNULL(m.MODI_URL, N''))) = @PolicyPage)
    THROW 52217, N'模块 110310 的编辑承载页未登记，迁移中止。', 1;

IF NOT EXISTS (SELECT 1 FROM dbo.WORKBENCH_MODULE_DIRTY d WHERE d.MODULE_ID = @Module AND d.DIRTY_TAG = 1)
    THROW 52218, N'模块 110310 未标记为待发布，迁移中止。', 1;

PRINT N'== 已为 110310 配上「哨兵存量归位」自定义按钮（需重发布该模块快照后生效）==';
GO
