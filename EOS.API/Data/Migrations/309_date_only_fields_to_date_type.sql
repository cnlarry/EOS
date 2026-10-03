-- ============================================================================
-- EOS.ERP migration 309: 日期类字段的类型与显示格式一并收口
-- ----------------------------------------------------------------------------
-- 统一表单按 FIELDS.F_TYPE 决定录入控件：datetime 渲染成"日期 + 时分秒"，
-- date 渲染成纯日期。而有一批字段的 DISPLAY_FORMAT 早已声明为 'yyyy-MM-dd'
-- （显示层一直按纯日期渲染），类型却还是 datetime —— 录入端于是要求用户填时间，
-- 与显示口径自相矛盾（最近入库日 / 最近出库日 / 最近交易日 / 发证日期 等）。
-- 另有一批 datetime 字段干脆没有 DISPLAY_FORMAT，而**空格式的显示回落是"只到日"**
-- （渲染层回落到 toLocaleDateString），列表侧时间分量被吞掉——定义校验器
-- （WorkbenchDefinitionValidator 的 datetime_display_format_hint）对此一直报警告。
--
-- 本迁移把两件事一次做完：
--   ① **类型收口**：显示格式已是纯日期的 datetime 家族字段 → F_TYPE = date，
--      并把 DISPLAY_FORMAT 显式写成 'yyyy-MM-dd'（幂等规范化）；
--   ② **格式收口**：其余 datetime 家族字段 → DISPLAY_FORMAT = 'yyyy-MM-dd HH:mm:ss'，
--      让列表 / 报表 / 打印侧的时间分量显式可见。
--
-- **小时用 HH，不用 hh**：本平台的日期 token 与 .NET 一致——`hh` 是 **12 小时制**且无
-- AM/PM 设计符，15:28 会渲染成 03:28。库内既有的 'yyyyMMdd hhmmss' 正是这个问题，
-- 本迁移把它一并规范成 24 小时制。
--
-- **只动字段元数据，不动物理列**：F_TYPE 改变的是录入控件与显示口径，库里既有的
-- 时间部分不截断，存储过程里按 datetime 比较与赋值也不受影响——这与
-- PRODUCT.LAST_CHECK_DATE 的现状一致（元数据是 date、物理列仍是 datetime）。
--
-- 类型收口的范围口径（三条同时成立）：
--   F_TYPE ∈ datetime / smalldatetime / datetime2；
--   DISPLAY_FORMAT = 'yyyy-MM-dd'：显示格式已经声明"只到日"；
--   字段描述不含"时间"：打卡时间 / 处理时间 / 完成时间 / 预计开始·结束时间
--   这类名字就写着时间的字段另算，不因为显示格式凑巧只到日就丢掉时间语义
--   （它们归入 ②，仍在列表上带时分秒）。
-- 建立 / 修改 / 批核 / 结案等生命周期列（DISPLAY_FORMAT 为空）**类型不动**：
-- 时间部分对审计有意义，但格式由 ② 补齐，不再让它"列表上只剩日期"。
--
-- 幂等：三处更新都按目标值写入且带"尚未达标"的过滤，重复执行无副作用。
-- 回滚：F_TYPE 置回 datetime；DISPLAY_FORMAT 按各字段原有值恢复
-- （类型收口那批原值为 'yyyy-MM-dd'，其余多为空或 'yyyyMMdd hhmmss'）。
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
    THROW 52500, @GUARD_MESSAGE, 1;

IF COL_LENGTH(N'dbo.FIELDS', N'F_TYPE') IS NULL OR COL_LENGTH(N'dbo.FIELDS', N'DISPLAY_FORMAT') IS NULL
    THROW 52501, N'FIELDS 缺少 F_TYPE / DISPLAY_FORMAT 列，迁移中止。', 1;

IF COL_LENGTH(N'dbo.WORKBENCH_MODULE_DIRTY', N'DIRTY_TAG') IS NULL
    THROW 52502, N'WORKBENCH_MODULE_DIRTY 结构不符（缺少 DIRTY_TAG），迁移中止。', 1;

DECLARE @DATE_FORMAT NVARCHAR(50) = N'yyyy-MM-dd';
DECLARE @DATETIME_FORMAT NVARCHAR(50) = N'yyyy-MM-dd HH:mm:ss';

/* ---------- ① 类型收口的目标清单 ---------- */
/* 更新、脏标记、断言三处共用这一份清单，不会各算各的 */
DECLARE @DateTarget TABLE (T_ID NVARCHAR(200) NOT NULL, F_ID NVARCHAR(200) NOT NULL, PRIMARY KEY (T_ID, F_ID));

INSERT @DateTarget (T_ID, F_ID)
SELECT DISTINCT RTRIM(T_ID), RTRIM(F_ID)
  FROM dbo.FIELDS
 WHERE F_TYPE IN (N'datetime', N'smalldatetime', N'datetime2')
   AND RTRIM(ISNULL(DISPLAY_FORMAT, N'')) = @DATE_FORMAT
   AND CHARINDEX(N'时间', ISNULL(F_DESC, N'')) = 0;

/* ---------- ② 类型与格式一起收口 ---------- */
UPDATE f
   SET F_TYPE = N'date',
       DISPLAY_FORMAT = @DATE_FORMAT,
       LAST_UPDATE_BY = N'DbUp',
       LAST_UPDATE_DATE = GETDATE()
  FROM dbo.FIELDS AS f
  JOIN @DateTarget AS t ON RTRIM(f.T_ID) = t.T_ID AND RTRIM(f.F_ID) = t.F_ID
 WHERE f.F_TYPE IN (N'datetime', N'smalldatetime', N'datetime2');

/* ---------- ③ 其余 datetime 家族字段补齐时间格式 ---------- */
/* 顺序即互斥：② 之后那批已是 date，不再落进这里 */
UPDATE dbo.FIELDS
   SET DISPLAY_FORMAT = @DATETIME_FORMAT,
       LAST_UPDATE_BY = N'DbUp',
       LAST_UPDATE_DATE = GETDATE()
 WHERE F_TYPE IN (N'datetime', N'smalldatetime', N'datetime2')
   AND RTRIM(ISNULL(DISPLAY_FORMAT, N'')) <> @DATETIME_FORMAT;

/* ---------- ④ date 字段的格式补齐（当前应为 0 行，防将来漏配） ---------- */
UPDATE dbo.FIELDS
   SET DISPLAY_FORMAT = @DATE_FORMAT,
       LAST_UPDATE_BY = N'DbUp',
       LAST_UPDATE_DATE = GETDATE()
 WHERE F_TYPE = N'date'
   AND RTRIM(ISNULL(DISPLAY_FORMAT, N'')) <> @DATE_FORMAT;

/* ---------- ⑤ 受影响模块标记为待发布 ---------- */
/* 类型与显示格式都进了工作台定义，已发布快照不标脏就会继续按旧口径渲染（静默落后） */
DECLARE @Touched TABLE (T_ID NVARCHAR(200) NOT NULL PRIMARY KEY);
INSERT @Touched (T_ID)
SELECT DISTINCT T_ID FROM @DateTarget
UNION
SELECT DISTINCT RTRIM(T_ID) FROM dbo.FIELDS WHERE F_TYPE IN (N'datetime', N'smalldatetime', N'datetime2', N'date');

MERGE dbo.WORKBENCH_MODULE_DIRTY AS D
USING (
    SELECT DISTINCT m.M_IDX
      FROM dbo.MODULES AS m
     WHERE LTRIM(RTRIM(ISNULL(m.MASTER_TABLE, N''))) IN (SELECT T_ID FROM @Touched)
        OR LTRIM(RTRIM(ISNULL(m.DETAIL_TABLE, N''))) IN (SELECT T_ID FROM @Touched)
) AS S
   ON D.M_IDX = S.M_IDX
WHEN MATCHED THEN UPDATE SET D.DIRTY_TAG = 1, D.LAST_MODIFIED_BY = N'DbUp', D.LAST_MODIFIED_AT = SYSDATETIME()
WHEN NOT MATCHED THEN INSERT (M_IDX, DIRTY_TAG, LAST_MODIFIED_BY, LAST_MODIFIED_AT)
    VALUES (S.M_IDX, 1, N'DbUp', SYSDATETIME());

/* ---------- ⑥ 核对断言 ---------- */
IF EXISTS (SELECT 1
             FROM dbo.FIELDS AS f
             JOIN @DateTarget AS t ON RTRIM(f.T_ID) = t.T_ID AND RTRIM(f.F_ID) = t.F_ID
            WHERE f.F_TYPE <> N'date' OR RTRIM(ISNULL(f.DISPLAY_FORMAT, N'')) <> @DATE_FORMAT)
    THROW 52503, N'类型收口的目标字段未全部改成 date + yyyy-MM-dd，迁移中止。', 1;

IF EXISTS (SELECT 1
             FROM dbo.FIELDS
            WHERE F_TYPE IN (N'datetime', N'smalldatetime', N'datetime2')
              AND RTRIM(ISNULL(DISPLAY_FORMAT, N'')) <> @DATETIME_FORMAT)
    THROW 52504, N'仍有 datetime 家族字段的显示格式不是 yyyy-MM-dd HH:mm:ss（空格式会让列表只剩日期），迁移中止。', 1;

IF EXISTS (SELECT 1 FROM dbo.FIELDS WHERE F_TYPE = N'date' AND RTRIM(ISNULL(DISPLAY_FORMAT, N'')) <> @DATE_FORMAT)
    THROW 52505, N'仍有 date 字段的显示格式不是 yyyy-MM-dd，迁移中止。', 1;

IF EXISTS (
    SELECT 1
      FROM dbo.MODULES AS m
     WHERE (LTRIM(RTRIM(ISNULL(m.MASTER_TABLE, N''))) IN (SELECT T_ID FROM @Touched)
         OR LTRIM(RTRIM(ISNULL(m.DETAIL_TABLE, N''))) IN (SELECT T_ID FROM @Touched))
       AND NOT EXISTS (SELECT 1 FROM dbo.WORKBENCH_MODULE_DIRTY AS d WHERE d.M_IDX = m.M_IDX AND d.DIRTY_TAG = 1))
    THROW 52506, N'受影响模块未标记为待发布，迁移中止。', 1;

DECLARE @ConvertedCount INT = (SELECT COUNT(*) FROM @DateTarget);
DECLARE @DateTimeCount INT = (SELECT COUNT(*) FROM dbo.FIELDS WHERE F_TYPE IN (N'datetime', N'smalldatetime', N'datetime2') AND RTRIM(ISNULL(DISPLAY_FORMAT, N'')) = @DATETIME_FORMAT);
DECLARE @TouchedCount INT = (SELECT COUNT(*) FROM @Touched);

PRINT N'== 类型收口：' + CAST(@ConvertedCount AS NVARCHAR(10)) + N' 个字段改为 date + '
    + @DATE_FORMAT + N'；显示格式收口：' + CAST(@DateTimeCount AS NVARCHAR(10)) + N' 个 datetime 字段统一为 '
    + @DATETIME_FORMAT + N'（覆盖 ' + CAST(@TouchedCount AS NVARCHAR(10)) + N' 张表，相关模块已标脏，需重发布后生效）==';
GO
