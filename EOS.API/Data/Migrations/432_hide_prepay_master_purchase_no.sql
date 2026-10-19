-- ============================================================================
-- EOS.ERP migration 432: 预付帐款单主表不再显示「采购单号」
-- ----------------------------------------------------------------------------
-- 背景：PUR_PREPAY_M.PURCHASE_NO 是虚拟列，取值表达式是 PUR_PREPAY_D.PURCHASE_NO，
-- 而主表与明细按 (PREPAY_TYPE, PREPAY_NO) 关联是 1:N（实测一张单最多 20 条明细）。
-- 把它当主表列展示，等于让主表列表按明细行数放大：分页 total 仍按主表算（1 行），
-- 实际返回 20 行；行标识 PREPAY_TYPE+PREPAY_NO 随之重复，前端的 React 行键撞键
-- （LESSONS.md L110：窗口化下表体会被画成大面积空白）。
-- 采购单号本就是明细行的信息，明细表 PUR_PREPAY_D 的同名列照常显示
-- （170203 的明细版式与明细默认列里都保留着它）。
--
-- 做法：
--   ① FIELDS.IS_VISIBLE=0：运行态字段视图在 FormFieldSelector.Select 就先剔除不可见字段；
--   ② 删掉引用该字段的版式行与主表默认列（沿用迁移 254 的"版式不保留不显示字段"口径；
--      明细表的同名列不在删除范围）；
--   ③ 标脏 170203 / 170206：定义快照随发布更新，否则会被 check-snapshot-staleness
--      判为静默落后（配置变了却没有任何重发布信号）。
--
-- 边界（断言，任一条不成立即中止整笔）：
--   · 目标字段必须仍是虚拟列、取值仍来自 PUR_PREPAY_D（口径被改过就不该照此隐藏）；
--   · 收口后不得再有不显示字段的版式行与主表默认列；
--   · 有当前快照的受影响模块必须都已标记待发布。
--
-- 幂等：重复执行改 0 行；断言都基于终态，仍成立。
-- 回滚：把 IS_VISIBLE 改回 1 并重新发布；版式行与默认列需在设计态/列设置里重新加回。
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
    THROW 52810, @GUARD_MESSAGE, 1;

BEGIN TRANSACTION;

DECLARE @table NVARCHAR(100) = N'PUR_PREPAY_M';
DECLARE @field NVARCHAR(100) = N'PURCHASE_NO';

/* ---------- 0. 口径断言：这一列确实取自明细，否则不按本脚本处理 ---------- */
IF NOT EXISTS (
    SELECT 1
      FROM dbo.FIELDS
     WHERE LTRIM(RTRIM(T_ID)) = @table
       AND LTRIM(RTRIM(F_ID)) = @field
       AND COALESCE(IS_VIRTUAL, 0) = 1
       AND UPPER(LTRIM(RTRIM(ISNULL(VIRTUAL_EXP, N'')))) LIKE N'PUR_PREPAY_D.%')
    THROW 52811, N'PUR_PREPAY_M.PURCHASE_NO 不再是"取自明细 PUR_PREPAY_D"的虚拟列，迁移中止。', 1;

/* ---------- 1. 运行态不再显示 ---------- */
UPDATE dbo.FIELDS
   SET IS_VISIBLE = 0,
       LAST_UPDATE_BY = N'EOS-MIG',
       LAST_UPDATE_DATE = SYSDATETIME()
 WHERE LTRIM(RTRIM(T_ID)) = @table
   AND LTRIM(RTRIM(F_ID)) = @field
   AND COALESCE(IS_VISIBLE, 1) <> 0;

DECLARE @visibilityChanged INT = @@ROWCOUNT;
PRINT N'== 已隐藏字段 ' + @table + N'.' + @field + N'（影响 ' + CONVERT(NVARCHAR(10), @visibilityChanged) + N' 行）==';

/* ---------- 2. 清掉引用它的版式行与主表默认列（明细表的同名列保留） ---------- */
DELETE FROM dbo.MODULE_FORM_LAYOUT
 WHERE LTRIM(RTRIM(T_ID)) = @table
   AND LTRIM(RTRIM(F_ID)) = @field;

PRINT N'== 已删除 ' + CONVERT(NVARCHAR(10), @@ROWCOUNT) + N' 行引用该字段的版式行 ==';

DELETE FROM dbo.SYSQL_DEFAULT
 WHERE LTRIM(RTRIM(T_ID)) = @table
   AND LTRIM(RTRIM(T_ID_R)) = @table
   AND LTRIM(RTRIM(F_ID)) = @field;

PRINT N'== 已删除 ' + CONVERT(NVARCHAR(10), @@ROWCOUNT) + N' 行主表默认列 ==';

/* ---------- 3. 标脏：已有当前快照的受影响模块（否则快照会被判为静默落后） ----------
   只在"本次真的改了可见性"时标脏：这个脚本在手工落地后还会被 DbUp 在下次启动时再执行一次，
   那时字段已是隐藏态、快照也已重发布，再标脏只会平白制造一批"待发布"积压。 */
IF @visibilityChanged > 0
BEGIN
    MERGE dbo.WORKBENCH_MODULE_DIRTY AS D
    USING (SELECT s.M_IDX
             FROM dbo.WORKBENCH_DEFINITION_SNAPSHOT s
            WHERE s.M_IDX IN (170203, 170206)
              AND s.IS_CURRENT = 1) AS S
       ON D.M_IDX = S.M_IDX
    WHEN MATCHED THEN UPDATE SET D.DIRTY_TAG = 1, D.LAST_MODIFIED_BY = N'EOS-MIG', D.LAST_MODIFIED_AT = SYSDATETIME()
    WHEN NOT MATCHED THEN INSERT (M_IDX, DIRTY_TAG, LAST_MODIFIED_BY, LAST_MODIFIED_AT)
        VALUES (S.M_IDX, 1, N'EOS-MIG', SYSDATETIME());

    PRINT N'== 已标记 ' + CONVERT(NVARCHAR(10), @@ROWCOUNT) + N' 个模块待重发布 ==';
END
ELSE
    PRINT N'== 字段已是隐藏态，跳过标脏（重复执行不产生新的待发布积压）==';

/* ---------- 4. 收口断言 ---------- */
IF EXISTS (
    SELECT 1 FROM dbo.FIELDS
     WHERE LTRIM(RTRIM(T_ID)) = @table
       AND LTRIM(RTRIM(F_ID)) = @field
       AND COALESCE(IS_VISIBLE, 1) <> 0)
    THROW 52812, N'字段仍处于显示态，迁移中止。', 1;

IF EXISTS (
    SELECT 1 FROM dbo.MODULE_FORM_LAYOUT
     WHERE LTRIM(RTRIM(T_ID)) = @table
       AND LTRIM(RTRIM(F_ID)) = @field)
    THROW 52813, N'仍存在引用该字段的版式行，迁移中止。', 1;

IF EXISTS (
    SELECT 1 FROM dbo.SYSQL_DEFAULT
     WHERE LTRIM(RTRIM(T_ID)) = @table
       AND LTRIM(RTRIM(T_ID_R)) = @table
       AND LTRIM(RTRIM(F_ID)) = @field)
    THROW 52814, N'仍存在引用该字段的主表默认列，迁移中止。', 1;

IF @visibilityChanged > 0
   AND EXISTS (
       SELECT 1
         FROM dbo.WORKBENCH_DEFINITION_SNAPSHOT s
        WHERE s.M_IDX IN (170203, 170206)
          AND s.IS_CURRENT = 1
          AND NOT EXISTS (
              SELECT 1 FROM dbo.WORKBENCH_MODULE_DIRTY d
               WHERE d.M_IDX = s.M_IDX AND d.DIRTY_TAG = 1))
    THROW 52815, N'受影响模块未标记待发布，快照会被判为静默落后，迁移中止。', 1;

COMMIT;

PRINT N'== 收口：预付帐款单主表不再显示采购单号，170203/170206 待重发布 ==';
