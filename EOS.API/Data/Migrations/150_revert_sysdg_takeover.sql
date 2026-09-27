-- ============================================================================
-- EOS.ERP migration 151: 回退 150 对模块 2305 的效果接管（该模块不在工作台运行白名单内）
-- ----------------------------------------------------------------------------
-- 事实（2026-09-18 复核）：2305（用户组管理）的 `M_URL` 是 `/admin/groups`，**不在统一表单/工作台
-- 运行白名单内**，发布定义会被 `runtime_whitelist` 校验直接拒绝（实测 REJECTED）。而 C# 领域规则的
-- 唯一派发入口是**工作台保存链**（`WorkbenchDefinitionBuilder` 把 `DomainRuleMap` 写进定义 →
-- 保存时按定义派发），效果管线同理只服务工作台模块 ⇒ **`DomainRuleMap[2305]="sysdg"` 与其 C#
-- 从未被调用过**（死登记），迁移 150 播下的 SAVE 动作与引擎开关同样不可能生效。
-- 处置：本迁移回退 150 的库内改动（`EFFECT_ENGINE_TAG` 1 → 0、删除该 SAVE 动作），把"孤儿权限行
-- 清理"的落点问题移交（建议落在权限保存路径 `RightsAdminRepository.SaveAsync`，
-- 而不是效果目录）。C# 侧的 `SysDomainRules.SysdgAfterSaveAsync` 与 `DomainRuleMap` 登记已随代码
-- 删除（删的是**死代码**，不改变运行期行为）。
-- 幂等：仅在"确实是我们播下的那条动作 + 开关为 1"时回退；重复执行无副作用。
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

DECLARE @ModuleId INT = 2305;

/* 守卫：该模块当前只应有一条 orphan-cleanup 的 SAVE 动作（我们播下的那条），否则中止人工复核 */
IF EXISTS (SELECT 1 FROM dbo.MODULE_BUSINESS_ACTION
           WHERE MODULE_ID = @ModuleId AND (EVENT_CODE <> N'SAVE' OR EFFECT_KEY <> N'orphan-cleanup' OR SEQ <> 1))
    THROW 50101, N'模块 2305 存在非本次播下的业务动作配置，迁移中止（先人工核对）。', 1;

DELETE FROM dbo.MODULE_BUSINESS_ACTION
WHERE MODULE_ID = @ModuleId AND EVENT_CODE = N'SAVE' AND SEQ = 1 AND EFFECT_KEY = N'orphan-cleanup';

UPDATE dbo.MODULES SET EFFECT_ENGINE_TAG = 0, LAST_UPDATE_BY = N'DbUp', LAST_UPDATE_DATE = SYSDATETIME()
WHERE M_IDX = @ModuleId AND ISNULL(EFFECT_ENGINE_TAG, 0) = 1;

/* 收口断言：回到接管前状态（无动作、开关关闭） */
IF EXISTS (SELECT 1 FROM dbo.MODULE_BUSINESS_ACTION WHERE MODULE_ID = @ModuleId)
    THROW 50102, N'模块 2305 仍有业务动作，回退未完成，迁移中止。', 1;

IF EXISTS (SELECT 1 FROM dbo.MODULES WHERE M_IDX = @ModuleId AND ISNULL(EFFECT_ENGINE_TAG, 0) <> 0)
    THROW 50103, N'模块 2305 的效果引擎开关未回退为 0，迁移中止。', 1;

PRINT N'== 已回退 2305 的效果接管（EFFECT_ENGINE_TAG=0、SAVE 动作已删）；该模块不在工作台白名单内，其域规则属死登记 ==';
