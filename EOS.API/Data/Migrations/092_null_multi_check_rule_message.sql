-- ============================================================================
-- EOS.ERP migration 093: 多断言规则不再携带规则级文案
-- ----------------------------------------------------------------------------
-- reference-exists 的一条规则可含多条断言（如"库别不存在 / 产品不存在"），每条断言自带
-- 专属文案；规则级 MESSAGE 只是工作区里的概述。执行器按"规则级优先"取值时，第二条及之后
-- 的断言失败会报出第一条的文案（实测：130102 明细产品不存在却报"库别编号不存在"）。
--
-- 执行器已改为断言文案优先（compiled.Message ?? rule.Message）；本迁移同步清掉多断言规则的
-- 规则级文案，避免同一份配置里出现两个消息来源。
--
-- 幂等：只处理 断言数 > 1 的 reference-exists 规则。
-- ============================================================================

SET NOCOUNT ON;

DECLARE @GUARD_MESSAGE NVARCHAR(400) = N'本脚本只能在 EOS.ERP 数据库内执行，当前库为 ' + DB_NAME() + N'。';
IF DB_NAME() <> N'EOS.ERP'
    THROW 50000, @GUARD_MESSAGE, 1;

PRINT N'== 清理多断言规则的规则级文案 ==';

UPDATE r
SET r.MESSAGE = NULL,
    r.LAST_UPDATE_BY = N'migration-093',
    r.LAST_UPDATE_DATE = SYSDATETIME()
FROM dbo.MODULE_VALIDATION_RULE r
WHERE r.VALIDATION_KEY = N'reference-exists'
  AND r.MESSAGE IS NOT NULL
  AND (SELECT COUNT(*) FROM OPENJSON(r.PARAM_STRUCT, N'$.checks')) > 1;

SELECT CONCAT(N'  仍带规则级文案的多断言规则=', COUNT(*)) AS SUMMARY
FROM dbo.MODULE_VALIDATION_RULE r
WHERE r.VALIDATION_KEY = N'reference-exists'
  AND r.MESSAGE IS NOT NULL
  AND (SELECT COUNT(*) FROM OPENJSON(r.PARAM_STRUCT, N'$.checks')) > 1;

PRINT N'== 清理完成 ==';
