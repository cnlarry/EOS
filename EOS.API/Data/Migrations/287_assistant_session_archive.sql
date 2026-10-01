-- ============================================================================
-- EOS.ERP migration 287: 工作助手会话归档列（ASSISTANT_SESSION.ARCHIVED_AT）
-- ----------------------------------------------------------------------------
-- 背景：会话原先只有「删除」一条出路，误删即永久丢历史。产品决定：**界面不再提供删除**，
--   改用「归档」——归档的会话默认不出现在列表里，但数据完整保留、随时可取消归档。
--
-- 语义：ARCHIVED_AT IS NULL = 在列（正常会话）；非 NULL = 已归档，值即归档时刻。
--   列表端点默认过滤已归档；带 ?archived=true 时一并返回（供前端「显示已归档」开关）。
--
-- 幂等：COL_LENGTH 守卫，重复执行为空操作。
-- 回滚：ALTER TABLE dbo.ASSISTANT_SESSION DROP COLUMN ARCHIVED_AT;
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

IF COL_LENGTH('dbo.ASSISTANT_SESSION', 'ARCHIVED_AT') IS NULL
BEGIN
    ALTER TABLE dbo.ASSISTANT_SESSION ADD ARCHIVED_AT DATETIME2(3) NULL;
END
GO
