-- ============================================================================
-- EOS.ERP migration 193: 送货单库存校验启用位置维度（cop-send-check）
-- ----------------------------------------------------------------------------
-- 背景：库存余额表在 里已扩为 (料号, 库别, 库位, 批次) 四键，但送货单保存期的
-- 「出库数量不超库存」判据仍只按 (料号, 库别) 汇总 —— 库位管理启用后，库别合计足够
-- 并不代表**指定的那个库位**有货，校验会失去管控意义。
--
-- 本迁移只做一件事：把模块 1406 的校验描述符补上可选维度键
--   check.detail.locationField = LOCATION_NO
--   check.stock.locationField  = LOCATION_NO
-- 位置键必须成对配置（生成器在只配一边时直接报配置错误）。
-- **批次维度不启用**：批号库存已由同一描述符的 batchStock（INV_BATCH_M）单独判据覆盖，
-- 再叠加会让同一件事报两遍。
--
-- 文案调整：维度启用后该列展示「库别/库位」，因此 stockNotEnough 的表头同步由
-- 「库别」改为「库别/库位」。这是本迁移唯一一处用户可见文案变化，与启用维度同时发生，
-- 不是顺带改动。
--
-- 对现有数据的影响：核对确认 COP_SEND_D 25,456 行**全部未填位置**、余额表 1,690 行
-- **全部落在哨兵位置** ⇒ 两侧都归到哨兵，行为与改造前完全一致（见 的等价性断言）。
--
-- 注意：运行时读的是**已发布快照**（EffectPlanLoader 解析 DEFINITION_JSON），不是
-- MODULE_VALIDATION_RULE。因此本迁移额外把模块 1406 标记为「脏」，提示必须重新发布；
-- 未重发布前后端行为不变。
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
    THROW 52000, @GUARD_MESSAGE, 1;

DECLARE @ModuleId INT = 1406;

IF NOT EXISTS (SELECT 1 FROM dbo.MODULE_VALIDATION_RULE
                WHERE MODULE_ID = @ModuleId AND STAGE = N'SAVE' AND VALIDATION_KEY = N'custom-validation'
                  AND PARAM_STRUCT LIKE N'%"handler":"cop-send-check"%')
    THROW 52001, N'模块 1406 缺少 cop-send-check 的 SAVE 期校验配置，迁移中止。', 1;

DECLARE @Param NVARCHAR(MAX) = (
    SELECT PARAM_STRUCT FROM dbo.MODULE_VALIDATION_RULE
     WHERE MODULE_ID = @ModuleId AND STAGE = N'SAVE' AND VALIDATION_KEY = N'custom-validation');

IF @Param IS NULL
    THROW 52002, N'模块 1406 的 cop-send-check 参数为空，迁移中止。', 1;

/* 文本必须携带**真实**的 CR/LF：JSON_MODIFY 会把它编码成 \r\n；
   若这里写反斜杠字面量，落库会变成 \\r\\n，前端就会原样显示出转义符。 */
DECLARE @StockHeader NVARCHAR(200) =
    N'库存数量不足' + NCHAR(13) + NCHAR(10)
    + N'料号---------------库别/库位----出库数量----库存数量---不足数量' + NCHAR(13) + NCHAR(10);

SET @Param = JSON_MODIFY(@Param, '$.check.detail.locationField', N'LOCATION_NO');
SET @Param = JSON_MODIFY(@Param, '$.check.stock.locationField', N'LOCATION_NO');
SET @Param = JSON_MODIFY(@Param, '$.check.messages.stockNotEnough', @StockHeader);

UPDATE dbo.MODULE_VALIDATION_RULE
   SET PARAM_STRUCT = @Param,
       REMARK = N'送货单保存期五条判据（批号必填/日期超 30 天/库别存在/库存不足/批号库存不足）；'
              + N'库存充足性已启用位置维度（detail/stock 的 locationField=LOCATION_NO）',
       LAST_UPDATE_BY = N'DbUp', LAST_UPDATE_DATE = SYSDATETIME()
 WHERE MODULE_ID = @ModuleId AND STAGE = N'SAVE' AND VALIDATION_KEY = N'custom-validation';

/* 运行时读已发布快照，标记为「脏」提示需要重新发布 */
MERGE dbo.WORKBENCH_MODULE_DIRTY AS t
USING (SELECT @ModuleId AS MODULE_ID) AS s ON t.MODULE_ID = s.MODULE_ID
WHEN MATCHED THEN UPDATE SET DIRTY_TAG = 1, LAST_MODIFIED_BY = N'DbUp', LAST_MODIFIED_AT = SYSDATETIME()
WHEN NOT MATCHED THEN INSERT (MODULE_ID, DIRTY_TAG, LAST_MODIFIED_BY, LAST_MODIFIED_AT)
    VALUES (@ModuleId, 1, N'DbUp', SYSDATETIME());

/* ---------- 收口断言 ---------- */
DECLARE @Stored NVARCHAR(MAX) = (
    SELECT PARAM_STRUCT FROM dbo.MODULE_VALIDATION_RULE
     WHERE MODULE_ID = @ModuleId AND STAGE = N'SAVE' AND VALIDATION_KEY = N'custom-validation');

IF JSON_VALUE(@Stored, '$.check.detail.locationField') <> N'LOCATION_NO'
   OR JSON_VALUE(@Stored, '$.check.stock.locationField') <> N'LOCATION_NO'
    THROW 52003, N'位置维度键未按预期写入，迁移中止。', 1;

IF JSON_VALUE(@Stored, '$.check.stock.batchField') IS NOT NULL
    THROW 52004, N'批次维度不应被本迁移启用，迁移中止。', 1;

-- 消息仍带真实换行（JSON_VALUE 解出的是解码后的文本）
IF CHARINDEX(NCHAR(13), JSON_VALUE(@Stored, '$.check.messages.stockNotEnough')) = 0
    THROW 52005, N'库存不足文案丢失了换行，迁移中止。', 1;

IF CHARINDEX(N'库别/库位', JSON_VALUE(@Stored, '$.check.messages.stockNotEnough')) = 0
    THROW 52006, N'库存不足文案表头未更新，迁移中止。', 1;

-- 原有判据必须完好（JSON_MODIFY 只动了三个路径）
IF JSON_VALUE(@Stored, '$.check.gateFlag') <> N'SEND_TAG'
   OR JSON_VALUE(@Stored, '$.check.master.noField') <> N'SEND_NO'
   OR JSON_VALUE(@Stored, '$.check.stock.table') <> N'INV_PRO_DEPOT'
    THROW 52007, N'描述符的既有内容被破坏，迁移中止。', 1;

IF NOT EXISTS (SELECT 1 FROM dbo.WORKBENCH_MODULE_DIRTY WHERE MODULE_ID = @ModuleId AND DIRTY_TAG = 1)
    THROW 52008, N'模块 1406 未标记为待发布，迁移中止。', 1;

PRINT N'== 送货单（1406）库存校验已启用位置维度，模块已标记为待发布 ==';
