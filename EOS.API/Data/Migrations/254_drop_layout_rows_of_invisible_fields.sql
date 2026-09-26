-- ============================================================================
-- EOS.ERP migration 252: 版式里不再保留"字段已标记不显示"的行
-- ----------------------------------------------------------------------------
-- 背景：FIELDS.IS_VISIBLE=0 是管理员在字段维护里标记的"不显示"字段——运行态一律不渲染它们
-- （FormFieldSelector.Select 先把它们剔掉，版式施加在它之后，改版式也变不出来）。
-- 但库里仍留着引用这类字段的版式行：全库 1042 行（1016 行 IS_HIDDEN=1、26 行还显示中），
-- 涉及 95 个模块。设计态画布照样把它们画出来（那 26 行还画成普通字段），设计者看到一批
-- "排了也没用"的字段，却看不出它们在运行态不会出现。
--
-- 做法：删掉这些版式行。运行态定义**逐字不变**（不可见字段在版式施加前就被剔除），
-- 变的是设计态画布——它只画得出来的字段了。
--
-- 为什么仍要标脏：快照的 DEFINITION_JSON 里带着版式行，删行后"按当前配置重建"与已发布快照
-- 不再逐字相同；不标脏会被 check-snapshot-staleness 判为**静默落后**（配置变了却无任何信号）。
-- 故只给"已有当前快照"的受影响模块标脏（与既有迁移同一口径），让它们以"待发布积压"的形式可见。
--
-- 边界（断言，任一条不成立即中止整笔，不做静默换版式的事）：
--   · 不得把任何"模块 × 表"清成零行——零行 = 该表未定制，运行态会落回"全部可见字段"，
--     等于替用户换了一张表单；
--   · 收口后不得再有引用 IS_VISIBLE=0 字段的版式行；
--   · 受影响且有当前快照的模块必须都已标记待发布。
--
-- 幂等：重复执行删 0 行；断言都基于终态，仍成立。
-- 回滚：无（删的是"运行态永远不渲染"的行；确实想恢复就在设计态重新排入，但排了也不会出现在运行态）。
-- ============================================================================

SET NOCOUNT ON;

SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET ANSI_PADDING ON;
SET ANSI_WARNINGS ON;
SET CONCAT_NULL_YIELDS_NULL ON;
SET ARITHABORT ON;
SET XACT_ABORT ON;

DECLARE @GUARD_MESSAGE NVARCHAR(400) = N'本脚本只能在 EOS.ERP 数据库内执行，当前库为 ' + DB_NAME() + N'。';
IF DB_NAME() <> N'EOS.ERP'
    THROW 52800, @GUARD_MESSAGE, 1;

BEGIN TRANSACTION;

/* ---------- 1. 目标：引用"不显示"字段的版式行落在哪些「模块 × 表」上 ---------- */
DECLARE @touched TABLE (M_IDX INT NOT NULL, T_ID NVARCHAR(100) NOT NULL, PRIMARY KEY (M_IDX, T_ID));

INSERT INTO @touched (M_IDX, T_ID)
SELECT DISTINCT l.M_IDX, LTRIM(RTRIM(l.T_ID))
  FROM dbo.MODULE_FORM_LAYOUT l
  JOIN dbo.FIELDS f
    ON LTRIM(RTRIM(f.T_ID)) = LTRIM(RTRIM(l.T_ID))
   AND LTRIM(RTRIM(f.F_ID)) = LTRIM(RTRIM(l.F_ID))
 WHERE COALESCE(f.IS_VISIBLE, 1) = 0;

DECLARE @pending INT = (
    SELECT COUNT(1)
      FROM dbo.MODULE_FORM_LAYOUT l
      JOIN dbo.FIELDS f
        ON LTRIM(RTRIM(f.T_ID)) = LTRIM(RTRIM(l.T_ID))
       AND LTRIM(RTRIM(f.F_ID)) = LTRIM(RTRIM(l.F_ID))
     WHERE COALESCE(f.IS_VISIBLE, 1) = 0);

/* ---------- 2. 删除这些行 ---------- */
DELETE l
  FROM dbo.MODULE_FORM_LAYOUT l
  JOIN dbo.FIELDS f
    ON LTRIM(RTRIM(f.T_ID)) = LTRIM(RTRIM(l.T_ID))
   AND LTRIM(RTRIM(f.F_ID)) = LTRIM(RTRIM(l.F_ID))
 WHERE COALESCE(f.IS_VISIBLE, 1) = 0;

DECLARE @deleted INT = @@ROWCOUNT;
DECLARE @touchedPairs INT = (SELECT COUNT(1) FROM @touched);
PRINT N'== 已删除 ' + CONVERT(NVARCHAR(10), @deleted) + N' 行引用"不显示"字段的版式行（命中前 ' + CONVERT(NVARCHAR(10), @pending) + N' 行，涉及 ' + CONVERT(NVARCHAR(10), @touchedPairs) + N' 个「模块 × 表」）==';

/* ---------- 3. 断言：不得把任何「模块 × 表」清成零行 ---------- */
IF EXISTS (
    SELECT 1
      FROM @touched t
     WHERE NOT EXISTS (
         SELECT 1 FROM dbo.MODULE_FORM_LAYOUT l
          WHERE l.M_IDX = t.M_IDX AND LTRIM(RTRIM(l.T_ID)) = t.T_ID))
    THROW 52801, N'清理后出现「模块 × 表」零行（该表变成未定制），运行态会落回"全部可见字段"，迁移中止。', 1;

/* ---------- 4. 标脏：已有当前快照的受影响模块（否则会被判为快照静默落后） ---------- */
MERGE dbo.WORKBENCH_MODULE_DIRTY AS D
USING (
    SELECT DISTINCT t.M_IDX
      FROM @touched t
      JOIN dbo.WORKBENCH_DEFINITION_SNAPSHOT s ON s.M_IDX = t.M_IDX AND s.IS_CURRENT = 1) AS S
   ON D.M_IDX = S.M_IDX
WHEN MATCHED THEN UPDATE SET D.DIRTY_TAG = 1, D.LAST_MODIFIED_BY = N'DbUp', D.LAST_MODIFIED_AT = SYSDATETIME()
WHEN NOT MATCHED THEN INSERT (M_IDX, DIRTY_TAG, LAST_MODIFIED_BY, LAST_MODIFIED_AT)
    VALUES (S.M_IDX, 1, N'DbUp', SYSDATETIME());

PRINT N'== 已标记 ' + CONVERT(NVARCHAR(10), @@ROWCOUNT) + N' 个受影响模块待重发布 ==';

/* ---------- 5. 收口断言 ---------- */
IF EXISTS (
    SELECT 1
      FROM dbo.MODULE_FORM_LAYOUT l
      JOIN dbo.FIELDS f
        ON LTRIM(RTRIM(f.T_ID)) = LTRIM(RTRIM(l.T_ID))
       AND LTRIM(RTRIM(f.F_ID)) = LTRIM(RTRIM(l.F_ID))
     WHERE COALESCE(f.IS_VISIBLE, 1) = 0)
    THROW 52802, N'仍存在引用"不显示"字段的版式行，迁移中止。', 1;

IF EXISTS (
    SELECT 1
      FROM @touched t
      JOIN dbo.WORKBENCH_DEFINITION_SNAPSHOT s ON s.M_IDX = t.M_IDX AND s.IS_CURRENT = 1
     WHERE NOT EXISTS (
         SELECT 1 FROM dbo.WORKBENCH_MODULE_DIRTY d WHERE d.M_IDX = t.M_IDX AND d.DIRTY_TAG = 1))
    THROW 52803, N'受影响模块未标记待发布，快照会被判为静默落后，迁移中止。', 1;

COMMIT;

PRINT N'== 收口：版式不再保留"字段已标记不显示"的行，受影响模块待重发布 ==';
