-- ============================================================================
-- EOS.ERP migration 139: 下线按名调用点 P_Change_M_IDX 与其唯一牵连者 xp_menu_manage
-- ----------------------------------------------------------------------------
-- 背景：模块编号变更的引用级联已由 `MenuAdminRepository.ChangeModuleIndexSql` 承接
-- （原 `EXEC dbo.P_Change_M_IDX` 调用已删除），且原过程本体**今天是跑不通的**——它第 12 条
-- 语句写的是已被 取代、库内已不存在的 `FIELDS_CHOOSER`（现表为 `FIELD_DATASOURCE`
-- 的同名列 `SOURCE_M_IDX`），移植实现据此改用现表。
-- 三方核查（2026-09-18 实测）：
--   · `xp_menu_manage`（旧菜单保存过程，4099 字符）：库内无调用方、MODULES 无引用、
--     REPORT_SORT 无引用、EOS.API/EOS.API.Tests/scripts/EOS.Web 无任何字面量引用；
--   · `P_Change_M_IDX`：删除 `xp_menu_manage` 后库内引用归零，代码侧引用已随移植移除。
-- 迁移按"先删牵连者、再断言、再删目标"的顺序执行，任一条不成立即 THROW。
-- 幂等：不存在则跳过；末尾断言 dbo 过程总数与 `xp_*` 数量。
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
    THROW 50000, @GUARD_MESSAGE, 1;

DECLARE @MenuManage NVARCHAR(128) = N'xp_menu_manage';
DECLARE @ChangeIndex NVARCHAR(128) = N'P_Change_M_IDX';

/* ---------- ① xp_menu_manage：确认无人调用 ---------- */
IF EXISTS (
    SELECT 1 FROM sys.sql_expression_dependencies d
    WHERE d.referenced_class = 1 AND d.referenced_entity_name = @MenuManage
)
    THROW 50001, N'xp_menu_manage 仍被库内对象调用，迁移中止。', 1;
IF EXISTS (
    SELECT 1 FROM dbo.MODULES m
    WHERE ISNULL(m.UPDATE_SP, N'') + ISNULL(m.AFTERSAVE_SP, N'') LIKE N'%' + @MenuManage + N'%'
)
    THROW 50002, N'xp_menu_manage 仍被 MODULES 元数据引用，迁移中止。', 1;
IF EXISTS (SELECT 1 FROM dbo.REPORT_SORT s WHERE ISNULL(s.SORT_FIELDS, N'') LIKE N'%' + @MenuManage + N'%')
    THROW 50003, N'xp_menu_manage 仍被报表排序字段引用，迁移中止。', 1;

IF OBJECT_ID(N'dbo.xp_menu_manage') IS NOT NULL
    EXEC sp_executesql N'DROP PROCEDURE dbo.xp_menu_manage;';

/* ---------- ② P_Change_M_IDX：牵连者已删，重新断言库内引用归零 ---------- */
IF EXISTS (
    SELECT 1 FROM sys.sql_expression_dependencies d
    WHERE d.referenced_class = 1 AND d.referenced_entity_name = @ChangeIndex
)
    THROW 50004, N'P_Change_M_IDX 仍被库内对象调用，迁移中止。', 1;
IF EXISTS (
    SELECT 1 FROM dbo.MODULES m
    WHERE ISNULL(m.UPDATE_SP, N'') + ISNULL(m.AFTERSAVE_SP, N'') LIKE N'%' + @ChangeIndex + N'%'
)
    THROW 50005, N'P_Change_M_IDX 仍被 MODULES 元数据引用，迁移中止。', 1;

IF OBJECT_ID(N'dbo.P_Change_M_IDX') IS NOT NULL
    EXEC sp_executesql N'DROP PROCEDURE dbo.P_Change_M_IDX;';

/* ---------- ③ 断言收口 ---------- */
DECLARE @Total INT = (SELECT COUNT(*) FROM sys.objects WHERE type = 'P' AND SCHEMA_NAME(schema_id) = 'dbo');
DECLARE @XpCount INT = (SELECT COUNT(*) FROM sys.objects WHERE type = 'P' AND SCHEMA_NAME(schema_id) = 'dbo' AND name LIKE N'xp[_]%');
IF @Total <> 93
    THROW 50006, N'下线后 dbo 过程总数不是预期的 93 个，迁移中止（请复核引用清单）。', 1;
IF @XpCount <> 9
    THROW 50007, N'下线后 xp_* 过程数不是预期的 9 个，迁移中止。', 1;

PRINT N'== P_Change_M_IDX 与 xp_menu_manage 已下线；dbo 过程合计 '
    + CAST(@Total AS NVARCHAR(10)) + N' 个（xp_* ' + CAST(@XpCount AS NVARCHAR(10)) + N' 个）==';
