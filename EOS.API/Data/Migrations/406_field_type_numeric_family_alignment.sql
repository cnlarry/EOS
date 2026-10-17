-- ============================================================================
-- EOS.ERP migration 406: 数值族内部的 F_TYPE 对齐（float/real/money ↔ 整型/定点）
-- ----------------------------------------------------------------------------
-- 症状：模块 18010110（宿舍设定）的 `BED_SUM`（床位数）提交一个 40 位数字 → **500**：
--   SqlException: 类型 smallint 发生算术溢出错误，值 = 1.0000000000000000000000000000000000000000E+40
-- 这是一次负面边界探针（C 层 HUGE_NUMBER 因子）打出来的，真实用户粘贴一串长数字同样会 500。
--
-- 根因：`HR_DORM.BED_SUM` 物理是 `smallint`，而 `FIELDS.F_TYPE` 写的是 `float`。
-- 校验器按 **F_TYPE** 决定用哪条转换分支：
--   · `float` → `double.TryParse`，40 位数字合法（1E+40），且不做范围校验；
--   · `smallint` → `int.Parse` 会 OverflowException → 变成可读的 400 INVALID_VALUE。
-- 于是"声明得比物理宽"的列，脏值总能绕过校验漏到 SQL，再以 500 收场。
--
-- 迁移 405 只处理了"字符串族 → 数值/日期/bit"这一个方向，刻意没动数值族内部；
-- 本次实测证明**数值族内部的不一致同样致命**（它就是 500 的来源）。全库共 19 处。
--
-- 做法：把这类字段的 F_TYPE 对齐到物理列类型，两个方向都收：
--   ① 声明 float/real/money/smallmoney，物理是整型/定点（本次的 500 来源）；
--   ② 声明整型/定点，物理是 float/real/money（反向：会把能放小数的列当整数渲染）。
-- `decimal` 与 `numeric` 是同义别名，不在此列（避免制造无谓差异）。
-- 物理本来就是 float 的列（ADR-038 声明的 NON_NUMERIC 遗留）不动。
--
-- 副作用：F_TYPE 参与表单定义，改后需重新发布受影响模块才在界面上生效；本迁移对
-- "已有当前快照"的模块标脏（否则会被 check-snapshot-staleness 判为静默落后）。
-- 幂等：条件本身是"当前仍不一致"，重复执行命中 0 行。
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
    THROW 53100, @GUARD_MESSAGE, 1;

BEGIN TRANSACTION;

/* ---------- 1. 对齐数值族内部的 F_TYPE ---------- */
UPDATE f
   SET f.F_TYPE = TYPE_NAME(c.user_type_id),
       f.LAST_UPDATE_BY = N'migration-406',
       f.LAST_UPDATE_DATE = SYSDATETIME()
  FROM dbo.FIELDS f
  JOIN sys.columns c
    ON c.object_id = OBJECT_ID(N'dbo.' + LTRIM(RTRIM(f.T_ID)))
   AND c.name = LTRIM(RTRIM(f.F_ID))
 WHERE (
         -- ① 声明比物理宽：float/real/money 声明，物理是整型/定点
         (LTRIM(RTRIM(ISNULL(f.F_TYPE, ''))) IN (N'float', N'real', N'money', N'smallmoney')
          AND TYPE_NAME(c.user_type_id) IN (N'smallint', N'int', N'bigint', N'tinyint', N'decimal', N'numeric'))
         OR
         -- ② 反向：声明是整型/定点，物理却是浮点/货币
         (LTRIM(RTRIM(ISNULL(f.F_TYPE, ''))) IN (N'smallint', N'int', N'bigint', N'tinyint', N'decimal', N'numeric')
          AND TYPE_NAME(c.user_type_id) IN (N'float', N'real', N'money', N'smallmoney'))
       )
   -- decimal ↔ numeric 是同义别名，不制造无谓差异
   AND NOT (LTRIM(RTRIM(ISNULL(f.F_TYPE, ''))) = N'numeric' AND TYPE_NAME(c.user_type_id) = N'decimal')
   AND NOT (LTRIM(RTRIM(ISNULL(f.F_TYPE, ''))) = N'decimal' AND TYPE_NAME(c.user_type_id) = N'numeric');

DECLARE @fixed INT = @@ROWCOUNT;
PRINT N'== 已对齐 ' + CONVERT(NVARCHAR(10), @fixed) + N' 个字段的数值族 F_TYPE ==';

/* ---------- 2. 标脏：受影响且已有当前快照的模块 ---------- */
MERGE dbo.WORKBENCH_MODULE_DIRTY AS D
USING (SELECT DISTINCT m.M_IDX
         FROM dbo.MODULES m
         JOIN dbo.FIELDS f
           ON LTRIM(RTRIM(f.T_ID)) = LTRIM(RTRIM(m.MASTER_TABLE))
           OR LTRIM(RTRIM(f.T_ID)) = LTRIM(RTRIM(m.DETAIL_TABLE))
        WHERE f.LAST_UPDATE_BY = N'migration-406'
          AND EXISTS (SELECT 1 FROM dbo.WORKBENCH_DEFINITION_SNAPSHOT S
                       WHERE S.M_IDX = m.M_IDX AND S.IS_CURRENT = 1)) AS S
   ON D.M_IDX = S.M_IDX
WHEN MATCHED THEN
    UPDATE SET D.DIRTY_TAG = 1, D.LAST_MODIFIED_BY = N'DbUp', D.LAST_MODIFIED_AT = SYSDATETIME()
WHEN NOT MATCHED THEN
    INSERT (M_IDX, DIRTY_TAG, LAST_MODIFIED_BY, LAST_MODIFIED_AT)
    VALUES (S.M_IDX, 1, N'DbUp', SYSDATETIME());

PRINT N'== 已标记 ' + CONVERT(NVARCHAR(10), @@ROWCOUNT) + N' 个模块待重发布 ==';

/* ---------- 3. 收口断言 ---------- */
/* 3.1 数值族内部不再有"声明与物理不一致"的字段 */
IF EXISTS (
    SELECT 1
      FROM dbo.FIELDS f
      JOIN sys.columns c
        ON c.object_id = OBJECT_ID(N'dbo.' + LTRIM(RTRIM(f.T_ID)))
       AND c.name = LTRIM(RTRIM(f.F_ID))
     WHERE (
             (LTRIM(RTRIM(ISNULL(f.F_TYPE, ''))) IN (N'float', N'real', N'money', N'smallmoney')
              AND TYPE_NAME(c.user_type_id) IN (N'smallint', N'int', N'bigint', N'tinyint', N'decimal', N'numeric'))
             OR
             (LTRIM(RTRIM(ISNULL(f.F_TYPE, ''))) IN (N'smallint', N'int', N'bigint', N'tinyint', N'decimal', N'numeric')
              AND TYPE_NAME(c.user_type_id) IN (N'float', N'real', N'money', N'smallmoney'))
           )
       AND NOT (LTRIM(RTRIM(ISNULL(f.F_TYPE, ''))) = N'numeric' AND TYPE_NAME(c.user_type_id) = N'decimal')
       AND NOT (LTRIM(RTRIM(ISNULL(f.F_TYPE, ''))) = N'decimal' AND TYPE_NAME(c.user_type_id) = N'numeric'))
    THROW 53101, N'收口失败：仍有数值族 F_TYPE 与物理列不一致的字段，迁移中止。', 1;

/* 3.2 有当前快照的受影响模块必须已标脏 */
IF EXISTS (
    SELECT 1
      FROM dbo.MODULES m
      JOIN dbo.WORKBENCH_DEFINITION_SNAPSHOT S ON S.M_IDX = m.M_IDX AND S.IS_CURRENT = 1
     WHERE EXISTS (SELECT 1 FROM dbo.FIELDS f
                    WHERE f.LAST_UPDATE_BY = N'migration-406'
                      AND (LTRIM(RTRIM(f.T_ID)) = LTRIM(RTRIM(m.MASTER_TABLE))
                           OR LTRIM(RTRIM(f.T_ID)) = LTRIM(RTRIM(m.DETAIL_TABLE))))
       AND NOT EXISTS (SELECT 1 FROM dbo.WORKBENCH_MODULE_DIRTY D
                        WHERE D.M_IDX = m.M_IDX AND D.DIRTY_TAG = 1))
    THROW 53102, N'收口失败：受影响模块未标记待发布，迁移中止。', 1;

COMMIT;

PRINT N'== 收口：数值族 F_TYPE 已与物理列对齐，受影响模块待重发布 ==';
