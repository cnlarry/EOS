/*
    判官（ADR-044）在 1.0.0 默认关闭：它是预览能力。
    供应商未连通时开着只会每轮记一次失败，而用户看不到任何判定结果。

    只改"没人显式改过"的行（PARAM_VALUE IS NULL 且 DEFAULT_VALUE 仍是旧默认 '1'）；
    已手动开启的账号不受影响。DEFAULT_VALUE 是"未显式设置时取的值"，
    PARAM_VALUE 才是用户/运维的显式选择。
*/

SET NOCOUNT ON;

UPDATE dbo.SYS_PARAMETER
   SET DEFAULT_VALUE = N'0',
       MODIFIED_BY = N'mig-472',
       MODIFIED_TIME = SYSDATETIME()
 WHERE OWNER_MODULE = 3105
   AND PARAM_KEY = N'JUDGE_ENABLED'
   AND PARAM_VALUE IS NULL
   AND DEFAULT_VALUE = N'1';

IF EXISTS (
    SELECT 1
      FROM dbo.SYS_PARAMETER
     WHERE OWNER_MODULE = 3105
       AND PARAM_KEY = N'JUDGE_ENABLED'
       AND PARAM_VALUE IS NULL
       AND DEFAULT_VALUE IS NOT NULL
       AND DEFAULT_VALUE <> N'0')
    THROW 51979, N'JUDGE_ENABLED 的默认值未改为 0，迁移终止。', 1;

PRINT N'== 判官默认关闭：JUDGE_ENABLED 的 DEFAULT_VALUE 已置 0（显式开启的行未改）==';
