-- ============================================================================
-- EOS.ERP migration 295: 修正迁移 294 的形态换算artifact（MEM_ENABLE_AUTO_DISTILL）
-- ----------------------------------------------------------------------------
--  294 在把 `EnableAutoDistill`（'true'/'false'）折成 bit（'1'/'0'）时，写成了：
--      CASE WHEN s.PARAM_VALUE IN (N'true', N'1', N'是') THEN N'1' ELSE N'0' END
--  旧表**没有这一行**（= 从未覆盖过，默认"开"）时 `s.PARAM_VALUE` 为 NULL，
--  NULL IN (...) 为 UNKNOWN ⇒ 走 ELSE ⇒ 落成 `'0'`。
--  于是"从未覆盖"被静默写成了"明确关掉"——**参数值看着像事实，实际是凭空造的**。
--
--  294 的脚本内容已就地修正（换算前先判 `s.PARAM_KEY IS NULL`），但 294 在本机已执行过，
--  DbUp 按**文件名**判重、不会重跑。因此这一条负责修已经落库的那一行。
--
--  **修得很窄**，只动"确实是 294 写出来的那一行"：
--    · OWNER_MODULE = 3105 且 PARAM_KEY = MEM_ENABLE_AUTO_DISTILL；
--    · 取值恰为 '0'（294 写错时的唯一产物）；
--    · `CREATE_PERSON = 'mig-294'`（本行由 294 建立）；
--    · `LAST_UPDATE_BY IS NULL`（此后没有人手工改过它 —— 改过就说明那是人的决定，不动）。
--  四条同时成立才清空取值（回到"未覆盖 = 用默认值"）。任何一条不成立都原样保留。
--
--  幂等：修正后再跑一次，第一条 EXISTS 已不成立，什么也不做。
-- ============================================================================
SET NOCOUNT ON;
SET XACT_ABORT ON;

IF DB_NAME() <> N'EOS.ERP'
    THROW 50000, N'本脚本只能在 EOS.ERP 数据库内执行。', 1;

DECLARE @Suspect INT = (
    SELECT COUNT(*) FROM dbo.SYSSS
    WHERE OWNER_MODULE = 3105
      AND PARAM_KEY = N'MEM_ENABLE_AUTO_DISTILL'
      AND PARAM_VALUE = N'0'
      AND CREATE_PERSON = N'mig-294'
      AND LAST_UPDATE_BY IS NULL);

IF @Suspect > 0
BEGIN
    BEGIN TRANSACTION;

    UPDATE dbo.SYSSS
    SET PARAM_VALUE = NULL
    WHERE OWNER_MODULE = 3105
      AND PARAM_KEY = N'MEM_ENABLE_AUTO_DISTILL'
      AND PARAM_VALUE = N'0'
      AND CREATE_PERSON = N'mig-294'
      AND LAST_UPDATE_BY IS NULL;

    COMMIT TRANSACTION;
    PRINT N'== 已清空 294 误写的 MEM_ENABLE_AUTO_DISTILL 覆盖值（回到"未覆盖 = 默认开启"）==';
END
ELSE
    PRINT N'== 无需修正：该参数没有 294 写出的覆盖值（或已被人工改过）==';

-- 收口断言：那一行必须还在（本迁移只清取值，不删行）
IF NOT EXISTS (SELECT 1 FROM dbo.SYSSS WHERE OWNER_MODULE = 3105 AND PARAM_KEY = N'MEM_ENABLE_AUTO_DISTILL')
    THROW 50200, N'MEM_ENABLE_AUTO_DISTILL 的参数行缺失，迁移未收口。', 1;
