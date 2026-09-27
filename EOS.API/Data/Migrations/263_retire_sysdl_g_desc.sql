-- 退役 dbo.SYSDL.G_DESC（“默认群组”）字段元数据。
--
-- 为什么退役：该字段不是 SYSDL 的物理列，而是靠虚拟表达式 SYSDG.G_DESC 取值的虚拟列。SYSDG 不在
-- SYSDL 的 QUERY_RELATION 里，引用名解析不到 ⇒ 运行期判未解析、字段不渲染，长期为空且不报错；
-- 而 SYSDL 也没有组键——用户与组由 SYSDG_USER 承担且是多对多（实测 39 个用户里 30 个属多组），
-- 不存在唯一的“默认群组”，补 JOIN 只会让主表行按组重复。该语义在现系统另有正确承载：
-- 用户管理/用户组权限页用 SYSDG_USER ⋈ SYSDG 把多组拼接展示，不需要“默认组”这一事实。
--
-- 清理范围与字段维护的删除路径同一套（7 张引用表 + FIELDS 本体 + 版式行），避免留下悬空引用。
-- 审计不动：审计是追加型账，字段退役不移除历史留痕。
-- 不标脏：SYSDL 所属模块（2306/230906/239806）都不在统一表单白名单内，没有已发布快照会因此落后。
--
-- 幂等：字段行已不存在时不再删除，只做引用复点；任何后置不符即报错回滚。

SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @GUARD_MESSAGE NVARCHAR(400) = N'本脚本只能在 EOS.ERP 数据库内执行，当前库为 ' + DB_NAME() + N'。';
IF DB_NAME() <> N'EOS.ERP'
    THROW 50000, @GUARD_MESSAGE, 1;

DECLARE @TableId NVARCHAR(200) = N'SYSDL';
DECLARE @FieldId NVARCHAR(200) = N'G_DESC';
DECLARE @TableDotField NVARCHAR(400) = N'SYSDL.G_DESC';

BEGIN TRANSACTION;

-- ① 前置自证：字段行要么不存在（已退役），要么就是那条“默认群组”（避免删错行）
IF EXISTS (SELECT 1 FROM dbo.FIELDS WHERE T_ID = @TableId AND F_ID = @FieldId)
   AND NOT EXISTS (SELECT 1 FROM dbo.FIELDS
                   WHERE T_ID = @TableId AND F_ID = @FieldId AND LTRIM(RTRIM(ISNULL(F_DESC, N''))) = N'默认群组')
    THROW 55000, N'dbo.SYSDL.G_DESC 的标题不是「默认群组」，与本迁移的预期不符。', 1;

IF NOT EXISTS (SELECT 1 FROM dbo.FIELDS WHERE T_ID = @TableId AND F_ID = @FieldId)
    PRINT N'dbo.SYSDL.G_DESC 已不存在，视为本迁移已落地（仅复点引用表）。';

-- ② 清引用（谓词与字段维护的删除路径一致：F_ID + 表名，或点号全名）
DELETE FROM dbo.SYSQL_FIELDS WHERE F_ID = @FieldId AND (T_ID = @TableId OR T_ID_R = @TableId);
DELETE FROM dbo.SYSQL_DEFAULT WHERE F_ID = @FieldId AND (T_ID = @TableId OR T_ID_R = @TableId);
DELETE FROM dbo.SYSQL_CONDITION WHERE F_ID = @FieldId AND (T_ID = @TableId OR T_ID_R = @TableId);
DELETE FROM dbo.SYSQL_COND_DFT WHERE F_ID = @FieldId AND (T_ID = @TableId OR T_ID_R = @TableId);
DELETE FROM dbo.SYSQD_CONDITION WHERE F_ID = @FieldId AND (T_ID = @TableId OR T_ID_R = @TableId);
DELETE FROM dbo.SYSQQ WHERE F_ID = @TableDotField;
DELETE FROM dbo.SYSQR_DEFAULT WHERE F_ID = @TableDotField;
-- 版式行（模块级表单版式）：实查 0 行，防御性清理，避免留下指向已退役字段的格子
DELETE FROM dbo.MODULE_FORM_LAYOUT WHERE T_ID = @TableId AND F_ID = @FieldId;

-- ③ 删本体
DELETE FROM dbo.FIELDS WHERE T_ID = @TableId AND F_ID = @FieldId;

-- ④ 后置自证：本体与各引用表都不再有该键
IF EXISTS (SELECT 1 FROM dbo.FIELDS WHERE T_ID = @TableId AND F_ID = @FieldId)
    THROW 55001, N'dbo.FIELDS 里仍存在 dbo.SYSDL.G_DESC。', 1;

IF EXISTS (SELECT 1 FROM dbo.SYSQL_FIELDS WHERE F_ID = @FieldId AND (T_ID = @TableId OR T_ID_R = @TableId))
    OR EXISTS (SELECT 1 FROM dbo.SYSQL_DEFAULT WHERE F_ID = @FieldId AND (T_ID = @TableId OR T_ID_R = @TableId))
    OR EXISTS (SELECT 1 FROM dbo.SYSQL_CONDITION WHERE F_ID = @FieldId AND (T_ID = @TableId OR T_ID_R = @TableId))
    OR EXISTS (SELECT 1 FROM dbo.SYSQL_COND_DFT WHERE F_ID = @FieldId AND (T_ID = @TableId OR T_ID_R = @TableId))
    OR EXISTS (SELECT 1 FROM dbo.SYSQD_CONDITION WHERE F_ID = @FieldId AND (T_ID = @TableId OR T_ID_R = @TableId))
    OR EXISTS (SELECT 1 FROM dbo.SYSQQ WHERE F_ID = @TableDotField)
    OR EXISTS (SELECT 1 FROM dbo.SYSQR_DEFAULT WHERE F_ID = @TableDotField)
    OR EXISTS (SELECT 1 FROM dbo.MODULE_FORM_LAYOUT WHERE T_ID = @TableId AND F_ID = @FieldId)
    THROW 55002, N'仍有引用表保留 dbo.SYSDL.G_DESC 的行。', 1;

COMMIT TRANSACTION;
