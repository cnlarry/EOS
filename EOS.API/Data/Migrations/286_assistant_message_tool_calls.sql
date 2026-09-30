-- ============================================================================
-- EOS.ERP migration 286: 补 ASSISTANT_MESSAGE.TOOL_CALLS_JSON（工具调用摘要列）
-- ----------------------------------------------------------------------------
-- 现象：工作助手**只要一轮回答用到工具**，收尾就抛
--   `列名 'TOOL_CALLS_JSON' 无效`（`AssistantRepository.UpdateToolCallsJsonAsync`），
--   而该调用位于 `ChatService` 发 `done` 事件**之前**且未捕获 ⇒ SSE 在 done 之前断流：
--   前端既拿不到工具摘要，也拿不到"回复已落库"那一帧（回答看着像没回完）。
--
-- 成因：`006_assistant_chat.sql` 建表时没有这一列，此后也没有迁移补上，
--   而代码与 ADR-007 的记录都按"该列存在"写 —— 库与代码不一致。
--
-- 幂等：COL_LENGTH 守卫，重复执行为空操作。
-- 回滚：ALTER TABLE dbo.ASSISTANT_MESSAGE DROP COLUMN TOOL_CALLS_JSON;
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
    THROW 52500, @GUARD_MESSAGE, 1;

IF COL_LENGTH('dbo.ASSISTANT_MESSAGE', 'TOOL_CALLS_JSON') IS NULL
BEGIN
    ALTER TABLE dbo.ASSISTANT_MESSAGE ADD TOOL_CALLS_JSON NVARCHAR(MAX) NULL;
END
GO
