-- ============================================================================
-- EOS.ERP 迁移 014：清理字段级拒绝遗留哨兵值 '0'
-- ----------------------------------------------------------------------------
-- 库：EOS.ERP（全新系统的唯一业务数据库）
--
-- 背景（2026-08-27 定制页验收，EOS-23）：
--   旧系统历史数据把「无禁止字段」写成 '0'（旧网格按 ';' 拆分后把 '0' 当列名隐藏，
--   无此列即无效果）。新系统权限矩阵保存时按字段白名单校验，'0' 不是任何模块的
--   物理字段，导致「批量保存」报 400（主表禁止查看字段 0 不是模块表 X 的字段）。
--   本迁移把 SYSDH/SYSDD 六个字段级拒绝列的遗留 '0' 置空（幂等）；代码侧
--   RightsAdminLogic.ParseDenyList 已同步把 '0' 视为空（读/写统一剔除）。
--
-- 幂等：WHERE 守卫精确匹配 '0'，重复执行无副作用。
-- 命名约定（AGENTS.md 强制）：对象名全大写。
-- ============================================================================

SET NOCOUNT ON;
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;

DECLARE @GUARD_MESSAGE NVARCHAR(400) = N'本脚本只能在 EOS.ERP 数据库内执行，当前库为 ' + DB_NAME() + N'。';
IF DB_NAME() <> N'EOS.ERP'
    THROW 50000, @GUARD_MESSAGE, 1;

BEGIN TRANSACTION;

DECLARE @DENY_COLUMNS TABLE (COLUMN_NAME sysname);
INSERT INTO @DENY_COLUMNS (COLUMN_NAME) VALUES
    (N'DENY_VIEW_FIELD_MASTER'), (N'DENY_VIEW_FIELD_DETAIL'),
    (N'DENY_NEW_FIELD_MASTER'), (N'DENY_NEW_FIELD_DETAIL'),
    (N'DENY_MODI_FIELD_MASTER'), (N'DENY_MODI_FIELD_DETAIL');

DECLARE @TABLE_SQL NVARCHAR(MAX) = N'';
SELECT @TABLE_SQL = @TABLE_SQL +
    N'UPDATE dbo.' + t.TABLE_NAME + N' SET ' + QUOTENAME(c.COLUMN_NAME) + N' = NULL WHERE LTRIM(RTRIM(ISNULL(' + QUOTENAME(c.COLUMN_NAME) + N', N''''))) = N''0'';' + CHAR(10)
FROM @DENY_COLUMNS c
CROSS JOIN (VALUES (N'SYSDH'), (N'SYSDD')) AS t(TABLE_NAME);

EXEC sys.sp_executesql @TABLE_SQL;

PRINT N'[EOS-23] 字段级拒绝遗留哨兵 ''0'' 清理完成（SYSDH/SYSDD 六列）。';

COMMIT TRANSACTION;
