-- ============================================================================
-- EOS.ERP migration 440: 口径视图的数值列声明对齐物理类型
-- ----------------------------------------------------------------------------
-- 背景：V_COP_ORDER_D_UNDELIVERED（迁移 339）与 V_COP_ORDER_D_UNPRODUCED
-- （迁移 318）建视图时，FIELDS 的 F_TYPE 按当时基表的声明复制，那一代数值列是
-- float。ADR-038 的数值批量遍历的是基表物理列，视图上的列声明没跟着走：
-- 物理列随基表变成 decimal(18,4)/(18,6)/(9,6)，视图里的加减法还会把精度顶到
-- decimal(19,6)，而 F_TYPE 仍写着 float。
--
-- 后果：声明比物理宽，校验器按 float 分支解析（合法且不校验范围），越界值会一路
-- 漏到 SQL 变成 500（与迁移 405/406 处理的是同一个缝隙）。当前两个模块都是只读
-- 查询页（MODULES.UNIFIED_FORM_ENABLED / UNIFIED_FORM_EDIT_ENABLED 均为 0），
-- 尚无写路径可达，但声明与物理不一致本身会误导读取端，且门禁
-- check-field-type-alignment 会持续报警。
--
-- 做法：把这两个视图上「数值族不一致」的字段 F_TYPE 改为 decimal——这些字段的
-- 物理类型只可能是 decimal/numeric，其余族别不进本迁移：
--   · 字符串：声明 nvarchar/char 比物理 nchar 窄，不会放行越界值，改了反而会动
--     显示与比较口径；
--   · 日期：声明 date 与物理 datetime 是"显示到日 / 存到分钟"的刻意差别。
--
-- 幂等：第二次执行改 0 行，不重复标脏。
-- 回滚：F_TYPE 改回 float（不建议：那正是本迁移要消除的声明宽于物理）。
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
    THROW 52830, @GUARD_MESSAGE, 1;

BEGIN TRANSACTION;

/* ---------- 0. 前置断言：两个视图必须都在 ---------- */
IF OBJECT_ID(N'dbo.V_COP_ORDER_D_UNDELIVERED', N'V') IS NULL
    THROW 52831, N'视图 V_COP_ORDER_D_UNDELIVERED 不存在，迁移中止。', 1;
IF OBJECT_ID(N'dbo.V_COP_ORDER_D_UNPRODUCED', N'V') IS NULL
    THROW 52832, N'视图 V_COP_ORDER_D_UNPRODUCED 不存在，迁移中止。', 1;

/* ---------- 1. 命中集：数值族声明与物理不一致的字段 ----------
   判据与 scripts/check-field-type-alignment.ps1 同一条（两边不一致时以门禁为准，
   这里是它的落地）。只收这两个视图，避免误伤其它表。 */
DECLARE @Mismatch TABLE (T_ID NVARCHAR(100), F_ID NVARCHAR(100));

INSERT @Mismatch (T_ID, F_ID)
SELECT LTRIM(RTRIM(f.T_ID)), LTRIM(RTRIM(f.F_ID))
  FROM dbo.FIELDS f
  JOIN sys.columns c
    ON c.object_id = OBJECT_ID(N'dbo.' + LTRIM(RTRIM(f.T_ID)))
   AND c.name = LTRIM(RTRIM(f.F_ID))
 WHERE LTRIM(RTRIM(f.T_ID)) IN (N'V_COP_ORDER_D_UNDELIVERED', N'V_COP_ORDER_D_UNPRODUCED')
   AND (
        (LTRIM(RTRIM(ISNULL(f.F_TYPE, N''))) IN (N'float', N'real', N'money', N'smallmoney')
         AND TYPE_NAME(c.user_type_id) IN (N'smallint', N'int', N'bigint', N'tinyint', N'decimal', N'numeric'))
        OR
        (LTRIM(RTRIM(ISNULL(f.F_TYPE, N''))) IN (N'smallint', N'int', N'bigint', N'tinyint', N'decimal', N'numeric')
         AND TYPE_NAME(c.user_type_id) IN (N'float', N'real', N'money', N'smallmoney'))
       );

DECLARE @before INT = (SELECT COUNT(*) FROM @Mismatch);
PRINT N'== 待对齐字段 ' + CONVERT(NVARCHAR(10), @before) + N' 个 ==';

/* ---------- 2. 对齐声明 ---------- */
UPDATE f
   SET f.F_TYPE = N'decimal',
       f.LAST_UPDATE_BY = N'EOS-MIG',
       f.LAST_UPDATE_DATE = SYSDATETIME()
  FROM dbo.FIELDS f
  JOIN @Mismatch m
    ON LTRIM(RTRIM(f.T_ID)) = m.T_ID
   AND LTRIM(RTRIM(f.F_ID)) = m.F_ID;

DECLARE @changed INT = @@ROWCOUNT;
PRINT N'== 已对齐 ' + CONVERT(NVARCHAR(10), @changed) + N' 个字段的 F_TYPE ==';

/* ---------- 3. 标脏：F_TYPE 随定义进快照 ----------
   两个视图上的模块当前都没有已发布快照（只读查询页），这段今天是空跑；
   留着是为了将来给它们发布时不再补标脏。只在本次真的改了时执行。 */
IF @changed > 0
BEGIN
    MERGE dbo.WORKBENCH_MODULE_DIRTY AS D
    USING (SELECT DISTINCT s.M_IDX
             FROM dbo.WORKBENCH_DEFINITION_SNAPSHOT s
             JOIN dbo.MODULES mm ON mm.M_IDX = s.M_IDX
            WHERE s.IS_CURRENT = 1
              AND (LTRIM(RTRIM(ISNULL(mm.MASTER_TABLE, N''))) IN (N'V_COP_ORDER_D_UNDELIVERED', N'V_COP_ORDER_D_UNPRODUCED')
                OR LTRIM(RTRIM(ISNULL(mm.DETAIL_TABLE, N''))) IN (N'V_COP_ORDER_D_UNDELIVERED', N'V_COP_ORDER_D_UNPRODUCED'))) AS S
       ON D.M_IDX = S.M_IDX
    WHEN MATCHED THEN UPDATE SET D.DIRTY_TAG = 1, D.LAST_MODIFIED_BY = N'EOS-MIG', D.LAST_MODIFIED_AT = SYSDATETIME()
    WHEN NOT MATCHED THEN INSERT (M_IDX, DIRTY_TAG, LAST_MODIFIED_BY, LAST_MODIFIED_AT)
        VALUES (S.M_IDX, 1, N'EOS-MIG', SYSDATETIME());

    PRINT N'== 已标记 ' + CONVERT(NVARCHAR(10), @@ROWCOUNT) + N' 个模块待重发布 ==';
END

/* ---------- 4. 收口断言：取向稳定，改完必须清零 ---------- */
IF EXISTS (
    SELECT 1
      FROM dbo.FIELDS f
      JOIN sys.columns c
        ON c.object_id = OBJECT_ID(N'dbo.' + LTRIM(RTRIM(f.T_ID)))
       AND c.name = LTRIM(RTRIM(f.F_ID))
     WHERE LTRIM(RTRIM(f.T_ID)) IN (N'V_COP_ORDER_D_UNDELIVERED', N'V_COP_ORDER_D_UNPRODUCED')
       AND LTRIM(RTRIM(ISNULL(f.F_TYPE, N''))) IN (N'float', N'real', N'money', N'smallmoney')
       AND TYPE_NAME(c.user_type_id) IN (N'smallint', N'int', N'bigint', N'tinyint', N'decimal', N'numeric'))
    THROW 52833, N'仍有字段声明为浮点/货币族而物理是定点/整型，迁移中止。', 1;

COMMIT;

PRINT N'== 收口：两个口径视图的数值列声明已与物理类型一致 ==';
