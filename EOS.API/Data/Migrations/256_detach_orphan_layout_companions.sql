-- ============================================================================
-- EOS.ERP migration 254: 拆开"没有主字段"的孤儿复合格（从字段降级为独立格）
-- ----------------------------------------------------------------------------
-- 背景：复合格 = 主字段（CELL_ROLE=1）+ 同组从字段（CELL_ROLE=2），一格最多一个主字段。
-- 清理"引用 IS_VISIBLE=0 字段的版式行"时（迁移 252），其中一部分组的**主字段正好是那些
-- 不显示字段**，主字段行被删掉后从字段就失去配对方：全库 10 行，落在 9 个「模块 × 表」上
-- （产品资料/样品/开模申请/上模·刀模开模单/员工·离职员工）。
--
-- 为什么要修（不只是门禁红）：
--   · 这 10 行让 check-form-layout 第①条（从字段必须有同组主字段）FAIL；
--   · 同一判据也是保存期校验（FormLayoutValidator 的 FORM_LAYOUT_COMPANION_WITHOUT_MAIN，
--     fail-closed）——受影响的 8 个「模块 × 表」**在设计器里存不了版式**，前端会先报
--     "从字段没有配主字段"，而界面上看不出该点哪一项才能解开。
--
-- 做法 = 设计器右键「移出复合格」的同一件事：置 CELL_GROUP=NULL、CELL_ROLE=0，行本身
-- （位置、跨度、页签、隐藏位）全部保留。
--   · 不删行：渲染侧对"孤立的从字段"是**降级成独立格、绝不丢弃**（formLayout.ts
--     buildPackedCells），删行等于把字段从表单上静默拿掉——其中 BUSINESS_TAG、APPLY_NO_OLD
--     还是可填的真实字段。
--   · 不恢复主字段：那些主字段是管理员在字段维护里标记"不显示"的，运行态本来就不渲染它们。
--   · 降级后运行态渲染逐字不变（同一条降级路径），变的只是重建定义里的 cellGroup/cellRole。
--
-- 为什么仍要标脏：快照的 DEFINITION_JSON 里带着版式行，改行后"按当前配置重建"与已发布快照
-- 不再逐字相同；不标脏会被 check-snapshot-staleness 判为**静默落后**。故只给"已有当前快照"
-- 的受影响模块标脏（与既有迁移同一口径），让它们以"待发布积压"的形式可见。
--
-- 边界（断言，任一条不成立即中止整笔）：
--   · 收口后不得再有"从字段没有同组主字段"的版式行；
--   · 本批命中的行必须都已拆开（不再带组名、角色归 0）；
--   · 受影响且有当前快照的模块必须都已标记待发布。
--
-- 幂等：重复执行改 0 行（命中集是终态判据，已拆开的行不再命中）；断言都基于终态，仍成立。
-- 回滚：无（版式属可再设计数据；要恢复成组就在设计态把它们重新"合并为一格"）。
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
    THROW 52900, @GUARD_MESSAGE, 1;

BEGIN TRANSACTION;

/* ---------- 1. 目标：从字段有组名，但同「模块 × 表」内没有同组主字段 ---------- */
DECLARE @orphans TABLE (
    M_IDX INT           NOT NULL,
    T_ID  NVARCHAR(100) NOT NULL,
    F_ID  NVARCHAR(100) NOT NULL,
    PRIMARY KEY (M_IDX, T_ID, F_ID));

INSERT INTO @orphans (M_IDX, T_ID, F_ID)
SELECT l.M_IDX, LTRIM(RTRIM(l.T_ID)), LTRIM(RTRIM(l.F_ID))
  FROM dbo.MODULE_FORM_LAYOUT l
 WHERE COALESCE(l.CELL_ROLE, 0) = 2
   AND COALESCE(l.CELL_GROUP, N'') <> N''
   AND NOT EXISTS (SELECT 1 FROM dbo.MODULE_FORM_LAYOUT g
                    WHERE g.M_IDX = l.M_IDX
                      AND g.T_ID = l.T_ID
                      AND COALESCE(g.CELL_GROUP, N'') = l.CELL_GROUP
                      AND COALESCE(g.CELL_ROLE, 0) = 1);

DECLARE @pending INT = (SELECT COUNT(1) FROM @orphans);

DECLARE @touched TABLE (M_IDX INT NOT NULL, T_ID NVARCHAR(100) NOT NULL, PRIMARY KEY (M_IDX, T_ID));
INSERT INTO @touched (M_IDX, T_ID) SELECT DISTINCT M_IDX, T_ID FROM @orphans;

/* ---------- 2. 拆开：清组名、角色归 0，位置与占位一律不动 ---------- */
UPDATE l
   SET l.CELL_GROUP = NULL,
       l.CELL_ROLE = 0,
       l.UPDATED_BY = N'DbUp',
       l.UPDATED_AT = SYSUTCDATETIME()
  FROM dbo.MODULE_FORM_LAYOUT l
  JOIN @orphans o
    ON o.M_IDX = l.M_IDX
   AND o.T_ID = LTRIM(RTRIM(l.T_ID))
   AND o.F_ID = LTRIM(RTRIM(l.F_ID));

DECLARE @detached INT = @@ROWCOUNT;
DECLARE @touchedPairs INT = (SELECT COUNT(1) FROM @touched);
PRINT N'== 已拆开 ' + CONVERT(NVARCHAR(10), @detached) + N' 行"没有主字段"的复合格从字段（命中前 '
    + CONVERT(NVARCHAR(10), @pending) + N' 行，涉及 ' + CONVERT(NVARCHAR(10), @touchedPairs) + N' 个「模块 × 表」）==';

/* ---------- 3. 断言：本批命中的行确实已拆开 ---------- */
IF EXISTS (
    SELECT 1
      FROM dbo.MODULE_FORM_LAYOUT l
      JOIN @orphans o
        ON o.M_IDX = l.M_IDX AND o.T_ID = LTRIM(RTRIM(l.T_ID)) AND o.F_ID = LTRIM(RTRIM(l.F_ID))
     WHERE COALESCE(l.CELL_GROUP, N'') <> N'' OR COALESCE(l.CELL_ROLE, 0) <> 0)
    THROW 52901, N'命中行未拆开（仍带组名或角色非 0），迁移中止。', 1;

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

/* ---------- 5. 收口断言：全库不再有"从字段没有同组主字段"的行 ---------- */
IF EXISTS (
    SELECT 1
      FROM dbo.MODULE_FORM_LAYOUT l
     WHERE COALESCE(l.CELL_ROLE, 0) = 2
       AND COALESCE(l.CELL_GROUP, N'') <> N''
       AND NOT EXISTS (SELECT 1 FROM dbo.MODULE_FORM_LAYOUT g
                        WHERE g.M_IDX = l.M_IDX
                          AND g.T_ID = l.T_ID
                          AND COALESCE(g.CELL_GROUP, N'') = l.CELL_GROUP
                          AND COALESCE(g.CELL_ROLE, 0) = 1))
    THROW 52902, N'仍存在"从字段没有同组主字段"的版式行，迁移中止。', 1;

IF EXISTS (
    SELECT 1
      FROM @touched t
      JOIN dbo.WORKBENCH_DEFINITION_SNAPSHOT s ON s.M_IDX = t.M_IDX AND s.IS_CURRENT = 1
     WHERE NOT EXISTS (
         SELECT 1 FROM dbo.WORKBENCH_MODULE_DIRTY d WHERE d.M_IDX = t.M_IDX AND d.DIRTY_TAG = 1))
    THROW 52903, N'受影响模块未标记待发布，快照会被判为静默落后，迁移中止。', 1;

COMMIT;

PRINT N'== 收口：复合格主从成对，受影响模块待重发布 ==';
