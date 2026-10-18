-- ============================================================================
-- EOS.ERP migration 411: 修掉 1204 两条 line-require 文案里的 {ROWS} 占位符
-- ----------------------------------------------------------------------------
-- `LineRequireCheck` 的逐行诊断是**追加**到消息末尾的（`message + "\r\n" + rows`），
-- 不做 `{ROWS}` 替换——替换只发生在 `reference-exists` / `duplicate-check` 里。
-- 所以 line-require 的 MESSAGE 写 `{ROWS}` 不会生效，用户会直接看到这几个字。
--
-- 实测口径（迁移执行时）：库内 41 条启用的 line-require 规则中，
--   · 33 条没有占位符、靠追加拿诊断行（主流约定）；
--   · 3 条写了 `{ROWS}`（其中 2 条属 1204，1 条属其它模块）。
-- 因此不能把 `LineRequireCheck` 改成 `Replace` —— 那会让 33 条规则丢掉诊断行。
-- 正确做法是把配置改回"追加"口径，本脚本只处理 1204 的两条。
--
-- 159 的 SEQ=3（底数）与 408 的 SEQ=5（损耗率）都带这个占位符，一并清掉。
-- 文案保持原意，只去掉占位符与尾部换行（否则追加后会多一个空行）。
--
-- 幂等：仅在文本仍含 {ROWS} 或尾部有多余换行时更新。
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
    THROW 57700, @GUARD_MESSAGE, 1;

DECLARE @ModuleId INT = 1204;
DECLARE @Mig NVARCHAR(100) = N'DbUp';

IF NOT EXISTS (SELECT 1 FROM dbo.MODULE_VALIDATION_RULE
               WHERE M_IDX = @ModuleId AND STAGE = N'SAVE' AND VALIDATION_KEY = N'line-require' AND SEQ = 3)
    THROW 57701, N'1204 的 line-require seq=3（底数）不存在，口径已变，迁移中止。', 1;

IF NOT EXISTS (SELECT 1 FROM dbo.MODULE_VALIDATION_RULE
               WHERE M_IDX = @ModuleId AND STAGE = N'SAVE' AND VALIDATION_KEY = N'line-require' AND SEQ = 5)
    THROW 57702, N'1204 的 line-require seq=5（损耗率）不存在，先落迁移 408，迁移中止。', 1;

BEGIN TRANSACTION;

/* 底数：原为 '以下序号项元件底数不能小于0 \r\n{ROWS}' */
UPDATE dbo.MODULE_VALIDATION_RULE
   SET MESSAGE = N'以下序号项元件底数不能小于0',
       REMARK = N'BOM 明细元件底数必须大于零（逐行回报序号；文案按"追加诊断行"口径，不用 {ROWS} 占位符）',
       LAST_UPDATE_BY = @Mig, LAST_UPDATE_DATE = SYSDATETIME()
 WHERE M_IDX = @ModuleId AND STAGE = N'SAVE' AND VALIDATION_KEY = N'line-require' AND SEQ = 3
   AND (CHARINDEX(N'{ROWS}', ISNULL(MESSAGE, N'')) > 0 OR MESSAGE LIKE N'%' + CHAR(10));
PRINT N'[411] seq=3（底数）文案清理：' + CAST(@@ROWCOUNT AS NVARCHAR(10)) + N' 行';

/* 损耗率：原为 '以下序号项损耗率必须介于 0 与 100 之间 \r\n{ROWS}' */
UPDATE dbo.MODULE_VALIDATION_RULE
   SET MESSAGE = N'以下序号项损耗率必须介于 0 与 100 之间',
       REMARK = N'BOM 明细损耗率必须在 0..100 之间（逐行回报序号；不填不校验；文案按"追加诊断行"口径）',
       LAST_UPDATE_BY = @Mig, LAST_UPDATE_DATE = SYSDATETIME()
 WHERE M_IDX = @ModuleId AND STAGE = N'SAVE' AND VALIDATION_KEY = N'line-require' AND SEQ = 5
   AND (CHARINDEX(N'{ROWS}', ISNULL(MESSAGE, N'')) > 0 OR MESSAGE LIKE N'%' + CHAR(10));
PRINT N'[411] seq=5（损耗率）文案清理：' + CAST(@@ROWCOUNT AS NVARCHAR(10)) + N' 行';

/* 收口断言：1204 的两条 line-require 文案里不得再有占位符或换行 */
IF EXISTS (SELECT 1 FROM dbo.MODULE_VALIDATION_RULE
           WHERE M_IDX = @ModuleId AND STAGE = N'SAVE' AND VALIDATION_KEY = N'line-require' AND SEQ IN (3, 5)
             AND (CHARINDEX(N'{ROWS}', ISNULL(MESSAGE, N'')) > 0 OR MESSAGE LIKE N'%' + CHAR(10)))
    THROW 57710, N'1204 的 line-require 文案仍含 {ROWS} 或换行，迁移中止。', 1;

COMMIT TRANSACTION;

PRINT N'== 1204 两条 line-require 文案已改为"追加诊断行"口径（{ROWS} 在 line-require 里不生效） ==';
