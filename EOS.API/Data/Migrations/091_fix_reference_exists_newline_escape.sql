-- ============================================================================
-- EOS.ERP migration 092: 修复 091 播种的 PARAM_STRUCT 里的裸换行
-- ----------------------------------------------------------------------------
-- 091 首次执行时把消息里的换行写成了真实 CRLF，而 JSON 字符串内不允许出现裸控制字符，
-- 导致那 37 行 PARAM_STRUCT 不是合法 JSON（ISJSON = 0），装载时会被判为配置错误。
-- 091 文件本身已改为写转义序列 \r\n；本迁移负责修复"已经跑过旧版 091"的库。
--
-- 幂等：只处理仍非法（ISJSON = 0）的 reference-exists 行；修好后重跑不再命中。
-- 仅影响换行：PARAM_STRUCT 中出现的 CRLF 只可能来自消息文本。
-- ============================================================================

SET NOCOUNT ON;

DECLARE @GUARD_MESSAGE NVARCHAR(400) = N'本脚本只能在 EOS.ERP 数据库内执行，当前库为 ' + DB_NAME() + N'。';
IF DB_NAME() <> N'EOS.ERP'
    THROW 50000, @GUARD_MESSAGE, 1;

PRINT N'== 修复 reference-exists 的非法 JSON 参数 ==';

UPDATE dbo.MODULE_VALIDATION_RULE
SET PARAM_STRUCT = REPLACE(PARAM_STRUCT, NCHAR(13) + NCHAR(10), N'\r\n'),
    LAST_UPDATE_BY = N'migration-092',
    LAST_UPDATE_DATE = SYSDATETIME()
WHERE VALIDATION_KEY = N'reference-exists'
  AND ISJSON(PARAM_STRUCT) = 0;

SELECT CONCAT(N'  剩余非法 JSON 行=', COUNT(*)) AS SUMMARY
FROM dbo.MODULE_VALIDATION_RULE
WHERE VALIDATION_KEY = N'reference-exists' AND ISJSON(PARAM_STRUCT) = 0;

PRINT N'== 修复完成 ==';
