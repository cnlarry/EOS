-- ============================================================================
-- EOS.ERP migration 078: 预收/预付单要求明细资料
-- ----------------------------------------------------------------------------
-- 预收帐款单（170103）、预付帐款单（170203）此前 DETAIL_NO_SAVE=0：提交不含明细时
-- 走"清空该单据全部明细"分支，而不是拒绝。这两张单据的明细承载业务内容（来源单据/
-- 金额/币别），只有主表即保存属不完整状态；存量数据亦无"先建主表后补明细"的痕迹
-- （COP_PREPAY_M 116 行、PUR_PREPAY_M 1718 行，无明细者均为 0 行）。
-- 因此置 DETAIL_NO_SAVE=1：无明细提交一律拒绝。
--
-- 仅改模块元数据；该字段随模块发布进入 Definition 快照，需重新发布后对保存生效。
-- 幂等：仅当仍为 0 时更新。
-- ============================================================================

SET NOCOUNT ON;

DECLARE @GUARD_MESSAGE NVARCHAR(400) = N'本脚本只能在 EOS.ERP 数据库内执行，当前库为 ' + DB_NAME() + N'。';
IF DB_NAME() <> N'EOS.ERP'
    THROW 50000, @GUARD_MESSAGE, 1;

PRINT N'== 开始收口预收/预付单的明细必填 ==';

UPDATE dbo.MODULES
SET DETAIL_NO_SAVE = 1
WHERE M_IDX IN (170103, 170203) AND DETAIL_NO_SAVE = 0;

PRINT N'-- 目标模块现状 --';
SELECT CONCAT(N'  module=', M_IDX, N' ', ISNULL(M_DESC, N''),
              N' DETAIL_NO_SAVE=', ISNULL(CONVERT(nvarchar(5), DETAIL_NO_SAVE), N'<null>')) AS SUMMARY
FROM dbo.MODULES
WHERE M_IDX IN (170103, 170203)
ORDER BY M_IDX;

PRINT N'== 预收/预付单明细必填收口完成 ==';
