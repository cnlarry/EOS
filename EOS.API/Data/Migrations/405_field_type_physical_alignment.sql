-- ============================================================================
-- EOS.ERP migration 405: 把 F_TYPE 对齐到物理列类型（数值/日期/bit）
-- ----------------------------------------------------------------------------
-- 症状：模块 18010110（宿舍设定）建单稳定 500：
--   SqlException: 在将 nvarchar 值 'ZZSBUF4' 转换成数据类型 smallint 时失败。
--   —— 用户在一个"看起来是文本框"的格子里填了非数字，服务端把它当字符串交给 SQL。
--
-- 根因：FIELDS.F_TYPE 写的是字符串族（nvarchar），而物理列是 smallint/datetime/bit。
-- 这套元数据同时决定三件事，任一处都会错：
--   ① 前端按 F_TYPE 选控件 → 数值列渲染成文本框，用户能敲进任何字符；
--   ② RecordPayloadValidator.TryConvert 按 F_TYPE 校验 → 非数字一样放行；
--   ③ 只有到 SQL 写入时才转换失败 → 抛 SqlException → 对外 500（而不是可读的 400）。
-- 全库这类"有害不一致"共上百处，集中在 SERIAL_NO（明细序号）、BILL_DATE（单据日期）、
-- SEX / PERSON_SUM（人数与性别）等列上。
--
-- 做法：一次性把 F_TYPE 对齐到 sys.columns 的物理类型（只改"字符串族 → 数值/日期/bit"
-- 这一个方向）。反方向（F_TYPE 数值、物理是字符串）不动：那会让控件从数字框变成文本框，
-- 语义变化更大，需逐列确认。`nchar` vs `nvarchar` 这类同族差异也不动（都是字符）。
--
-- 副作用：F_TYPE 参与表单定义，改后需重新发布受影响的模块才在界面上生效；本迁移对
-- "已有当前快照"的模块标脏（否则会被 check-snapshot-staleness 判为静默落后）。
--
-- 幂等：条件本身是"当前仍不一致"，重复执行命中 0 行。
-- 回滚：无（回退会把可读的 400 变回 500，不建议）。
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
    THROW 53000, @GUARD_MESSAGE, 1;

BEGIN TRANSACTION;

/* ---------- 1. 对齐 F_TYPE ---------- */
UPDATE f
   SET f.F_TYPE = TYPE_NAME(c.user_type_id),
       f.LAST_UPDATE_BY = N'migration-405',
       f.LAST_UPDATE_DATE = SYSDATETIME()
  FROM dbo.FIELDS f
  JOIN sys.columns c
    ON c.object_id = OBJECT_ID(N'dbo.' + LTRIM(RTRIM(f.T_ID)))
   AND c.name = LTRIM(RTRIM(f.F_ID))
 WHERE LTRIM(RTRIM(ISNULL(f.F_TYPE, ''))) IN (N'nvarchar', N'varchar', N'nchar', N'char', N'ntext', N'text')
   AND TYPE_NAME(c.user_type_id) IN (N'smallint', N'int', N'bigint', N'tinyint', N'decimal', N'numeric',
                                     N'float', N'real', N'money', N'smallmoney',
                                     N'datetime', N'smalldatetime', N'date', N'bit');

DECLARE @fixed INT = @@ROWCOUNT;
PRINT N'== 已对齐 ' + CONVERT(NVARCHAR(10), @fixed) + N' 个字段的 F_TYPE ==';

/* ---------- 2. 标脏：受影响且已有当前快照的模块 ---------- */
MERGE dbo.WORKBENCH_MODULE_DIRTY AS D
USING (SELECT DISTINCT m.M_IDX
         FROM dbo.MODULES m
         JOIN dbo.FIELDS f
           ON LTRIM(RTRIM(f.T_ID)) = LTRIM(RTRIM(m.MASTER_TABLE))
           OR LTRIM(RTRIM(f.T_ID)) = LTRIM(RTRIM(m.DETAIL_TABLE))
        WHERE f.LAST_UPDATE_BY = N'migration-405'
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
/* 3.1 有害不一致必须清零 */
IF EXISTS (
    SELECT 1
      FROM dbo.FIELDS f
      JOIN sys.columns c
        ON c.object_id = OBJECT_ID(N'dbo.' + LTRIM(RTRIM(f.T_ID)))
       AND c.name = LTRIM(RTRIM(f.F_ID))
     WHERE LTRIM(RTRIM(ISNULL(f.F_TYPE, ''))) IN (N'nvarchar', N'varchar', N'nchar', N'char', N'ntext', N'text')
       AND TYPE_NAME(c.user_type_id) IN (N'smallint', N'int', N'bigint', N'tinyint', N'decimal', N'numeric',
                                         N'float', N'real', N'money', N'smallmoney',
                                         N'datetime', N'smalldatetime', N'date', N'bit'))
    THROW 53001, N'收口失败：仍有 F_TYPE 与物理列不一致的字段，迁移中止。', 1;

/* 3.2 有当前快照的受影响模块必须已标脏，否则快照会被判静默落后 */
IF EXISTS (
    SELECT 1
      FROM dbo.MODULES m
      JOIN dbo.WORKBENCH_DEFINITION_SNAPSHOT S ON S.M_IDX = m.M_IDX AND S.IS_CURRENT = 1
     WHERE EXISTS (SELECT 1 FROM dbo.FIELDS f
                    WHERE f.LAST_UPDATE_BY = N'migration-405'
                      AND (LTRIM(RTRIM(f.T_ID)) = LTRIM(RTRIM(m.MASTER_TABLE))
                           OR LTRIM(RTRIM(f.T_ID)) = LTRIM(RTRIM(m.DETAIL_TABLE))))
       AND NOT EXISTS (SELECT 1 FROM dbo.WORKBENCH_MODULE_DIRTY D
                        WHERE D.M_IDX = m.M_IDX AND D.DIRTY_TAG = 1))
    THROW 53002, N'收口失败：受影响模块未标记待发布，迁移中止。', 1;

COMMIT;

PRINT N'== 收口：F_TYPE 已与物理列对齐，受影响模块待重发布 ==';
