-- ============================================================================
-- EOS.ERP migration 222: 退役库存策略（110310）的自定义按钮「哨兵存量归位」
-- ----------------------------------------------------------------------------
-- 归位收回策略页自己管：新增独立端点 POST /admin/depot-stock-policy/{库别}/relocate
-- （预览 + 确认执行）与 DELETE 策略行，不再走单据动作框架。本迁移清掉框架侧的配置：
--     MODULE_BUSINESS_ACTION 的 MANUAL 行（EFFECT_KEY = 'relocate-sentinel'）
--     SYSDD_BUTTON / SYSDH_BUTTON 里该键的授权行（键已不存在，留着就是孤儿）
--     MODULES.MODI_URL 还原为空（回到迁移 221 之前；写路径封堵回到 7 端点全 404）
--
-- 幂等：删除与还原按合并键执行；脏标记按 MODULE_ID 合并。
-- 回滚：重跑迁移 221（按钮配置）即可恢复框架侧入口。
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
DECLARE @PolicyPage NVARCHAR(200) = N'/admin/depot-stock-policy';

IF OBJECT_ID(N'dbo.MODULE_BUSINESS_ACTION', N'U') IS NULL
    THROW 52220, N'dbo.MODULE_BUSINESS_ACTION 不存在，迁移中止。', 1;

IF OBJECT_ID(N'dbo.SYSDD_BUTTON', N'U') IS NULL OR OBJECT_ID(N'dbo.SYSDH_BUTTON', N'U') IS NULL
    THROW 52221, N'按钮授权表 SYSDD_BUTTON / SYSDH_BUTTON 不存在，请先执行迁移 218，迁移中止。', 1;

IF EXISTS (SELECT 1 FROM dbo.MODULES
           WHERE M_IDX = @Module
             AND LTRIM(RTRIM(ISNULL(MODI_URL, N''))) NOT IN (N'', @PolicyPage))
    THROW 52222, N'模块 110310 的 MODI_URL 已指向其它页面，迁移不覆盖，请先人工确认。', 1;

DELETE FROM dbo.MODULE_BUSINESS_ACTION
WHERE MODULE_ID = @Module AND EVENT_CODE = N'MANUAL' AND EFFECT_KEY = @ActionKey;

DELETE FROM dbo.SYSDD_BUTTON
WHERE M_IDX = @Module AND BUTTON_KEY = @ActionKey;

DELETE FROM dbo.SYSDH_BUTTON
WHERE M_IDX = @Module AND BUTTON_KEY = @ActionKey;

UPDATE dbo.MODULES
   SET MODI_URL = N'',
       LAST_UPDATE_BY = N'DbUp',
       LAST_UPDATE_DATE = SYSDATETIME()
 WHERE M_IDX = @Module;

MERGE dbo.WORKBENCH_MODULE_DIRTY AS D
USING (SELECT @Module AS MODULE_ID) AS S
   ON D.MODULE_ID = S.MODULE_ID
WHEN MATCHED THEN UPDATE SET D.DIRTY_TAG = 1, D.LAST_MODIFIED_BY = N'DbUp', D.LAST_MODIFIED_AT = SYSDATETIME()
WHEN NOT MATCHED THEN INSERT (MODULE_ID, DIRTY_TAG, LAST_MODIFIED_BY, LAST_MODIFIED_AT)
     VALUES (S.MODULE_ID, 1, N'DbUp', SYSDATETIME());

IF EXISTS (SELECT 1 FROM dbo.MODULE_BUSINESS_ACTION
           WHERE MODULE_ID = @Module AND EVENT_CODE = N'MANUAL' AND EFFECT_KEY = @ActionKey)
    THROW 52223, N'「哨兵存量归位」按钮行未清理干净，迁移中止。', 1;

IF EXISTS (SELECT 1 FROM dbo.SYSDD_BUTTON WHERE M_IDX = @Module AND BUTTON_KEY = @ActionKey)
    OR EXISTS (SELECT 1 FROM dbo.SYSDH_BUTTON WHERE M_IDX = @Module AND BUTTON_KEY = @ActionKey)
    THROW 52224, N'该按钮的授权行未清理干净，迁移中止。', 1;

IF NOT EXISTS (SELECT 1 FROM dbo.MODULES m WHERE m.M_IDX = @Module
                 AND LTRIM(RTRIM(ISNULL(m.MODI_URL, N''))) = N'')
    THROW 52225, N'模块 110310 的 MODI_URL 未还原为空，迁移中止。', 1;

IF NOT EXISTS (SELECT 1 FROM dbo.WORKBENCH_MODULE_DIRTY d WHERE d.MODULE_ID = @Module AND d.DIRTY_TAG = 1)
    THROW 52226, N'模块 110310 未标记为待发布，迁移中止。', 1;

PRINT N'== 已退役 110310 的「哨兵存量归位」自定义按钮（归位改走策略页独立端点）==';
GO
