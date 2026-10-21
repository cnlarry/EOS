-- ============================================================================
-- EOS.ERP migration 439: 其它付款凭证主表汇率改为只读联动
-- ----------------------------------------------------------------------------
-- 背景：PUR_PAY_OTHER.CURR_RATE 与 PUR_PREPAY_M.CURR_RATE 同族——两表的主表汇率
-- 都由「币别」(CURR_ID) 与「供应商」(SUPPLIER_ID) 两个选择器的 RETURN_ITEMS 回填
-- （数据源 1619/1630 与 1642/1649）。预付帐款单把它当只读联动列，其它付款凭证
-- 却可手工编辑：手工值与随后的选择器回填互相覆盖，两张同族单据的录入口径也不一致。
-- 本迁移把 PUR_PAY_OTHER.CURR_RATE 对齐为只读。
--
-- 做法：
--   ① FIELDS.IS_READONLY=1；
--   ② 标脏 170204：只读属性随定义进快照，不重新发布则运行态仍是可编辑，
--      且会被 check-snapshot-staleness 判为静默落后。
--
-- 边界（断言，任一条不成立即整笔中止）：
--   · 目标字段必须存在且不是虚拟列（虚拟列的值由表达式算，不该有编辑语义）；
--   · 必须有启用的数据源仍把 CURR_RATE 列为回填目标（否则改成只读等于让字段永远填不上）；
--   · 同族的 PUR_PREPAY_M.CURR_RATE 必须仍是只读（口径来源变了就不该照此对齐）；
--   · 终态：IS_READONLY=1，且有当前快照的模块已标记待发布。
--
-- 幂等：重复执行改 0 行，不重复标脏。
-- 回滚：IS_READONLY 置回 0 并重新发布 170204。
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
    THROW 52820, @GUARD_MESSAGE, 1;

BEGIN TRANSACTION;

DECLARE @table NVARCHAR(100) = N'PUR_PAY_OTHER';
DECLARE @field NVARCHAR(100) = N'CURR_RATE';

/* ---------- 0. 口径断言 ---------- */
IF NOT EXISTS (
    SELECT 1
      FROM dbo.FIELDS
     WHERE LTRIM(RTRIM(T_ID)) = @table
       AND LTRIM(RTRIM(F_ID)) = @field
       AND COALESCE(IS_VIRTUAL, 0) = 0)
    THROW 52821, N'PUR_PAY_OTHER.CURR_RATE 不存在或已是虚拟列，迁移中止。', 1;

IF NOT EXISTS (
    SELECT 1
      FROM dbo.FIELDS
     WHERE LTRIM(RTRIM(T_ID)) = N'PUR_PREPAY_M'
       AND LTRIM(RTRIM(F_ID)) = @field
       AND IS_READONLY = 1)
    THROW 52822, N'同族的 PUR_PREPAY_M.CURR_RATE 已不是只读，迁移中止。', 1;

IF NOT EXISTS (
    SELECT 1
      FROM dbo.FIELD_DATASOURCE
     WHERE LTRIM(RTRIM(T_ID)) = @table
       AND COALESCE(ACTIVE_TAG, 0) = 1
       AND LTRIM(RTRIM(F_ID)) IN (N'CURR_ID', N'SUPPLIER_ID')
       AND CHARINDEX(N'"CURR_RATE"', ISNULL(RETURN_ITEMS, N'')) > 0)
    THROW 52823, N'没有启用的数据源把 CURR_RATE 列为回填目标，改成只读会让该字段永远填不上，迁移中止。', 1;

/* ---------- 1. 改为只读 ---------- */
UPDATE dbo.FIELDS
   SET IS_READONLY = 1,
       LAST_UPDATE_BY = N'EOS-MIG',
       LAST_UPDATE_DATE = SYSDATETIME()
 WHERE LTRIM(RTRIM(T_ID)) = @table
   AND LTRIM(RTRIM(F_ID)) = @field
   AND COALESCE(IS_READONLY, 0) = 0;

DECLARE @readonlyChanged INT = @@ROWCOUNT;
PRINT N'== 已把 ' + @table + N'.' + @field + N' 改为只读（影响 ' + CONVERT(NVARCHAR(10), @readonlyChanged) + N' 行）==';

/* ---------- 2. 标脏：只读属性进定义快照 ----------
   只在本次真的改了时标脏：脚本在手工落地后还会被 DbUp 再执行一次，
   那时字段已是只读态、快照也已重发布，再标脏只会平白制造待发布积压。 */
IF @readonlyChanged > 0
BEGIN
    MERGE dbo.WORKBENCH_MODULE_DIRTY AS D
    USING (SELECT s.M_IDX
             FROM dbo.WORKBENCH_DEFINITION_SNAPSHOT s
            WHERE s.M_IDX = 170204
              AND s.IS_CURRENT = 1) AS S
       ON D.M_IDX = S.M_IDX
    WHEN MATCHED THEN UPDATE SET D.DIRTY_TAG = 1, D.LAST_MODIFIED_BY = N'EOS-MIG', D.LAST_MODIFIED_AT = SYSDATETIME()
    WHEN NOT MATCHED THEN INSERT (M_IDX, DIRTY_TAG, LAST_MODIFIED_BY, LAST_MODIFIED_AT)
        VALUES (S.M_IDX, 1, N'EOS-MIG', SYSDATETIME());

    PRINT N'== 已标记 ' + CONVERT(NVARCHAR(10), @@ROWCOUNT) + N' 个模块待重发布 ==';
END
ELSE
    PRINT N'== 字段已是只读态，跳过标脏（重复执行不产生新的待发布积压）==';

/* ---------- 3. 收口断言 ---------- */
IF EXISTS (
    SELECT 1 FROM dbo.FIELDS
     WHERE LTRIM(RTRIM(T_ID)) = @table
       AND LTRIM(RTRIM(F_ID)) = @field
       AND COALESCE(IS_READONLY, 0) = 0)
    THROW 52824, N'字段仍是可编辑态，迁移中止。', 1;

IF @readonlyChanged > 0
   AND EXISTS (
       SELECT 1
         FROM dbo.WORKBENCH_DEFINITION_SNAPSHOT s
        WHERE s.M_IDX = 170204
          AND s.IS_CURRENT = 1
          AND NOT EXISTS (
              SELECT 1 FROM dbo.WORKBENCH_MODULE_DIRTY d
               WHERE d.M_IDX = s.M_IDX AND d.DIRTY_TAG = 1))
    THROW 52825, N'受影响模块未标记待发布，快照会被判为静默落后，迁移中止。', 1;

COMMIT;

PRINT N'== 收口：其它付款凭证主表汇率改为只读联动，170204 待重发布 ==';
