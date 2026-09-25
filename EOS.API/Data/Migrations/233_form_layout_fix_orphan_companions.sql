-- ============================================================================
-- EOS.ERP migration 234: 订正孤儿从字段（复合格主从不配对）
-- ----------------------------------------------------------------------------
-- 复合格 = 同一格里的 [主字段][选择钮][从字段]，靠 FIELDS.FORM_CELL_GROUP 同名 +
-- FORM_CELL_ROLE（1 主 / 2 从）配对。库里存在**从字段配了组、主字段却没配组**的行：
-- 从字段被判为"复合格成员"而表单渲染只认主字段所在的格，于是这个字段既进不了表单、
-- 又不报错——配错了没有任何信号，是"配置错了也不响"的典型。
--
-- 实测四处（本迁移逐处订正，并在末尾做全库自证）：
--   COP_ORDER_M.CLIENT_ID      ← 从字段 CLIENT_NAME 的组名 'CLIENT'
--   PUR_PURCHASE_M.SUPPLIER_ID ← 从字段 SUPPLIER_NAME 的组名 'SUPPLIER_ID'
--   INV_OCCUR_IN_M.OCCUR_TYPE  ← 从字段 BILL_NAME 的组名 'OCCUR_TYPE'
--   SYSDL.G_DESC               ← 主字段 G_IDX 已不存在（虚拟列，物理列与 FIELDS 行均已退役）
--
-- 前三处按"从字段已有的组名"给主字段补组与角色 1；组名在这三张表内各自唯一（只有该从字段
-- 在用），因此不会被别处当成业务分节。
--
-- 第四处不是同一个问题：SYSDL 的 G_IDX 已随列退役，FIELDS 里没有它的行，**没有可配对的主字段**。
-- 故只清除该从字段的悬空关联（清组名与角色），不凭空造一个主字段；它本就是虚拟列，
-- 清掉关联后主表表单行查询不会再取到它（虚拟值由列表侧按虚拟表达式解析，不受影响）。
--
-- 幂等：四处均为"写目标值 + 存在性自证"，可重复执行。
-- 回滚：UPDATE dbo.FIELDS SET FORM_CELL_GROUP = NULL, FORM_CELL_ROLE = 0
--       WHERE (T_ID = 'COP_ORDER_M' AND F_ID = 'CLIENT_ID')
--          OR (T_ID = 'PUR_PURCHASE_M' AND F_ID = 'SUPPLIER_ID')
--          OR (T_ID = 'INV_OCCUR_IN_M' AND F_ID = 'OCCUR_TYPE');
--       UPDATE dbo.FIELDS SET FORM_CELL_GROUP = N'G_IDX', FORM_CELL_ROLE = 2
--       WHERE T_ID = 'SYSDL' AND F_ID = 'G_DESC';
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
    THROW 52400, @GUARD_MESSAGE, 1;

IF OBJECT_ID(N'dbo.FIELDS', N'U') IS NULL
    THROW 52401, N'dbo.FIELDS 不存在，迁移中止。', 1;

BEGIN TRANSACTION;

/* ---------- ① 三处可从主字段订正的：按从字段已有的组名给主字段补组与角色 ---------- */
DECLARE @targets TABLE (T_ID SYSNAME NOT NULL, MAIN_F_ID NVARCHAR(100) NOT NULL, CELL_GROUP NVARCHAR(50) NOT NULL);
INSERT INTO @targets (T_ID, MAIN_F_ID, CELL_GROUP) VALUES
    (N'COP_ORDER_M',    N'CLIENT_ID',   N'CLIENT'),
    (N'PUR_PURCHASE_M', N'SUPPLIER_ID', N'SUPPLIER_ID'),
    (N'INV_OCCUR_IN_M', N'OCCUR_TYPE',  N'OCCUR_TYPE');

DECLARE @tableName SYSNAME, @mainField NVARCHAR(100), @cellGroup NVARCHAR(50);
DECLARE target_cursor CURSOR LOCAL FAST_FORWARD FOR SELECT T_ID, MAIN_F_ID, CELL_GROUP FROM @targets;
OPEN target_cursor;
FETCH NEXT FROM target_cursor INTO @tableName, @mainField, @cellGroup;
WHILE @@FETCH_STATUS = 0
BEGIN
    /* 组名必须已是某个从字段的组名（否则说明订正目标写错了，宁可中止也不猜） */
    IF NOT EXISTS (
        SELECT 1 FROM dbo.FIELDS f
        WHERE f.T_ID = @tableName AND LTRIM(RTRIM(f.F_ID)) <> @mainField
          AND ISNULL(f.FORM_CELL_GROUP, N'') = @cellGroup AND ISNULL(f.FORM_CELL_ROLE, 0) = 2)
        THROW 52402, N'目标从字段不存在，迁移中止。', 1;

    /* 组名不得被 ≥2 个主字段共用：那会让它变成"业务分节"，是另一回事 */
    IF EXISTS (
        SELECT 1 FROM dbo.FIELDS f
        WHERE f.T_ID = @tableName AND ISNULL(f.FORM_CELL_GROUP, N'') = @cellGroup
          AND ISNULL(f.FORM_CELL_ROLE, 0) = 1 AND LTRIM(RTRIM(f.F_ID)) <> @mainField)
        THROW 52403, N'组名已被其它主字段占用，迁移中止。', 1;

    UPDATE dbo.FIELDS
       SET FORM_CELL_GROUP = @cellGroup,
           FORM_CELL_ROLE = 1
     WHERE T_ID = @tableName AND LTRIM(RTRIM(F_ID)) = @mainField
       AND (ISNULL(FORM_CELL_GROUP, N'') <> @cellGroup OR ISNULL(FORM_CELL_ROLE, 0) <> 1);

    PRINT N'== 已订正主字段 ' + @tableName + N'.' + @mainField + N'：组 ' + @cellGroup + N'，角色 1 ==';
    FETCH NEXT FROM target_cursor INTO @tableName, @mainField, @cellGroup;
END
CLOSE target_cursor;
DEALLOCATE target_cursor;

/* ---------- ② 主字段已退役的那一处：清除从字段的悬空关联 ---------- */
UPDATE dbo.FIELDS
   SET FORM_CELL_GROUP = NULL,
       FORM_CELL_ROLE = 0
 WHERE T_ID = N'SYSDL' AND LTRIM(RTRIM(F_ID)) = N'G_DESC'
   AND (FORM_CELL_GROUP IS NOT NULL OR ISNULL(FORM_CELL_ROLE, 0) <> 0);

IF @@ROWCOUNT > 0
    PRINT N'== 已清除 SYSDL.G_DESC 的悬空从字段关联（主字段 G_IDX 已退役）==';

/* ---------- ③ 自证：三处主从成对 ---------- */
IF EXISTS (
    SELECT 1 FROM (VALUES (N'COP_ORDER_M', N'CLIENT_ID', N'CLIENT'),
                         (N'PUR_PURCHASE_M', N'SUPPLIER_ID', N'SUPPLIER_ID'),
                         (N'INV_OCCUR_IN_M', N'OCCUR_TYPE', N'OCCUR_TYPE')) AS expected(T_ID, F_ID, CELL_GROUP)
    WHERE NOT EXISTS (
        SELECT 1 FROM dbo.FIELDS f
        WHERE f.T_ID = expected.T_ID AND LTRIM(RTRIM(f.F_ID)) = expected.F_ID
          AND ISNULL(f.FORM_CELL_GROUP, N'') = expected.CELL_GROUP AND ISNULL(f.FORM_CELL_ROLE, 0) = 1))
    THROW 52404, N'主字段未按目标组名成对，迁移中止。', 1;

IF EXISTS (
    SELECT 1 FROM dbo.FIELDS f
    WHERE f.T_ID = N'SYSDL' AND LTRIM(RTRIM(f.F_ID)) = N'G_DESC'
      AND (f.FORM_CELL_GROUP IS NOT NULL OR ISNULL(f.FORM_CELL_ROLE, 0) <> 0))
    THROW 52405, N'SYSDL.G_DESC 的悬空关联未清除，迁移中止。', 1;

/* ---------- ④ 全库自证：不得再有"从字段无主字段"的行 ---------- */
IF EXISTS (
    SELECT 1
    FROM dbo.FIELDS f
    WHERE ISNULL(f.FORM_CELL_ROLE, 0) = 2 AND ISNULL(f.FORM_CELL_GROUP, N'') <> N''
      AND NOT EXISTS (
          SELECT 1 FROM dbo.FIELDS g
          WHERE g.T_ID = f.T_ID AND ISNULL(g.FORM_CELL_GROUP, N'') = f.FORM_CELL_GROUP
            AND ISNULL(g.FORM_CELL_ROLE, 0) = 1))
    THROW 52406, N'仍存在无主字段的孤儿从字段，迁移中止。', 1;

/* ---------- ⑤ 自证输出 ---------- */
SELECT N'PAIRED_COMPANIONS' AS OBJECT_NAME, COUNT(*) AS CNT
FROM dbo.FIELDS f
WHERE ISNULL(f.FORM_CELL_ROLE, 0) = 2 AND ISNULL(f.FORM_CELL_GROUP, N'') <> N''
  AND EXISTS (SELECT 1 FROM dbo.FIELDS g
              WHERE g.T_ID = f.T_ID AND ISNULL(g.FORM_CELL_GROUP, N'') = f.FORM_CELL_GROUP
                AND ISNULL(g.FORM_CELL_ROLE, 0) = 1);

SELECT N'ORPHAN_COMPANIONS' AS OBJECT_NAME, COUNT(*) AS CNT
FROM dbo.FIELDS f
WHERE ISNULL(f.FORM_CELL_ROLE, 0) = 2 AND ISNULL(f.FORM_CELL_GROUP, N'') <> N''
  AND NOT EXISTS (SELECT 1 FROM dbo.FIELDS g
                  WHERE g.T_ID = f.T_ID AND ISNULL(g.FORM_CELL_GROUP, N'') = f.FORM_CELL_GROUP
                    AND ISNULL(g.FORM_CELL_ROLE, 0) = 1);

COMMIT TRANSACTION;
