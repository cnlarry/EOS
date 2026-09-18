-- ============================================================================
-- EOS.ERP migration 168: 送货单入校验目录并退役 C#（cop-send：1406）
-- ----------------------------------------------------------------------------
-- C# `CopDomainRules.CopSendAfterSaveAsync`（判据来源旧过程 `P_COP_SEND_After_Save`）此前与目录里的
-- 引用校验/不超量校验**并存**（后者已由迁移 099/101/102 等落地）；本迁移承接剩余五条判据与一段写：
--   ① 批管品必填批号（无门控，回报明细序号）；
--   ② 送货日期不得早于建立日期 30 天；
--   ③ 受 `SYSSS.SEND_TAG=1` 门控的三条：库别不存在（回报序号+库别）、出库数量不超库存
--      （**按产品单位折算**：单位一致取 1，否则取对应单位换算率）、批号出库数量不超批号库存（同折算）；
--   ④ 末段写：给"每个品号客户订单号最大的一行"打 `mo_no='showbaozhuang'` 标记。
-- 承载方式：①~③ = **校验目录的 `custom-validation`**（注册实现 `cop-send-check`，含多单位换算 CASE 与
-- 跨表聚合比较，模板表达不了；按旧顺序逐条执行、文案逐字一致）；④ = 新增服务处理器 `cop-send-mo-flag`。
-- 校验排在既有目录规则之后（SEQ=3），写入走 SAVE 期动作（本模块此前没有 SAVE 期动作）。
-- 幂等：规则按 模块+SAVE+VALIDATION_KEY+SEQ 合并；动作按 模块+SAVE+SEQ 合并；快照族名仅当仍含族名时改写。
-- 注意：PARAM_STRUCT 里的换行必须是转义的 `\r\n`（JSON 不允许裸控制字符），MESSAGE 列才用真实 CRLF。
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

DECLARE @ModuleId INT = 1406;
DECLARE @Family NVARCHAR(40) = N'cop-send';

IF NOT EXISTS (SELECT 1 FROM dbo.MODULES m WHERE m.M_IDX = @ModuleId
                 AND LTRIM(RTRIM(ISNULL(m.MASTER_TABLE, ''))) = N'COP_SEND_M'
                 AND LTRIM(RTRIM(ISNULL(m.DETAIL_TABLE, ''))) = N'COP_SEND_D')
    THROW 50001, N'模块 1406 形态不符（应为 COP_SEND_M / COP_SEND_D），迁移中止。', 1;

IF EXISTS (SELECT 1 FROM dbo.MODULE_VALIDATION_RULE
            WHERE MODULE_ID = @ModuleId AND STAGE = N'SAVE' AND VALIDATION_KEY = N'custom-validation')
    THROW 50002, N'模块 1406 已有 custom-validation 规则，迁移中止（先核对配置）。', 1;

IF EXISTS (SELECT 1 FROM dbo.MODULE_BUSINESS_ACTION WHERE MODULE_ID = @ModuleId AND EVENT_CODE = N'SAVE')
    THROW 50003, N'模块 1406 已有 SAVE 期业务动作配置，迁移中止（先核对配置）。', 1;

/* ① 五条判据（custom-validation → cop-send-check） */
DECLARE @Check NVARCHAR(MAX) =
    N'{"master":{"typeField":"SEND_TYPE","noField":"SEND_NO","dateField":"SEND_DATE","createDateField":"CREATE_DATE"},'
    + N'"detail":{"table":"COP_SEND_D","productField":"PRO_NO","qtyField":"QTY","spareQtyField":"SPARE_QTY",'
    + N'"batchField":"BATCH_NO","depotField":"DEPOT_ID","unitField":"UNIT_ID","serialField":"SERIAL_NO"},'
    + N'"product":{"table":"PRODUCT","keyField":"PRO_NO","manageBatchField":"MANAGE_BATCH","unitField":"UNIT_ID",'
    + N'"unit1Field":"UNIT_ID_1","unitRate1Field":"UNIT_RATE_1","unit2Field":"UNIT_ID_2","unitRate2Field":"UNIT_RATE_2",'
    + N'"unit3Field":"UNIT_ID_3","unitRate3Field":"UNIT_RATE_3","unit4Field":"UNIT_ID_4","unitRate4Field":"UNIT_RATE_4"},'
    + N'"depot":{"table":"DEPOT","keyField":"DEPOT_ID"},'
    + N'"stock":{"table":"INV_PRO_DEPOT","productField":"PRO_NO","depotField":"DEPOT_ID","qtyField":"QTY"},'
    + N'"batchStock":{"table":"INV_BATCH_M","batchField":"BATCH_NO","productField":"PRO_NO","inField":"IN_SUM","outField":"OUT_SUM"},'
    + N'"gateFlag":"SEND_TAG","maxDays":30,'
    + N'"messages":{"batchRequired":"以下序号项需要输入批号 \r\n","dateTooOld":"送货日期不能小于建立日期30天",'
    + N'"depotMissing":"以下库别不存在\r\n序号----库别\r\n","stockNotEnough":"库存数量不足\r\n料号---------------库别----出库数量----库存数量---不足数量\r\n",'
    + N'"batchStockNotEnough":"批号库存数量不足\r\n"}}';

DECLARE @Param NVARCHAR(MAX) = N'{"handler":"cop-send-check","check":' + @Check + N'}';

MERGE dbo.MODULE_VALIDATION_RULE AS T
USING (SELECT @ModuleId AS MODULE_ID) AS S
   ON T.MODULE_ID = S.MODULE_ID AND T.STAGE = N'SAVE'
  AND T.VALIDATION_KEY = N'custom-validation' AND T.SEQ = 3
WHEN MATCHED THEN
    UPDATE SET T.PARAM_STRUCT = @Param, T.MESSAGE = NULL, T.ENABLED = 1,
               T.REMARK = N'送货单保存期五条判据（批号必填/日期超 30 天/库别存在/库存不足/批号库存不足；原 C# 判据的忠实移植）',
               T.LAST_UPDATE_BY = N'DbUp', T.LAST_UPDATE_DATE = SYSDATETIME()
WHEN NOT MATCHED THEN
    INSERT (MODULE_ID, STAGE, SEQ, VALIDATION_KEY, ENABLED, PARAM_STRUCT, MESSAGE, REMARK, SOURCE_REF,
            CREATE_PERSON, CREATE_DATE, LAST_UPDATE_BY, LAST_UPDATE_DATE)
    VALUES (S.MODULE_ID, N'SAVE', 3, N'custom-validation', 1, @Param, NULL,
            N'送货单保存期五条判据（批号必填/日期超 30 天/库别存在/库存不足/批号库存不足；原 C# 判据的忠实移植）',
            @Family, N'DbUp', SYSDATETIME(), N'DbUp', SYSDATETIME());

/* ② 末段写：mo_no 标记 */
DECLARE @EffectParam NVARCHAR(MAX) =
    N'{"typeField":"SEND_TYPE","noField":"SEND_NO",'
    + N'"detail":{"table":"COP_SEND_D","productField":"PRO_NO","clientOrderNoField":"CLIENT_ORDER_NO",'
    + N'"flagField":"mo_no","flagValue":"showbaozhuang"}}';

MERGE dbo.MODULE_BUSINESS_ACTION AS T
USING (SELECT @ModuleId AS MODULE_ID) AS S
   ON T.MODULE_ID = S.MODULE_ID AND T.EVENT_CODE = N'SAVE' AND T.SEQ = 1
WHEN MATCHED THEN
    UPDATE SET T.EFFECT_KEY = N'cop-send-mo-flag', T.EFFECT_NAME = N'送货单包装标记（保存期）',
               T.ENABLED = 1, T.FAIL_MODE = N'BLOCK', T.PARAM_STRUCT = @EffectParam,
               T.REVERSE_STRUCT = N'{"kind":"none"}',
               T.REMARK = N'原 C# 送货单保存后动作的忠实移植（每品号客户订单号最大的一行打包装标记）',
               T.LAST_UPDATE_BY = N'DbUp', T.LAST_UPDATE_DATE = SYSDATETIME()
WHEN NOT MATCHED THEN
    INSERT (MODULE_ID, EVENT_CODE, SEQ, EFFECT_KEY, EFFECT_NAME, ENABLED, FAIL_MODE, CONDITION_STRUCT,
            PARAM_STRUCT, REVERSE_STRUCT, REMARK, SOURCE_REF, CREATE_PERSON, CREATE_DATE, LAST_UPDATE_BY, LAST_UPDATE_DATE)
    VALUES (S.MODULE_ID, N'SAVE', 1, N'cop-send-mo-flag', N'送货单包装标记（保存期）', 1, N'BLOCK', NULL,
            @EffectParam, N'{"kind":"none"}',
            N'原 C# 送货单保存后动作的忠实移植（每品号客户订单号最大的一行打包装标记）', @Family,
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

IF NOT EXISTS (SELECT 1 FROM dbo.MODULE_VALIDATION_RULE
               WHERE MODULE_ID = @ModuleId AND STAGE = N'SAVE' AND ENABLED = 1
                 AND VALIDATION_KEY = N'custom-validation' AND SEQ = 3
                 AND PARAM_STRUCT LIKE N'%"handler":"cop-send-check"%')
    THROW 50005, N'退役 C# 后缺少启用的 SAVE 期 custom-validation（cop-send-check）规则，迁移中止。', 1;

IF NOT EXISTS (SELECT 1 FROM dbo.MODULE_BUSINESS_ACTION
               WHERE MODULE_ID = @ModuleId AND EVENT_CODE = N'SAVE' AND SEQ = 1
                 AND EFFECT_KEY = N'cop-send-mo-flag' AND ENABLED = 1)
    THROW 50006, N'退役 C# 后缺少启用的 SAVE 期 cop-send-mo-flag 动作，迁移中止。', 1;

PRINT N'== cop-send 入校验目录并退役 C# 完成（1406，custom-validation → cop-send-check + cop-send-mo-flag）==';
