-- ============================================================================
-- EOS.ERP migration 154: 加工备案手册入效果目录并退役 C#（cus-manual：3006）
-- ----------------------------------------------------------------------------
-- C# 侧四步（`CusDomainRules.CusManualAfterSaveAsync`）：
--   ① UPDATE CUS_MANUAL_PRO SET USE_STATE=0 WHERE MANUAL_NO=@No;
--   ② UPDATE CUS_MANUAL_PRO SET USE_STATE=1 WHERE MANUAL_NO=@No
--        AND EXISTS (SELECT 1 FROM CUS_MANUAL_BOM WHERE SERIAL_NO=CUS_MANUAL_PRO.SERIAL_NO AND MANUAL_NO=@No);
--   ③ UPDATE p SET p.PROCESS_PRICE=m.PROCESS_PRICE, p.PROCESS_AMOUNT=p.PROCESS_PRICE*p.CUS_QTY
--        FROM CUS_MANUAL_PRO p JOIN CUS_MANUAL_M m ON m.MANUAL_NO=p.MANUAL_NO WHERE p.MANUAL_NO=@No;
--   ④ UPDATE m SET m.PROCESS_AMOUNT=ROUND(SUM(...),2), m.CUS_QTY=ROUND(SUM(...),2) FROM 明细按单号分组;
-- 承接：新增服务处理器 **`detail-flag-and-rollup`**（两表 + BOM 表 + 八个列名 + 舍入位数，
-- 全闭合参数并逐个校验为物理列；主键值只作参数传入）。③ 的"金额＝原单价×数量"由**单条 UPDATE**
-- 复刻（SQL Server 的 SET 右值取更新前行值，与原实现一致）。
-- **本迁移把 3006 接进效果引擎**（`EFFECT_ENGINE_TAG` 0 → 1）：该模块此前未接管、目录里既无校验
-- 规则也无业务动作；守卫断言接管前为空配置。
-- 幂等：动作按 模块+SAVE+SEQ 合并；快照族名仅当仍含 `cus-manual` 时改写，改写后复核。
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

DECLARE @ModuleId INT = 3006;
DECLARE @Family NVARCHAR(40) = N'cus-manual';

IF NOT EXISTS (SELECT 1 FROM dbo.MODULES m WHERE m.M_IDX = @ModuleId
                 AND LTRIM(RTRIM(ISNULL(m.MASTER_TABLE, ''))) = N'CUS_MANUAL_M'
                 AND LTRIM(RTRIM(ISNULL(m.DETAIL_TABLE, ''))) = N'CUS_MANUAL_PRO')
    THROW 50001, N'模块 3006 形态不符（应为 CUS_MANUAL_M / CUS_MANUAL_PRO），迁移中止。', 1;

IF (SELECT COUNT(*) FROM dbo.MODULE_VALIDATION_RULE WHERE MODULE_ID = @ModuleId) <> 0
    THROW 50002, N'模块 3006 已有校验规则，迁移中止（先核对配置）。', 1;

IF (SELECT COUNT(*) FROM dbo.MODULE_BUSINESS_ACTION WHERE MODULE_ID = @ModuleId) <> 0
    THROW 50003, N'模块 3006 已有业务动作配置，迁移中止（先核对配置）。', 1;

/* ① 接管 */
UPDATE dbo.MODULES SET EFFECT_ENGINE_TAG = 1, LAST_UPDATE_BY = N'DbUp', LAST_UPDATE_DATE = SYSDATETIME()
WHERE M_IDX = @ModuleId AND ISNULL(EFFECT_ENGINE_TAG, 0) = 0;

/* ② 播种 SAVE 期动作 */
DECLARE @Params NVARCHAR(MAX) =
    N'{"masterTable":"CUS_MANUAL_M","detailTable":"CUS_MANUAL_PRO","keyField":"MANUAL_NO",'
    + N'"bomTable":"CUS_MANUAL_BOM","bomKeyField":"MANUAL_NO","bomSerialField":"SERIAL_NO",'
    + N'"detailSerialField":"SERIAL_NO","stateField":"USE_STATE","priceField":"PROCESS_PRICE",'
    + N'"amountField":"PROCESS_AMOUNT","qtyField":"CUS_QTY","roundDigits":2}';

MERGE dbo.MODULE_BUSINESS_ACTION AS T
USING (SELECT @ModuleId AS MODULE_ID) AS S
   ON T.MODULE_ID = S.MODULE_ID AND T.EVENT_CODE = N'SAVE' AND T.SEQ = 1
WHEN MATCHED THEN
    UPDATE SET T.EFFECT_KEY = N'detail-flag-and-rollup', T.EFFECT_NAME = N'成品单耗状态与加工金额汇总（保存期）',
               T.ENABLED = 1, T.FAIL_MODE = N'BLOCK', T.PARAM_STRUCT = @Params,
               T.REVERSE_STRUCT = N'{"kind":"none"}',
               T.REMARK = N'原 C# 加工备案手册保存后动作的忠实移植（可用标记 + 主表价格下发 + 明细金额回算 + 主表汇总）',
               T.LAST_UPDATE_BY = N'DbUp', T.LAST_UPDATE_DATE = SYSDATETIME()
WHEN NOT MATCHED THEN
    INSERT (MODULE_ID, EVENT_CODE, SEQ, EFFECT_KEY, EFFECT_NAME, ENABLED, FAIL_MODE, CONDITION_STRUCT,
            PARAM_STRUCT, REVERSE_STRUCT, REMARK, SOURCE_REF, CREATE_PERSON, CREATE_DATE, LAST_UPDATE_BY, LAST_UPDATE_DATE)
    VALUES (S.MODULE_ID, N'SAVE', 1, N'detail-flag-and-rollup', N'成品单耗状态与加工金额汇总（保存期）', 1, N'BLOCK', NULL,
            @Params, N'{"kind":"none"}',
            N'原 C# 加工备案手册保存后动作的忠实移植（可用标记 + 主表价格下发 + 明细金额回算 + 主表汇总）', @Family,
            N'DbUp', SYSDATETIME(), N'DbUp', SYSDATETIME());

/* ③ 置空已发布快照里的族名 */
UPDATE S
   SET S.DEFINITION_JSON = REPLACE(S.DEFINITION_JSON, N'"DomainRule":"' + @Family + N'"', N'"DomainRule":null')
FROM dbo.WORKBENCH_DEFINITION_SNAPSHOT S
WHERE S.MODULE_ID = @ModuleId AND S.IS_CURRENT = 1
  AND S.DEFINITION_JSON LIKE N'%"DomainRule":"' + @Family + N'"%';

/* ④ 收口断言 */
IF EXISTS (SELECT 1 FROM dbo.WORKBENCH_DEFINITION_SNAPSHOT S
           WHERE S.MODULE_ID = @ModuleId AND S.IS_CURRENT = 1
             AND S.DEFINITION_JSON LIKE N'%"DomainRule":"' + @Family + N'"%')
    THROW 50004, N'当前快照仍残留已退役的族名，迁移中止。', 1;

IF NOT EXISTS (SELECT 1 FROM dbo.MODULE_BUSINESS_ACTION A
               WHERE A.MODULE_ID = @ModuleId AND A.EVENT_CODE = N'SAVE' AND A.SEQ = 1
                 AND A.EFFECT_KEY = N'detail-flag-and-rollup' AND A.ENABLED = 1)
    THROW 50005, N'退役 C# 后缺少启用的 SAVE 期 detail-flag-and-rollup 动作，迁移中止。', 1;

IF NOT EXISTS (SELECT 1 FROM dbo.MODULES WHERE M_IDX = @ModuleId AND ISNULL(EFFECT_ENGINE_TAG, 0) = 1)
    THROW 50006, N'模块 3006 未接管进效果引擎（EFFECT_ENGINE_TAG 仍为 0），迁移中止。', 1;

PRINT N'== cus-manual 入效果目录并退役 C# 完成（3006，SAVE 期 detail-flag-and-rollup；快照族名已置空）==';
