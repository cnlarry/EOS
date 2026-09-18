-- ============================================================================
-- EOS.ERP migration 157: 工序生产计划入效果目录并退役 C#（sfc-plan：2708）
-- ----------------------------------------------------------------------------
-- C# 侧是一条长的"明细行补全 + 五步回填"链（`SfcDomainRules.SfcPlanAfterSaveAsync`）：
--   ① 取本单明细最大序号；
--   ② 从 `SFC_PLAN_MORE ⋈ SFC_PROCESS_D` 找出尚未成行的（产品，工序）组合，按
--      `SUM(待排数量 × 单位用量 × 单人时)` 算工时并**追加明细行**（序号递增、客户取 PRODUCT.CLIENT_ID）；
--   ③ 本单明细数量清零；
--   ④ 回填 数量/生产数量/工时/单人时（工时：标准时间>0 取标准时间，否则取上式之和）；
--   ⑤ 回填工序类别（取工序主档 `SFC_PROCEDURE`）；
--   ⑥ 固定时间工序（`USE_STAND_TIME=1`）数量归一为 1；
--   ⑦ 生产号追加"待排表生产号后四位"。
-- 承接：新增服务处理器 **`sfc-plan-sync`**（六张表 + 各列名按 master/detail/more/process/processDetail/product
-- 分组声明，全部闭合并逐个校验为物理列；②需逐行 INSERT，与原实现一样在事务内读取后循环执行）。
-- 2708 此前已接管（`EFFECT_ENGINE_TAG=1`，有一条批核期 field-accumulate），故只需播 SAVE 动作 + 置空快照族名。
-- 幂等：动作按 模块+SAVE+SEQ 合并；快照族名仅当仍含 `sfc-plan` 时改写，改写后复核。
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

DECLARE @ModuleId INT = 2708;
DECLARE @Family NVARCHAR(40) = N'sfc-plan';

IF NOT EXISTS (SELECT 1 FROM dbo.MODULES m WHERE m.M_IDX = @ModuleId
                 AND LTRIM(RTRIM(ISNULL(m.MASTER_TABLE, ''))) = N'SFC_PLAN_M'
                 AND LTRIM(RTRIM(ISNULL(m.DETAIL_TABLE, ''))) = N'SFC_PLAN_D')
    THROW 50001, N'模块 2708 形态不符（应为 SFC_PLAN_M / SFC_PLAN_D），迁移中止。', 1;

IF (SELECT COUNT(*) FROM dbo.MODULE_VALIDATION_RULE WHERE MODULE_ID = @ModuleId) <> 0
    THROW 50002, N'模块 2708 已有校验规则，迁移中止（先核对配置）。', 1;

IF EXISTS (SELECT 1 FROM dbo.MODULE_BUSINESS_ACTION WHERE MODULE_ID = @ModuleId AND EVENT_CODE = N'SAVE')
    THROW 50003, N'模块 2708 已有 SAVE 期业务动作配置，迁移中止（先核对配置）。', 1;

IF EXISTS (SELECT 1 FROM dbo.MODULE_BUSINESS_ACTION WHERE MODULE_ID = @ModuleId
             AND (EVENT_CODE <> N'APPROVE_EFFECT' OR EFFECT_KEY <> N'field-accumulate'))
    THROW 50004, N'模块 2708 存在非预期的业务动作配置，迁移中止（先核对配置）。', 1;

/* ① 播种 SAVE 期动作 */
DECLARE @Params NVARCHAR(MAX) =
    N'{"master":{"table":"SFC_PLAN_M","typeField":"PLAN_TYPE","noField":"PLAN_NO"},'
    + N'"detail":{"table":"SFC_PLAN_D","productField":"PRO_NO","procedureField":"PROCEDURE_ID","serialField":"SERIAL_NO",'
    + N'"qtyField":"QTY","produceQtyField":"PRODUCE_QTY","hoursField":"HOURS","personUnitHourField":"PERSON_UNIT_HOUR",'
    + N'"procTypeField":"PROCEDURE_TYPE_ID","produceNoField":"PRODUCE_NO"},'
    + N'"more":{"table":"SFC_PLAN_MORE","qtyField":"QTY","produceQtyField":"PRODUCE_QTY","produceNoField":"PRODUCE_NO"},'
    + N'"process":{"table":"SFC_PROCEDURE","procedureField":"PROCEDURE_ID","typeField":"PROCEDURE_TYPE_ID",'
    + N'"useStandTimeField":"USE_STAND_TIME"},'
    + N'"processDetail":{"table":"SFC_PROCESS_D","productField":"PRO_NO","procedureField":"PROCEDURE_ID",'
    + N'"standardTimeField":"STANDARD_TIME","personUnitHourField":"PERSON_UNIT_HOUR","processQtyField":"PROCESS_QTY",'
    + N'"personHourUnitField":"PERSON_HOUR_UNIT"},'
    + N'"product":{"table":"PRODUCT","productField":"PRO_NO","clientField":"CLIENT_ID"}}';

MERGE dbo.MODULE_BUSINESS_ACTION AS T
USING (SELECT @ModuleId AS MODULE_ID) AS S
   ON T.MODULE_ID = S.MODULE_ID AND T.EVENT_CODE = N'SAVE' AND T.SEQ = 1
WHEN MATCHED THEN
    UPDATE SET T.EFFECT_KEY = N'sfc-plan-sync', T.EFFECT_NAME = N'计划明细补全与工时回填（保存期）',
               T.ENABLED = 1, T.FAIL_MODE = N'BLOCK', T.PARAM_STRUCT = @Params,
               T.REVERSE_STRUCT = N'{"kind":"none"}',
               T.REMARK = N'原 C# 工序生产计划保存后动作的忠实移植（明细补全 + 数量/工时/工序类别/生产号回填）',
               T.LAST_UPDATE_BY = N'DbUp', T.LAST_UPDATE_DATE = SYSDATETIME()
WHEN NOT MATCHED THEN
    INSERT (MODULE_ID, EVENT_CODE, SEQ, EFFECT_KEY, EFFECT_NAME, ENABLED, FAIL_MODE, CONDITION_STRUCT,
            PARAM_STRUCT, REVERSE_STRUCT, REMARK, SOURCE_REF, CREATE_PERSON, CREATE_DATE, LAST_UPDATE_BY, LAST_UPDATE_DATE)
    VALUES (S.MODULE_ID, N'SAVE', 1, N'sfc-plan-sync', N'计划明细补全与工时回填（保存期）', 1, N'BLOCK', NULL,
            @Params, N'{"kind":"none"}',
            N'原 C# 工序生产计划保存后动作的忠实移植（明细补全 + 数量/工时/工序类别/生产号回填）', @Family,
            N'DbUp', SYSDATETIME(), N'DbUp', SYSDATETIME());

/* ② 置空已发布快照里的族名 */
UPDATE S
   SET S.DEFINITION_JSON = REPLACE(S.DEFINITION_JSON, N'"DomainRule":"' + @Family + N'"', N'"DomainRule":null')
FROM dbo.WORKBENCH_DEFINITION_SNAPSHOT S
WHERE S.MODULE_ID = @ModuleId AND S.IS_CURRENT = 1
  AND S.DEFINITION_JSON LIKE N'%"DomainRule":"' + @Family + N'"%';

/* ③ 收口断言 */
IF EXISTS (SELECT 1 FROM dbo.WORKBENCH_DEFINITION_SNAPSHOT S
           WHERE S.MODULE_ID = @ModuleId AND S.IS_CURRENT = 1
             AND S.DEFINITION_JSON LIKE N'%"DomainRule":"' + @Family + N'"%')
    THROW 50005, N'当前快照仍残留已退役的族名，迁移中止。', 1;

IF NOT EXISTS (SELECT 1 FROM dbo.MODULE_BUSINESS_ACTION A
               WHERE A.MODULE_ID = @ModuleId AND A.EVENT_CODE = N'SAVE' AND A.SEQ = 1
                 AND A.EFFECT_KEY = N'sfc-plan-sync' AND A.ENABLED = 1)
    THROW 50006, N'退役 C# 后缺少启用的 SAVE 期 sfc-plan-sync 动作，迁移中止。', 1;

PRINT N'== sfc-plan 入效果目录并退役 C# 完成（2708，SAVE 期 sfc-plan-sync；快照族名已置空）==';
