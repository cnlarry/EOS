-- ============================================================================
-- EOS.ERP migration 075: 1202 商编变更单引擎标记复位（下线收尾）
-- ----------------------------------------------------------------------------
-- 背景：1202 已于 D1 迁移 072 清空悬空 UPDATE_SP 引用、D3 迁移 073 藏菜单入口
-- （M_TAG=0），用户 D7 已拍板保持下线。但 EFFECT_ENGINE_TAG 仍为 1，
-- 而模块 0 业务动作——悬空的引擎开关（与当初 2915/2916 同类）。
-- 本迁移将其复位为 0，使"下线"状态完整自洽：菜单隐藏 + 引擎关闭 + 无 SP 引用。
-- 行为等价性：EE=1 无动作时引擎本就回落遗留桥（NULL SP 下仅置 CONFIRM_TAG +
-- 审计，不报错）；EE=0 直接走同一条遗留路径，结果一致，仅消除悬空配置。
-- 可逆：恢复上线时置回 EFFECT_ENGINE_TAG=1（若重配动作）或保持 0，M_TAG=1，
-- 并重发布快照。
-- 幂等：仅当 EFFECT_ENGINE_TAG=1 时改写。
-- ============================================================================

SET NOCOUNT ON;

DECLARE @GUARD_MESSAGE NVARCHAR(400) = N'本脚本只能在 EOS.ERP 数据库内执行，当前库为 ' + DB_NAME() + N'。';
IF DB_NAME() <> N'EOS.ERP'
    THROW 50000, @GUARD_MESSAGE, 1;

BEGIN TRANSACTION;

UPDATE dbo.MODULES
   SET EFFECT_ENGINE_TAG = 0,
       LAST_UPDATE_BY = N'DbUp',
       LAST_UPDATE_DATE = GETDATE()
 WHERE M_IDX = 1202
   AND ISNULL(EFFECT_ENGINE_TAG, 0) = 1;

COMMIT;
