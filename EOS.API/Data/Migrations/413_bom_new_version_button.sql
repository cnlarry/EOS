-- ============================================================================
-- EOS.ERP migration 413: 1204 产品BOM 的「新建版本」按钮（MANUAL 动作）
-- ----------------------------------------------------------------------------
-- 背景：版次是记录身份（`(PRO_NO, EDITION)` 复合主键，迁移 409），界面上只读。
-- 要建第二版必须由服务端复制，否则前端无论怎么填都会撞主键。本迁移只挂按钮行；
-- 复制逻辑在 `BomNewVersionHandler`（键 `bom-new-version`，服务端自算版次）。
--
-- 为什么 PARAM_STRUCT 留空：处理器用常量表名/列名（1204 专用动作，与
-- `produce-calc-materials` 同族）。给它改表名的自由只会制造"配错了照样能存"的空间。
--
-- 为什么 CONFIRM_TAG=1：它会新建一条记录。确认流程走单据动作的标准两段
-- （confirm=false 探路回滚 → 用户确认 → confirm=true 执行）。
--
-- **授权不在本迁移**：按钮授权是 fail-closed 的独立表（`DocumentActionAuthorization`），
-- 配了行但没授权时按钮**不出现**在界面上（不是灰掉）。授权在 230103「自定义按钮」
-- 页面授予，与其它 MANUAL 按钮同一套流程；迁移替用户做这件事等于绕过授权。
--
-- 幂等：按 模块 + MANUAL + SEQ 合并。
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
    THROW 57900, @GUARD_MESSAGE, 1;

DECLARE @ModuleId INT = 1204;
DECLARE @Seq INT = 1;
DECLARE @Mig NVARCHAR(100) = N'DbUp';

/* ---------------------------------------------------------------- 前置 */
IF NOT EXISTS (SELECT 1 FROM dbo.MODULES
               WHERE M_IDX = @ModuleId
                 AND LTRIM(RTRIM(ISNULL(MASTER_TABLE, N''))) = N'BOM_STRU_M')
    THROW 57901, N'模块 1204 形态不符，迁移中止。', 1;

/* 版次维度必须已就位：没有 (PRO_NO, EDITION) 复合主键，"新建版本"复制出来的是同一行。 */
IF NOT EXISTS (SELECT 1 FROM sys.index_columns
               WHERE object_id = OBJECT_ID(N'dbo.BOM_STRU_M')
                 AND index_id = (SELECT unique_index_id FROM sys.key_constraints
                                 WHERE name = N'PK_BOMIA' AND parent_object_id = OBJECT_ID(N'dbo.BOM_STRU_M')))
    THROW 57902, N'BOM_STRU_M 缺少主键，迁移 409 未执行，本迁移不能先落，中止。', 1;

/* SEQ 冲突：该位置已被别的按钮占用时不覆盖，交人工裁决。 */
IF EXISTS (SELECT 1 FROM dbo.MODULE_BUSINESS_ACTION
           WHERE M_IDX = @ModuleId AND EVENT_CODE = N'MANUAL' AND SEQ = @Seq
             AND LTRIM(RTRIM(EFFECT_KEY)) <> N'bom-new-version')
    THROW 57903, N'1204 的 MANUAL seq=1 已被其它按钮占用，先人工裁决，中止。', 1;

BEGIN TRANSACTION;

MERGE dbo.MODULE_BUSINESS_ACTION AS T
USING (SELECT @ModuleId AS M_IDX) AS S
   ON T.M_IDX = S.M_IDX AND T.EVENT_CODE = N'MANUAL' AND T.SEQ = @Seq
WHEN MATCHED THEN
    UPDATE SET T.EFFECT_KEY = N'bom-new-version', T.EFFECT_NAME = N'新建BOM版本（复制当前结构）',
               T.LABEL = N'新建版本', T.ENABLED = 1, T.FAIL_MODE = N'BLOCK', T.CONFIRM_TAG = 1,
               T.PARAM_STRUCT = NULL, T.REVERSE_STRUCT = NULL,
               T.REMARK = N'复制当前版次的整棵结构到下一个版次；版次由服务端算（MAX+1，两位数字，上限 99）',
               T.LAST_UPDATE_BY = @Mig, T.LAST_UPDATE_DATE = SYSDATETIME()
WHEN NOT MATCHED THEN
    INSERT (M_IDX, EVENT_CODE, SEQ, EFFECT_KEY, EFFECT_NAME, ENABLED, FAIL_MODE, CONDITION_STRUCT,
            PARAM_STRUCT, REVERSE_STRUCT, REMARK, SOURCE_REF, LABEL, CONFIRM_TAG,
            CREATE_PERSON, CREATE_DATE, LAST_UPDATE_BY, LAST_UPDATE_DATE)
    VALUES (S.M_IDX, N'MANUAL', @Seq, N'bom-new-version', N'新建BOM版本（复制当前结构）',
            1, N'BLOCK', NULL, NULL, NULL,
            N'复制当前版次的整棵结构到下一个版次；版次由服务端算（MAX+1，两位数字，上限 99）',
            N'bom-version', N'新建版本', 1,
            @Mig, SYSDATETIME(), @Mig, SYSDATETIME());
PRINT N'[413] 1204 MANUAL seq=1 按钮已配置（bom-new-version）';

/* 按钮行进了配置，模块要重新发布才随定义下发给前端 */
MERGE dbo.WORKBENCH_MODULE_DIRTY AS T
USING (SELECT @ModuleId AS M_IDX) AS S
   ON T.M_IDX = S.M_IDX
WHEN MATCHED THEN
    UPDATE SET T.DIRTY_TAG = 1, T.LAST_MODIFIED_BY = @Mig, T.LAST_MODIFIED_AT = SYSDATETIME()
WHEN NOT MATCHED THEN
    INSERT (M_IDX, DIRTY_TAG, LAST_MODIFIED_BY, LAST_MODIFIED_AT)
    VALUES (S.M_IDX, 1, @Mig, SYSDATETIME());
PRINT N'[413] 模块 1204 已标脏（按钮随定义下发，须重新发布）';

/* ---------------------------------------------------------------- 收口断言 */
IF NOT EXISTS (SELECT 1 FROM dbo.MODULE_BUSINESS_ACTION
               WHERE M_IDX = @ModuleId AND EVENT_CODE = N'MANUAL' AND SEQ = @Seq
                 AND EFFECT_KEY = N'bom-new-version' AND ENABLED = 1 AND CONFIRM_TAG = 1)
    THROW 57910, N'新建版本按钮未启用或缺少确认标记，迁移中止。', 1;

IF NOT EXISTS (SELECT 1 FROM dbo.WORKBENCH_MODULE_DIRTY WHERE M_IDX = @ModuleId AND DIRTY_TAG = 1)
    THROW 57911, N'模块 1204 未标脏，按钮不会随定义下发，迁移中止。', 1;

COMMIT TRANSACTION;

PRINT N'== 1204 新建版本按钮已接线：复制到下一版次，版次服务端自算；授权请在 230103 自定义按钮页授予 ==';
