-- ============================================================================
-- EOS.ERP migration 082: 停用离职工资表（180310/1803101）的 duplicate-check 实例
-- ----------------------------------------------------------------------------
-- 离职工资表的保存后行为是"先删同月旧档，再按每月每人一份判重"——删除本身就是为了让判重通过
-- （离职补发会替换同月同人的旧明细）。
-- 校验目录实例在保存路径上先于保存后行为执行，即"先判重、后删旧档"，会把本应由删除化解的
-- 情形判成重复而拦下保存（假拒绝）。故该族保留 C# 实现（HrDomainRules.HrWageAfterSaveAsync），
-- 目录实例置 ENABLED=0。
-- 幂等：仅在仍启用时更新。
-- ============================================================================

SET NOCOUNT ON;

DECLARE @GUARD_MESSAGE NVARCHAR(400) = N'本脚本只能在 EOS.ERP 数据库内执行，当前库为 ' + DB_NAME() + N'。';
IF DB_NAME() <> N'EOS.ERP'
    THROW 50000, @GUARD_MESSAGE, 1;

PRINT N'== 停用离职工资表的 duplicate-check 实例 ==';

UPDATE dbo.MODULE_VALIDATION_RULE
SET ENABLED = 0,
    REMARK = N'停用：本族保存后先删同月旧档再判重，目录校验先于删除执行会假拒绝；保留 C# 实现',
    LAST_UPDATE_BY = N'migration-082', LAST_UPDATE_DATE = SYSDATETIME()
WHERE MODULE_ID IN (180310, 1803101) AND STAGE = N'SAVE' AND VALIDATION_KEY = N'duplicate-check' AND ENABLED = 1;

SELECT CONCAT(N'  module=', MODULE_ID, N' enabled=', ENABLED) AS SUMMARY
FROM dbo.MODULE_VALIDATION_RULE WHERE MODULE_ID IN (180310, 1803101) ORDER BY MODULE_ID;

PRINT N'== 离职工资表目录实例已停用 ==';
